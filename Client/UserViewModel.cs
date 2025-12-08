using subphimv1.Models; 
using subphimv1.Services;
using subphimv1.Subphim;
using subphimv1.UserView;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using static subphimv1.Services.ApiService;

namespace subphimv1.ViewModels
{
    public class UserViewModel : INotifyPropertyChanged
    {
        public async Task RefreshProfileAsync()
        {
            if (!IsLoggedIn)
            {
                return;
            }
            var (success, refreshedUser, message) = await ApiService.RefreshUserProfileAsync();
            if (success && refreshedUser != null)
            {
                ApplyUserSession(refreshedUser);
            }
            else
            {
            }
        }
        public string UsageInfoString
        {
            get
            {
                if (!IsLoggedIn) return "Chưa đăng nhập";
                if (Tier == "Free")
                {
                    string aioInfo = $"Ký tự Server: {AioCharactersUsedToday:N0} / {(AioCharacterLimit <= 0 ? "N/A" : AioCharacterLimit.ToString("N0"))}";
                    string requestInfo = $"Lượt dịch API: {RemainingRequests}";
                    return $"{requestInfo} | {aioInfo}";
                }
                else
                {
                    return $"Ký tự Server: {AioCharactersUsedToday:N0} / {(AioCharacterLimit <= 0 ? "N/A" : AioCharacterLimit.ToString("N0"))}";
                }
            }
        }
        private long _aioCharactersUsedToday;
        public long AioCharactersUsedToday
        {
            get => _aioCharactersUsedToday;
            set { _aioCharactersUsedToday = value; OnPropertyChanged(); OnPropertyChanged(nameof(AioCharacterInfo)); OnPropertyChanged(nameof(UsageInfoString)); } // Thêm
        }

        private long _aioCharacterLimit;
        public long AioCharacterLimit
        {
            get => _aioCharacterLimit;
            set { _aioCharacterLimit = value; OnPropertyChanged(); OnPropertyChanged(nameof(AioCharacterInfo)); OnPropertyChanged(nameof(UsageInfoString)); } // Thêm
        }
        private int _localSrtLinesUsedToday;
        public int LocalSrtLinesUsedToday
        {
            get => _localSrtLinesUsedToday;
            set { _localSrtLinesUsedToday = value; OnPropertyChanged(); OnPropertyChanged(nameof(LocalSrtLimitInfo)); }
        }

        private int _dailyLocalSrtLineLimit;
        public int DailyLocalSrtLineLimit
        {
            get => _dailyLocalSrtLineLimit;
            set { _dailyLocalSrtLineLimit = value; OnPropertyChanged(); OnPropertyChanged(nameof(LocalSrtLimitInfo)); }
        }

        public string LocalSrtLimitInfo => IsLoggedIn ? $"{LocalSrtLinesUsedToday:N0} / {DailyLocalSrtLineLimit:N0} dòng" : " N/A";
        public string AioCharacterInfo => IsLoggedIn ? $"{AioCharactersUsedToday:N0} / {(AioCharacterLimit <= 0 ? "N/A" : AioCharacterLimit.ToString("N0"))} ký tự" : "N/A";
        
        private int _vipSrtLinesUsedToday;
        public int VipSrtLinesUsedToday
        {
            get => _vipSrtLinesUsedToday;
            set { _vipSrtLinesUsedToday = value; OnPropertyChanged(); OnPropertyChanged(nameof(VipSrtLimitInfo)); }
        }

        private int _dailyVipSrtLimit;
        public int DailyVipSrtLimit
        {
            get => _dailyVipSrtLimit;
            set { _dailyVipSrtLimit = value; OnPropertyChanged(); OnPropertyChanged(nameof(VipSrtLimitInfo)); }
        }

        public string VipSrtLimitInfo => IsLoggedIn ? $"{VipSrtLinesUsedToday:N0} / {DailyVipSrtLimit:N0} dòng" : "N/A";
        
        private long _ttsCharactersUsed;
        public long TtsCharactersUsed
        {
            get => _ttsCharactersUsed;
            set { _ttsCharactersUsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(TtsCharacterInfo)); }
        }

        private long _ttsCharacterLimit;
        public long TtsCharacterLimit
        {
            get => _ttsCharacterLimit;
            set { _ttsCharacterLimit = value; OnPropertyChanged(); OnPropertyChanged(nameof(TtsCharacterInfo)); }
        }

        public string TtsCharacterInfo => IsLoggedIn ? $"{TtsCharactersUsed} / {(TtsCharacterLimit >= 9999999 ? "Không giới hạn" : TtsCharacterLimit.ToString("N0"))}" : "0 / 0";
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            });
        }
        public string Token { get; set; }

        private string _uid;
        public string Uid
        {
            get => _uid;
            set
            {
                if (_uid != value)
                {
                    _uid = value;
                    OnPropertyChanged();
                }
            }
        }
        private int _srtLinesUsedToday;
        public int SrtLinesUsedToday
        {
            get => _srtLinesUsedToday;
            set { _srtLinesUsedToday = value; OnPropertyChanged(); OnPropertyChanged(nameof(SrtLimitInfo)); }
        }

        private int _dailySrtLineLimit;
        public int DailySrtLineLimit
        {
            get => _dailySrtLineLimit;
            set { _dailySrtLineLimit = value; OnPropertyChanged(); OnPropertyChanged(nameof(SrtLimitInfo)); }
        }

        public string SrtLimitInfo => IsLoggedIn ? $"{SrtLinesUsedToday:N0}/{DailySrtLineLimit:N0} dòng" : "N/A";

        private string _tier;
        public string Tier
        {
            get => _tier;
            set { if (_tier != value) { _tier = value; OnPropertyChanged(); OnPropertyChanged(nameof(UsageInfoString)); } } // Thêm
        }
        private GrantedFeatures _grantedFeatures; // Đây là trường private đã có
        public GrantedFeatures GrantedFeatures
        {
            get => _grantedFeatures;
            set { if (_grantedFeatures != value) { _grantedFeatures = value; OnPropertyChanged(); } }
        }
        public string VideoLimitInfo => IsLoggedIn ? $"{VideosProcessedToday} / {(DailyVideoLimit < 0 ? "Không giới hạn" : DailyVideoLimit.ToString())}" : "N/A";
        public string VideoResetTimeInfo
        {
            get
            {
                if (!IsLoggedIn) return "N/A";
                TimeSpan timeUntilReset = LimitResetTimeUtc.ToLocalTime() - DateTime.Now;
                return timeUntilReset.TotalSeconds < 0 ? "Sẵn sàng làm mới" : $"Làm mới sau: {timeUntilReset:hh\\:mm\\:ss}";
            }
        }

        public string RequestLimitInfo => IsLoggedIn ? (RemainingRequests >= 9999 ? "Không giới hạn" : $"{RemainingRequests:N0} lượt") : "N/A";

        private AllowedApis _allowedApiAccess;
        public AllowedApis AllowedApiAccess
        {
            get => _allowedApiAccess;
            set
            {
                if (_allowedApiAccess != value)
                {
                    _allowedApiAccess = value;
                    OnPropertyChanged(); // Luôn thông báo khi thay đổi
                }
            }
        }
        private int _remainingRequests;
        public int RemainingRequests
        {
            get => _remainingRequests;
            set
            {

                if (_remainingRequests != value)
                {
                    _remainingRequests = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UsageInfoString));
                    OnPropertyChanged(nameof(RequestLimitInfo)); // Thêm
                }
            }
        }
        private bool _canProcessNewVideo;
        public bool CanProcessNewVideo
        {
            get => _canProcessNewVideo;
            set { _canProcessNewVideo = value; OnPropertyChanged(); }
        }

        private int _remainingVideosToday;
        public int RemainingVideosToday
        {
            get => _remainingVideosToday;
            set { _remainingVideosToday = value; OnPropertyChanged(); }
        }

        private int _maxVideoDurationMinutes;
        public int MaxVideoDurationMinutes
        {
            get => _maxVideoDurationMinutes;
            set { _maxVideoDurationMinutes = value; OnPropertyChanged(); }
        }
        private int _videosProcessedToday;
        private int _dailyVideoLimit;
        public int VideosProcessedToday
        {
            get => _videosProcessedToday;
            set
            {
                if (_videosProcessedToday != value)
                {
                    _videosProcessedToday = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(VideoLimitInfo));
                }
            }
        }

        public int DailyVideoLimit
        {
            get => _dailyVideoLimit;
            set
            {
                if (_dailyVideoLimit != value)
                {
                    _dailyVideoLimit = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(VideoLimitInfo));
                }
            }
        }
        private DateTime _limitResetTimeUtc;
        public DateTime LimitResetTimeUtc
        {
            get => _limitResetTimeUtc;
            set { _limitResetTimeUtc = value; OnPropertyChanged();
                OnPropertyChanged(nameof(VideoResetTimeInfo));
            }
        }

        private string _lastLoginIp;
        public string LastLoginIp { get => _lastLoginIp; set { _lastLoginIp = value; OnPropertyChanged(); } }
        private Timer _heartbeatTimer;
        private int _failedHeartbeatCount = 0;
        private const int MaxFailedHeartbeats = 3;

        #region Thuộc tính (Properties)
        private int _id;
        public int Id { get => _id; set { _id = value; OnPropertyChanged(); } }

        private string _lastLoginInfo;
        public string LastLoginInfo { get => _lastLoginInfo; set { _lastLoginInfo = value; OnPropertyChanged(); } }

        private int _deviceCount;
        public int DeviceCount { get => _deviceCount; set { _deviceCount = value; OnPropertyChanged(); } }
        private bool _isLoggedIn;
        public bool IsLoggedIn
        {
            get => _isLoggedIn;
            set
            {
                if (_isLoggedIn != value)
                {
                    _isLoggedIn = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsGuest));
                    OnPropertyChanged(nameof(AvatarUrl));
                    OnPropertyChanged(nameof(UsageInfoString)); 
                }
            }
        }
        private bool _rememberMe;
        public bool RememberMe
        {
            get => _rememberMe;
            set { _rememberMe = value; OnPropertyChanged(); }
        }

        public UserViewModel()
        {
            Logout(isStartup: true);
        }
        public bool IsGuest => !IsLoggedIn;

        private string _username;
        public string Username
        {
            get => _username;
            set { _username = value; OnPropertyChanged(); }
        }

        private string _email;
        public string Email
        {
            get => _email;
            set { _email = value; OnPropertyChanged(); }
        }


        private string _subscriptionTierName;
        public string SubscriptionTierName
        {
            get => _subscriptionTierName;
            set { if (_subscriptionTierName != value) { _subscriptionTierName = value; OnPropertyChanged(); } }
        }

        private string _subscriptionExpiryInfo;
        public string SubscriptionExpiryInfo
        {
            get => _subscriptionExpiryInfo;
            set { if (_subscriptionExpiryInfo != value) { _subscriptionExpiryInfo = value; OnPropertyChanged(); } }
        }

        #endregion

        #region Phương thức chính (Methods)

        public async Task<(bool success, string message)> Login(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return (false, "Tên đăng nhập và mật khẩu không được để trống.");
            }

            string hwid = Helpers.HwidHelper.GetHwid();
            var result = await ApiService.LoginAsync(username, password, hwid);

            if (result.success && result.user != null)
            {
                ApplyUserSession(result.user);
                StartHeartbeat(); 
                if (RememberMe)
                {
                    // Nếu người dùng muốn ghi nhớ, LƯU lại
                    CredentialService.SaveCredentials(username, password);
                }
                else
                {
                    CredentialService.ClearCredentials();
                }
            }

            return (result.success, result.message);
        }
        public async Task<(bool success, string message)> Register(string username, string password, string email)
        {
            string hwid = Helpers.HwidHelper.GetHwid();
            var result = await Services.ApiService.RegisterAsync(username, password, email, hwid);
            return result;
        }
        public void UpdateUsageStatus(UsageStatusDto dto)
        {
            if (dto == null) return;
            CanProcessNewVideo = dto.CanProcessNewVideo;
            RemainingVideosToday = dto.RemainingVideosToday;
            MaxVideoDurationMinutes = dto.MaxVideoDurationMinutes;
            LimitResetTimeUtc = dto.LimitResetTimeUtc;
        }
        public void Logout(string reason = null, bool isStartup = false)
        {
            ApiService.ClearSession();
            if (!isStartup)
            {
                CredentialService.ClearCredentials();
            }

           (Application.Current as App)?.StopProfileRefreshTimer();
            App.Updater?.Stop();

            IsLoggedIn = false;
            Username = "Guest";
            Email = string.Empty;
            SubscriptionTierName = string.Empty;
            SubscriptionExpiryInfo = string.Empty;
            GrantedFeatures = GrantedFeatures.None;
            AllowedApiAccess = AllowedApis.None;
            Id = 0;
            LastLoginIp = string.Empty;
            DeviceCount = 0;
            RememberMe = false;
            if (!string.IsNullOrEmpty(reason))
            {
                (Application.Current as App).ShowNotification(reason, isError: true);
            }
        }
        public void ForceLogoutAndClearCredentials()
        {
            // Gọi phương thức logout thông thường để reset trạng thái ViewModel
            Logout("Bạn đã đăng xuất.");

            // Gọi đến service để xóa thông tin trong Registry
            CredentialService.ClearCredentials();
        }
        public string AvatarUrl => IsLoggedIn
            ? $"https://i.pravatar.cc/40?u={Email ?? Username}"
            : "https://i.pravatar.cc/40?u=guest";
        public async Task<bool> ValidateFeatureAccess(GrantedFeatures feature)
        {
            if (!IsLoggedIn)
            {
                (Application.Current as App)?.ShowNotification("Vui lòng đăng nhập để sử dụng tính năng này.", isError: true);
                return false;
            }

            var (success, refreshedUser, message) = await ApiService.RefreshUserProfileAsync();

            if (!success || refreshedUser == null)
            {
                Logout("Phiên làm việc đã hết hạn hoặc tài khoản có vấn đề. Vui lòng đăng nhập lại.");
                return false;
            }
            ApplyUserSession(refreshedUser);
            bool hasAccess = (this.GrantedFeatures & feature) == feature;

            if (hasAccess)
            {
                return true;
            }
            else
            {
                (Application.Current as App)?.ShowNotification("Gói của bạn không quyền truy cập tính năng này. Nâng cấp gói để sử dụng.", isError: true);
                return false;
            }
        }
        public void UpdateFromDto(UserDto dto)
        {
            if (dto == null) return;

            Id = dto.Id;
            Uid = dto.Uid;
            Username = dto.Username;
            Email = dto.Email;
            Token = dto.Token;
            Tier = dto.SubscriptionTier;
            GrantedFeatures = dto.GrantedFeatures;
            AllowedApiAccess = dto.AllowedApiAccess;
            RemainingRequests = dto.RemainingRequests;
            VideosProcessedToday = dto.VideosProcessedToday;
            DailyVideoLimit = dto.DailyVideoLimit;
            SrtLinesUsedToday = dto.SrtLinesUsedToday;
            DailySrtLineLimit = dto.DailySrtLineLimit;
            TtsCharactersUsed = dto.TtsCharactersUsed;
            TtsCharacterLimit = dto.TtsCharacterLimit;
            AioCharactersUsedToday = dto.AioCharactersUsedToday;
            AioCharacterLimit = dto.AioCharacterLimit;
            LocalSrtLinesUsedToday = dto.LocalSrtLinesUsedToday;
            DailyLocalSrtLineLimit = dto.DailyLocalSrtLineLimit;
            VipSrtLinesUsedToday = dto.VipSrtLinesUsedToday;
            DailyVipSrtLimit = dto.DailyVipSrtLimit;
            var latestDevice = dto.Devices?.FirstOrDefault();
            DeviceCount = dto.Devices?.Count ?? 0;
            LastLoginIp = latestDevice?.LastLoginIp ?? string.Empty;

            if (dto.SubscriptionTier == "Lifetime")
            {
                SubscriptionTierName = "Gói Vĩnh Viễn";
                SubscriptionExpiryInfo = "Không thời hạn";
            }
            else if (dto.SubscriptionTier == "Free")
            {
                SubscriptionTierName = "Tài khoản Miễn Phí";
                SubscriptionExpiryInfo = "";
            }
            else
            {
                SubscriptionTierName = dto.SubscriptionTier switch
                {
                    "Daily" => "Gói Ngày",
                    "Monthly" => "Gói Tháng",
                    "Yearly" => "Gói Năm",
                    _ => dto.SubscriptionTier
                };
                SubscriptionExpiryInfo = $"Hết hạn: {dto.SubscriptionExpiry:dd/MM/yyyy}";
            }
        }
        #region Heartbeat & Session Management

        private void StartHeartbeat()
        {
            _failedHeartbeatCount = 0;
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = new Timer(
                async (state) => await HeartbeatTick(),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5)
            );
        }

        private void StopHeartbeat()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
        }

        private async Task HeartbeatTick()
        {
            if (!IsLoggedIn)
            {
                StopHeartbeat();
                return;
            }

            var (success, refreshedUser, message) = await ApiService.RefreshUserProfileAsync();

            if (success && refreshedUser != null)
            {
                _failedHeartbeatCount = 0;
                ApplyUserSession(refreshedUser);
            }
            else
            {
                _failedHeartbeatCount++;
                if (_failedHeartbeatCount >= MaxFailedHeartbeats)
                {
                    Logout("Mất kết nối với máy chủ. Vui lòng kiểm tra lại mạng và đăng nhập lại.");
                }
            }
        }

        private void ApplyUserSession(UserDto user)
        {
            if (user == null) return;
            IsLoggedIn = true;
            UpdateFromDto(user); // Luôn dùng UpdateFromDto để cập nhật
        }

        #endregion
        public bool CanAccessFeature(GrantedFeatures feature)
        {
            if (!IsLoggedIn) return false;
            return (_grantedFeatures & feature) == feature;
        }
       

        #endregion
    }
}