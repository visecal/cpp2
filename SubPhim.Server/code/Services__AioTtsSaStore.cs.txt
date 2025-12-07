using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using System.Collections.Concurrent;

namespace SubPhim.Server.Services
{
    public class AioTtsSaStore
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AioTtsSaStore> _logger;
        private List<AioTtsServiceAccount> _accountsCache = new();
        private ConcurrentDictionary<string, long> _usageCache = new();
        private string _currentMonthKey = string.Empty;
        private readonly object _lock = new();
        private const long MONTHLY_LIMIT_CHARS = 1_000_000;

        public AioTtsSaStore(IServiceProvider serviceProvider, ILogger<AioTtsSaStore> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _currentMonthKey = GetCurrentMonthKey();
            // Tải lần đầu khi khởi động
            LoadAccountsAndUsage().GetAwaiter().GetResult();
        }

        private string GetCurrentMonthKey() => DateTime.UtcNow.ToString("yyyy-MM");

        private async Task LoadAccountsAndUsage()
        {
            lock (_lock)
            {
                if (GetCurrentMonthKey() != _currentMonthKey)
                {
                    _logger.LogInformation("Đã sang tháng mới. Reset lại quota sử dụng của AIOLauncher TTS.");
                    _currentMonthKey = GetCurrentMonthKey();
                    _usageCache.Clear();
                }
            }

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var allAccounts = await context.AioTtsServiceAccounts.AsNoTracking().Where(sa => sa.IsEnabled).ToListAsync();

            lock (_lock)
            {
                _accountsCache = allAccounts;
                foreach (var acc in _accountsCache)
                {
                    if (acc.UsageMonth == _currentMonthKey)
                    {
                        _usageCache.TryAdd(acc.ClientEmail, acc.CharactersUsed);
                    }
                    else
                    {
                        _usageCache.TryAdd(acc.ClientEmail, 0);
                    }
                }
            }
            _logger.LogInformation("AIOLauncher TTS: Đã nạp {Count} Service Accounts đang hoạt động.", _accountsCache.Count);
        }

        public List<AioTtsServiceAccount> GetAvailableAccounts()
        {
            lock (_lock)
            {
                return new List<AioTtsServiceAccount>(_accountsCache);
            }
        }

        // HÀM MỚI: Thử đặt chỗ quota một cách an toàn
        public bool TryReserveQuota(string clientEmail, int neededChars)
        {
            // Tự động reset nếu cần
            if (GetCurrentMonthKey() != _currentMonthKey)
            {
                LoadAccountsAndUsage().GetAwaiter().GetResult();
            }

            // Vòng lặp để đảm bảo hoạt động trừ quota là an toàn (atomic)
            while (true)
            {
                long currentUsage = _usageCache.GetValueOrDefault(clientEmail, 0);
                long potentialUsage = currentUsage + neededChars;

                if (potentialUsage > MONTHLY_LIMIT_CHARS)
                {
                    return false; // Không đủ quota
                }

                // Cố gắng cập nhật giá trị một cách an toàn.
                // Chỉ thành công nếu giá trị hiện tại vẫn là `currentUsage`.
                if (_usageCache.TryUpdate(clientEmail, potentialUsage, currentUsage))
                {
                    // Đặt chỗ thành công, kích hoạt lưu vào DB ở chế độ nền
                    _ = UpdateDbUsageAsync(clientEmail, potentialUsage);
                    return true;
                }
                // Nếu TryUpdate thất bại, nghĩa là một thread khác đã cập nhật,
                // vòng lặp sẽ chạy lại để lấy giá trị mới và thử lại.
            }
        }

        // HÀM MỚI: Hoàn trả quota nếu xử lý thất bại
        public void ReleaseQuota(string clientEmail, int charsToRelease)
        {
            long newUsage = _usageCache.AddOrUpdate(clientEmail, 0, (key, currentUsage) => System.Math.Max(0, currentUsage - charsToRelease));
            _ = UpdateDbUsageAsync(clientEmail, newUsage);
        }

        // Hàm helper để cập nhật DB ở chế độ nền
        private async Task UpdateDbUsageAsync(string clientEmail, long newUsage)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var accountInDb = await context.AioTtsServiceAccounts.FirstOrDefaultAsync(sa => sa.ClientEmail == clientEmail);
                if (accountInDb != null)
                {
                    if (accountInDb.UsageMonth != _currentMonthKey)
                    {
                        accountInDb.UsageMonth = _currentMonthKey;
                    }
                    accountInDb.CharactersUsed = newUsage;
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật usage cho SA {Email} trong DB.", clientEmail);
            }
        }

        public async Task RefreshCacheAsync()
        {
            _logger.LogInformation("AIOLauncher TTS: Yêu cầu làm mới cache SA...");
            await LoadAccountsAndUsage();
        }
    }
}
