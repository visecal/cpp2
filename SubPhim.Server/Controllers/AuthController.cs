using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SubPhim.Server.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options; 
using SubPhim.Server.Settings;
using SubPhim.Server.Services;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    [HttpPost("check-feature-access")]
    [Authorize] // Chỉ người dùng đã đăng nhập mới được gọi
    public async Task<IActionResult> CheckFeatureAccess([FromBody] FeatureAccessRequest request)
    {
        var userIdString = User.FindFirstValue("id");
        if (!int.TryParse(userIdString, out int userId))
        {
            return Unauthorized();
        }

        // Luôn lấy thông tin mới nhất từ DB để đảm bảo quyền là chính xác
        var userInDb = await _context.Users.FindAsync(userId);
        if (userInDb == null)
        {
            return NotFound("Tài khoản không tồn tại.");
        }

        // Chuyển đổi tên tính năng từ chuỗi sang enum
        if (!Enum.TryParse<GrantedFeatures>(request.FeatureName, true, out var featureToCheck))
        {
            return BadRequest($"Tên tính năng không hợp lệ: {request.FeatureName}");
        }

        // Kiểm tra quyền
        bool hasAccess = userInDb.GrantedFeatures.HasFlag(featureToCheck);

        if (hasAccess)
        {
            return Ok(new { HasAccess = true, Message = "OK" });
        }
        else
        {
            return Ok(new { HasAccess = false, Message = $"Bạn không có quyền truy cập tính năng '{request.FeatureName}'. Vui lòng liên hệ admin." });
        }
    }
    public record FeatureAccessRequest(string FeatureName);
    public record DeviceDto(string Hwid, string LastLoginIp, DateTime LastLogin);
    private readonly AppDbContext _context;
    private readonly IOptionsMonitor<UsageLimitsSettings> _usageLimitsMonitor;
    private readonly ILogger<AuthController> _logger;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ITierSettingsService _tierSettingsService;

    public AuthController(
         AppDbContext context,
         ILogger<AuthController> logger,
         IMemoryCache cache,
         IConfiguration configuration,
         ITierSettingsService tierSettingsService) 
    {
        _context = context;
        _logger = logger;
        _cache = cache;
        _configuration = configuration;
        _tierSettingsService = tierSettingsService;
    }


    public record UsageStatusDto(
       bool CanProcessNewVideo,
       int RemainingVideosToday,
       int MaxVideoDurationMinutes,
       DateTime LimitResetTimeUtc,
       string Message
   );
    public record ViolationReport(string Reason, string Hwid);
    public record RegisterRequest(string Username, string Password, string Email, string Hwid);
    public record LoginRequest(string Username, string Password, string Hwid);
    public record UserDto(
        int Id, string Username, string Uid, string Email, string SubscriptionTier, DateTime SubscriptionExpiry,
        string Token, GrantedFeatures GrantedFeatures,
        AllowedApis AllowedApiAccess,
        int RemainingRequests, int VideosProcessedToday,
        int DailyVideoLimit, List<DeviceDto> Devices,
         int SrtLinesUsedToday,
    int DailySrtLineLimit
    );
    public record SrtTranslateCheckRequest(int LineCount);

    [HttpPost("pre-srt-translate-check")]
    [Authorize]
    public async Task<IActionResult> PreSrtTranslateCheck([FromBody] SrtTranslateCheckRequest request)
    {
        var userIdString = User.FindFirstValue("id");
        if (!int.TryParse(userIdString, out int userId))
        {
            return Unauthorized();
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound("Không tìm thấy người dùng.");
        }

        // Chỉ user Free mới bị kiểm tra giới hạn này. Các gói trả phí không giới hạn.
        if (user.Tier != SubscriptionTier.Free)
        {
            return Ok(new { CanTranslate = true, RemainingLines = 99999, Message = "OK" });
        }

        // Logic reset bộ đếm theo múi giờ Việt Nam
        var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
        var lastResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastSrtLineResetUtc, vietnamTimeZone);

        if (lastResetInVietnam.Date < vietnamNow.Date)
        {
            user.SrtLinesUsedToday = 0;
            user.LastSrtLineResetUtc = DateTime.UtcNow.Date;
        }

        int remainingLines = user.DailySrtLineLimit - user.SrtLinesUsedToday;

        if (remainingLines <= 0)
        {
            return StatusCode(429, new
            {
                CanTranslate = false,
                RemainingLines = 0,
                Message = $"Bạn đã hết {user.DailySrtLineLimit} lượt dịch SRT trong ngày."
            });
        }

        if (request.LineCount > remainingLines)
        {
            return StatusCode(429, new
            {
                CanTranslate = false,
                RemainingLines = remainingLines,
                Message = $"Bạn chỉ còn {remainingLines} lượt dịch, không đủ để dịch {request.LineCount} dòng."
            });
        }

        // Nếu đủ, trừ đi và lưu lại
        user.SrtLinesUsedToday += request.LineCount;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            CanTranslate = true,
            RemainingLines = user.DailySrtLineLimit - user.SrtLinesUsedToday,
            Message = "OK"
        });
    }
    [HttpPost("report-violation")]
    [Authorize]
    public async Task<IActionResult> ReportViolation([FromBody] ViolationReport report)
    {
        var userIdString = User.FindFirstValue("id");
        if (!int.TryParse(userIdString, out int userId))
        {
            return Unauthorized();
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound("Không tìm thấy người dùng.");
        }

        user.IsBlocked = true;

        _logger.LogWarning(
            "Tự động khóa tài khoản. User: {Username} (ID: {UserId}), Lý do: {Reason}, HWID: {Hwid}",
            user.Username, user.Id, report.Reason, report.Hwid
        );

        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (await _context.BannedDevices.AnyAsync(b => b.Hwid == request.Hwid))
        {
            return StatusCode(403, "Thiết bị của bạn đã bị cấm đăng ký tài khoản mới.");
        }

        if (await _context.Devices.AnyAsync(d => d.Hwid == request.Hwid))
        {
            return BadRequest("Mỗi thiết bị chỉ được phép đăng ký một tài khoản duy nhất.");
        }

        if (await _context.Users.AnyAsync(u => u.Username.ToLower() == request.Username.ToLower()))
        {
            return BadRequest("Tên tài khoản đã tồn tại.");
        }
        if (await _context.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == request.Email.ToLower()))
        {
            return BadRequest("Email đã được sử dụng.");
        }
        string newUid;
        var random = new Random();
        do
        {
            // Tạo một số ngẫu nhiên 9 chữ số
            newUid = random.Next(100_000_000, 1_000_000_000).ToString();
        }
        // Kiểm tra để đảm bảo UID là duy nhất trong DB
        while (await _context.Users.AnyAsync(u => u.Uid == newUid));
        var user = new User
        {
            Uid = newUid,
            Username = request.Username,
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            CreatedAt = DateTime.UtcNow,
            IsBlocked = false
        };
        _tierSettingsService.ApplyTierSettings(user, SubscriptionTier.Free);
        user.Devices.Add(new Device { Hwid = request.Hwid, LastLogin = DateTime.UtcNow });
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return Ok("Đăng ký thành công!");
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "IP không xác định";
        var cacheKey = $"login_fail_{clientIp}_{request.Username}";

        // 1. Kiểm tra giới hạn đăng nhập sai
        if (_cache.TryGetValue(cacheKey, out int failCount) && failCount >= 5)
        {
            return StatusCode(429, "Bạn đã nhập sai quá nhiều lần. Vui lòng thử lại sau 1 giờ.");
        }

        // 2. Kiểm tra HWID đã bị ban chưa (kiểm tra lại ở cả login)
        if (await _context.BannedDevices.AnyAsync(b => b.Hwid == request.Hwid))
        {
            return StatusCode(403, "Thiết bị của bạn đã bị cấm truy cập hệ thống.");
        }

        var user = await _context.Users
            .Include(u => u.Devices)
            .FirstOrDefaultAsync(u => u.Username.ToLower() == request.Username.ToLower());

        // 3. Xử lý logic khi đăng nhập sai
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            var newFailCount = failCount + 1;
            var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromHours(1));
            _cache.Set(cacheKey, newFailCount, cacheEntryOptions);

            _logger.LogWarning("Login failed for user {Username} from IP {IP}. Fail count: {Count}", request.Username, clientIp, newFailCount);
            return BadRequest("Tên đăng nhập hoặc mật khẩu không đúng.");
        }

        // Nếu đăng nhập thành công, xóa key cache
        _cache.Remove(cacheKey);

        if (user.IsBlocked)
        {
            return StatusCode(403, "Tài khoản của bạn đã bị khóa.");
        }

        if (user.Tier != SubscriptionTier.Free && user.Tier != SubscriptionTier.Lifetime && user.SubscriptionExpiry < DateTime.UtcNow)
        {
            _logger.LogInformation("Tài khoản '{Username}' (ID: {UserId}) đã hết hạn. Tự động chuyển về gói Free.", user.Username, user.Id);
            _tierSettingsService.ApplyTierSettings(user, SubscriptionTier.Free);
            await _context.SaveChangesAsync();
        }


        var device = user.Devices.FirstOrDefault(d => d.Hwid == request.Hwid);
        if (device == null)
        {
            if (user.Devices.Count >= user.MaxDevices)
            {
                return StatusCode(403, $"Tài khoản của bạn đã đạt giới hạn {user.MaxDevices} thiết bị.");
            }
            user.Devices.Add(new Device { Hwid = request.Hwid, LastLogin = DateTime.UtcNow, LastLoginIp = clientIp });
        }
        else
        {
            device.LastLogin = DateTime.UtcNow;
            device.LastLoginIp = clientIp;
        }

        await _context.SaveChangesAsync();
        var token = GenerateJwtToken(user);
        int dailyTranslationLimit;
        // Ưu tiên giá trị ghi đè trong DB trước tiên
        if (user.DailyRequestLimitOverride != -1)
        {
            dailyTranslationLimit = user.DailyRequestLimitOverride;
        }
        else // Nếu không có ghi đè, dùng mặc định theo gói
        {
            dailyTranslationLimit = user.Tier switch
            {
                SubscriptionTier.Free => 30,
                _ => 9999 // Các gói trả phí không giới hạn
            };
        }
        int remainingRequests = dailyTranslationLimit - user.DailyRequestCount;
        var userDto = new UserDto(
    user.Id,
    user.Username,
    user.Uid,
    user.Email,
    user.Tier.ToString(),
    user.SubscriptionExpiry ?? DateTime.MinValue,
    token,
    user.GrantedFeatures,
    user.AllowedApiAccess,
    Math.Max(0, remainingRequests),
    user.VideosProcessedToday,
    user.DailyVideoLimit,
    user.Devices.OrderByDescending(d => d.LastLogin)
                 .Select(d => new DeviceDto(d.Hwid, d.LastLoginIp, d.LastLogin)).ToList(),
    // --- THÊM 2 DÒNG NÀY ---
    user.SrtLinesUsedToday,
    user.DailySrtLineLimit
);
        return Ok(userDto);
        // --- KẾT THÚC SỬA LOGIC ---
    }

    [HttpGet("refresh-profile")]
    [Authorize]
    public async Task<IActionResult> RefreshProfile()
    {
        var userIdString = User.FindFirstValue("id");
        if (!int.TryParse(userIdString, out int userId))
        {
            return Unauthorized("Token không hợp lệ.");
        }

        // *** SỬA LỖI: Không dùng AsNoTracking() ở đây vì chúng ta cần LƯU thay đổi ***
        var user = await _context.Users
            .Include(u => u.Devices)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return NotFound("Tài khoản không còn tồn tại.");
        }

        if (user.IsBlocked)
        {
            return StatusCode(403, "Tài khoản của bạn đã bị khóa.");
        }

        // --- BẮT ĐẦU LOGIC RESET TẬP TRUNG ---
        var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
        bool hasChanges = false;

        // 1. Reset bộ đếm Dịch Truyện
        var lastRequestResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastRequestResetUtc, vietnamTimeZone);
        if (lastRequestResetInVietnam.Date < vietnamNow.Date)
        {
            user.DailyRequestCount = 0;
            user.LastRequestResetUtc = DateTime.UtcNow.Date;
            hasChanges = true;
        }

        // 2. Reset bộ đếm Xử lý Video
        var lastVideoResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastVideoResetUtc, vietnamTimeZone);
        if (lastVideoResetInVietnam.Date < vietnamNow.Date)
        {
            user.VideosProcessedToday = 0;
            user.LastVideoResetUtc = DateTime.UtcNow.Date;
            hasChanges = true;
        }

        // 3. Reset bộ đếm Dịch SRT
        var lastSrtLineResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastSrtLineResetUtc, vietnamTimeZone);
        if (lastSrtLineResetInVietnam.Date < vietnamNow.Date)
        {
            user.SrtLinesUsedToday = 0;
            user.LastSrtLineResetUtc = DateTime.UtcNow.Date;
            hasChanges = true;
        }

        // 4. Reset bộ đếm Dịch SRT Local
        var lastLocalSrtResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastLocalSrtResetUtc, vietnamTimeZone);
        if (lastLocalSrtResetInVietnam.Date < vietnamNow.Date)
        {
            user.LocalSrtLinesUsedToday = 0;
            user.LastLocalSrtResetUtc = DateTime.UtcNow.Date;
            hasChanges = true;
        }

        // Chỉ lưu vào DB nếu có sự thay đổi
        if (hasChanges)
        {
            await _context.SaveChangesAsync();
        }
        // --- KẾT THÚC LOGIC RESET TẬP TRUNG ---

        var currentToken = HttpContext.Request.Headers["Authorization"]
                                      .ToString()
                                      .Replace("Bearer ", "");

        int dailyTranslationLimit;
        if (user.DailyRequestLimitOverride != -1) { dailyTranslationLimit = user.DailyRequestLimitOverride; }
        else { dailyTranslationLimit = user.Tier switch { SubscriptionTier.Free => 30, _ => 9999 }; }
        int remainingRequests = dailyTranslationLimit - user.DailyRequestCount;

        var userDto = new UserDto(
            user.Id,
            user.Username,
            user.Uid,
            user.Email,
            user.Tier.ToString(),
            user.SubscriptionExpiry ?? DateTime.MinValue,
            currentToken,
            user.GrantedFeatures,
            user.AllowedApiAccess,
            Math.Max(0, remainingRequests),
            user.VideosProcessedToday,
            user.DailyVideoLimit,
            user.Devices.OrderByDescending(d => d.LastLogin)
                         .Select(d => new DeviceDto(d.Hwid, d.LastLoginIp, d.LastLogin)).ToList(),
            user.SrtLinesUsedToday,
            user.DailySrtLineLimit
        );
        return Ok(userDto);
    }
    [HttpGet("usage-status")]
    [Authorize]
    public async Task<IActionResult> GetUsageStatus()
    {
        var userIdString = User.FindFirstValue("id");
        if (!int.TryParse(userIdString, out int userId))
        {
            return Unauthorized();
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound("Không tìm thấy người dùng.");
        }

        // Logic reset bộ đếm theo múi giờ Việt Nam
        var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
        var lastResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastVideoResetUtc, vietnamTimeZone);

        if (lastResetInVietnam.Date < vietnamNow.Date)
        {
            user.VideosProcessedToday = 0;
            user.LastVideoResetUtc = DateTime.UtcNow.Date;
            await _context.SaveChangesAsync();
        }

        // SỬA LỖI: Không đọc từ IOptionsMonitor nữa.
        // Tất cả các giới hạn đã được lưu trực tiếp vào đối tượng User trong DB.
        // Chúng ta chỉ cần sử dụng các thuộc tính đó.

        bool canProcess = true;
        string message = "Bạn có thể xử lý video mới.";

        // Dùng giới hạn đã lưu trong DB cho user này
        if (user.DailyVideoLimit != -1 && user.VideosProcessedToday >= user.DailyVideoLimit)
        {
            canProcess = false;
            message = $"Đã đạt giới hạn {user.DailyVideoLimit} video/ngày.";
        }

        var nextResetUtc = user.LastVideoResetUtc.AddDays(1);

        var remaining = (user.DailyVideoLimit == -1) ? 99999 : user.DailyVideoLimit - user.VideosProcessedToday;

        var status = new UsageStatusDto(
            CanProcessNewVideo: canProcess,
            RemainingVideosToday: Math.Max(0, remaining),
            MaxVideoDurationMinutes: user.VideoDurationLimitMinutes, // Đọc trực tiếp từ user
            LimitResetTimeUtc: nextResetUtc,
            Message: message
        );

        return Ok(status);
    }


    [HttpPost("start-processing")]
    [Authorize]
    public async Task<IActionResult> StartVideoProcessing()
    {
        var userIdString = User.FindFirstValue("id");
        if (!int.TryParse(userIdString, out int userId))
        {
            return Unauthorized();
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound("Không tìm thấy người dùng.");
        }
        var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
        var lastResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastVideoResetUtc, vietnamTimeZone);

        if (lastResetInVietnam.Date < vietnamNow.Date)
        {
            user.VideosProcessedToday = 0; // Reset bộ đếm nếu đã sang ngày mới
            user.LastVideoResetUtc = DateTime.UtcNow.Date;
            // Không cần save changes ở đây, sẽ save ở cuối
        }
        if (user.DailyVideoLimit != -1 && user.VideosProcessedToday >= user.DailyVideoLimit)
        {
            // Nếu đã hết lượt, trả về lỗi "Too Many Requests"
            _logger.LogWarning("User '{Username}' (ID: {UserId}) was denied video processing. Limit reached: {Processed}/{Limit}",
                                user.Username, user.Id, user.VideosProcessedToday, user.DailyVideoLimit);

            return StatusCode(429, $"Bạn đã đạt giới hạn {user.DailyVideoLimit} cho hôm nay. Nâng cấp gói để mở khoá giới hạn.");
        }

        // --- BƯỚC 3: Nếu được phép, trừ đi một lượt và lưu lại NGAY LẬP TỨC ---
        user.VideosProcessedToday++;
        await _context.SaveChangesAsync();

        _logger.LogInformation("User '{Username}' (ID: {UserId}) granted video processing. New count: {Count}/{Limit}",
                                user.Username, user.Id, user.VideosProcessedToday, user.DailyVideoLimit);

        // Trả về "OK" để báo cho client biết họ có thể bắt đầu xử lý
        return Ok(new { Message = "Bắt đầu..." });
    }

    [HttpPost("pre-translate-check")]
    [Authorize]
    public async Task<IActionResult> PreTranslateCheck([FromBody] PreTranslateCheckRequest request)
    {
            var userIdString = User.FindFirstValue("id");
            if (!int.TryParse(userIdString, out int userId))
            {
                return Unauthorized();
            }

            // Luôn tải đối tượng user đầy đủ từ DB
            var userInDb = await _context.Users.FindAsync(userId);
            if (userInDb == null)
            {
                return NotFound("Không tìm thấy người dùng.");
            }

            if (!Enum.TryParse<ApiProviderType>(request.Provider, true, out var requestedProvider))
            {
                return BadRequest("Tên nhà cung cấp API không hợp lệ.");
            }

            bool isAllowed = false;
            // SỬA LỖI: Luôn dùng đối tượng 'userInDb' từ DB để kiểm tra
            var allowedApisForUser = userInDb.AllowedApiAccess;

            if (allowedApisForUser != AllowedApis.None)
            {
                isAllowed = requestedProvider switch
                {
                    ApiProviderType.ChutesAI => allowedApisForUser.HasFlag(AllowedApis.ChutesAI),
                    ApiProviderType.Gemini => allowedApisForUser.HasFlag(AllowedApis.Gemini),
                    ApiProviderType.OpenRouter => allowedApisForUser.HasFlag(AllowedApis.OpenRouter),
                    _ => false
                };
            }
            else
            {
                // SỬA LỖI: Luôn dùng đối tượng 'userInDb' từ DB để kiểm tra
                if (userInDb.Tier == SubscriptionTier.Lifetime) { isAllowed = true; }
                else if (userInDb.Tier == SubscriptionTier.Free) { isAllowed = (requestedProvider == ApiProviderType.OpenRouter); }
                else { isAllowed = true; }
            }

            if (!isAllowed)
            {
                _logger.LogWarning("User {Username} (Tier: {Tier}) bị từ chối truy cập API {Provider}.", userInDb.Username, userInDb.Tier, requestedProvider);
                return StatusCode(403, $"Gói dịch vụ hoặc quyền của bạn không hỗ trợ API '{requestedProvider}'.");
            }

            int dailyLimit = userInDb.DailyRequestLimitOverride;
            if (dailyLimit == -1)
            {
                dailyLimit = userInDb.Tier switch
                {
                    SubscriptionTier.Free => 30,
                    _ => int.MaxValue
                };
            }

            if (dailyLimit == int.MaxValue || userInDb.Tier == SubscriptionTier.Lifetime)
            {
                await _context.SaveChangesAsync();
                return Ok(new { RemainingRequests = 9999 });
            }

            var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
            var lastResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(userInDb.LastRequestResetUtc, vietnamTimeZone);

            if (lastResetInVietnam.Date < vietnamNow.Date)
            {
                userInDb.DailyRequestCount = 0;
                userInDb.LastRequestResetUtc = DateTime.UtcNow;
            }

            if (userInDb.DailyRequestCount >= dailyLimit)
            {
                return StatusCode(429, $"Bạn đã hết {dailyLimit} lượt dịch trong ngày.");
            }

            userInDb.DailyRequestCount++;
            await _context.SaveChangesAsync();

            return Ok(new { RemainingRequests = dailyLimit - userInDb.DailyRequestCount });
        }

   
    public record PreTranslateCheckRequest(string Provider);

    
    public enum ApiProviderType
    {
        ChutesAI,
        Gemini,
        OpenRouter
    }
    private void ApplyTierSettings(User user, SubscriptionTier tier)
    {
        // Lấy cấu hình MỚI NHẤT từ IOptionsMonitor
        var currentLimits = _usageLimitsMonitor.CurrentValue;
        TierConfig tierConfig = tier switch
        {
            SubscriptionTier.Free => currentLimits.Free,
            SubscriptionTier.Daily => currentLimits.Daily,
            SubscriptionTier.Monthly => currentLimits.Monthly,
            SubscriptionTier.Yearly => currentLimits.Yearly,
            SubscriptionTier.Lifetime => currentLimits.Lifetime,
            _ => currentLimits.Free // Mặc định an toàn
        };

        user.Tier = tier;

        // Parse và áp dụng các giá trị từ đối tượng cấu hình
        if (Enum.TryParse<GrantedFeatures>(tierConfig.GrantedFeatures, true, out var features))
            user.GrantedFeatures = features;

        if (Enum.TryParse<AllowedApis>(tierConfig.AllowedApis, true, out var apis))
            user.AllowedApiAccess = apis;

        user.VideoDurationLimitMinutes = tierConfig.VideoDurationMinutes;
        user.DailyVideoLimit = tierConfig.DailyVideoCount;
        user.DailyRequestLimitOverride = tierConfig.DailyTranslationRequests;
        user.DailySrtLineLimit = tierConfig.DailySrtLineLimit;

        user.MaxDevices = (tier == SubscriptionTier.Free) ? 1 : 1;

        if (tier == SubscriptionTier.Free)
        {
            user.SubscriptionExpiry = null;
        }
    }

    private string GenerateJwtToken(User user)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var keyString = _configuration["Jwt:Key"] ?? "SubPhim-Super-Secret-Key-For-JWT-Authentication-2024-@!#$";
        var key = Encoding.ASCII.GetBytes(keyString);

        var claims = new List<Claim>
    {
        new Claim("id", user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Tier.ToString()),
        new Claim("features", ((int)user.GrantedFeatures).ToString()),
        // === THÊM DÒNG NÀY VÀO ĐÂY ===
        new Claim("allowedApis", ((int)user.AllowedApiAccess).ToString())
    };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddDays(7),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}