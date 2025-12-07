using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using SubPhim.Server.Services;
using System.ComponentModel.DataAnnotations;

namespace SubPhim.Server.Pages.Admin.VipTranslation
{
    public class IndexModel : PageModel
    {
        private readonly AppDbContext _context;
        private readonly IEncryptionService _encryptionService;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(AppDbContext context, IEncryptionService encryptionService, ILogger<IndexModel> logger)
        {
            _context = context;
            _encryptionService = encryptionService;
            _logger = logger;
        }

        public List<ApiKeyViewModel> ApiKeys { get; set; } = new();

        [BindProperty]
        public SettingsInputModel Settings { get; set; }

        [TempData] public string SuccessMessage { get; set; }
        [TempData] public string ErrorMessage { get; set; }

        public class ApiKeyViewModel
        {
            public VipApiKey KeyData { get; set; }
            public string DecryptedApiKey { get; set; }
        }

        public class ApiKeyInputModel
        {
            [Required(ErrorMessage = "Bạn phải nhập ít nhất một API Key.")]
            public string ApiKeys { get; set; }
        }

        public class SettingsInputModel
        {
            [Required][Range(1, 1000)] public int Rpm { get; set; }
            [Required][Range(1, 500)] public int BatchSize { get; set; }
            [Required][Range(0, 10)] public int MaxRetries { get; set; }
            [Required][Range(0, 60000)] public int RetryDelayMs { get; set; }
            [Required][Range(0, 60000)] public int DelayBetweenBatchesMs { get; set; }
            [Required][Range(0.0, 2.0)] public decimal Temperature { get; set; }
            [Required][Range(1, 32000)] public int MaxOutputTokens { get; set; }
            public bool EnableThinkingBudget { get; set; }
            [Range(0, 16384)] public int ThinkingBudget { get; set; }
            [Required][Range(1, 1000)] public int RpmPerProxy { get; set; }
        }

        public async Task OnGetAsync()
        {
            await LoadDataAsync();
            var settingsFromDb = await _context.VipTranslationSettings.FindAsync(1) ?? new VipTranslationSetting();
            Settings = new SettingsInputModel
            {
                Rpm = settingsFromDb.Rpm,
                BatchSize = settingsFromDb.BatchSize,
                MaxRetries = settingsFromDb.MaxRetries,
                RetryDelayMs = settingsFromDb.RetryDelayMs,
                DelayBetweenBatchesMs = settingsFromDb.DelayBetweenBatchesMs,
                Temperature = settingsFromDb.Temperature,
                MaxOutputTokens = settingsFromDb.MaxOutputTokens,
                EnableThinkingBudget = settingsFromDb.EnableThinkingBudget,
                ThinkingBudget = settingsFromDb.ThinkingBudget,
                RpmPerProxy = settingsFromDb.RpmPerProxy
            };
        }

        public async Task<IActionResult> OnPostDeleteSelectedKeysAsync([FromForm] int[] selectedKeyIds)
        {
            if (selectedKeyIds == null || !selectedKeyIds.Any())
            {
                ErrorMessage = "Vui lòng chọn ít nhất một API key để xóa.";
                return RedirectToPage();
            }

            var keysToDelete = await _context.VipApiKeys
                .Where(k => selectedKeyIds.Contains(k.Id))
                .ToListAsync();

            if (keysToDelete.Any())
            {
                _context.VipApiKeys.RemoveRange(keysToDelete);
                await _context.SaveChangesAsync();
                SuccessMessage = $"Đã xóa thành công {keysToDelete.Count} API key.";
            }

            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDisableSelectedKeysAsync([FromForm] int[] selectedKeyIds)
        {
            if (selectedKeyIds == null || !selectedKeyIds.Any())
            {
                ErrorMessage = "Vui lòng chọn ít nhất một API key để tắt.";
                return RedirectToPage();
            }

            var keysToDisable = await _context.VipApiKeys
                .Where(k => selectedKeyIds.Contains(k.Id))
                .ToListAsync();

            if (keysToDisable.Any())
            {
                foreach (var key in keysToDisable)
                {
                    key.IsEnabled = false;
                }
                await _context.SaveChangesAsync();
                SuccessMessage = $"Đã vô hiệu hóa thành công {keysToDisable.Count} API key.";
            }

            return RedirectToPage();
        }

        private async Task LoadDataAsync()
        {
            ApiKeys = await GetApiKeysAsync();
        }

        private async Task<List<ApiKeyViewModel>> GetApiKeysAsync()
        {
            var apiKeys = new List<ApiKeyViewModel>();
            var keysFromDb = await _context.VipApiKeys.OrderByDescending(k => k.CreatedAt).ToListAsync();
            foreach (var key in keysFromDb)
            {
                try { apiKeys.Add(new ApiKeyViewModel { KeyData = key, DecryptedApiKey = _encryptionService.Decrypt(key.EncryptedApiKey, key.Iv) }); }
                catch { apiKeys.Add(new ApiKeyViewModel { KeyData = key, DecryptedApiKey = "!!! LỖI GIẢI MÃ !!!" }); }
            }
            return apiKeys;
        }

        public async Task<IActionResult> OnPostUpdateSettingsAsync()
        {
            if (!TryValidateModel(Settings, nameof(Settings)))
            {
                TempData["ErrorMessage"] = "Dữ liệu cài đặt không hợp lệ. Vui lòng kiểm tra lại.";
                return RedirectToPage();
            }
            try
            {
                var settingsInDb = await _context.VipTranslationSettings.FindAsync(1);
                if (settingsInDb == null)
                {
                    settingsInDb = new VipTranslationSetting { Id = 1 };
                    _context.VipTranslationSettings.Add(settingsInDb);
                }
                settingsInDb.Rpm = Settings.Rpm;
                settingsInDb.BatchSize = Settings.BatchSize;
                settingsInDb.MaxRetries = Settings.MaxRetries;
                settingsInDb.RetryDelayMs = Settings.RetryDelayMs;
                settingsInDb.DelayBetweenBatchesMs = Settings.DelayBetweenBatchesMs;
                settingsInDb.Temperature = Settings.Temperature;
                settingsInDb.MaxOutputTokens = Settings.MaxOutputTokens;
                settingsInDb.EnableThinkingBudget = Settings.EnableThinkingBudget;
                settingsInDb.ThinkingBudget = Settings.ThinkingBudget;
                settingsInDb.RpmPerProxy = Settings.RpmPerProxy;
                await _context.SaveChangesAsync();
                SuccessMessage = "Đã lưu thành công cài đặt.";
            }
            catch (Exception ex)
            {
                ErrorMessage = "Lỗi khi lưu cài đặt: " + ex.Message;
            }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostCreateKeyAsync([FromForm] ApiKeyInputModel apiKeyInput)
        {
            ModelState.Clear();

            if (!TryValidateModel(apiKeyInput, nameof(apiKeyInput)))
            {
                TempData["ErrorMessage"] = "Bạn phải nhập ít nhất một API Key.";
                return RedirectToPage();
            }

            var keys = apiKeyInput.ApiKeys.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var key in keys)
            {
                var trimmedKey = key.Trim();
                if (!string.IsNullOrWhiteSpace(trimmedKey))
                {
                    var (encryptedText, iv) = _encryptionService.Encrypt(trimmedKey);
                    _context.VipApiKeys.Add(new VipApiKey { EncryptedApiKey = encryptedText, Iv = iv });
                }
            }
            await _context.SaveChangesAsync();
            SuccessMessage = $"Đã thêm thành công {keys.Length} API key.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostToggleEnabledAsync(int id)
        {
            var keyInDb = await _context.VipApiKeys.FindAsync(id);
            if (keyInDb != null)
            {
                keyInDb.IsEnabled = !keyInDb.IsEnabled;
                await _context.SaveChangesAsync();
            }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeleteKeyAsync(int id)
        {
            var keyToDelete = await _context.VipApiKeys.FindAsync(id);
            if (keyToDelete != null)
            {
                _context.VipApiKeys.Remove(keyToDelete);
                await _context.SaveChangesAsync();
            }
            return RedirectToPage();
        }
    }
}
