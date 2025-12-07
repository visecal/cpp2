using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;

namespace SubPhim.Server.Services
{
    public class VipTranslationResetService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<VipTranslationResetService> _logger;

        public VipTranslationResetService(
            IServiceProvider serviceProvider,
            ILogger<VipTranslationResetService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("VipTranslationResetService started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                    var nowVietnam = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
                    var nextMidnight = nowVietnam.Date.AddDays(1); // 12:00 AM ngày mai theo giờ VN
                    var delayUntilMidnight = nextMidnight - nowVietnam;

                    _logger.LogInformation(
                        "VipTranslation: Next reset at {NextMidnight} Vietnam time (in {Hours}h {Minutes}m)",
                        nextMidnight, delayUntilMidnight.Hours, delayUntilMidnight.Minutes);

                    await Task.Delay(delayUntilMidnight, stoppingToken);

                    // Reset user VIP translation quotas
                    await ResetUserQuotasAsync();

                    // Reset API key request counts
                    await ResetApiKeyRequestCountsAsync();
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("VipTranslationResetService is stopping");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in VipTranslationResetService");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }
        }

        private async Task ResetUserQuotasAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var nowUtc = DateTime.UtcNow;
            var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            var nowVietnam = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, vietnamTimeZone);
            var midnightVietnamUtc = TimeZoneInfo.ConvertTimeToUtc(nowVietnam.Date, vietnamTimeZone);

            var usersToReset = await context.Users
                .Where(u => u.LastVipTranslationResetUtc < midnightVietnamUtc)
                .ToListAsync();

            foreach (var user in usersToReset)
            {
                user.VipTranslationLinesUsedToday = 0;
                user.LastVipTranslationResetUtc = nowUtc;
            }

            await context.SaveChangesAsync();
            _logger.LogInformation("Reset VIP translation quota for {Count} users", usersToReset.Count);
        }

        private async Task ResetApiKeyRequestCountsAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var nowUtc = DateTime.UtcNow;
            var keysToReset = await context.VipApiKeys
                .Where(k => k.LastRequestCountResetUtc.Date < nowUtc.Date)
                .ToListAsync();

            foreach (var key in keysToReset)
            {
                key.RequestsToday = 0;
                key.LastRequestCountResetUtc = nowUtc;
            }

            await context.SaveChangesAsync();
            _logger.LogInformation("Reset request count for {Count} VIP API keys", keysToReset.Count);
        }
    }
}
