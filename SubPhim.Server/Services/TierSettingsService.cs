using Microsoft.Extensions.Options;
using SubPhim.Server.Data;
using SubPhim.Server.Settings;
using System;
using System.Diagnostics;

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
            Debug.WriteLine($"[TierSettingsService] Applying settings for Tier '{tier}' to User '{user.Username}'.");

            // Tạo một scope riêng để lấy DbContext, tránh lỗi lifetime
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // SỬA LỖI: Đọc cấu hình mặc định từ database
            var defaultSettings = context.TierDefaultSettings.Find(tier);

            if (defaultSettings == null)
            {
                // Fallback an toàn nếu không tìm thấy setting trong DB
                // (trường hợp này hiếm khi xảy ra sau khi đã seed)
                Debug.WriteLine($"[TierSettingsService] WARNING: No default settings found for Tier '{tier}'. Applying safe defaults.");
                ApplySafeDefaults(user, tier);
                return;
            }

            user.Tier = tier;
            user.GrantedFeatures = defaultSettings.GrantedFeatures;
            user.AllowedApiAccess = defaultSettings.AllowedApiAccess;
            user.VideoDurationLimitMinutes = defaultSettings.VideoDurationMinutes;
            user.DailyVideoLimit = defaultSettings.DailyVideoCount;
            user.DailyRequestLimitOverride = defaultSettings.DailyTranslationRequests;
            user.DailySrtLineLimit = defaultSettings.DailySrtLineLimit;

            // <<< BẮT ĐẦU SỬA ĐỔI >>>
            user.TtsCharacterLimit = defaultSettings.TtsCharacterLimit;
            // Khi đổi gói, reset lại số ký tự đã dùng
            user.TtsCharactersUsed = 0;
            user.LastTtsResetUtc = DateTime.UtcNow;
            Debug.WriteLine($"[TierSettingsService] Applied TTS Character Limit: {user.TtsCharacterLimit}. Usage reset.");
            // <<< KẾT THÚC SỬA ĐỔI >>>

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