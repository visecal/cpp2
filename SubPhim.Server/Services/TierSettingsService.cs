using Microsoft.Extensions.Options;
using SubPhim.Server.Data;
using SubPhim.Server.Settings;
using System;

namespace SubPhim.Server.Services
{
    public class TierSettingsService : ITierSettingsService
    {
        // SỬA LỖI: Không dùng IOptionsMonitor nữa, dùng IServiceProvider để lấy DbContext
        private readonly IServiceProvider _serviceProvider;

        public TierSettingsService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void ApplyTierSettings(User user, SubscriptionTier tier)
        {
            // Tạo một scope riêng để lấy DbContext, tránh lỗi lifetime
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // SỬA LỖI: Đọc cấu hình mặc định từ database
            var defaultSettings = context.TierDefaultSettings.Find(tier);

            if (defaultSettings == null)
            {
                // Fallback an toàn nếu không tìm thấy setting trong DB
                // (trường hợp này hiếm khi xảy ra sau khi đã seed)
                ApplySafeDefaults(user, tier);
                return;
            }

            user.Tier = tier;
            user.GrantedFeatures = defaultSettings.GrantedFeatures;
            user.AllowedApiAccess = defaultSettings.AllowedApis;
            user.VideoDurationLimitMinutes = defaultSettings.VideoDurationMinutes;
            user.DailyVideoLimit = defaultSettings.DailyVideoCount;
            user.DailyRequestLimitOverride = defaultSettings.DailyTranslationRequests;
            user.DailySrtLineLimit = defaultSettings.DailySrtLineLimit;
            user.MaxDevices = (tier == SubscriptionTier.Free) ? 1 : 1;

            if (tier == SubscriptionTier.Free)
            {
                user.SubscriptionExpiry = null;
            }
        }

        private void ApplySafeDefaults(User user, SubscriptionTier tier)
        {
            user.Tier = tier;
            if (tier == SubscriptionTier.Free)
            {
                user.GrantedFeatures = GrantedFeatures.None;
                user.AllowedApiAccess = AllowedApis.OpenRouter;
                user.VideoDurationLimitMinutes = 30;
                user.DailyVideoLimit = 2;
                user.DailyRequestLimitOverride = 30;
                user.DailySrtLineLimit = 1000;
                user.MaxDevices = 1;
                user.SubscriptionExpiry = null;
            }
            else
            {
                // Mặc định an toàn cho các gói trả phí
                user.GrantedFeatures = GrantedFeatures.SubPhim | GrantedFeatures.DichThuat;
                user.AllowedApiAccess = AllowedApis.ChutesAI | AllowedApis.Gemini | AllowedApis.OpenRouter;
                user.VideoDurationLimitMinutes = 120;
                user.DailyVideoLimit = -1;
                user.DailyRequestLimitOverride = -1;
                user.DailySrtLineLimit = 99999;
                user.MaxDevices = 1;
            }
        }
    }
}