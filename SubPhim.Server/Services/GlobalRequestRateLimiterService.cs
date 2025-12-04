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
        private SemaphoreSlim _semaphore;
        private int _currentMaxRequests;
        private int _currentWindowMinutes;

        // Track timestamps của các request đang chạy để auto-release
        private readonly ConcurrentDictionary<string, DateTime> _activeRequests = new();

        // Lock để đảm bảo việc cập nhật settings thread-safe
        private readonly object _configLock = new();

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
        /// Cập nhật cài đặt từ database nếu có thay đổi
        /// </summary>
        public async Task RefreshSettingsAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var settings = await context.LocalApiSettings.FindAsync(1);

                if (settings == null)
                {
                    _logger.LogWarning("LocalApiSettings not found in database, using defaults");
                    return;
                }

                int newMaxRequests = settings.GlobalMaxRequests > 0 ? settings.GlobalMaxRequests : 20;
                int newWindowMinutes = settings.GlobalWindowMinutes > 0 ? settings.GlobalWindowMinutes : 2;

                lock (_configLock)
                {
                    if (newMaxRequests != _currentMaxRequests || newWindowMinutes != _currentWindowMinutes)
                    {
                        _logger.LogInformation(
                            "Updating GlobalRequestRateLimiter: {OldMax}/{OldWindow}min -> {NewMax}/{NewWindow}min",
                            _currentMaxRequests, _currentWindowMinutes, newMaxRequests, newWindowMinutes);

                        // Tạo semaphore mới nếu capacity thay đổi
                        if (newMaxRequests != _currentMaxRequests)
                        {
                            var oldSemaphore = _semaphore;
                            _semaphore = new SemaphoreSlim(newMaxRequests, newMaxRequests);
                            _currentMaxRequests = newMaxRequests;

                            // Dispose old semaphore
                            try { oldSemaphore?.Dispose(); }
                            catch { /* Ignore */ }
                        }

                        _currentWindowMinutes = newWindowMinutes;
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
            return (
                _currentMaxRequests,
                _currentWindowMinutes,
                _semaphore.CurrentCount,
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

            _logger.LogDebug(
                "Job {JobId} waiting for global rate limit slot. Available: {Available}/{Max}",
                jobId, _semaphore.CurrentCount, _currentMaxRequests);

            // Đợi có slot khả dụng
            await _semaphore.WaitAsync(cancellationToken);

            var waitTime = DateTime.UtcNow - startWait;
            _activeRequests[requestId] = DateTime.UtcNow;

            _logger.LogInformation(
                "Job {JobId} acquired slot after {WaitMs}ms. Active: {Active}/{Max}",
                jobId, waitTime.TotalMilliseconds, _activeRequests.Count, _currentMaxRequests);

            // Đặt timer tự động release sau window time để tránh leak
            ScheduleAutoRelease(requestId, TimeSpan.FromMinutes(_currentWindowMinutes));

            return requestId;
        }

        /// <summary>
        /// Giải phóng slot sau khi request hoàn thành.
        /// </summary>
        public void ReleaseSlot(string requestId)
        {
            if (_activeRequests.TryRemove(requestId, out var startTime))
            {
                var duration = DateTime.UtcNow - startTime;

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
            }
        }

        /// <summary>
        /// Lên lịch tự động release slot sau một khoảng thời gian.
        /// Đây là failsafe để tránh leak slot nếu request không gọi ReleaseSlot.
        /// </summary>
        private void ScheduleAutoRelease(string requestId, TimeSpan delay)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay);

                // Chỉ release nếu slot vẫn đang active (chưa được release manually)
                if (_activeRequests.TryRemove(requestId, out _))
                {
                    try
                    {
                        _semaphore.Release();
                        _logger.LogWarning(
                            "Auto-released slot {RequestId} after timeout {DelayMinutes}min",
                            requestId, delay.TotalMinutes);
                    }
                    catch (SemaphoreFullException)
                    {
                        // Ignore - already at full capacity
                    }
                }
            });
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
