using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;


namespace SubPhim.Server.Data
{
    public enum TtsProvider
    {
        ElevenLabs = 1,
        Gemini = 2
    }

    public enum GeminiModelType
    {
        None = 0, // Dùng cho các provider không phải Gemini
        Flash = 1, // gemini-2.5-flash-preview-tts
        Pro = 2    // gemini-2.5-pro-preview-tts
    }

    [Flags]
    public enum GrantedFeatures
    {
        None = 0,
        SubPhim = 1,
        DichThuat = 2,
        OcrTruyen = 4,
        EditTruyen = 8,
        Capcut = 16,
        Jianying = 32
    }
    [Flags]
    public enum AllowedApis
    {
        None = 0,
        ChutesAI = 1,
        Gemini = 2,
        OpenRouter = 4
    }
    [Flags]
    public enum ApiPoolType
    {
        Paid = 1, // Dành cho các gói trả phí
        Free = 2  // Dành cho gói Free
    }
    public enum SubscriptionTier { Free, Daily, Monthly, Yearly, Lifetime }

    public class User
    {

        [Display(Name = "UID")]
        [StringLength(9)]
        public string Uid { get; set; }
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Username { get; set; }
        [EmailAddress]
        [StringLength(255)]
        public string? Email { get; set; }
        [Required]
        public string PasswordHash { get; set; }
        public SubscriptionTier Tier { get; set; }
        public DateTime? SubscriptionExpiry { get; set; }
        [Display(Name = "Ghi đè giới hạn Request/Ngày")]
        public int DailyRequestLimitOverride { get; set; } = -1;

        public AllowedApis AllowedApiAccess { get; set; } = AllowedApis.None;
        public GrantedFeatures GrantedFeatures { get; set; } = GrantedFeatures.None;
        public DateTime CreatedAt { get; set; }
        public bool IsBlocked { get; set; }
        public int MaxDevices { get; set; } = 1;
        [Display(Name = "Số lượt dịch đã dùng/ngày")]
        public int DailyRequestCount { get; set; } = 0;
        public DateTime LastRequestResetUtc { get; set; } = DateTime.UtcNow;
        public ICollection<Device> Devices { get; set; } = new List<Device>();
        [Display(Name = "Số video đã xử lý hôm nay")]
        public int VideosProcessedToday { get; set; } = 0;

        [Display(Name = "Lần cuối reset bộ đếm video (UTC)")]
        public DateTime LastVideoResetUtc { get; set; } = DateTime.UtcNow;

        [Display(Name = "Giới hạn thời lượng video (phút)")]
        public int VideoDurationLimitMinutes { get; set; } = 30; // Mặc định cho Free

        [Display(Name = "Giới hạn số video/ngày")]
        public int DailyVideoLimit { get; set; } = 2;
        // === THÊM CÁC TRƯỜNG MỚI CHO LOGIC DỊCH SRT "LOCAL" ===
        [Display(Name = "Giới hạn dịch SRT Local/Ngày")]
        public int DailyLocalSrtLimit { get; set; } = 0; // Mặc định là 0 cho các gói thấp

        [Display(Name = "Số dòng SRT Local đã dùng/Ngày")]
        public int LocalSrtLinesUsedToday { get; set; } = 0;

        [Display(Name = "Lần cuối reset bộ đếm SRT Local (UTC)")]
        public DateTime LastLocalSrtResetUtc { get; set; } = DateTime.UtcNow;
        [Display(Name = "Giới hạn dịch SRT/Ngày")]
        public int DailySrtLineLimit { get; set; } = 1000; // Mặc định 1500 cho user mới

        [Display(Name = "Số dòng SRT đã dịch/Ngày")]
        public int SrtLinesUsedToday { get; set; } = 0;

        [Display(Name = "Lần cuối reset bộ đếm SRT (UTC)")]
        public DateTime LastSrtLineResetUtc { get; set; } = DateTime.UtcNow;
        public bool IsAdmin { get; set; } = false;
        [Display(Name = "Giới hạn ký tự TTS")]
        public long TtsCharacterLimit { get; set; } = 500; // Mặc định cho Free

        [Display(Name = "Số ký tự TTS đã dùng")]
        public long TtsCharactersUsed { get; set; } = 0;

        [Display(Name = "Lần cuối reset bộ đếm TTS (UTC)")]
        public DateTime LastTtsResetUtc { get; set; } = DateTime.UtcNow;
    }
    public class TtsApiKey
    {
        public int Id { get; set; }

        [Required]
        public string EncryptedApiKey { get; set; }
        [Required]
        public string Iv { get; set; }

        [Display(Name = "Nhà cung cấp")]
        public TtsProvider Provider { get; set; }

        [Display(Name = "Tên Model (chỉ cho Gemini)")]
        [StringLength(100)]
        public string? ModelName { get; set; } // Sửa thành nullable, vì ElevenLabs không cần

        [Display(Name = "Đang hoạt động")]
        public bool IsEnabled { get; set; } = true;

        [Display(Name = "Số Request Hôm Nay (Gemini)")]
        public int RequestsToday { get; set; } = 0;

        // <<< BẮT ĐẦU THÊM/SỬA CÁC TRƯỜNG CHO ELEVENLABS >>>
        [Display(Name = "Giới hạn Ký tự")]
        public long CharacterLimit { get; set; } = 10000; // Mặc định 10,000 cho ElevenLabs

        [Display(Name = "Ký tự Đã dùng")]
        public long CharactersUsed { get; set; } = 0;
        // <<< KẾT THÚC THÊM/SỬA CÁC TRƯỜNG CHO ELEVENLABS >>>

        [Display(Name = "Lần cuối reset (UTC)")]
        public DateTime LastResetUtc { get; set; } = DateTime.UtcNow;

        [Display(Name = "Lý do bị vô hiệu hóa")]
        [StringLength(200)]
        public string? DisabledReason { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
    public class TtsModelSetting
    {
        public int Id { get; set; }

        [Required]
        [Display(Name = "Tên Model (chỉ cho Gemini)")]
        [StringLength(100)]
        public string? ModelName { get; set; }

        [Required]
        [StringLength(50)]
        [Display(Name = "Mã nhận dạng (Flash/Pro)")]
        public string Identifier { get; set; }

        [Display(Name = "Giới hạn Request/Ngày (RPD)")]
        public int MaxRequestsPerDay { get; set; }

        [Display(Name = "Giới hạn Request/Phút (RPM)")]
        public int MaxRequestsPerMinute { get; set; }

        [Display(Name = "Nhà cung cấp")]
        public TtsProvider Provider { get; set; } 

    }
    public class Device
    {
        public int Id { get; set; }

        // SỬA LỖI: Bỏ `required`
        [Required]
        public string Hwid { get; set; }
        public DateTime LastLogin { get; set; }
        public string? DeviceName { get; set; }
        public string? LastLoginIp { get; set; }
        public int UserId { get; set; }
        public User User { get; set; } = null!;
    }
    public class BannedDevice
    {
        public int Id { get; set; }

        [Required]
        public string Hwid { get; set; }
        public string? LastKnownIp { get; set; }
        public string? AssociatedUsername { get; set; } // Username liên quan lúc bị ban
        public string BanReason { get; set; }
        public DateTime BannedAt { get; set; } = DateTime.UtcNow;
    }
    public class ManagedApiKey
    {
        public int Id { get; set; }

        [Required]
        public string EncryptedApiKey { get; set; }
        [Required]
        public string Iv { get; set; }
        [Display(Name = "Đang hoạt động")]
        public bool IsEnabled { get; set; } = true;

        [Display(Name = "Tổng Tokens Đã Dùng")]
        public long TotalTokensUsed { get; set; } = 0;

        [Display(Name = "Số Request Hôm Nay")]
        public int RequestsToday { get; set; } = 0;

        [Display(Name = "Lần cuối reset bộ đếm Request (UTC)")]
        public DateTime LastRequestCountResetUtc { get; set; } = DateTime.UtcNow;

        [Display(Name = "Nhóm API (Local)")]
        public ApiPoolType PoolType { get; set; } = ApiPoolType.Paid;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
    public enum JobStatus
    {
        Pending,
        Processing,
        Completed,
        Failed
    }

    public class TranslationJobDb
    {
        [Key]
        public string SessionId { get; set; } // Dùng SessionId làm khóa chính
        public int UserId { get; set; }
        public string Genre { get; set; }
        public string TargetLanguage { get; set; }
        public JobStatus Status { get; set; } = JobStatus.Pending;
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public ICollection<OriginalSrtLineDb> OriginalLines { get; set; }
        public ICollection<TranslatedSrtLineDb> TranslatedLines { get; set; }
    }

    public class OriginalSrtLineDb
    {
        public long Id { get; set; }
        public int LineIndex { get; set; }
        public string OriginalText { get; set; }
        public string SessionId { get; set; } // Khóa ngoại
        [ForeignKey("SessionId")]
        public TranslationJobDb Job { get; set; }
    }

    public class TranslatedSrtLineDb
    {
        public long Id { get; set; }
        public int LineIndex { get; set; }
        public string TranslatedText { get; set; }
        public bool Success { get; set; }
        public string SessionId { get; set; } // Khóa ngoại
        [ForeignKey("SessionId")]
        public TranslationJobDb Job { get; set; }
    }
    public class TierDefaultSetting
    {
        [Key] // Dùng chính SubscriptionTier làm khóa chính
        public SubscriptionTier Tier { get; set; }

        public int VideoDurationMinutes { get; set; }
        public int DailyVideoCount { get; set; }
        public int DailyTranslationRequests { get; set; }
        public AllowedApis AllowedApis { get; set; }
        public AllowedApis AllowedApiAccess { get; set; }
        public GrantedFeatures GrantedFeatures { get; set; }
        public int DailySrtLineLimit { get; set; }
        [Display(Name = "Giới hạn ký tự TTS")]
        public long TtsCharacterLimit { get; set; }
    }
    public class AvailableApiModel
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        [Display(Name = "Tên Model")]
        public string ModelName { get; set; }

        [Display(Name = "Đang được kích hoạt")]
        public bool IsActive { get; set; }
        [Display(Name = "Nhóm API")]
        public ApiPoolType PoolType { get; set; } = ApiPoolType.Paid;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
    public class LocalApiSetting
    {
        // Dùng Id cố định là 1 để luôn chỉ có 1 dòng setting
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public int Id { get; set; } = 1;

        [Display(Name = "Request/Phút (RPM)")]
        public int Rpm { get; set; } = 100;

        [Display(Name = "Số dòng/Request (Batch Size)")]
        public int BatchSize { get; set; } = 40;

        // --- BẮT ĐẦU THÊM CÁC TRƯỜNG MỚI ---

        [Display(Name = "Số lần thử lại nếu lỗi")]
        public int MaxRetries { get; set; } = 3;

        [Display(Name = "Delay giữa các lần thử lại (ms)")]
        public int RetryDelayMs { get; set; } = 5000;

        [Display(Name = "Delay giữa các batch (ms)")]
        public int DelayBetweenBatchesMs { get; set; } = 1000;

        [Display(Name = "Temperature (0-2)")]
        [Column(TypeName = "decimal(3, 2)")] // Để lưu số thập phân chính xác
        public decimal Temperature { get; set; } = 0.7m;

        [Display(Name = "Max Output Tokens")]
        public int MaxOutputTokens { get; set; } = 8192;

        [Display(Name = "Bật Thinking Budget (IQ AI)")]
        public bool EnableThinkingBudget { get; set; } = true;

        [Display(Name = "Thinking Budget (IQ AI)")]
        public int ThinkingBudget { get; set; } = 8192;

        // --- KẾT THÚC THÊM CÁC TRƯỜNG MỚI ---
    }

    public class TranslationLog
    {
        public long Id { get; set; }

        public int UserId { get; set; }
        [ForeignKey("UserId")]
        public User User { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public int ApiKeyId { get; set; } // ID của key đã được dùng

        [StringLength(100)]
        public string ModelUsed { get; set; }

        public int LineCountRequested { get; set; }
        public int LineCountApproved { get; set; }

        [StringLength(50)]
        public string Provider { get; set; }
        public int TokensUsed { get; set; } = 0;
    }

}