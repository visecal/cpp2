using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using System.Collections.Concurrent;

namespace SubPhim.Server.Services
{
    /// <summary>
    /// Service kiểm soát số lượng request toàn server trong một cửa sổ thời gian.
    /// Ví dụ: 20 request / 2 phút, khi request hoàn thành sẽ giải phóng slot.
    /// </summary>
    public class GlobalRequestRateLimiterService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<GlobalRequestRateLimiterService> _logger;

        // Semaphore để kiểm soát số concurrent requests
        private volatile SemaphoreSlim _semaphore;
        private volatile int _currentMaxRequests;
        private volatile int _currentWindowMinutes;

        // Track timestamps của các request đang chạy để auto-release
        private readonly ConcurrentDictionary<string, (DateTime StartTime, CancellationTokenSource AutoReleaseCts)> _activeRequests = new();

        // Lock để đảm bảo việc cập nhật settings thread-safe
        private readonly object _configLock = new();
        
        // Cache cho settings refresh - refresh tối đa mỗi 5 giây
        private DateTime _lastSettingsRefresh = DateTime.MinValue;
        private readonly TimeSpan _settingsRefreshInterval = TimeSpan.FromSeconds(5);

        public GlobalRequestRateLimiterService(
            IServiceProvider serviceProvider,
            ILogger<GlobalRequestRateLimiterService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;

            // Khởi tạo với giá trị mặc định, sẽ được cập nhật khi load settings
            _currentMaxRequests = 20;
            _currentWindowMinutes = 2;
            _semaphore = new SemaphoreSlim(_currentMaxRequests, _currentMaxRequests);

            _logger.LogInformation(
                "GlobalRequestRateLimiter initialized with default settings: {MaxRequests} requests / {WindowMinutes} minutes",
                _currentMaxRequests, _currentWindowMinutes);
        }

        /// <summary>
        /// Cập nhật cài đặt từ database nếu có thay đổi.
        /// Sử dụng cơ chế cache để giảm tải database.
        /// </summary>
        public async Task RefreshSettingsAsync(bool forceRefresh = false)
        {
            // Kiểm tra cache - chỉ refresh nếu đã quá interval hoặc force
            if (!forceRefresh && DateTime.UtcNow - _lastSettingsRefresh < _settingsRefreshInterval)
            {
                return;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var settings = await context.LocalApiSettings.FindAsync(1);

                _lastSettingsRefresh = DateTime.UtcNow;

                if (settings == null)
                {
                    _logger.LogWarning("LocalApiSettings not found in database, using defaults");
                    return;
                }

                int newMaxRequests = settings.GlobalMaxRequests > 0 ? settings.GlobalMaxRequests : 20;
                int newWindowMinutes = settings.GlobalWindowMinutes > 0 ? settings.GlobalWindowMinutes : 2;

                lock (_configLock)
                {
                    bool needsNewSemaphore = newMaxRequests != _currentMaxRequests;
                    
                    if (needsNewSemaphore || newWindowMinutes != _currentWindowMinutes)
                    {
                        _logger.LogInformation(
                            "Updating GlobalRequestRateLimiter: {OldMax}/{OldWindow}min -> {NewMax}/{NewWindow}min",
                            _currentMaxRequests, _currentWindowMinutes, newMaxRequests, newWindowMinutes);

                        // Cập nhật window minutes trước
                        _currentWindowMinutes = newWindowMinutes;

                        // Tạo semaphore mới nếu capacity thay đổi
                        // Sử dụng volatile và chỉ swap reference - không dispose ngay để tránh race condition
                        // với các thread đang chờ WaitAsync() hoặc gọi Release()
                        if (needsNewSemaphore)
                        {
                            _currentMaxRequests = newMaxRequests;
                            // Tạo semaphore mới, để cho GC thu dọn cái cũ
                            // Việc này an toàn hơn so với dispose vì các thread đang chờ sẽ không bị exception
                            _semaphore = new SemaphoreSlim(newMaxRequests, newMaxRequests);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing GlobalRequestRateLimiter settings");
            }
        }

        /// <summary>
        /// Lấy thông tin hiện tại về rate limiter
        /// </summary>
        public (int maxRequests, int windowMinutes, int availableSlots, int activeRequests) GetCurrentStatus()
        {
            var semaphore = _semaphore; // Capture reference để tránh race condition
            return (
                _currentMaxRequests,
                _currentWindowMinutes,
                semaphore.CurrentCount,
                _activeRequests.Count
            );
        }

        /// <summary>
        /// Đợi cho đến khi có slot khả dụng và acquire nó.
        /// Trả về requestId để dùng khi release.
        /// </summary>
        public async Task<string> AcquireSlotAsync(string jobId, CancellationToken cancellationToken = default)
        {
            // Refresh settings mỗi lần acquire để đảm bảo settings mới nhất
            await RefreshSettingsAsync();

            var requestId = $"{jobId}_{Guid.NewGuid():N}";
            var startWait = DateTime.UtcNow;
            var semaphore = _semaphore; // Capture reference

            _logger.LogDebug(
                "Job {JobId} waiting for global rate limit slot. Available: {Available}/{Max}",
                jobId, semaphore.CurrentCount, _currentMaxRequests);

            // Đợi có slot khả dụng
            await semaphore.WaitAsync(cancellationToken);

            var waitTime = DateTime.UtcNow - startWait;
            
            // Tạo CTS cho auto-release
            var autoReleaseCts = new CancellationTokenSource();
            _activeRequests[requestId] = (DateTime.UtcNow, autoReleaseCts);

            _logger.LogInformation(
                "Job {JobId} acquired slot after {WaitMs}ms. Active: {Active}/{Max}",
                jobId, waitTime.TotalMilliseconds, _activeRequests.Count, _currentMaxRequests);

            // Đặt timer tự động release sau window time để tránh leak
            ScheduleAutoRelease(requestId, semaphore, TimeSpan.FromMinutes(_currentWindowMinutes), autoReleaseCts.Token);

            return requestId;
        }

        /// <summary>
        /// Giải phóng slot sau khi request hoàn thành.
        /// </summary>
        public void ReleaseSlot(string requestId)
        {
            if (_activeRequests.TryRemove(requestId, out var entry))
            {
                var duration = DateTime.UtcNow - entry.StartTime;

                // Hủy auto-release timer
                try
                {
                    entry.AutoReleaseCts.Cancel();
                    entry.AutoReleaseCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore - already disposed
                }

                try
                {
                    _semaphore.Release();
                    _logger.LogDebug(
                        "Released slot {RequestId} after {DurationMs}ms. Available: {Available}/{Max}",
                        requestId, duration.TotalMilliseconds, _semaphore.CurrentCount, _currentMaxRequests);
                }
                catch (SemaphoreFullException)
                {
                    // Đã được release bởi auto-release timer
                    _logger.LogDebug("Slot {RequestId} was already released by auto-release", requestId);
                }
                catch (ObjectDisposedException)
                {
                    // Semaphore đã bị disposed do settings change - ignore
                    _logger.LogDebug("Slot {RequestId} release skipped - semaphore was replaced", requestId);
                }
            }
        }

        /// <summary>
        /// Lên lịch tự động release slot sau một khoảng thời gian.
        /// Đây là failsafe để tránh leak slot nếu request không gọi ReleaseSlot.
        /// </summary>
        private void ScheduleAutoRelease(string requestId, SemaphoreSlim semaphore, TimeSpan delay, CancellationToken cancellationToken)
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, cancellationToken);

                    // Chỉ release nếu slot vẫn đang active (chưa được release manually)
                    if (_activeRequests.TryRemove(requestId, out var entry))
                    {
                        // Dispose CTS
                        try { entry.AutoReleaseCts.Dispose(); }
                        catch { /* Ignore */ }

                        try
                        {
                            semaphore.Release();
                            _logger.LogWarning(
                                "Auto-released slot {RequestId} after timeout {DelayMinutes}min",
                                requestId, delay.TotalMinutes);
                        }
                        catch (SemaphoreFullException)
                        {
                            // Ignore - already at full capacity
                        }
                        catch (ObjectDisposedException)
                        {
                            // Semaphore đã bị disposed - ignore
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Task was cancelled because slot was released manually - this is expected
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in auto-release for slot {RequestId}", requestId);
                }
            }, CancellationToken.None); // Use CancellationToken.None for the Task itself
        }

        /// <summary>
        /// Kiểm tra xem có slot khả dụng không (không block)
        /// </summary>
        public bool HasAvailableSlot()
        {
            return _semaphore.CurrentCount > 0;
        }

        /// <summary>
        /// Lấy số lượng slot khả dụng
        /// </summary>
        public int GetAvailableSlots()
        {
            return _semaphore.CurrentCount;
        }
    }
}
