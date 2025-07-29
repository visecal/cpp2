using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using System.Text.Json;

namespace SubPhim.Server.Pages.Admin
{
    public class UserBackupModel
    {
        // Thông tin cơ bản
        public string Uid { get; set; }
        public int Id { get; set; }
        public string Username { get; set; }
        public string? Email { get; set; }
        public string PasswordHash { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsBlocked { get; set; }
        public bool IsAdmin { get; set; }

        // Thông tin gói và quyền
        public SubscriptionTier Tier { get; set; }
        public DateTime? SubscriptionExpiry { get; set; }
        public AllowedApis AllowedApiAccess { get; set; }
        public GrantedFeatures GrantedFeatures { get; set; }
        public int MaxDevices { get; set; }

        // Giới hạn và bộ đếm Dịch Truyện
        public int DailyRequestLimitOverride { get; set; }
        public int DailyRequestCount { get; set; }
        public DateTime LastRequestResetUtc { get; set; }

        // Giới hạn và bộ đếm Video
        public int VideosProcessedToday { get; set; }
        public DateTime LastVideoResetUtc { get; set; }
        public int VideoDurationLimitMinutes { get; set; }
        public int DailyVideoLimit { get; set; }

        // Giới hạn và bộ đếm Dịch SRT (API)
        public int DailySrtLineLimit { get; set; }
        public int SrtLinesUsedToday { get; set; }
        public DateTime LastSrtLineResetUtc { get; set; }

        // Giới hạn và bộ đếm Dịch SRT (Local)
        public int DailyLocalSrtLimit { get; set; }
        public int LocalSrtLinesUsedToday { get; set; }
        public DateTime LastLocalSrtResetUtc { get; set; }
    }

    public class BackupRestoreModel : PageModel
    {
        private readonly AppDbContext _context;
        private readonly ILogger<BackupRestoreModel> _logger;

        public BackupRestoreModel(AppDbContext context, ILogger<BackupRestoreModel> logger)
        {
            _context = context;
            _logger = logger;
        }

        [TempData]
        public string SuccessMessage { get; set; }
        [TempData]
        public string ErrorMessage { get; set; }

        public void OnGet()
        {
            // Chỉ hiển thị trang, không cần logic gì
        }

        public async Task<IActionResult> OnGetDownloadBackupAsync()
        {
            try
            {
                var usersToBackup = await _context.Users
                    .AsNoTracking()
                    .Select(u => new UserBackupModel
                    {
                        // Thông tin cơ bản
                        Uid = u.Uid,
                        Id = u.Id,
                        Username = u.Username,
                        Email = u.Email,
                        PasswordHash = u.PasswordHash,
                        CreatedAt = u.CreatedAt,
                        IsBlocked = u.IsBlocked,
                        IsAdmin = u.IsAdmin,

                        // Thông tin gói và quyền
                        Tier = u.Tier,
                        SubscriptionExpiry = u.SubscriptionExpiry,
                        AllowedApiAccess = u.AllowedApiAccess,
                        GrantedFeatures = u.GrantedFeatures,
                        MaxDevices = u.MaxDevices,

                        // Giới hạn và bộ đếm Dịch Truyện
                        DailyRequestLimitOverride = u.DailyRequestLimitOverride,
                        DailyRequestCount = u.DailyRequestCount,
                        LastRequestResetUtc = u.LastRequestResetUtc,

                        // Giới hạn và bộ đếm Video
                        VideosProcessedToday = u.VideosProcessedToday,
                        LastVideoResetUtc = u.LastVideoResetUtc,
                        VideoDurationLimitMinutes = u.VideoDurationLimitMinutes,
                        DailyVideoLimit = u.DailyVideoLimit,

                        // Giới hạn và bộ đếm Dịch SRT (API)
                        DailySrtLineLimit = u.DailySrtLineLimit,
                        SrtLinesUsedToday = u.SrtLinesUsedToday,
                        LastSrtLineResetUtc = u.LastSrtLineResetUtc,

                        // Giới hạn và bộ đếm Dịch SRT (Local)
                        DailyLocalSrtLimit = u.DailyLocalSrtLimit,
                        LocalSrtLinesUsedToday = u.LocalSrtLinesUsedToday,
                        LastLocalSrtResetUtc = u.LastLocalSrtResetUtc
                    })
                    .ToListAsync();

                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                var jsonString = JsonSerializer.Serialize(usersToBackup, jsonOptions);
                var fileName = $"subphim_users_backup_{DateTime.Now:yyyyMMdd_HHmm}.json";

                return File(System.Text.Encoding.UTF8.GetBytes(jsonString), "application/json", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tạo file backup người dùng.");
                ErrorMessage = "Đã có lỗi xảy ra trong quá trình tạo file backup. Vui lòng kiểm tra log.";
                return RedirectToPage();
            }
        }
        public async Task<IActionResult> OnPostImportAsync(IFormFile backupFile)
        {
            if (backupFile == null || backupFile.Length == 0)
            {
                ErrorMessage = "Vui lòng chọn một file backup để tải lên.";
                return Page();
            }

            if (!backupFile.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "File không hợp lệ. Chỉ chấp nhận file .json.";
                return Page();
            }

            List<UserBackupModel> usersFromBackup;
            try
            {
                using var streamReader = new StreamReader(backupFile.OpenReadStream());
                var jsonString = await streamReader.ReadToEndAsync();
                usersFromBackup = JsonSerializer.Deserialize<List<UserBackupModel>>(jsonString);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc file JSON backup.");
                ErrorMessage = "File backup không thể đọc được. Cấu trúc JSON không hợp lệ.";
                return Page();
            }

            if (usersFromBackup == null || !usersFromBackup.Any())
            {
                SuccessMessage = "File backup không chứa dữ liệu người dùng nào.";
                return RedirectToPage();
            }

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                int importedCount = 0;
                int updatedCount = 0;
                var existingUsers = await _context.Users.ToDictionaryAsync(u => u.Username, u => u);

                foreach (var userFromFile in usersFromBackup)
                {
                    if (existingUsers.TryGetValue(userFromFile.Username, out var userInDb))
                    {
                        // === CẬP NHẬT USER ĐÃ CÓ ===
                        userInDb.Email = userFromFile.Email;
                        userInDb.PasswordHash = userFromFile.PasswordHash;
                        userInDb.IsBlocked = userFromFile.IsBlocked;
                        userInDb.IsAdmin = userFromFile.IsAdmin;
                        userInDb.Tier = userFromFile.Tier;
                        userInDb.SubscriptionExpiry = userFromFile.SubscriptionExpiry;
                        userInDb.AllowedApiAccess = userFromFile.AllowedApiAccess;
                        userInDb.GrantedFeatures = userFromFile.GrantedFeatures;
                        userInDb.MaxDevices = userFromFile.MaxDevices;
                        userInDb.DailyRequestLimitOverride = userFromFile.DailyRequestLimitOverride;
                        userInDb.DailyRequestCount = userFromFile.DailyRequestCount;
                        userInDb.LastRequestResetUtc = userFromFile.LastRequestResetUtc;
                        userInDb.VideosProcessedToday = userFromFile.VideosProcessedToday;
                        userInDb.LastVideoResetUtc = userFromFile.LastVideoResetUtc;
                        userInDb.VideoDurationLimitMinutes = userFromFile.VideoDurationLimitMinutes;
                        userInDb.DailyVideoLimit = userFromFile.DailyVideoLimit;
                        userInDb.DailySrtLineLimit = userFromFile.DailySrtLineLimit;
                        userInDb.SrtLinesUsedToday = userFromFile.SrtLinesUsedToday;
                        userInDb.LastSrtLineResetUtc = userFromFile.LastSrtLineResetUtc;
                        userInDb.DailyLocalSrtLimit = userFromFile.DailyLocalSrtLimit;
                        userInDb.LocalSrtLinesUsedToday = userFromFile.LocalSrtLinesUsedToday;
                        userInDb.LastLocalSrtResetUtc = userFromFile.LastLocalSrtResetUtc;

                        updatedCount++;
                    }
                    else
                    {
                        // === THÊM USER MỚI ===
                        var newUser = new User
                        {
                            Uid = userFromFile.Uid,
                            Username = userFromFile.Username,
                            Email = userFromFile.Email,
                            PasswordHash = userFromFile.PasswordHash,
                            CreatedAt = userFromFile.CreatedAt,
                            IsBlocked = userFromFile.IsBlocked,
                            IsAdmin = userFromFile.IsAdmin,
                            Tier = userFromFile.Tier,
                            SubscriptionExpiry = userFromFile.SubscriptionExpiry,
                            AllowedApiAccess = userFromFile.AllowedApiAccess,
                            GrantedFeatures = userFromFile.GrantedFeatures,
                            MaxDevices = userFromFile.MaxDevices,
                            DailyRequestLimitOverride = userFromFile.DailyRequestLimitOverride,
                            DailyRequestCount = userFromFile.DailyRequestCount,
                            LastRequestResetUtc = userFromFile.LastRequestResetUtc,
                            VideosProcessedToday = userFromFile.VideosProcessedToday,
                            LastVideoResetUtc = userFromFile.LastVideoResetUtc,
                            VideoDurationLimitMinutes = userFromFile.VideoDurationLimitMinutes,
                            DailyVideoLimit = userFromFile.DailyVideoLimit,
                            DailySrtLineLimit = userFromFile.DailySrtLineLimit,
                            SrtLinesUsedToday = userFromFile.SrtLinesUsedToday,
                            LastSrtLineResetUtc = userFromFile.LastSrtLineResetUtc,
                            DailyLocalSrtLimit = userFromFile.DailyLocalSrtLimit,
                            LocalSrtLinesUsedToday = userFromFile.LocalSrtLinesUsedToday,
                            LastLocalSrtResetUtc = userFromFile.LastLocalSrtResetUtc
                        };
                        _context.Users.Add(newUser);
                        importedCount++;
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                SuccessMessage = $"Khôi phục thành công! Đã thêm mới {importedCount} user và cập nhật {updatedCount} user.";
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Lỗi nghiêm trọng khi import dữ liệu người dùng.");
                ErrorMessage = "Đã có lỗi xảy ra trong quá trình import, mọi thay đổi đã được hoàn tác. Chi tiết: " + ex.Message;
            }

            return RedirectToPage();
        }
    }
}