# Đặc Tả Tính Năng: External API Key Management System

## Mục Lục
1. [Tổng Quan](#1-tổng-quan)
2. [Bối Cảnh Hệ Thống Hiện Tại](#2-bối-cảnh-hệ-thống-hiện-tại)
3. [Yêu Cầu Chi Tiết](#3-yêu-cầu-chi-tiết)
4. [Thiết Kế Database](#4-thiết-kế-database)
5. [Thiết Kế API](#5-thiết-kế-api)
6. [Xác Thực & Bảo Mật](#6-xác-thực--bảo-mật)
7. [Hệ Thống Credit](#7-hệ-thống-credit)
8. [Rate Limiting](#8-rate-limiting)
9. [Giao Diện Admin](#9-giao-diện-admin)
10. [Xử Lý Lỗi & Hoàn Tiền](#10-xử-lý-lỗi--hoàn-tiền)
11. [Files Cần Tạo/Sửa](#11-files-cần-tạosửa)

---

## 1. Tổng Quan

### 1.1 Mục Đích
Xây dựng hệ thống API Key cho phép khách hàng bên ngoài (external clients) sử dụng dịch vụ VIP Translation thông qua API key thay vì phải đăng nhập qua ứng dụng. Hệ thống hoạt động theo mô hình tương tự Google Cloud, OpenAI, và Anthropic.

### 1.2 Tính Năng Chính
- **Xác thực bằng API Key**: Cho phép gọi API VIP Translation bằng API key (ngoài JWT hiện tại)
- **Hệ thống Credit**: Tính phí theo số ký tự dịch, quy đổi sang VND
- **Rate Limiting**: Giới hạn RPM (requests per minute) cho mỗi API key
- **Quản lý minh bạch**: Lịch sử sử dụng, chi phí, và credit chi tiết
- **Hoàn tiền tự động**: Hoàn trả credit nếu job lỗi
- **Multi-job support**: Mỗi API key có thể chạy nhiều job đồng thời

---

## 2. Bối Cảnh Hệ Thống Hiện Tại

### 2.1 Cấu Trúc Project
```
SubPhim.Server/
├── Controllers/
│   ├── AuthController.cs          # Xác thực JWT hiện tại
│   └── VipTranslationController.cs # API dịch VIP (cần sửa)
├── Pages/Admin/VipTranslation/
│   ├── Index.cshtml               # Trang quản lý VIP (cần thêm tab mới)
│   └── Index.cshtml.cs            # Code-behind
├── Services/
│   └── VipTranslationService.cs   # Service xử lý dịch
├── Data/
│   └── AppDbContext.cs            # Database context (cần thêm entities)
└── Models/                        # Các model (cần thêm entities mới)
```

### 2.2 Xác Thực Hiện Tại
- Sử dụng JWT token với claims: `id`, `Name`, `Role`, `features`, `allowedApis`
- Attribute `[Authorize]` trên controller
- User ID lấy từ `User.FindFirstValue("id")`

### 2.3 VipTranslationController Hiện Tại
```csharp
[ApiController]
[Route("api/viptranslation")]
[Authorize]  // <-- Cần thêm support cho API Key authentication
public class VipTranslationController : ControllerBase
{
    // POST /api/viptranslation/start
    // GET  /api/viptranslation/result/{sessionId}
    // POST /api/viptranslation/cancel/{sessionId}
}
```

---

## 3. Yêu Cầu Chi Tiết

### 3.1 API Key Authentication
| Yêu cầu | Chi tiết |
|---------|----------|
| Format API Key | `AIO_` + 48 ký tự random (Base64URL safe) |
| Header xác thực | `X-API-Key: AIO_xxxxxxxxxx...` hoặc `Authorization: Bearer AIO_xxx...` |
| Lưu trữ | Hash SHA-256, KHÔNG lưu plaintext |
| Hiển thị | Chỉ hiện đầy đủ 1 lần khi tạo, sau đó chỉ hiện `AIO_...xxxx` (4 ký tự cuối) |

### 3.2 Hệ Thống Credit
| Tham số | Giá trị mặc định | Có thể thay đổi |
|---------|------------------|-----------------|
| Credit/Ký tự | 5 credit = 1 ký tự | ✅ Có |
| VND/Credit | 10,000 VND = 1,000 credit | ✅ Có |
| Cách tính | Chỉ tính ký tự OUTPUT (kết quả dịch) | - |
| Thời điểm tính | Sau khi job HOÀN THÀNH thành công | - |

### 3.3 Rate Limiting
| Tham số | Giá trị mặc định | Phạm vi |
|---------|------------------|---------|
| RPM mặc định | 100 requests/phút | Mỗi API key |
| Concurrent jobs | Không giới hạn | Mỗi API key |
| Response khi vượt limit | HTTP 429 + `Retry-After` header | - |

### 3.4 Tính Năng Quản Lý
- **Tạo API Key**: Admin tạo, gán cho khách hàng cụ thể
- **Vô hiệu hóa/Xóa**: Có thể disable hoặc xóa key
- **Nạp credit**: Admin nạp credit cho mỗi key
- **Xem lịch sử**: Chi tiết từng lần gọi API với credit đã dùng
- **Export báo cáo**: Xuất lịch sử sử dụng

---

## 4. Thiết Kế Database

### 4.1 Entity: `ExternalApiKey`
```csharp
public class ExternalApiKey
{
    public int Id { get; set; }
    
    // Định danh & Bảo mật
    public string KeyHash { get; set; }           // SHA-256 hash của API key
    public string KeyPrefix { get; set; }         // "AIO_" (để nhận diện loại key)
    public string KeySuffix { get; set; }         // 4 ký tự cuối (để hiển thị)
    public string? DisplayName { get; set; }      // Tên hiển thị do admin đặt
    
    // Gán cho ai
    public string? AssignedTo { get; set; }       // Tên khách hàng/công ty
    public string? Email { get; set; }            // Email liên hệ
    public string? Notes { get; set; }            // Ghi chú của admin
    
    // Credit
    public long CreditBalance { get; set; }       // Số credit còn lại
    public long TotalCreditsUsed { get; set; }    // Tổng credit đã dùng
    public long TotalCreditsAdded { get; set; }   // Tổng credit đã nạp
    
    // Rate Limiting
    public int RpmLimit { get; set; } = 100;      // Requests per minute
    
    // Trạng thái
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }      // Null = không hết hạn
    
    // Navigation
    public ICollection<ExternalApiUsageLog> UsageLogs { get; set; }
    public ICollection<ExternalApiCreditTransaction> CreditTransactions { get; set; }
}
```

### 4.2 Entity: `ExternalApiUsageLog`
```csharp
public class ExternalApiUsageLog
{
    public long Id { get; set; }
    
    public int ApiKeyId { get; set; }
    public ExternalApiKey ApiKey { get; set; }
    
    // Thông tin request
    public string SessionId { get; set; }         // VIP Translation session ID
    public string Endpoint { get; set; }          // "/api/viptranslation/start"
    public string? TargetLanguage { get; set; }
    
    // Thống kê
    public int InputLines { get; set; }           // Số dòng SRT đầu vào
    public int OutputCharacters { get; set; }     // Số ký tự output (để tính credit)
    public long CreditsCharged { get; set; }      // Credit đã trừ
    
    // Trạng thái
    public UsageStatus Status { get; set; }       // Pending, Completed, Failed, Cancelled, Refunded
    public string? ErrorMessage { get; set; }
    public string? GeminiErrors { get; set; }     // JSON array các lỗi Gemini nếu có
    
    // Thời gian
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
    
    // IP & Metadata
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
}

public enum UsageStatus
{
    Pending = 0,      // Job đang chạy
    Completed = 1,    // Hoàn thành, đã tính credit
    Failed = 2,       // Lỗi, đã hoàn credit
    Cancelled = 3,    // Bị hủy, đã hoàn credit
    Refunded = 4      // Đã hoàn tiền thủ công
}
```

### 4.3 Entity: `ExternalApiCreditTransaction`
```csharp
public class ExternalApiCreditTransaction
{
    public long Id { get; set; }
    
    public int ApiKeyId { get; set; }
    public ExternalApiKey ApiKey { get; set; }
    
    public TransactionType Type { get; set; }
    public long Amount { get; set; }              // Số credit (+ hoặc -)
    public long BalanceAfter { get; set; }        // Số dư sau giao dịch
    
    public string Description { get; set; }       // Mô tả giao dịch
    public long? RelatedUsageLogId { get; set; }  // Liên kết với usage log nếu có
    
    public string? CreatedBy { get; set; }        // Admin username (nếu nạp thủ công)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum TransactionType
{
    Deposit = 1,      // Nạp credit
    Usage = 2,        // Sử dụng (trừ)
    Refund = 3,       // Hoàn trả do lỗi
    Adjustment = 4,   // Điều chỉnh thủ công
    Bonus = 5         // Tặng thưởng
}
```

### 4.4 Entity: `ExternalApiSettings`
```csharp
public class ExternalApiSettings
{
    public int Id { get; set; } = 1;              // Singleton pattern
    
    // Quy đổi Credit
    public int CreditsPerCharacter { get; set; } = 5;     // 5 credit = 1 ký tự
    public decimal VndPerCredit { get; set; } = 10;       // 10 VND = 1 credit (tức 10,000 VND = 1,000 credit)
    
    // Mặc định cho API key mới
    public int DefaultRpm { get; set; } = 100;
    public long DefaultInitialCredits { get; set; } = 0;
    
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
```

### 4.5 Cập Nhật `AppDbContext`
```csharp
// Thêm vào AppDbContext.cs
public DbSet<ExternalApiKey> ExternalApiKeys { get; set; }
public DbSet<ExternalApiUsageLog> ExternalApiUsageLogs { get; set; }
public DbSet<ExternalApiCreditTransaction> ExternalApiCreditTransactions { get; set; }
public DbSet<ExternalApiSettings> ExternalApiSettings { get; set; }

// Trong OnModelCreating, thêm:
modelBuilder.Entity<ExternalApiKey>(entity =>
{
    entity.HasIndex(e => e.KeyHash).IsUnique();
    entity.HasIndex(e => e.IsEnabled);
});

modelBuilder.Entity<ExternalApiUsageLog>(entity =>
{
    entity.HasIndex(e => e.SessionId);
    entity.HasIndex(e => e.ApiKeyId);
    entity.HasIndex(e => e.StartedAt);
});

modelBuilder.Entity<ExternalApiCreditTransaction>(entity =>
{
    entity.HasIndex(e => e.ApiKeyId);
    entity.HasIndex(e => e.CreatedAt);
});
```

---

## 5. Thiết Kế API

### 5.1 Endpoints Cho External Client

#### 5.1.1 Bắt Đầu Dịch
```
POST /api/v1/external/translation/start
Header: X-API-Key: AIO_xxxxxxxxxx...

Request Body:
{
    "targetLanguage": "vi",
    "lines": [
        { "index": 1, "start": "00:00:01,000", "end": "00:00:03,000", "text": "Hello world" },
        ...
    ],
    "systemInstruction": "Dịch tự nhiên, phù hợp ngữ cảnh"
}

Response 200:
{
    "status": "Accepted",
    "sessionId": "abc123...",
    "estimatedCredits": 5000,
    "message": "Job started successfully"
}

Response 402 (Không đủ credit):
{
    "status": "InsufficientCredits",
    "currentBalance": 1000,
    "estimatedRequired": 5000,
    "message": "Không đủ credit. Vui lòng nạp thêm."
}

Response 429 (Rate limit):
{
    "status": "RateLimited",
    "retryAfter": 30,
    "message": "Vượt quá giới hạn 100 requests/phút"
}
```

#### 5.1.2 Lấy Kết Quả
```
GET /api/v1/external/translation/result/{sessionId}
Header: X-API-Key: AIO_xxxxxxxxxx...

Response 200 (Đang xử lý):
{
    "status": "Processing",
    "progress": {
        "completedLines": 50,
        "totalLines": 100,
        "percentage": 50
    },
    "newLines": [
        { "index": 1, "translatedText": "Xin chào thế giới" },
        ...
    ]
}

Response 200 (Hoàn thành):
{
    "status": "Completed",
    "result": {
        "lines": [...],
        "totalCharacters": 1000,
        "creditsCharged": 5000,
        "geminiErrors": []  // Danh sách lỗi Gemini nếu có
    }
}

Response 200 (Lỗi):
{
    "status": "Failed",
    "error": {
        "code": "GEMINI_ERROR",
        "message": "...",
        "creditsRefunded": 5000
    }
}
```

#### 5.1.3 Hủy Job
```
POST /api/v1/external/translation/cancel/{sessionId}
Header: X-API-Key: AIO_xxxxxxxxxx...

Response 200:
{
    "status": "Cancelled",
    "creditsRefunded": 2500,
    "message": "Job đã hủy. Credit chưa sử dụng đã được hoàn trả."
}
```

#### 5.1.4 Kiểm Tra Thông Tin API Key
```
GET /api/v1/external/account/info
Header: X-API-Key: AIO_xxxxxxxxxx...

Response 200:
{
    "keyId": "AIO_...xxxx",
    "displayName": "Client ABC",
    "creditBalance": 50000,
    "rpmLimit": 100,
    "currentRpmUsage": 45,
    "pricing": {
        "creditsPerCharacter": 5,
        "vndPerCredit": 10
    }
}
```

#### 5.1.5 Xem Lịch Sử Sử Dụng
```
GET /api/v1/external/account/usage?from=2024-01-01&to=2024-01-31&page=1&pageSize=50
Header: X-API-Key: AIO_xxxxxxxxxx...

Response 200:
{
    "summary": {
        "totalJobs": 150,
        "totalCreditsUsed": 500000,
        "totalCharactersTranslated": 100000,
        "estimatedCostVnd": 5000000
    },
    "items": [
        {
            "sessionId": "abc123",
            "startedAt": "2024-01-15T10:30:00Z",
            "completedAt": "2024-01-15T10:32:00Z",
            "status": "Completed",
            "inputLines": 100,
            "outputCharacters": 2000,
            "creditsCharged": 10000,
            "targetLanguage": "vi",
            "geminiErrors": []
        },
        ...
    ],
    "pagination": {
        "page": 1,
        "pageSize": 50,
        "totalPages": 3,
        "totalItems": 150
    }
}
```

#### 5.1.6 Xem Lịch Sử Credit
```
GET /api/v1/external/account/transactions?page=1&pageSize=50
Header: X-API-Key: AIO_xxxxxxxxxx...

Response 200:
{
    "currentBalance": 50000,
    "items": [
        {
            "id": 123,
            "type": "Usage",
            "amount": -10000,
            "balanceAfter": 50000,
            "description": "Dịch job abc123 - 2000 ký tự",
            "createdAt": "2024-01-15T10:32:00Z"
        },
        {
            "id": 122,
            "type": "Deposit",
            "amount": 100000,
            "balanceAfter": 60000,
            "description": "Nạp credit bởi admin",
            "createdAt": "2024-01-14T09:00:00Z"
        },
        ...
    ]
}
```

#### 5.1.7 Ước Tính Chi Phí
```
POST /api/v1/external/estimate
Header: X-API-Key: AIO_xxxxxxxxxx...

Request Body:
{
    "characterCount": 10000
}

Response 200:
{
    "characterCount": 10000,
    "estimatedCredits": 50000,
    "estimatedCostVnd": 500000,
    "currentBalance": 100000,
    "hasEnoughCredits": true
}
```

---

## 6. Xác Thực & Bảo Mật

### 6.1 Middleware: `ExternalApiKeyAuthenticationHandler`
```csharp
// File: Authentication/ExternalApiKeyAuthenticationHandler.cs

public class ExternalApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. Lấy API key từ header
        string? apiKey = null;
        
        if (Request.Headers.TryGetValue("X-API-Key", out var xApiKey))
        {
            apiKey = xApiKey.FirstOrDefault();
        }
        else if (Request.Headers.TryGetValue("Authorization", out var auth))
        {
            var authHeader = auth.FirstOrDefault();
            if (authHeader?.StartsWith("Bearer AIO_") == true)
            {
                apiKey = authHeader.Substring("Bearer ".Length);
            }
        }
        
        if (string.IsNullOrEmpty(apiKey) || !apiKey.StartsWith("AIO_"))
        {
            return AuthenticateResult.NoResult();
        }
        
        // 2. Hash và tìm trong DB
        var keyHash = ComputeSha256Hash(apiKey);
        
        // Cache để giảm DB queries
        var cacheKey = $"external_api_key_{keyHash}";
        if (!_cache.TryGetValue(cacheKey, out ExternalApiKey? keyEntity))
        {
            keyEntity = await _context.ExternalApiKeys
                .FirstOrDefaultAsync(k => k.KeyHash == keyHash && k.IsEnabled);
            
            if (keyEntity != null)
            {
                _cache.Set(cacheKey, keyEntity, TimeSpan.FromMinutes(5));
            }
        }
        
        if (keyEntity == null)
        {
            return AuthenticateResult.Fail("API key không hợp lệ hoặc đã bị vô hiệu hóa");
        }
        
        // 3. Kiểm tra hết hạn
        if (keyEntity.ExpiresAt.HasValue && keyEntity.ExpiresAt < DateTime.UtcNow)
        {
            return AuthenticateResult.Fail("API key đã hết hạn");
        }
        
        // 4. Tạo claims và principal
        var claims = new[]
        {
            new Claim("api_key_id", keyEntity.Id.ToString()),
            new Claim("api_key_name", keyEntity.DisplayName ?? ""),
            new Claim("assigned_to", keyEntity.AssignedTo ?? ""),
            new Claim(ClaimTypes.AuthenticationMethod, "ExternalApiKey")
        };
        
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        
        return AuthenticateResult.Success(ticket);
    }
    
    private static string ComputeSha256Hash(string rawData)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToBase64String(bytes);
    }
}
```

### 6.2 Rate Limiting Middleware
```csharp
// File: Middleware/ExternalApiRateLimitMiddleware.cs

public class ExternalApiRateLimitMiddleware
{
    private readonly IMemoryCache _cache;
    
    public async Task InvokeAsync(HttpContext context, AppDbContext dbContext)
    {
        // Chỉ áp dụng cho external API routes
        if (!context.Request.Path.StartsWithSegments("/api/v1/external"))
        {
            await _next(context);
            return;
        }
        
        var apiKeyId = context.User.FindFirstValue("api_key_id");
        if (string.IsNullOrEmpty(apiKeyId))
        {
            await _next(context);
            return;
        }
        
        // Lấy RPM limit từ DB (có cache)
        var keyEntity = await GetApiKeyAsync(dbContext, int.Parse(apiKeyId));
        var rpmLimit = keyEntity?.RpmLimit ?? 100;
        
        // Sliding window rate limiting
        var windowKey = $"rpm_{apiKeyId}_{DateTime.UtcNow:yyyyMMddHHmm}";
        var currentCount = _cache.GetOrCreate(windowKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            return 0;
        });
        
        if (currentCount >= rpmLimit)
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.Add("Retry-After", "60");
            await context.Response.WriteAsJsonAsync(new
            {
                status = "RateLimited",
                retryAfter = 60,
                message = $"Vượt quá giới hạn {rpmLimit} requests/phút"
            });
            return;
        }
        
        _cache.Set(windowKey, currentCount + 1);
        await _next(context);
    }
}
```

### 6.3 Cấu Hình trong `Program.cs`
```csharp
// Thêm authentication scheme
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ExternalApiKeyAuthenticationHandler>(
        "ExternalApiKey", null);

// Thêm authorization policy
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ExternalApiPolicy", policy =>
    {
        policy.AddAuthenticationSchemes("ExternalApiKey");
        policy.RequireClaim("api_key_id");
    });
});

// Thêm middleware
app.UseMiddleware<ExternalApiRateLimitMiddleware>();
```

---

## 7. Hệ Thống Credit

### 7.1 Service: `ExternalApiCreditService`
```csharp
// File: Services/ExternalApiCreditService.cs

public interface IExternalApiCreditService
{
    Task<bool> HasSufficientCredits(int apiKeyId, long requiredCredits);
    Task<long> EstimateCredits(int characterCount);
    Task<bool> ReserveCredits(int apiKeyId, string sessionId, long amount);
    Task ChargeCredits(int apiKeyId, string sessionId, int outputCharacters);
    Task RefundCredits(int apiKeyId, string sessionId, string reason);
    Task<long> GetBalance(int apiKeyId);
    Task AddCredits(int apiKeyId, long amount, string description, string adminUsername);
}

public class ExternalApiCreditService : IExternalApiCreditService
{
    private readonly AppDbContext _context;
    private readonly ILogger<ExternalApiCreditService> _logger;
    
    public async Task<long> EstimateCredits(int characterCount)
    {
        var settings = await GetSettingsAsync();
        return characterCount * settings.CreditsPerCharacter;
    }
    
    public async Task ChargeCredits(int apiKeyId, string sessionId, int outputCharacters)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var settings = await GetSettingsAsync();
            var creditsToCharge = outputCharacters * settings.CreditsPerCharacter;
            
            var apiKey = await _context.ExternalApiKeys.FindAsync(apiKeyId);
            if (apiKey == null) throw new InvalidOperationException("API Key not found");
            
            // Trừ credit
            apiKey.CreditBalance -= creditsToCharge;
            apiKey.TotalCreditsUsed += creditsToCharge;
            apiKey.LastUsedAt = DateTime.UtcNow;
            
            // Ghi transaction
            _context.ExternalApiCreditTransactions.Add(new ExternalApiCreditTransaction
            {
                ApiKeyId = apiKeyId,
                Type = TransactionType.Usage,
                Amount = -creditsToCharge,
                BalanceAfter = apiKey.CreditBalance,
                Description = $"Dịch job {sessionId} - {outputCharacters} ký tự",
                RelatedUsageLogId = await GetUsageLogId(sessionId)
            });
            
            // Cập nhật usage log
            var usageLog = await _context.ExternalApiUsageLogs
                .FirstOrDefaultAsync(l => l.SessionId == sessionId);
            if (usageLog != null)
            {
                usageLog.OutputCharacters = outputCharacters;
                usageLog.CreditsCharged = creditsToCharge;
                usageLog.Status = UsageStatus.Completed;
                usageLog.CompletedAt = DateTime.UtcNow;
            }
            
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            
            _logger.LogInformation(
                "Charged {Credits} credits from API Key {KeyId} for session {SessionId}",
                creditsToCharge, apiKeyId, sessionId);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
    
    public async Task RefundCredits(int apiKeyId, string sessionId, string reason)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var usageLog = await _context.ExternalApiUsageLogs
                .FirstOrDefaultAsync(l => l.SessionId == sessionId && l.ApiKeyId == apiKeyId);
            
            if (usageLog == null || usageLog.CreditsCharged == 0) return;
            
            var apiKey = await _context.ExternalApiKeys.FindAsync(apiKeyId);
            if (apiKey == null) return;
            
            var refundAmount = usageLog.CreditsCharged;
            
            // Hoàn credit
            apiKey.CreditBalance += refundAmount;
            apiKey.TotalCreditsUsed -= refundAmount;
            
            // Ghi transaction
            _context.ExternalApiCreditTransactions.Add(new ExternalApiCreditTransaction
            {
                ApiKeyId = apiKeyId,
                Type = TransactionType.Refund,
                Amount = refundAmount,
                BalanceAfter = apiKey.CreditBalance,
                Description = $"Hoàn tiền job {sessionId}: {reason}",
                RelatedUsageLogId = usageLog.Id
            });
            
            // Cập nhật usage log
            usageLog.Status = UsageStatus.Refunded;
            usageLog.CreditsCharged = 0;
            
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            
            _logger.LogInformation(
                "Refunded {Credits} credits to API Key {KeyId} for session {SessionId}. Reason: {Reason}",
                refundAmount, apiKeyId, sessionId, reason);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
```

### 7.2 Tích Hợp với VipTranslationService
```csharp
// Sửa VipTranslationService.cs - thêm callback khi job hoàn thành

public async Task OnJobCompleted(string sessionId, List<TranslatedSrtLine> results, List<string> geminiErrors)
{
    // Kiểm tra xem job này có phải từ External API không
    var usageLog = await _context.ExternalApiUsageLogs
        .FirstOrDefaultAsync(l => l.SessionId == sessionId);
    
    if (usageLog != null)
    {
        // Tính tổng ký tự output
        var totalOutputChars = results.Sum(r => r.TranslatedText?.Length ?? 0);
        
        // Lưu Gemini errors nếu có
        if (geminiErrors.Any())
        {
            usageLog.GeminiErrors = JsonSerializer.Serialize(geminiErrors);
        }
        
        // Charge credits
        await _creditService.ChargeCredits(usageLog.ApiKeyId, sessionId, totalOutputChars);
    }
}

public async Task OnJobFailed(string sessionId, string errorMessage)
{
    var usageLog = await _context.ExternalApiUsageLogs
        .FirstOrDefaultAsync(l => l.SessionId == sessionId);
    
    if (usageLog != null)
    {
        usageLog.Status = UsageStatus.Failed;
        usageLog.ErrorMessage = errorMessage;
        
        // Hoàn credit nếu đã reserve
        await _creditService.RefundCredits(usageLog.ApiKeyId, sessionId, errorMessage);
    }
}
```

---

## 8. Rate Limiting

### 8.1 Cách Hoạt Động
```
┌─────────────────────────────────────────────────────────────┐
│                    Request Flow                              │
├─────────────────────────────────────────────────────────────┤
│  1. Client gửi request với X-API-Key header                 │
│  2. Middleware kiểm tra rate limit trong cache:             │
│     - Key: "rpm_{apiKeyId}_{minute}"                        │
│     - Value: số request trong phút hiện tại                 │
│  3. Nếu < limit: cho qua, tăng counter                      │
│  4. Nếu >= limit: trả về 429 + Retry-After header           │
└─────────────────────────────────────────────────────────────┘
```

### 8.2 Response Headers
```
X-RateLimit-Limit: 100
X-RateLimit-Remaining: 45
X-RateLimit-Reset: 1704067260
```

---

## 9. Giao Diện Admin

### 9.1 Cấu Trúc Trang
```
/Admin/VipTranslation/ExternalApiKeys     # Trang chính quản lý External API Keys
```

### 9.2 Các Tab/Sections

#### Tab 1: Danh Sách API Keys
```
┌──────────────────────────────────────────────────────────────────────────────┐
│ 📋 Danh Sách External API Keys                                    [+ Tạo Mới] │
├──────────────────────────────────────────────────────────────────────────────┤
│ ☐ | Key ID      | Gán Cho      | Credit    | RPM  | Trạng Thái | Thao Tác   │
├───┼─────────────┼──────────────┼───────────┼──────┼────────────┼────────────┤
│ ☐ | AIO_...a1b2 | Công ty ABC  | 50,000    | 100  | ✅ Active   | 👁 📝 🗑   │
│ ☐ | AIO_...c3d4 | Freelancer X | 10,000    | 50   | ⛔ Disabled | 👁 📝 🗑   │
└──────────────────────────────────────────────────────────────────────────────┘
[Xóa đã chọn] [Vô hiệu hóa đã chọn]
```

#### Tab 2: Chi Tiết API Key (Modal/Page)
```
┌──────────────────────────────────────────────────────────────────────────────┐
│ 🔑 Chi Tiết API Key: AIO_...a1b2                                             │
├──────────────────────────────────────────────────────────────────────────────┤
│ Thông Tin Chung                                                              │
│ ├─ Tên hiển thị: [Công ty ABC        ]                                       │
│ ├─ Gán cho:      [client@abc.com     ]                                       │
│ ├─ Ghi chú:      [Khách hàng VIP     ]                                       │
│ ├─ RPM Limit:    [100                ]                                       │
│ ├─ Ngày tạo:     2024-01-15 10:30                                            │
│ └─ Lần dùng cuối: 2024-01-20 14:22                                           │
├──────────────────────────────────────────────────────────────────────────────┤
│ 💰 Credit                                                                     │
│ ├─ Số dư hiện tại:  50,000 credits                                           │
│ ├─ Tổng đã dùng:    150,000 credits                                          │
│ ├─ Tổng đã nạp:     200,000 credits                                          │
│ └─ [Nạp Credit: [______] credits] [+ Nạp]                                    │
├──────────────────────────────────────────────────────────────────────────────┤
│ 📊 Quy Đổi                                                                   │
│ ├─ 50,000 credits = 10,000 ký tự = 500,000 VND                               │
│ └─ (theo tỷ giá: 5 credit/ký tự, 10 VND/credit)                              │
└──────────────────────────────────────────────────────────────────────────────┘
```

#### Tab 3: Cài Đặt Hệ Thống
```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ⚙️ Cài Đặt External API                                                      │
├──────────────────────────────────────────────────────────────────────────────┤
│ Quy Đổi Credit                                                               │
│ ├─ Credit/Ký tự:     [5     ] credit = 1 ký tự output                        │
│ ├─ VND/Credit:       [10    ] VND = 1 credit                                 │
│ └─ → 1,000 ký tự = 5,000 credits = 50,000 VND                                │
├──────────────────────────────────────────────────────────────────────────────┤
│ Mặc Định Cho API Key Mới                                                     │
│ ├─ RPM mặc định:     [100   ] requests/phút                                  │
│ └─ Credit khởi tạo:  [0     ] credits                                        │
├──────────────────────────────────────────────────────────────────────────────┤
│ 🧮 Máy Tính Quy Đổi                                                          │
│ ├─ Nhập số ký tự:    [10000 ] → 50,000 credits → 500,000 VND                 │
│ └─ Nhập số tiền VND: [1000000] → 100,000 credits → 20,000 ký tự              │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                         [💾 Lưu Cài Đặt]     │
└──────────────────────────────────────────────────────────────────────────────┘
```

#### Tab 4: Lịch Sử Sử Dụng
```
┌──────────────────────────────────────────────────────────────────────────────┐
│ 📜 Lịch Sử Sử Dụng                                    [Lọc] [📥 Export CSV]  │
├──────────────────────────────────────────────────────────────────────────────┤
│ Bộ Lọc: API Key [Tất cả ▼] | Từ [____] đến [____] | Trạng thái [Tất cả ▼]   │
├──────────────────────────────────────────────────────────────────────────────┤
│ Session ID | API Key     | Thời Gian       | Ký Tự | Credit | VND    | TT   │
├────────────┼─────────────┼─────────────────┼───────┼────────┼────────┼──────┤
│ abc123...  | AIO_...a1b2 | 15/01 10:30     | 2,000 | 10,000 | 100k   | ✅   │
│ def456...  | AIO_...a1b2 | 15/01 11:45     | 1,500 | 7,500  | 75k    | ❌🔄 │
│ ghi789...  | AIO_...c3d4 | 14/01 09:00     | 3,000 | 15,000 | 150k   | ✅   │
└──────────────────────────────────────────────────────────────────────────────┘
Trang 1/10 | [<] [1] [2] [3] ... [10] [>]

TT: ✅ = Completed, ❌ = Failed, 🔄 = Refunded, ⏳ = Pending
```

### 9.3 Modal Tạo API Key Mới
```
┌──────────────────────────────────────────────────────────────────────────────┐
│ 🔑 Tạo External API Key Mới                                          [✕]    │
├──────────────────────────────────────────────────────────────────────────────┤
│ Tên hiển thị:  [                              ]                              │
│ Gán cho:       [                              ]                              │
│ Email:         [                              ]                              │
│ Ghi chú:       [                              ]                              │
│ RPM Limit:     [100                           ]                              │
│ Credit khởi tạo: [0                           ]                              │
│ Hết hạn:       [  ] Không bao giờ  [  ] Ngày: [__/__/____]                   │
├──────────────────────────────────────────────────────────────────────────────┤
│                                              [Hủy] [✨ Tạo API Key]          │
└──────────────────────────────────────────────────────────────────────────────┘

// Sau khi tạo thành công:
┌──────────────────────────────────────────────────────────────────────────────┐
│ ✅ API Key Đã Được Tạo!                                                      │
├──────────────────────────────────────────────────────────────────────────────┤
│ ⚠️ QUAN TRỌNG: Sao chép API key này ngay bây giờ.                            │
│    Bạn sẽ KHÔNG THỂ xem lại key đầy đủ sau khi đóng dialog này!              │
│                                                                              │
│ 🔑 API Key:                                                                  │
│ ┌────────────────────────────────────────────────────────────────────────┐   │
│ │ AIO_a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6q7r8s9t0u1v2w3x4y5z6              │   │
│ └────────────────────────────────────────────────────────────────────────┘   │
│                                                              [📋 Copy]       │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                              [✓ Đã Sao Chép] │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## 10. Xử Lý Lỗi & Hoàn Tiền

### 10.1 Quy Tắc Hoàn Credit

| Tình Huống | Hoàn Credit | Ghi Chú |
|------------|-------------|---------|
| Job hoàn thành thành công | ❌ Không | Tính credit dựa trên output |
| Server lỗi (500) | ✅ Toàn bộ | Tự động hoàn |
| Gemini API lỗi | ✅ Toàn bộ | Tự động hoàn |
| User hủy job (đang chạy) | ✅ Phần chưa dùng | Chỉ tính phần đã dịch xong |
| Rate limit (429) | ❌ Không | Không tính credit vì chưa bắt đầu |
| Credit không đủ (402) | ❌ Không | Không tính vì chưa bắt đầu |

### 10.2 Tracking Gemini Errors
```csharp
// Trong VipTranslationService, khi gọi Gemini API:
var geminiErrors = new List<string>();

try
{
    var response = await CallGeminiApi(request);
    // ...
}
catch (GeminiRateLimitException ex)
{
    geminiErrors.Add($"Rate limit at batch {batchIndex}: {ex.Message}");
    // Retry logic...
}
catch (GeminiContentFilterException ex)
{
    geminiErrors.Add($"Content filtered at line {lineIndex}: {ex.Message}");
    // Skip line...
}

// Khi job hoàn thành, lưu errors:
usageLog.GeminiErrors = JsonSerializer.Serialize(geminiErrors);
```

### 10.3 Tính Credit Chính Xác
```
CHỈ TÍNH OUTPUT, KHÔNG TÍNH INPUT!

Ví dụ:
- Input: 100 dòng SRT, tổng 5,000 ký tự
- Output (sau dịch): 4,500 ký tự (tiếng Việt ngắn hơn tiếng Anh)
- Credit = 4,500 × 5 = 22,500 credits
- Tiền = 22,500 × 10 = 225,000 VND
```

---

## 11. Files Cần Tạo/Sửa

### 11.1 Files Mới Cần Tạo
```
SubPhim.Server/
├── Models/
│   ├── ExternalApiKey.cs
│   ├── ExternalApiUsageLog.cs
│   ├── ExternalApiCreditTransaction.cs
│   └── ExternalApiSettings.cs
├── Services/
│   ├── IExternalApiCreditService.cs
│   ├── ExternalApiCreditService.cs
│   ├── IExternalApiKeyService.cs
│   └── ExternalApiKeyService.cs
├── Controllers/
│   └── ExternalTranslationController.cs
├── Authentication/
│   └── ExternalApiKeyAuthenticationHandler.cs
├── Middleware/
│   └── ExternalApiRateLimitMiddleware.cs
├── Pages/Admin/VipTranslation/
│   ├── ExternalApiKeys.cshtml
│   └── ExternalApiKeys.cshtml.cs
└── Migrations/
    └── [DateTime]_AddExternalApiEntities.cs
```

### 11.2 Files Cần Sửa
```
SubPhim.Server/
├── Data/
│   └── AppDbContext.cs                  # Thêm DbSets mới
├── Services/
│   └── VipTranslationService.cs         # Thêm hooks cho credit
├── Program.cs                           # Thêm authentication, middleware, services
└── Pages/Admin/VipTranslation/
    └── Index.cshtml                     # Thêm link đến trang External API Keys
```

### 11.3 Migration Commands
```bash
# Tạo migration
dotnet ef migrations add AddExternalApiEntities

# Apply migration
dotnet ef database update
```

---

## 12. Checklist Triển Khai

### Phase 1: Database & Entities
- [ ] Tạo các entity classes
- [ ] Cập nhật AppDbContext
- [ ] Tạo và chạy migration
- [ ] Seed default settings

### Phase 2: Authentication & Middleware
- [ ] Tạo ExternalApiKeyAuthenticationHandler
- [ ] Tạo ExternalApiRateLimitMiddleware
- [ ] Cấu hình trong Program.cs
- [ ] Test authentication

### Phase 3: Services
- [ ] Tạo ExternalApiCreditService
- [ ] Tạo ExternalApiKeyService
- [ ] Tích hợp với VipTranslationService
- [ ] Unit tests

### Phase 4: API Endpoints
- [ ] Tạo ExternalTranslationController
- [ ] Implement tất cả endpoints
- [ ] Validation & error handling
- [ ] API documentation

### Phase 5: Admin UI
- [ ] Tạo trang ExternalApiKeys
- [ ] CRUD API keys
- [ ] Quản lý credit
- [ ] Lịch sử sử dụng
- [ ] Export báo cáo

### Phase 6: Testing & QA
- [ ] Integration tests
- [ ] Load testing (rate limiting)
- [ ] Security review
- [ ] Documentation

---

## 13. Lưu Ý Quan Trọng

### 13.1 Bảo Mật
1. **KHÔNG BAO GIỜ** lưu API key plaintext trong database
2. **KHÔNG BAO GIỜ** log API key đầy đủ
3. Luôn hash với SHA-256 trước khi lưu
4. Chỉ hiện key đầy đủ MỘT LẦN khi tạo

### 13.2 Performance
1. Cache API key validation (5 phút)
2. Cache rate limit counters (2 phút)
3. Batch insert usage logs nếu cần
4. Index các cột thường query

### 13.3 Compatibility
1. Giữ nguyên authentication JWT hiện tại cho mobile app
2. External API chỉ dùng cho third-party integrations
3. Cả hai có thể cùng tồn tại và hoạt động song song

---

**Hết tài liệu đặc tả**
