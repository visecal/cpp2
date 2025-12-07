// VỊ TRÍ: Pages/Admin/TierManagement.cshtml.cs
// THAY THẾ TOÀN BỘ FILE

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using System.Threading.Tasks;

namespace SubPhim.Server.Pages.Admin
{
    public class TierManagementModel : PageModel
    {
        private readonly AppDbContext _context;

        public TierManagementModel(AppDbContext context)
        {
            _context = context;
        }

        [BindProperty]
        public Dictionary<SubscriptionTier, TierConfigModel> Configs { get; set; } = new();

        [TempData]
        public string SuccessMessage { get; set; }
        [TempData]
        public string ErrorMessage { get; set; }

        public class TierConfigModel
        {
            public int VideoDurationMinutes { get; set; }
            public int DailyVideoCount { get; set; }
            public int DailyTranslationRequests { get; set; }
            public AllowedApis AllowedApis { get; set; }
            public GrantedFeatures GrantedFeatures { get; set; }
            public int DailySrtLineLimit { get; set; }
            public long TtsCharacterLimit { get; set; }
            public int DailyLocalSrtLimit { get; set; }

            // === BẮT ĐẦU THÊM MỚI ===
            public long AioCharacterLimit { get; set; }
            public int AioRequestsPerMinute { get; set; }
            public int DailyVipTranslationLimit { get; set; }
            // === KẾT THÚC THÊM MỚI ===
        }

        public async Task OnGetAsync()
        {
            var defaultSettings = await _context.TierDefaultSettings.ToListAsync();

            foreach (var setting in defaultSettings)
            {
                if (!Configs.ContainsKey(setting.Tier))
                {
                    Configs.Add(setting.Tier, new TierConfigModel
                    {
                        VideoDurationMinutes = setting.VideoDurationMinutes,
                        DailyVideoCount = setting.DailyVideoCount,
                        DailyTranslationRequests = setting.DailyTranslationRequests,
                        AllowedApis = setting.AllowedApis,
                        GrantedFeatures = setting.GrantedFeatures,
                        DailySrtLineLimit = setting.DailySrtLineLimit,
                        TtsCharacterLimit = setting.TtsCharacterLimit,
                        DailyLocalSrtLimit = setting.DailyLocalSrtLimit,
                        // === BẮT ĐẦU THÊM MỚI ===
                        AioCharacterLimit = setting.AioCharacterLimit,
                        AioRequestsPerMinute = setting.AioRequestsPerMinute,
                        DailyVipTranslationLimit = setting.DailyVipTranslationLimit
                        // === KẾT THÚC THÊM MỚI ===
                    });
                }
            }
        }
        public async Task<IActionResult> OnPostSaveDefaultsAsync()
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                foreach (var configEntry in Configs)
                {
                    var tier = configEntry.Key;
                    var formConfig = configEntry.Value;

                    var settingInDb = await _context.TierDefaultSettings.FindAsync(tier);
                    if (settingInDb != null)
                    {
                        settingInDb.VideoDurationMinutes = formConfig.VideoDurationMinutes;
                        settingInDb.DailyVideoCount = formConfig.DailyVideoCount;
                        settingInDb.DailyTranslationRequests = formConfig.DailyTranslationRequests;
                        settingInDb.AllowedApis = formConfig.AllowedApis;
                        settingInDb.GrantedFeatures = formConfig.GrantedFeatures;
                        settingInDb.DailySrtLineLimit = formConfig.DailySrtLineLimit;
                        settingInDb.TtsCharacterLimit = formConfig.TtsCharacterLimit;
                        settingInDb.DailyLocalSrtLimit = formConfig.DailyLocalSrtLimit;
                        // === BẮT ĐẦU THÊM MỚI ===
                        settingInDb.AioCharacterLimit = formConfig.AioCharacterLimit;
                        settingInDb.AioRequestsPerMinute = formConfig.AioRequestsPerMinute;
                        settingInDb.DailyVipTranslationLimit = formConfig.DailyVipTranslationLimit;
                        // === KẾT THÚC THÊM MỚI ===
                    }
                }
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                SuccessMessage = "Đã lưu thành công cấu hình mặc định. Các tài khoản mới hoặc được áp dụng tier sẽ nhận cài đặt này.";
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                ErrorMessage = "Lỗi khi lưu cấu hình mặc định: " + ex.Message;
            }
            return RedirectToPage();
        }
        public async Task<IActionResult> OnPostApplyToAllUsersAsync(SubscriptionTier tier)
        {
            if (!Configs.TryGetValue(tier, out var configToApply))
            {
                ErrorMessage = "Không tìm thấy cấu hình cho nhóm được chọn.";
                return RedirectToPage();
            }

            try
            {
                var usersToUpdate = await _context.Users
                    .Where(u => u.Tier == tier)
                    .ToListAsync();

                if (!usersToUpdate.Any())
                {
                    SuccessMessage = $"Không có người dùng nào thuộc nhóm '{tier}' để áp dụng.";
                    return RedirectToPage();
                }

                foreach (var user in usersToUpdate)
                {
                    user.VideoDurationLimitMinutes = configToApply.VideoDurationMinutes;
                    user.DailyVideoLimit = configToApply.DailyVideoCount;
                    user.DailyRequestLimitOverride = configToApply.DailyTranslationRequests;
                    user.AllowedApiAccess = configToApply.AllowedApis;
                    user.GrantedFeatures = configToApply.GrantedFeatures;
                    user.DailySrtLineLimit = configToApply.DailySrtLineLimit;
                    user.TtsCharacterLimit = configToApply.TtsCharacterLimit;
                    user.DailyLocalSrtLimit = configToApply.DailyLocalSrtLimit;

                    user.AioCharacterLimitOverride = -1;
                    user.AioRpmOverride = -1;
                }

                var count = await _context.SaveChangesAsync();
                SuccessMessage = $"Đã áp dụng thành công cấu hình mới cho {count} người dùng thuộc nhóm '{tier}'.";
            }
            catch (Exception ex)
            {
                ErrorMessage = "Lỗi khi cập nhật hàng loạt người dùng: " + ex.Message;
            }

            return RedirectToPage();
        }
    }
}