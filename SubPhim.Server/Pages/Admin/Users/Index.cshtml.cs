using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using SubPhim.Server.Services;
using PagedList.Core;

namespace SubPhim.Server.Pages.Admin.Users
{
    public class IndexModel : PageModel
    {
        private readonly AppDbContext _context;
        private readonly ITierSettingsService _tierSettingsService;
        public IndexModel(AppDbContext context, ITierSettingsService tierSettingsService)
        {
            _context = context;
            _tierSettingsService = tierSettingsService;
        }

        [BindProperty(SupportsGet = true)]
        public string? SearchString { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        public IPagedList<UserViewModel>? Users { get; set; }

        public List<string> AllFeatureNames => Enum.GetNames(typeof(GrantedFeatures)).Where(f => f != "None").ToList();
        public List<string> AllApiNames => Enum.GetNames(typeof(AllowedApis)).Where(a => a != "None").ToList();

        public async Task OnGetAsync()
        {
            IQueryable<User> query = _context.Users
                .AsNoTracking()
                .Include(u => u.Devices); // Thêm Include ở đây

            if (!string.IsNullOrEmpty(SearchString))
            {
                var searchLower = SearchString.ToLower();
                query = query.Where(u => u.Username.ToLower().Contains(searchLower) ||
                                         (u.Email != null && u.Email.ToLower().Contains(searchLower)) ||
                                         u.Uid.Contains(SearchString));
            }

            var totalItemCount = await query.CountAsync();
            const int pageSize = 20;

            var usersForCurrentPage = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((PageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var userViewModels = usersForCurrentPage.Select(u => new UserViewModel
            {
                Id = u.Id,
                Uid = u.Uid,
                Username = u.Username,
                Email = u.Email,
                Tier = u.Tier.ToString(),
                SubscriptionExpiry = u.SubscriptionExpiry,
                IsBlocked = u.IsBlocked,
                VideosProcessedToday = u.VideosProcessedToday,
                DailyVideoLimit = u.DailyVideoLimit,
                DailyRequestCount = u.DailyRequestCount,
                DailyRequestLimitOverride = u.DailyRequestLimitOverride,
                AllowedApiAccess = u.AllowedApiAccess,
                GrantedFeatures = u.GrantedFeatures,
                LastLogin = u.Devices.Any() ? u.Devices.Max(d => d.LastLogin) : (DateTime?)null, // Bây giờ sẽ hoạt động đúng
                SrtLinesUsedToday = u.SrtLinesUsedToday,
                DailySrtLineLimit = u.DailySrtLineLimit,
                DailyLocalSrtLimit = u.DailyLocalSrtLimit,
                TtsCharactersUsed = u.TtsCharactersUsed,
                TtsCharacterLimit = u.TtsCharacterLimit
            }).ToList();

            Users = new StaticPagedList<UserViewModel>(userViewModels, PageNumber, pageSize, totalItemCount);
        }

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostResetAllDevicesAsync()
        {
            try
            {
                // Sử dụng ExecuteDeleteAsync() để xóa tất cả các dòng trong bảng Devices
                // một cách hiệu quả mà không cần tải chúng vào bộ nhớ.
                var devicesDeletedCount = await _context.Devices.ExecuteDeleteAsync();

                TempData["SuccessMessage"] = $"Thao tác thành công! Đã xóa toàn bộ {devicesDeletedCount} thiết bị khỏi hệ thống. Tất cả người dùng sẽ cần đăng nhập lại.";
            }
            catch (Exception ex)
            {
                // Ghi lại log lỗi nếu cần
                TempData["ErrorMessage"] = "Đã có lỗi xảy ra trong quá trình reset thiết bị: " + ex.Message;
            }

            return RedirectToPage();
        }

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostAddSubTimeAsync(int id, int days)
        {
            var userToUpdate = await _context.Users.FindAsync(id);
            if (userToUpdate == null) return NotFound(new { message = "Không tìm thấy người dùng." });

            DateTime startTime = (userToUpdate.SubscriptionExpiry.HasValue && userToUpdate.SubscriptionExpiry.Value > DateTime.UtcNow)
                ? userToUpdate.SubscriptionExpiry.Value
                : DateTime.UtcNow;

            userToUpdate.SubscriptionExpiry = startTime.AddDays(days);
            await _context.SaveChangesAsync();

            return new JsonResult(new
            {
                message = $"Đã thêm thành công {days} ngày.",
                newExpiry = userToUpdate.SubscriptionExpiry
            });
        }

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostSubtractSubTimeAsync(int id, int days)
        {
            var userToUpdate = await _context.Users.FindAsync(id);
            if (userToUpdate == null) return NotFound(new { message = "Không tìm thấy người dùng." });

            if (!userToUpdate.SubscriptionExpiry.HasValue || userToUpdate.SubscriptionExpiry.Value <= DateTime.UtcNow)
            {
                return BadRequest(new { message = "Tài khoản đã hết hạn hoặc không có gói để trừ." });
            }

            var newExpiryDate = userToUpdate.SubscriptionExpiry.Value.AddDays(-days);
            string successMessage;

            if (newExpiryDate <= DateTime.UtcNow)
            {
                userToUpdate.SubscriptionExpiry = null;
                userToUpdate.Tier = SubscriptionTier.Free; // Tự động hạ cấp về Free
                successMessage = $"Đã trừ {days} ngày. Tài khoản đã hết hạn và được chuyển về gói Free.";
            }
            else
            {
                userToUpdate.SubscriptionExpiry = newExpiryDate;
                successMessage = $"Đã trừ thành công {days} ngày.";
            }

            await _context.SaveChangesAsync();
            return new JsonResult(new
            {
                message = successMessage,
                newExpiry = userToUpdate.SubscriptionExpiry,
                newTier = userToUpdate.Tier.ToString()
            });
        }

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostResetDevicesAsync(int id)
        {
            var devices = await _context.Devices.Where(d => d.UserId == id).ToListAsync();
            if (devices.Any())
            {
                _context.Devices.RemoveRange(devices);
                await _context.SaveChangesAsync();
            }
            return new JsonResult(new { message = "Đã xóa toàn bộ thiết bị của người dùng." });
        }

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostBanUserAndDevicesAsync(int id)
        {
            var userToBan = await _context.Users.Include(u => u.Devices).FirstOrDefaultAsync(u => u.Id == id);
            if (userToBan == null) return NotFound(new { message = "Không tìm thấy người dùng." });

            userToBan.IsBlocked = true;

            foreach (var device in userToBan.Devices)
            {
                bool isAlreadyBanned = await _context.BannedDevices.AnyAsync(b => b.Hwid == device.Hwid);
                if (!isAlreadyBanned)
                {
                    _context.BannedDevices.Add(new BannedDevice
                    {
                        Hwid = device.Hwid,
                        LastKnownIp = device.LastLoginIp,
                        AssociatedUsername = userToBan.Username,
                        BanReason = $"Bị cấm cùng với tài khoản '{userToBan.Username}' từ modal.",
                        BannedAt = DateTime.UtcNow
                    });
                }
            }

            await _context.SaveChangesAsync();
            return new JsonResult(new { message = $"Đã cấm tài khoản '{userToBan.Username}' và {userToBan.Devices.Count} thiết bị liên quan." });
        }
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostUpdatePermissionsAsync([FromForm] UpdatePermissionsRequest request)
        {
            var userInDb = await _context.Users.FindAsync(request.Id);
            if (userInDb == null)
            {
                return NotFound(new { message = "Không tìm thấy người dùng." });
            }

            var oldTier = userInDb.Tier;
            var newTier = request.Tier;
            if (oldTier != newTier)
            {
                _tierSettingsService.ApplyTierSettings(userInDb, newTier);
                DateTime startTime = DateTime.UtcNow;
                switch (newTier)
                {
                    case SubscriptionTier.Daily: userInDb.SubscriptionExpiry = startTime.AddDays(1); break;
                    case SubscriptionTier.Monthly: userInDb.SubscriptionExpiry = startTime.AddMonths(1); break;
                    case SubscriptionTier.Yearly: userInDb.SubscriptionExpiry = startTime.AddYears(1); break;
                    case SubscriptionTier.Lifetime: userInDb.SubscriptionExpiry = startTime.AddYears(100); break;
                }
            }
            GrantedFeatures newFeatures = GrantedFeatures.None;
            if (request.Features != null)
            {
                foreach (var featureName in request.Features)
                {
                    if (Enum.TryParse<GrantedFeatures>(featureName, out var feature)) { newFeatures |= feature; }
                }
            }
            userInDb.GrantedFeatures = newFeatures;

            AllowedApis newApis = AllowedApis.None;
            if (request.Apis != null)
            {
                foreach (var apiName in request.Apis)
                {
                    if (Enum.TryParse<AllowedApis>(apiName, out var api)) { newApis |= api; }
                }
            }
            userInDb.AllowedApiAccess = newApis;

            userInDb.DailyVideoLimit = request.DailyVideoLimit;
            userInDb.DailyRequestLimitOverride = request.DailyRequestLimitOverride;
            userInDb.DailySrtLineLimit = request.DailySrtLineLimit;
            userInDb.DailyLocalSrtLimit = request.DailyLocalSrtLimit;

            await _context.SaveChangesAsync();

            // Trả về dữ liệu mới nhất để JavaScript cập nhật UI
            return new JsonResult(new
            {
                message = "Cập nhật thành công!",
                newTier = userInDb.Tier.ToString(), // Trả về tier mới
                newExpiry = userInDb.SubscriptionExpiry, // Trả về hạn dùng mới
                grantedFeatures = userInDb.GrantedFeatures.ToString(),
                allowedApis = userInDb.AllowedApiAccess.ToString(),
                dailyVideoLimit = userInDb.DailyVideoLimit,
                dailyRequestLimitOverride = userInDb.DailyRequestLimitOverride,
                dailySrtLineLimit = userInDb.DailySrtLineLimit,
                dailyLocalSrtLimit = userInDb.DailyLocalSrtLimit
            });
        }
    }


    public class UpdatePermissionsRequest
    {
        public int Id { get; set; }
        public List<string> Features { get; set; }
        public List<string> Apis { get; set; }
        public SubscriptionTier Tier { get; set; }
        public int DailyVideoLimit { get; set; }
        public int DailyRequestLimitOverride { get; set; }
        public int DailyLocalSrtLimit { get; set; }
        public int SrtLinesUsedToday { get; set; }
        public int DailySrtLineLimit { get; set; }
    }
    public class UserViewModel
    {
        public int Id { get; set; }
        public string Uid { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public string Tier { get; set; }
        public DateTime? SubscriptionExpiry { get; set; }
        public bool IsBlocked { get; set; }
        public DateTime? LastLogin { get; set; }

        // Thông tin giới hạn
        public int VideosProcessedToday { get; set; }
        public int DailyVideoLimit { get; set; }
        public int DailyRequestCount { get; set; }
        public int DailyRequestLimitOverride { get; set; }

        // Thông tin quyền
        public AllowedApis AllowedApiAccess { get; set; }
        public GrantedFeatures GrantedFeatures { get; set; }

        public List<DeviceViewModel> Devices { get; set; } = new();
        public int DailyLocalSrtLimit { get; set; }
        public int SrtLinesUsedToday { get; set; }
        public int DailySrtLineLimit { get; set; }

        //tts
        public long TtsCharactersUsed { get; set; }
        public long TtsCharacterLimit { get; set; }

    }

    public class DeviceViewModel
    {
        public string Hwid { get; set; }
    }

    public static class UserDisplayHelper
    {
        public static string GetSubscriptionStatus(SubscriptionTier tier, DateTime? expiry)
        {
            if (tier == SubscriptionTier.Lifetime)
            {
                return "Vĩnh viễn";
            }

            if (tier == SubscriptionTier.Free)
            {
                return "Tự do";
            }

            if (expiry == null)
            {
                return "Không xác định";
            }

            if (expiry.Value < DateTime.UtcNow)
            {
                return "Đã hết hạn";
            }

            var timeLeft = expiry.Value - DateTime.UtcNow;

            if (timeLeft.TotalDays >= 1)
            {
                return $"Còn {timeLeft.Days} ngày";
            }
            if (timeLeft.TotalHours >= 1)
            {
                return $"Còn {timeLeft.Hours} giờ";
            }
            if (timeLeft.TotalMinutes > 1)
            {
                return $"Còn {timeLeft.Minutes} phút";
            }

            return "Sắp hết hạn";
        }
    }
}