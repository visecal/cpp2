using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using System.ComponentModel.DataAnnotations;

namespace SubPhim.Server.Pages.Admin
{
    public class NewUserSettingsModel : PageModel
    {
        private readonly AppDbContext _context;

        public NewUserSettingsModel(AppDbContext context)
        {
            _context = context;
        }

        [BindProperty]
        public TierSettingsViewModel FreeTierSettings { get; set; } = new();

        [TempData]
        public string SuccessMessage { get; set; }
        [TempData]
        public string ErrorMessage { get; set; }

        public class TierSettingsViewModel
        {
            [Display(Name = "Giới hạn Video / Ngày")]
            public int DailyVideoCount { get; set; }

            [Display(Name = "Giới hạn Dịch Truyện / Ngày")]
            public int DailyTranslationRequests { get; set; }

            [Display(Name = "Giới hạn Dịch SRT (API bên thứ 3) / Ngày")]
            public int DailySrtLineLimit { get; set; }

            // === ĐÃ THÊM MỚI ===
            [Display(Name = "Giới hạn Dịch SRT (Local API Server) / Ngày")]
            public int DailyLocalSrtLimit { get; set; }

            [Display(Name = "Quyền Truy Cập API")]
            public AllowedApis AllowedApiAccess { get; set; }

            [Display(Name = "Quyền Truy Cập Tính Năng")]
            public GrantedFeatures GrantedFeatures { get; set; }

            [Display(Name = "Giới hạn Ký tự TTS / Ngày")]
            public long TtsCharacterLimit { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var freeSettings = await _context.TierDefaultSettings.FindAsync(SubscriptionTier.Free);

            if (freeSettings == null)
            {
                ErrorMessage = "Không tìm thấy cấu hình mặc định cho gói 'Free'. Vui lòng kiểm tra lại database.";
                return Page();
            }

            FreeTierSettings = new TierSettingsViewModel
            {
                DailyVideoCount = freeSettings.DailyVideoCount,
                DailyTranslationRequests = freeSettings.DailyTranslationRequests,
                DailySrtLineLimit = freeSettings.DailySrtLineLimit,
                DailyLocalSrtLimit = freeSettings.DailyLocalSrtLimit,
                AllowedApiAccess = freeSettings.AllowedApiAccess,
                GrantedFeatures = freeSettings.GrantedFeatures,
                TtsCharacterLimit = freeSettings.TtsCharacterLimit
            };

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            var freeSettingsInDb = await _context.TierDefaultSettings.FindAsync(SubscriptionTier.Free);
            if (freeSettingsInDb == null)
            {
                ErrorMessage = "Lỗi nghiêm trọng: Không tìm thấy cấu hình 'Free' để cập nhật.";
                return Page();
            }

            try
            {
                freeSettingsInDb.DailyVideoCount = FreeTierSettings.DailyVideoCount;
                freeSettingsInDb.DailyTranslationRequests = FreeTierSettings.DailyTranslationRequests;
                freeSettingsInDb.DailySrtLineLimit = FreeTierSettings.DailySrtLineLimit;
                freeSettingsInDb.DailyLocalSrtLimit = FreeTierSettings.DailyLocalSrtLimit;
                freeSettingsInDb.AllowedApiAccess = FreeTierSettings.AllowedApiAccess;
                freeSettingsInDb.GrantedFeatures = FreeTierSettings.GrantedFeatures;
                freeSettingsInDb.TtsCharacterLimit = FreeTierSettings.TtsCharacterLimit;

                await _context.SaveChangesAsync();
                SuccessMessage = "Đã cập nhật thành công cài đặt mặc định cho người dùng đăng ký mới!";
            }
            catch (Exception ex)
            {
                ErrorMessage = "Đã có lỗi xảy ra khi lưu cài đặt: " + ex.Message;
            }

            return RedirectToPage();
        }
    }
}