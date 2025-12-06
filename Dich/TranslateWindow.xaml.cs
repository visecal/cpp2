using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using subphimv1.Services;
using subphimv1.UserView;
using subphimv1.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static subphimv1.Services.ApiService;

namespace subphimv1
{

    public partial class TranslateWindow : Window, INotifyPropertyChanged
    {
        private const double BASE_CHAT_FONT_SIZE = 14.0;
        private double _chatFontSizeScale = 1.0;
        private string _selectedLanguage = "Tiếng Việt";
        public double ChatFontSize => BASE_CHAT_FONT_SIZE * _chatFontSizeScale;
        public ObservableCollection<ChatMessage> ChatHistory { get; set; } = new ObservableCollection<ChatMessage>();
        private bool _isPromptEditorVisible = false;
        #region Member Variables and Constants
        private volatile bool _stopProcessingDueToLimit = false;
        private bool _isProgrammaticallyChangingOpenRouterApiKey = false;
        // --- Cấu hình ứng dụng ---
        public AppSettings CurrentAppSettings { get; set; }
        private readonly string AppSettingsFilePath;
        private const string AppDataSubFolder = "LauncherAIO"; 
        private const string AppSettingsFileName = "settings.json";

        // --- Quản lý tiến trình ---
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        // --- Semaphores per-window (mỗi tab một bộ) ---
        private readonly SemaphoreSlim _apiSemaphore;
        private readonly SemaphoreSlim _fileProcessingSemaphore;

        // --- Limiter RPM cho lịch khởi chạy file của riêng tab này ---
        private SlidingRateLimiter _geminiWindowRpmLimiter;

        // --- Quản lý key Gemini ---
        private GeminiApiKeyManager _geminiKeyManager;
        private int _geminiApiRequestCount = 0;

        // --- Trạng thái UI và file ---
        private string selectedFilePath = "";
        private string _selectedGenre = "Huyền Huyễn Tiên Hiệp";
        private bool _isProgrammaticallyChangingText = false;
        private bool _isProgrammaticallyChangingChutesApiKey = false;
        private bool _isProgrammaticallyChangingGeminiApiKey = false;

        // --- Các biến runtime được load từ cài đặt ---
        private int _chutesMaxApiRetries_Setting;
        private int _chutesMaxContentRetries_Setting;
        private int _chutesDelayBetweenFileStartsMs_Setting;
        private int _chutesInterChunkDelayMs_Setting;
        private int _chutesDirectSendThreshold_Setting;
        private int _chutesChunkSize_Setting;
        private int _chutesApiRetryBaseDelayMs_Setting;
        private int _chutesContentRetryBaseDelayMs_Setting;

        private int _geminiMaxApiRetries_Setting;
        private int _geminiDelayBetweenFileStartsMs_Setting;
        private int _geminiInterChunkDelayMs_Setting;
        private int _geminiDirectSendThreshold_Setting;
        private int _geminiChunkSize_Setting;
        private int _geminiApiRetryBaseDelayMs_Setting;
        private double _geminiTemperature_Setting;
        private bool _geminiEnableThinkingBudget_Setting;
        private int _geminiThinkingBudget_Setting;
        private int _geminiMaxOutputTokens_Setting;
        private bool _geminiEnableRequestLimit_Setting;
        private int _geminiRequestLimit_Setting;
        private bool _geminiEnableRpmLimit_Setting;
        private int _geminiRpmLimit_Setting;
        public const string GEMINI_FINISH_REASON_MAX_TOKENS = "MAX_TOKENS";
        public const string GEMINI_FINISH_REASON_SAFETY = "SAFETY";
        public const string GEMINI_FINISH_REASON_OTHER = "OTHER";
        public const string DEFAULT_CHUTES_MODEL_TECHNICAL_NAME_CONST = "deepseek-ai/DeepSeek-V3-0324";
        public const string DEFAULT_CHUTES_MODEL_DISPLAY_NAME_CONST = "deepseek-ai/DeepSeek-V3-0324";
        private int _openRouterMaxApiRetries_Setting;
        private int _openRouterMaxContentRetries_Setting;
        private int _openRouterDelayBetweenFileStartsMs_Setting;
        private int _openRouterInterChunkDelayMs_Setting;
        private int _openRouterDirectSendThreshold_Setting;
        private int _openRouterChunkSize_Setting;
        private int _openRouterApiRetryBaseDelayMs_Setting;
        private int _openRouterContentRetryBaseDelayMs_Setting;
        // --- Prompt người dùng ---
        public string UserSuppliedPrompt { get; private set; } = "";
        public bool UseSystemBasePrompt { get; private set; } = true;
        private string _batchEditFolderPath = "";
        #endregion

        #region INotifyPropertyChanged Implementation
        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion
        private async Task RefreshUserSessionAsync()
        {
            if (App.User?.IsLoggedIn == true)
            {
                var (success, refreshedUser, _) = await ApiService.RefreshUserProfileAsync();
                if (success && refreshedUser != null)
                {
                    // Dòng này sẽ kích hoạt sự kiện PropertyChanged,
                    // và hàm User_PropertyChanged sẽ tự động gọi UpdateTitleBarInfo()
                    App.User.UpdateFromDto(refreshedUser);
                    Debug.WriteLine("[RefreshUserSessionAsync] User profile refreshed successfully.");
                }
                else
                {
                    Debug.WriteLine("[RefreshUserSessionAsync] Failed to refresh user profile.");
                }
            }
        }
        private void TxtInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            tbInputCharCount.Text = $"Đã nhập: {txtInput.Text.Length:N0} ký tự";
        }

        public class GeminiApiKeyManager
        {
            private readonly List<ApiKeyInfo> _keys = new List<ApiKeyInfo>();
            private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
            private int _lastKeyIndex = -1;
            private readonly int _requestLimitPerKey; // Tổng request limit chia đều cho keys
            private readonly bool _requestLimitEnabled;
            private readonly bool _advancedMode;
            private readonly int _rpdPerKey; // Requests Per Day per key
            private readonly bool _enableAutoRetry;
            private readonly bool _enableChunkIsolation;

            public GeminiApiKeyManager(AppSettings settings, string runtimeApiKeys = null)
            {
                _requestLimitEnabled = settings.GeminiEnableRequestLimit;
                _advancedMode = settings.GeminiAdvancedMultiKeyMode;
                _rpdPerKey = settings.GeminiRequestsPerDayPerKey;
                _enableAutoRetry = settings.GeminiEnableAutoRetryWithNewKey;
                _enableChunkIsolation = settings.GeminiEnableChunkIsolation;

                // Ưu tiên key truyền từ UI (theo từng TranslateWindow).
                string keySource = !string.IsNullOrWhiteSpace(runtimeApiKeys)
                    ? runtimeApiKeys
                    : settings.GeminiApiKey;

                var allKeys = (keySource ?? string.Empty)
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Trim())
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct()
                    .ToList();


                if (!allKeys.Any()) return;

                // RPM limit per key - luôn áp dụng để tránh 429
                int rpmLimitPerKey = settings.GeminiRateLimitPerMinute > 0
                    ? settings.GeminiRateLimitPerMinute
                    : 15; // Mặc định an toàn

                // Tính total request limit per key (nếu enabled)
                if (_requestLimitEnabled && settings.GeminiRequestLimit > 0 && allKeys.Count > 0)
                {
                    _requestLimitPerKey = (int)Math.Ceiling((double)settings.GeminiRequestLimit / allKeys.Count);
                }
                else
                {
                    _requestLimitPerKey = 0; // 0 = không giới hạn
                }

                // Tạo ApiKeyInfo cho mỗi key
                foreach (var key in allKeys)
                {
                    _keys.Add(new ApiKeyInfo(key, rpmLimitPerKey, _rpdPerKey, _advancedMode));
                }
            }

            public bool HasAvailableKeys()
            {
                return _keys.Any(k => !k.IsExhausted && !k.IsDailyLimitReached());
            }

            /// <summary>
            /// Lấy key tiếp theo available với round-robin
            /// </summary>
            public async Task<ApiKeyInfo> GetNextAvailableKeyAsync(CancellationToken token, string requestContext = "")
            {
                if (!_keys.Any())
                {
                    throw new InvalidOperationException("Không có Gemini API key nào được cấu hình.");
                }

                // Đánh dấu nếu đây là chunk request
                bool isChunkRequest = requestContext.Contains("chunk");

                while (!token.IsCancellationRequested)
                {
                    await _lock.WaitAsync(token);
                    ApiKeyInfo keyToUse = null;
                    try
                    {
                        // Round-robin để tìm key available
                        // Nếu chunk isolation enabled và là chunk request, ưu tiên key chưa dùng gần đây
                        if (_enableChunkIsolation && isChunkRequest)
                        {
                            keyToUse = FindBestKeyForChunk();
                        }

                        // Fallback to normal round-robin
                        if (keyToUse == null)
                        {
                            for (int i = 0; i < _keys.Count; i++)
                            {
                                _lastKeyIndex = (_lastKeyIndex + 1) % _keys.Count;
                                var candidateKey = _keys[_lastKeyIndex];

                                if (!candidateKey.IsExhausted && !candidateKey.IsDailyLimitReached())
                                {
                                    keyToUse = candidateKey;
                                    break;
                                }
                            }
                        }
                    }
                    finally
                    {
                        _lock.Release();
                    }

                    if (keyToUse == null)
                    {
                        // Tất cả keys đều đạt giới hạn
                        var exhaustedReasons = _keys
                            .Select(k => $"Key {k.GetMaskedKey()}: TotalReq={k.RequestCount}/{_requestLimitPerKey}, DailyReq={k.DailyRequestCount}/{_rpdPerKey}")
                            .ToList();

                        string detailMsg = string.Join("; ", exhaustedReasons);
                        throw new GeminiRequestLimitExceededException(
                            $"Tất cả {_keys.Count} Gemini API keys đều đã đạt giới hạn. Chi tiết: {detailMsg}");
                    }

                    // Đợi rate limiter của key này
                    await keyToUse.RateLimiter.WaitAsync(token);

                    // Kiểm tra lại sau khi đợi
                    if (keyToUse.IsExhausted || keyToUse.IsDailyLimitReached())
                    {
                        continue;
                    }

                    // Cập nhật counters
                    await _lock.WaitAsync(token);
                    try
                    {
                        keyToUse.RequestCount++;
                        keyToUse.IncrementDailyCount();
                        keyToUse.LastUsedTime = DateTime.UtcNow;

                        // Check exhaustion
                        if (_requestLimitEnabled && _requestLimitPerKey > 0 &&
                            keyToUse.RequestCount >= _requestLimitPerKey)
                        {
                            keyToUse.IsExhausted = true;
                        }
                    }
                    finally
                    {
                        _lock.Release();
                    }

                    return keyToUse;
                }

                throw new OperationCanceledException(token);
            }

            /// <summary>
            /// Tìm key tốt nhất cho chunk - key chưa dùng gần đây nhất
            /// </summary>
            private ApiKeyInfo FindBestKeyForChunk()
            {
                var availableKeys = _keys
                    .Where(k => !k.IsExhausted && !k.IsDailyLimitReached())
                    .OrderBy(k => k.LastUsedTime)
                    .ToList();

                return availableKeys.FirstOrDefault();
            }

            /// <summary>
            /// Đánh dấu key tạm thời unavailable (ví dụ khi gặp 429)
            /// </summary>
            public async Task MarkKeyTemporarilyUnavailableAsync(string key, TimeSpan duration)
            {
                await _lock.WaitAsync();
                try
                {
                    var keyInfo = _keys.FirstOrDefault(k => k.Key == key);
                    if (keyInfo != null)
                    {
                        keyInfo.TemporarilyUnavailableUntil = DateTime.UtcNow.Add(duration);
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }

            /// <summary>
            /// Lấy key khác để retry (khác với key hiện tại)
            /// </summary>
            public async Task<ApiKeyInfo> GetAlternativeKeyForRetryAsync(
                string currentKey,
                CancellationToken token)
            {
                if (!_enableAutoRetry)
                {
                    // Nếu auto retry không enabled, throw exception
                    throw new InvalidOperationException("Auto retry with new key is disabled");
                }

                await _lock.WaitAsync(token);
                try
                {
                    // Tìm key khác available
                    var alternativeKeys = _keys
                        .Where(k => k.Key != currentKey &&
                                   !k.IsExhausted &&
                                   !k.IsDailyLimitReached() &&
                                   k.TemporarilyUnavailableUntil < DateTime.UtcNow)
                        .OrderBy(k => k.RequestCount) // Ưu tiên key ít request
                        .ToList();

                    if (!alternativeKeys.Any())
                    {
                        throw new InvalidOperationException("Không có API key thay thế available");
                    }

                    return alternativeKeys.First();
                }
                finally
                {
                    _lock.Release();
                }
            }

            /// <summary>
            /// Reset daily counters - gọi vào midnight
            /// </summary>
            public async Task ResetDailyCountersAsync()
            {
                await _lock.WaitAsync();
                try
                {
                    foreach (var key in _keys)
                    {
                        key.ResetDailyCounter();
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }

            /// <summary>
            /// Lấy thống kê usage
            /// </summary>
            public async Task<Dictionary<string, object>> GetUsageStatsAsync()
            {
                await _lock.WaitAsync();
                try
                {
                    var stats = new Dictionary<string, object>
                    {
                        ["TotalKeys"] = _keys.Count,
                        ["AvailableKeys"] = _keys.Count(k => !k.IsExhausted && !k.IsDailyLimitReached()),
                        ["ExhaustedKeys"] = _keys.Count(k => k.IsExhausted),
                        ["DailyLimitReachedKeys"] = _keys.Count(k => k.IsDailyLimitReached()),
                        ["TotalRequests"] = _keys.Sum(k => k.RequestCount),
                        ["TotalDailyRequests"] = _keys.Sum(k => k.DailyRequestCount),
                        ["KeyDetails"] = _keys.Select(k => new
                        {
                            MaskedKey = k.GetMaskedKey(),
                            TotalRequests = k.RequestCount,
                            DailyRequests = k.DailyRequestCount,
                            IsExhausted = k.IsExhausted,
                            DailyLimitReached = k.IsDailyLimitReached()
                        }).ToList()
                    };
                    return stats;
                }
                finally
                {
                    _lock.Release();
                }
            }
        }

        /// <summary>
        /// Thông tin và state của 1 API key
        /// </summary>
        public class ApiKeyInfo
        {
            public string Key { get; }
            public SlidingRateLimiter RateLimiter { get; }

            // Counters
            public int RequestCount { get; set; } = 0; // Tổng request từ đầu session
            public int DailyRequestCount { get; private set; } = 0; // Request trong ngày
            public DateTime DailyCounterResetTime { get; private set; } = DateTime.UtcNow.Date;

            // Limits
            private readonly int _dailyLimit;
            private readonly bool _trackDaily;

            // State
            public bool IsExhausted { get; set; } = false; // Đạt total limit
            public DateTime LastUsedTime { get; set; } = DateTime.MinValue;
            public DateTime TemporarilyUnavailableUntil { get; set; } = DateTime.MinValue;

            public ApiKeyInfo(string key, int rpmLimit, int dailyLimit = 0, bool trackDaily = false)
            {
                Key = key;
                _dailyLimit = dailyLimit;
                _trackDaily = trackDaily;

                int safeRpmLimit = Math.Max(1, rpmLimit);
                RateLimiter = new SlidingRateLimiter(safeRpmLimit, TimeSpan.FromMinutes(1));
            }

            /// <summary>
            /// Kiểm tra xem đã đạt giới hạn daily chưa
            /// </summary>
            public bool IsDailyLimitReached()
            {
                if (!_trackDaily || _dailyLimit <= 0) return false;

                // Auto reset nếu sang ngày mới
                if (DateTime.UtcNow.Date > DailyCounterResetTime.Date)
                {
                    ResetDailyCounter();
                    return false;
                }

                return DailyRequestCount >= _dailyLimit;
            }

            /// <summary>
            /// Tăng daily counter
            /// </summary>
            public void IncrementDailyCount()
            {
                // Auto reset nếu sang ngày mới
                if (DateTime.UtcNow.Date > DailyCounterResetTime.Date)
                {
                    ResetDailyCounter();
                }

                DailyRequestCount++;
            }

            /// <summary>
            /// Reset daily counter
            /// </summary>
            public void ResetDailyCounter()
            {
                DailyRequestCount = 0;
                DailyCounterResetTime = DateTime.UtcNow.Date;
            }

            /// <summary>
            /// Lấy key đã mask để log an toàn
            /// </summary>
            public string GetMaskedKey()
            {
                if (string.IsNullOrEmpty(Key) || Key.Length < 10)
                    return "***";

                return $"{Key.Substring(0, 8)}...{Key.Substring(Key.Length - 4)}";
            }
        }

        /// <summary>
        /// Exception khi tất cả keys đều đạt giới hạn
        /// </summary>
        public class GeminiRequestLimitExceededException : Exception
        {
            public GeminiRequestLimitExceededException(string message) : base(message) { }
        }

        private SlidingRateLimiter _ttsBatchRpmLimiter;
        public TranslateWindow()
        {
            InitializeComponent();
            _apiSemaphore = new SemaphoreSlim(30);
            _fileProcessingSemaphore = new SemaphoreSlim(15);
            ChatHistoryItems.ItemsSource = ChatHistory;
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appSpecificFolder = Path.Combine(appDataPath, AppDataSubFolder);
            this.AppSettingsFilePath = Path.Combine(appSpecificFolder, AppSettingsFileName);
            if (!Directory.Exists(appSpecificFolder)) { try { Directory.CreateDirectory(appSpecificFolder); } catch { /* Ignore */ } }
            this.Loaded += TranslateWindow_Loaded;
            this.Closing += TranslateWindow_Closing;
            if (App.User != null)
            {
                App.User.PropertyChanged += User_PropertyChanged;
            }
            UpdateTitleBarInfo();
        }
        private void UpdateTitleBarInfo()
        {
            var user = App.User;
            if (user == null || !user.IsLoggedIn)
            {
                tbFreeRequests.Text = string.Empty;
                tbAioChars.Text = "Chưa đăng nhập";
                return;
            }
            if (user.Tier == "Free")
            {
                tbFreeRequests.Text = $"Lượt dịch Free: {user.RemainingRequests}";
                tbFreeRequests.Visibility = Visibility.Visible;
            }
            else
            {
                tbFreeRequests.Text = string.Empty;
                tbFreeRequests.Visibility = Visibility.Collapsed; 
            }
            string aioLimit = user.AioCharacterLimit <= 0 ? "Không giới hạn" : user.AioCharacterLimit.ToString("N0");
            tbAioChars.Text = $"Ký tự dã dùng cho API AIO: {user.AioCharactersUsedToday:N0} / {aioLimit}";
        }
        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            UpdateZoom(false);
        }
        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            UpdateZoom(true);
        }
        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                UpdateZoom(e.Delta > 0);
                e.Handled = true;
            }
        }
        private void UpdateZoom(bool zoomIn)
        {
            double newScale = _chatFontSizeScale + (zoomIn ? 0.1 : -0.1);
            _chatFontSizeScale = Math.Clamp(newScale, 0.2, 2.0);
            OnPropertyChanged(nameof(ChatFontSize));
        }
        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage message)
            {
                try
                {
                    System.Windows.Forms.Clipboard.SetText(message.MessageContent);
                    lblStatus.Text = "Đã copy nội dung.";
                }
                catch (Exception ex)
                {
                    lblStatus.Text = "Lỗi khi copy: " + ex.Message;
                }
            }
        }

        private void ToggleExpand_Click(object sender, RoutedEventArgs e)
        {
            // Lấy ChatMessage object và đảo ngược trạng thái IsExpanded của nó
            if ((sender as FrameworkElement)?.DataContext is ChatMessage message)
            {
                message.IsExpanded = !message.IsExpanded;
            }
        }
        private async void TranslateWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadAppSettings();
            ApplySettingsToRuntimeVariables();
            cmbGenre.SelectedIndex = 0;
            await RefreshUserSessionAsync();

            UpdateUiBasedOnPermissions();
        }
        private void TranslateWindow_Closing(object sender, CancelEventArgs e)
        {
            SaveAppSettings();
            _cts?.Cancel();
            _cts?.Dispose();
            _geminiWindowRpmLimiter?.Dispose();
            if (App.User != null)
            {
                App.User.PropertyChanged -= User_PropertyChanged;
            }
        }
        #region Window Controls
        private void UpdateStatusLabel()
        {
            var user = App.User;
            if (user == null) return;

            // Đây chính là đoạn code hiển thị trạng thái gốc của bạn
            if (user.Tier == "Free")
            {
                lblStatus.Text = $"Sẵn sàng. Còn lại {user.RemainingRequests} lượt dịch miễn phí.";
            }
            else
            {
                lblStatus.Text = "Sẵn sàng.";
            }
        }
        private void User_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Bất kỳ thuộc tính nào liên quan đến giới hạn thay đổi, gọi hàm cập nhật UI
            switch (e.PropertyName)
            {
                case nameof(UserViewModel.IsLoggedIn):
                case nameof(UserViewModel.Tier):
                case nameof(UserViewModel.RemainingRequests):
                case nameof(UserViewModel.AioCharactersUsedToday):
                case nameof(UserViewModel.AioCharacterLimit):
                    Dispatcher.Invoke(UpdateTitleBarInfo);
                    break;
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }
        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
        #endregion

        #region Settings Management

        private void LoadAppSettings()
        {
            if (File.Exists(AppSettingsFilePath))
            {
                try
                {
                    string json = File.ReadAllText(AppSettingsFilePath);
                    CurrentAppSettings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                catch (Exception ex)
                {
                    CurrentAppSettings = new AppSettings();
                }
            }
            else
            {
                CurrentAppSettings = new AppSettings();
            }
            ApplySettingsToUI();
        }

        private void SaveAppSettings()
        {
            if (string.IsNullOrEmpty(AppSettingsFilePath)) return;

            try
            {
                switch (cmbApiProvider.SelectedIndex)
                {
                    case 0: CurrentAppSettings.SelectedApiProvider = ApiProviderType.AIOLauncher; break;
                    case 1: CurrentAppSettings.SelectedApiProvider = ApiProviderType.ChutesAI; break;
                    case 2: CurrentAppSettings.SelectedApiProvider = ApiProviderType.Gemini; break;
                    case 3: CurrentAppSettings.SelectedApiProvider = ApiProviderType.OpenRouter; break;
                }
                CurrentAppSettings.UserPrompt = txtPromptEditor.Text;
                CurrentAppSettings.SelectedLanguage = (cmbLanguage.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Tiếng Việt";
                _selectedGenre = (cmbGenre.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Huyền Huyễn Tiên Hiệp";
                // ChutesAI Settings
                CurrentAppSettings.ChutesApiKey = (tglShowChutesApiKey.IsChecked == true ? txtChutesApiKey.Text : pwdChutesApiKey.Password)?.Trim() ?? string.Empty;
                CurrentAppSettings.ChutesShouldSaveApiKey = chkSaveChutesApiKey.IsChecked == true;
                CurrentAppSettings.SelectedChutesApiModel = cmbChutesApiModel.SelectedItem as string ?? AppSettings.DEFAULT_CHUTES_MODEL_TECHNICAL_NAME_CONST;
                if (int.TryParse(txtChutesRateLimitPerMinute.Text, out int chutesRateLimit)) CurrentAppSettings.ChutesRateLimitPerMinute = chutesRateLimit;
                if (int.TryParse(txtChutesMaxApiRetries.Text, out int chutesApiRetries)) CurrentAppSettings.ChutesMaxApiRetries = chutesApiRetries;
                if (int.TryParse(txtChutesMaxContentRetries.Text, out int chutesContentRetries)) CurrentAppSettings.ChutesMaxContentRetries = chutesContentRetries;
                if (int.TryParse(txtChutesDelayBetweenFiles.Text, out int chutesDelayFiles)) CurrentAppSettings.ChutesDelayBetweenFileStartsMs = chutesDelayFiles;
                if (int.TryParse(txtChutesDelayBetweenChunks.Text, out int chutesDelayChunks)) CurrentAppSettings.ChutesInterChunkDelayMs = chutesDelayChunks;
                if (int.TryParse(txtChutesApiRetryBaseDelayMs.Text, out int chutesApiBaseDelay)) CurrentAppSettings.ChutesApiRetryBaseDelayMs = chutesApiBaseDelay;
                if (int.TryParse(txtChutesContentRetryBaseDelayMs.Text, out int chutesContentBaseDelay)) CurrentAppSettings.ChutesContentRetryBaseDelayMs = chutesContentBaseDelay;
                if (int.TryParse(txtChutesDirectSendThreshold.Text, out int chutesDirectSend)) CurrentAppSettings.ChutesDirectSendThreshold = chutesDirectSend;
                if (int.TryParse(txtChutesChunkSize.Text, out int chutesChunkSizeVal)) CurrentAppSettings.ChutesChunkSize = chutesChunkSizeVal;

                // Gemini Settings
                CurrentAppSettings.GeminiUseMultiKey = chkGeminiUseMultiKey.IsChecked == true;
                CurrentAppSettings.GeminiAdvancedMultiKeyMode = chkGeminiAdvancedMultiKeyMode.IsChecked == true;

                // Chỉ lưu key khi user chọn "Lưu API key"
                if (CurrentAppSettings.GeminiUseMultiKey)
                {
                    var uiKeys = txtGeminiApiKeys.Text?.Trim() ?? string.Empty;

                    if (chkSaveGeminiApiKeys.IsChecked == true)
                    {
                        CurrentAppSettings.GeminiApiKey = uiKeys;
                        CurrentAppSettings.GeminiShouldSaveApiKey = true;
                    }
                    else
                    {
                        // Không lưu key multi-key xuống file
                        CurrentAppSettings.GeminiApiKey = string.Empty;
                        CurrentAppSettings.GeminiShouldSaveApiKey = false;
                    }
                }
                else
                {
                    var uiKey = (tglShowGeminiApiKey.IsChecked == true
                        ? txtGeminiApiKey.Text
                        : pwdGeminiApiKey.Password)?.Trim() ?? string.Empty;

                    if (chkSaveGeminiApiKey.IsChecked == true)
                    {
                        CurrentAppSettings.GeminiApiKey = uiKey;
                        CurrentAppSettings.GeminiShouldSaveApiKey = true;
                    }
                    else
                    {
                        // Không lưu key single-key xuống file
                        CurrentAppSettings.GeminiApiKey = string.Empty;
                        CurrentAppSettings.GeminiShouldSaveApiKey = false;
                    }
                }


                CurrentAppSettings.SelectedGeminiApiModel = cmbGeminiApiModel.SelectedItem as string ?? AppSettings.DEFAULT_GEMINI_MODEL_TECHNICAL_NAME_CONST;
                if (int.TryParse(txtGeminiRateLimitPerMinute.Text, out int geminiRateLimit))
                    CurrentAppSettings.GeminiRateLimitPerMinute = geminiRateLimit;
                if (int.TryParse(txtGeminiRequestsPerDayPerKey.Text, out int geminiRpd))
                    CurrentAppSettings.GeminiRequestsPerDayPerKey = geminiRpd;
                if (int.TryParse(txtGeminiMaxApiRetries.Text, out int geminiApiRetries))
                    CurrentAppSettings.GeminiMaxApiRetries = geminiApiRetries;
                if (int.TryParse(txtGeminiApiRetryBaseDelayMs.Text, out int geminiApiBaseDelay))
                    CurrentAppSettings.GeminiApiRetryBaseDelayMs = geminiApiBaseDelay;
                if (double.TryParse(txtGeminiTemperature.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double geminiTemp))
                    CurrentAppSettings.GeminiTemperature = Math.Clamp(geminiTemp, 0.0, 2.0);
                CurrentAppSettings.GeminiEnableThinkingBudget = chkGeminiEnableThinkingBudget.IsChecked == true;
                if (int.TryParse(txtGeminiThinkingBudget.Text, out int geminiThinking))
                    CurrentAppSettings.GeminiThinkingBudget = geminiThinking;
                if (int.TryParse(txtGeminiMaxOutputTokens.Text, out int geminiMaxTokens))
                    CurrentAppSettings.GeminiMaxOutputTokens = geminiMaxTokens;
                CurrentAppSettings.GeminiEnableRequestLimit = chkGeminiEnableRequestLimit.IsChecked == true;
                if (int.TryParse(txtGeminiRequestLimit.Text, out int geminiReqLimit))
                    CurrentAppSettings.GeminiRequestLimit = geminiReqLimit;
                CurrentAppSettings.GeminiEnableRpmLimit = chkGeminiEnableRpmLimit.IsChecked == true;
                if (int.TryParse(txtGeminiRpmLimit.Text, out int geminiRpmLimit))
                    CurrentAppSettings.GeminiRpmLimit = geminiRpmLimit;
                CurrentAppSettings.GeminiEnableChunkIsolation = chkGeminiEnableChunkIsolation.IsChecked == true;
                CurrentAppSettings.GeminiEnableAutoRetryWithNewKey = chkGeminiEnableAutoRetryWithNewKey.IsChecked == true;
                if (int.TryParse(txtGeminiDelayBetweenFiles.Text, out int geminiDelayFiles))
                    CurrentAppSettings.GeminiDelayBetweenFileStartsMs = geminiDelayFiles;
                if (int.TryParse(txtGeminiDelayBetweenChunks.Text, out int geminiDelayChunks))
                    CurrentAppSettings.GeminiInterChunkDelayMs = geminiDelayChunks;
                if (int.TryParse(txtGeminiDirectSendThreshold.Text, out int geminiDirectSend))
                    CurrentAppSettings.GeminiDirectSendThreshold = geminiDirectSend;
                if (int.TryParse(txtGeminiChunkSize.Text, out int geminiChunkSizeVal))
                    CurrentAppSettings.GeminiChunkSize = geminiChunkSizeVal;
                // OpenRouter Settings
                CurrentAppSettings.OpenRouterApiKey = (tglShowOpenRouterApiKey.IsChecked == true ? txtOpenRouterApiKey.Text : pwdOpenRouterApiKey.Password)?.Trim() ?? string.Empty;
                CurrentAppSettings.OpenRouterShouldSaveApiKey = chkSaveOpenRouterApiKey.IsChecked == true;
                string selectedDisplayName = cmbOpenRouterModel.Text;
                CurrentAppSettings.SelectedOpenRouterModel = GetTechnicalModelName(selectedDisplayName);
                CurrentAppSettings.HttpReferer = txtOpenRouterReferer.Text.Trim();
                CurrentAppSettings.XTitle = txtOpenRouterTitle.Text.Trim();
                if (int.TryParse(txtOpenRouterRateLimitPerMinute.Text, out int orRpm)) CurrentAppSettings.OpenRouterRateLimitPerMinute = orRpm;
                if (int.TryParse(txtOpenRouterMaxApiRetries.Text, out int orApiRetries)) CurrentAppSettings.OpenRouterMaxApiRetries = orApiRetries;
                if (int.TryParse(txtOpenRouterMaxContentRetries.Text, out int orContentRetries)) CurrentAppSettings.OpenRouterMaxContentRetries = orContentRetries;
                if (int.TryParse(txtOpenRouterApiRetryBaseDelayMs.Text, out int orApiDelay)) CurrentAppSettings.OpenRouterApiRetryBaseDelayMs = orApiDelay;
                if (int.TryParse(txtOpenRouterContentRetryBaseDelayMs.Text, out int orContentDelay)) CurrentAppSettings.OpenRouterContentRetryBaseDelayMs = orContentDelay;
                if (int.TryParse(txtOpenRouterDelayBetweenFiles.Text, out int orFileDelay)) CurrentAppSettings.OpenRouterDelayBetweenFileStartsMs = orFileDelay;
                if (int.TryParse(txtOpenRouterDelayBetweenChunks.Text, out int orChunkDelay)) CurrentAppSettings.OpenRouterInterChunkDelayMs = orChunkDelay;
                if (int.TryParse(txtOpenRouterDirectSendThreshold.Text, out int orThreshold)) CurrentAppSettings.OpenRouterDirectSendThreshold = orThreshold;
                if (int.TryParse(txtOpenRouterChunkSize.Text, out int orChunkSize)) CurrentAppSettings.OpenRouterChunkSize = orChunkSize;
                if (int.TryParse(txtAioLauncherDirectSendThreshold.Text, out int aioThreshold)) CurrentAppSettings.AioLauncherDirectSendThreshold = aioThreshold;
                if (int.TryParse(txtAioLauncherChunkSize.Text, out int aioChunkSize)) CurrentAppSettings.AioLauncherChunkSize = aioChunkSize;
                // Lưu vào file
                string json = JsonConvert.SerializeObject(CurrentAppSettings, Formatting.Indented);
                File.WriteAllText(AppSettingsFilePath, json);
                ApplySettingsToRuntimeVariables();
            }
            catch (Exception ex)
            {
                // Ghi log lỗi nếu cần
            }
        }
        private string GetGeminiApiKeysForCurrentWindow()
        {
            bool useMultiKey = chkGeminiUseMultiKey.IsChecked == true;

            if (useMultiKey)
            {
                string uiKeys = txtGeminiApiKeys.Text?.Trim() ?? string.Empty;

                if (chkSaveGeminiApiKeys.IsChecked == true)
                {
                    // Ưu tiên text hiện tại, nếu rỗng thì dùng key đã lưu
                    return !string.IsNullOrWhiteSpace(uiKeys)
                        ? uiKeys
                        : (CurrentAppSettings.GeminiApiKey ?? string.Empty);
                }

                // Không lưu => keys chỉ thuộc về cửa sổ này
                return uiKeys;
            }
            else
            {
                string uiKey = (tglShowGeminiApiKey.IsChecked == true
                    ? txtGeminiApiKey.Text
                    : pwdGeminiApiKey.Password)?.Trim() ?? string.Empty;

                if (chkSaveGeminiApiKey.IsChecked == true)
                {
                    return !string.IsNullOrWhiteSpace(uiKey)
                        ? uiKey
                        : (CurrentAppSettings.GeminiApiKey ?? string.Empty);
                }

                return uiKey;
            }
        }
        private void ApplySettingsToUI()
        {
            switch (CurrentAppSettings.SelectedApiProvider)
            {
                // <<< THÊM MỚI CASE >>>
                case ApiProviderType.AIOLauncher: cmbApiProvider.SelectedIndex = 0; break;
                case ApiProviderType.ChutesAI: cmbApiProvider.SelectedIndex = 1; break;
                case ApiProviderType.Gemini: cmbApiProvider.SelectedIndex = 2; break;
                case ApiProviderType.OpenRouter: cmbApiProvider.SelectedIndex = 3; break;
                default: cmbApiProvider.SelectedIndex = 3; break;
            }
            txtPromptEditor.Text = CurrentAppSettings.UserPrompt;
            UseSystemBasePrompt = CurrentAppSettings.UseSystemBasePrompt;
            cmbLanguage.SelectedIndex = 0; 
            cmbGenre.SelectedIndex = 0; 
            // --- Chutes AI Settings ---
            _isProgrammaticallyChangingChutesApiKey = true;
            if (CurrentAppSettings.ChutesShouldSaveApiKey)
            {
                pwdChutesApiKey.Password = CurrentAppSettings.ChutesApiKey;
                txtChutesApiKey.Text = CurrentAppSettings.ChutesApiKey;
            }
            else
            {
                pwdChutesApiKey.Password = string.Empty;
                txtChutesApiKey.Text = string.Empty;
            }
            chkSaveChutesApiKey.IsChecked = CurrentAppSettings.ChutesShouldSaveApiKey;
            _isProgrammaticallyChangingChutesApiKey = false;
            var distinctChutesModels = CurrentAppSettings.AvailableChutesApiModels.Distinct().ToList();
            CurrentAppSettings.AvailableChutesApiModels = new ObservableCollection<string>(distinctChutesModels);
            if (!CurrentAppSettings.AvailableChutesApiModels.Any())
            {
                CurrentAppSettings.AvailableChutesApiModels.Add(AppSettings.DEFAULT_CHUTES_MODEL_TECHNICAL_NAME_CONST);
            }
            cmbChutesApiModel.ItemsSource = CurrentAppSettings.AvailableChutesApiModels;
            if (CurrentAppSettings.AvailableChutesApiModels.Contains(CurrentAppSettings.SelectedChutesApiModel))
            {
                cmbChutesApiModel.SelectedItem = CurrentAppSettings.SelectedChutesApiModel;
            }
            else
            {
                cmbChutesApiModel.SelectedItem = CurrentAppSettings.AvailableChutesApiModels.FirstOrDefault();
            }
            txtChutesRateLimitPerMinute.Text = CurrentAppSettings.ChutesRateLimitPerMinute.ToString();
            txtChutesMaxApiRetries.Text = CurrentAppSettings.ChutesMaxApiRetries.ToString();
            txtChutesMaxContentRetries.Text = CurrentAppSettings.ChutesMaxContentRetries.ToString();
            txtChutesDelayBetweenFiles.Text = CurrentAppSettings.ChutesDelayBetweenFileStartsMs.ToString();
            txtChutesDelayBetweenChunks.Text = CurrentAppSettings.ChutesInterChunkDelayMs.ToString();
            txtChutesApiRetryBaseDelayMs.Text = CurrentAppSettings.ChutesApiRetryBaseDelayMs.ToString();
            txtChutesContentRetryBaseDelayMs.Text = CurrentAppSettings.ChutesContentRetryBaseDelayMs.ToString();
            txtChutesDirectSendThreshold.Text = CurrentAppSettings.ChutesDirectSendThreshold.ToString();
            txtChutesChunkSize.Text = CurrentAppSettings.ChutesChunkSize.ToString();


            // --- Gemini Settings ---
            _isProgrammaticallyChangingGeminiApiKey = true;

            // Multi-key checkbox
            chkGeminiUseMultiKey.IsChecked = CurrentAppSettings.GeminiUseMultiKey;
            chkGeminiAdvancedMultiKeyMode.IsChecked = CurrentAppSettings.GeminiAdvancedMultiKeyMode;

            // Apply keys based on mode
            if (CurrentAppSettings.GeminiUseMultiKey)
            {
                txtGeminiApiKeys.Text = CurrentAppSettings.GeminiShouldSaveApiKey
                    ? CurrentAppSettings.GeminiApiKey
                    : string.Empty;
                chkSaveGeminiApiKeys.IsChecked = CurrentAppSettings.GeminiShouldSaveApiKey;

                // Clear single key fields
                pwdGeminiApiKey.Password = string.Empty;
                txtGeminiApiKey.Text = string.Empty;
                chkSaveGeminiApiKey.IsChecked = false;
            }
            else
            {
                if (CurrentAppSettings.GeminiShouldSaveApiKey)
                {
                    pwdGeminiApiKey.Password = CurrentAppSettings.GeminiApiKey;
                    txtGeminiApiKey.Text = CurrentAppSettings.GeminiApiKey;
                }
                else
                {
                    pwdGeminiApiKey.Password = string.Empty;
                    txtGeminiApiKey.Text = string.Empty;
                }
                chkSaveGeminiApiKey.IsChecked = CurrentAppSettings.GeminiShouldSaveApiKey;

                // Clear multi-key fields
                txtGeminiApiKeys.Text = string.Empty;
                chkSaveGeminiApiKeys.IsChecked = false;
            }

            // Trigger visibility update
            ChkGeminiUseMultiKey_Changed(null, null);
            ChkGeminiAdvancedMultiKeyMode_Changed(null, null);

            _isProgrammaticallyChangingGeminiApiKey = false;

            // Model selection
            var distinctGeminiModels = CurrentAppSettings.AvailableGeminiApiModels.Distinct().ToList();
            CurrentAppSettings.AvailableGeminiApiModels = new ObservableCollection<string>(distinctGeminiModels);
            if (!CurrentAppSettings.AvailableGeminiApiModels.Any())
            {
                CurrentAppSettings.AvailableGeminiApiModels.Add(AppSettings.DEFAULT_GEMINI_MODEL_TECHNICAL_NAME_CONST);
            }
            cmbGeminiApiModel.ItemsSource = CurrentAppSettings.AvailableGeminiApiModels;
            if (CurrentAppSettings.AvailableGeminiApiModels.Contains(CurrentAppSettings.SelectedGeminiApiModel))
            {
                cmbGeminiApiModel.SelectedItem = CurrentAppSettings.SelectedGeminiApiModel;
            }
            else
            {
                cmbGeminiApiModel.SelectedItem = CurrentAppSettings.AvailableGeminiApiModels.FirstOrDefault();
            }

            // Other settings
            txtGeminiRateLimitPerMinute.Text = CurrentAppSettings.GeminiRateLimitPerMinute.ToString();
            txtGeminiRequestsPerDayPerKey.Text = CurrentAppSettings.GeminiRequestsPerDayPerKey.ToString();
            txtGeminiMaxApiRetries.Text = CurrentAppSettings.GeminiMaxApiRetries.ToString();
            txtGeminiApiRetryBaseDelayMs.Text = CurrentAppSettings.GeminiApiRetryBaseDelayMs.ToString();
            txtGeminiTemperature.Text = CurrentAppSettings.GeminiTemperature.ToString(CultureInfo.InvariantCulture);
            chkGeminiEnableThinkingBudget.IsChecked = CurrentAppSettings.GeminiEnableThinkingBudget;
            txtGeminiThinkingBudget.Text = CurrentAppSettings.GeminiThinkingBudget.ToString();
            txtGeminiMaxOutputTokens.Text = CurrentAppSettings.GeminiMaxOutputTokens.ToString();
            chkGeminiEnableRequestLimit.IsChecked = CurrentAppSettings.GeminiEnableRequestLimit;
            txtGeminiRequestLimit.Text = CurrentAppSettings.GeminiRequestLimit.ToString();
            chkGeminiEnableRpmLimit.IsChecked = CurrentAppSettings.GeminiEnableRpmLimit;
            txtGeminiRpmLimit.Text = CurrentAppSettings.GeminiRpmLimit.ToString();
            chkGeminiEnableChunkIsolation.IsChecked = CurrentAppSettings.GeminiEnableChunkIsolation;
            chkGeminiEnableAutoRetryWithNewKey.IsChecked = CurrentAppSettings.GeminiEnableAutoRetryWithNewKey;
            txtGeminiDelayBetweenFiles.Text = CurrentAppSettings.GeminiDelayBetweenFileStartsMs.ToString();
            txtGeminiDelayBetweenChunks.Text = CurrentAppSettings.GeminiInterChunkDelayMs.ToString();
            txtGeminiDirectSendThreshold.Text = CurrentAppSettings.GeminiDirectSendThreshold.ToString();
            txtGeminiChunkSize.Text = CurrentAppSettings.GeminiChunkSize.ToString();

            // --- OpenRouter Settings ---
            _isProgrammaticallyChangingOpenRouterApiKey = true;
            if (CurrentAppSettings.OpenRouterShouldSaveApiKey)
            {
                pwdOpenRouterApiKey.Password = CurrentAppSettings.OpenRouterApiKey;
                txtOpenRouterApiKey.Text = CurrentAppSettings.OpenRouterApiKey;
            }
            else
            {
                pwdOpenRouterApiKey.Password = string.Empty;
                txtOpenRouterApiKey.Text = string.Empty;
            }
            chkSaveOpenRouterApiKey.IsChecked = CurrentAppSettings.OpenRouterShouldSaveApiKey;
            _isProgrammaticallyChangingOpenRouterApiKey = false;

            var distinctOpenRouterModels = CurrentAppSettings.AvailableOpenRouterModels.Distinct().ToList();
            CurrentAppSettings.AvailableOpenRouterModels = new ObservableCollection<string>(distinctOpenRouterModels);
            if (!CurrentAppSettings.AvailableOpenRouterModels.Any())
            {
                CurrentAppSettings.AvailableOpenRouterModels.Add(AppSettings.DEFAULT_OPENROUTER_MODEL);
            }
            var displayModels = new ObservableCollection<string>();
            foreach (var technicalName in CurrentAppSettings.AvailableOpenRouterModels)
            {
                displayModels.Add(GetDisplayModelName(technicalName));
            }
            cmbOpenRouterModel.ItemsSource = displayModels;
            string selectedTechnicalName = CurrentAppSettings.SelectedOpenRouterModel;
            string selectedDisplayName = GetDisplayModelName(selectedTechnicalName);
            cmbOpenRouterModel.SelectedItem = selectedDisplayName;
            txtOpenRouterReferer.Text = CurrentAppSettings.HttpReferer;
            txtOpenRouterTitle.Text = CurrentAppSettings.XTitle;
            txtOpenRouterRateLimitPerMinute.Text = CurrentAppSettings.OpenRouterRateLimitPerMinute.ToString();
            txtOpenRouterMaxApiRetries.Text = CurrentAppSettings.OpenRouterMaxApiRetries.ToString();
            txtOpenRouterMaxContentRetries.Text = CurrentAppSettings.OpenRouterMaxContentRetries.ToString();
            txtOpenRouterApiRetryBaseDelayMs.Text = CurrentAppSettings.OpenRouterApiRetryBaseDelayMs.ToString();
            txtOpenRouterContentRetryBaseDelayMs.Text = CurrentAppSettings.OpenRouterContentRetryBaseDelayMs.ToString();
            txtOpenRouterDelayBetweenFiles.Text = CurrentAppSettings.OpenRouterDelayBetweenFileStartsMs.ToString();
            txtOpenRouterDelayBetweenChunks.Text = CurrentAppSettings.OpenRouterInterChunkDelayMs.ToString();
            txtOpenRouterDirectSendThreshold.Text = CurrentAppSettings.OpenRouterDirectSendThreshold.ToString();
            txtOpenRouterChunkSize.Text = CurrentAppSettings.OpenRouterChunkSize.ToString();
            cmbFindText.ItemsSource = CurrentAppSettings.FindHistory;
            cmbReplaceText.ItemsSource = CurrentAppSettings.ReplaceHistory;

            txtAioLauncherDirectSendThreshold.Text = CurrentAppSettings.AioLauncherDirectSendThreshold.ToString();
            txtAioLauncherChunkSize.Text = CurrentAppSettings.AioLauncherChunkSize.ToString();
        }
        private void ApplySettingsToRuntimeVariables()
        {
            _chutesMaxApiRetries_Setting = CurrentAppSettings.ChutesMaxApiRetries;
            _chutesMaxContentRetries_Setting = CurrentAppSettings.ChutesMaxContentRetries;
            _chutesDelayBetweenFileStartsMs_Setting = CurrentAppSettings.ChutesDelayBetweenFileStartsMs;
            _chutesInterChunkDelayMs_Setting = CurrentAppSettings.ChutesInterChunkDelayMs;
            _chutesDirectSendThreshold_Setting = CurrentAppSettings.ChutesDirectSendThreshold;
            _chutesChunkSize_Setting = CurrentAppSettings.ChutesChunkSize;
            _chutesApiRetryBaseDelayMs_Setting = CurrentAppSettings.ChutesApiRetryBaseDelayMs;
            _chutesContentRetryBaseDelayMs_Setting = CurrentAppSettings.ChutesContentRetryBaseDelayMs;

            _geminiMaxApiRetries_Setting = CurrentAppSettings.GeminiMaxApiRetries;
            _geminiDelayBetweenFileStartsMs_Setting = CurrentAppSettings.GeminiDelayBetweenFileStartsMs;
            _geminiInterChunkDelayMs_Setting = CurrentAppSettings.GeminiInterChunkDelayMs;
            _geminiDirectSendThreshold_Setting = CurrentAppSettings.GeminiDirectSendThreshold;
            _geminiChunkSize_Setting = CurrentAppSettings.GeminiChunkSize;
            _geminiApiRetryBaseDelayMs_Setting = CurrentAppSettings.GeminiApiRetryBaseDelayMs;
            _geminiTemperature_Setting = CurrentAppSettings.GeminiTemperature;
            _geminiEnableThinkingBudget_Setting = CurrentAppSettings.GeminiEnableThinkingBudget;
            _geminiThinkingBudget_Setting = CurrentAppSettings.GeminiThinkingBudget;
            _geminiMaxOutputTokens_Setting = CurrentAppSettings.GeminiMaxOutputTokens;
            _geminiEnableRequestLimit_Setting = CurrentAppSettings.GeminiEnableRequestLimit;
            _geminiRequestLimit_Setting = CurrentAppSettings.GeminiEnableRequestLimit ? CurrentAppSettings.GeminiRequestLimit : 0;
            _geminiEnableRpmLimit_Setting = CurrentAppSettings.GeminiEnableRpmLimit;
            _geminiRpmLimit_Setting = CurrentAppSettings.GeminiEnableRpmLimit ? CurrentAppSettings.GeminiRpmLimit : 0;
            _openRouterMaxApiRetries_Setting = CurrentAppSettings.OpenRouterMaxApiRetries;
            _openRouterMaxContentRetries_Setting = CurrentAppSettings.OpenRouterMaxContentRetries;
            _openRouterDelayBetweenFileStartsMs_Setting = CurrentAppSettings.OpenRouterDelayBetweenFileStartsMs;
            _openRouterInterChunkDelayMs_Setting = CurrentAppSettings.OpenRouterInterChunkDelayMs;
            _openRouterDirectSendThreshold_Setting = CurrentAppSettings.OpenRouterDirectSendThreshold;
            _openRouterChunkSize_Setting = CurrentAppSettings.OpenRouterChunkSize;
            _openRouterApiRetryBaseDelayMs_Setting = CurrentAppSettings.OpenRouterApiRetryBaseDelayMs;
            _openRouterContentRetryBaseDelayMs_Setting = CurrentAppSettings.OpenRouterContentRetryBaseDelayMs;
            UserSuppliedPrompt = CurrentAppSettings.UserPrompt;
            UseSystemBasePrompt = CurrentAppSettings.UseSystemBasePrompt;
        }

        #endregion

        private void ShowTranslationView_Click(object sender, RoutedEventArgs e)
        {
            TranslationView.Visibility = Visibility.Visible;
            BatchEditView.Visibility = Visibility.Collapsed;
        }

        private void ShowBatchEditView_Click(object sender, RoutedEventArgs e)
        {
            TranslationView.Visibility = Visibility.Collapsed;
            BatchEditView.Visibility = Visibility.Visible;
        }

        private void ShowCrawlView_Click(object sender, RoutedEventArgs e)
        {
            // Tạm thời chỉ hiển thị thông báo
            CustomMessageBox.Show("Chức năng 'Cào truyện hàng loạt' đang được phát triển.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void SelectBatchFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Chọn thư mục chứa các file văn bản";
                dialog.UseDescriptionForTitle = true;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _batchEditFolderPath = dialog.SelectedPath;
                    txtBatchFolderPath.Text = _batchEditFolderPath;
                    txtBatchFolderPath.Foreground = (System.Windows.Media.Brush)FindResource("TextColor");
                }
            }
        }
        private async void BatchEditStart_Click(object sender, RoutedEventArgs e)
        {
            // 1. Thu thập thông tin từ UI
            string findText = cmbFindText.Text;
            bool isDeleteAction = rbActionDelete.IsChecked == true;
            string replaceText = isDeleteAction ? "" : cmbReplaceText.Text;
            bool isCaseSensitive = chkCaseSensitive.IsChecked == true;

            // 2. Validate đầu vào
            if (string.IsNullOrEmpty(_batchEditFolderPath) || !Directory.Exists(_batchEditFolderPath))
            {
                CustomMessageBox.Show("Vui lòng chọn một thư mục hợp lệ.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(findText))
            {
                CustomMessageBox.Show("Vui lòng nhập chuỗi cần tìm.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 3. Chuẩn bị xử lý
            txtBatchEditLog.Text = "Đang bắt đầu...\n";
            btnBatchEditStart.IsEnabled = false;
            var stopwatch = Stopwatch.StartNew();
            int filesProcessed = 0;
            int filesChanged = 0;

            try
            {
                // 4. Lấy danh sách file .txt
                var files = Directory.GetFiles(_batchEditFolderPath, "*.txt", SearchOption.TopDirectoryOnly);
                txtBatchEditLog.Text += $"Tìm thấy {files.Length} file .txt.\n\n";

                StringComparison comparisonType = isCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

                // 5. Lặp qua từng file và xử lý
                foreach (var file in files)
                {
                    string originalContent = await File.ReadAllTextAsync(file);
                    string newContent = originalContent.Replace(findText, replaceText, comparisonType);

                    // Chỉ ghi lại file nếu có sự thay đổi
                    if (originalContent != newContent)
                    {
                        await File.WriteAllTextAsync(file, newContent);
                        filesChanged++;
                        txtBatchEditLog.Text += $"Đã xử lý: {Path.GetFileName(file)}\n";
                    }
                    filesProcessed++;
                }
            }
            catch (Exception ex)
            {
                txtBatchEditLog.Text += $"\n!!! Đã xảy ra lỗi nghiêm trọng: {ex.Message}\n";
            }
            finally
            {
                // 6. Cập nhật History và hoàn tất
                UpdateHistory(findText, isDeleteAction ? null : replaceText);
                stopwatch.Stop();
                txtBatchEditLog.Text += $"\n--- HOÀN TẤT ---\nĐã xử lý {filesProcessed} file, thay đổi {filesChanged} file trong {stopwatch.Elapsed.TotalSeconds:F2} giây.";
                btnBatchEditStart.IsEnabled = true;
            }
        }

        private void UpdateHistory(string findText, string replaceText)
        {
            // Cập nhật Find History
            if (!string.IsNullOrEmpty(findText))
            {
                if (CurrentAppSettings.FindHistory.Contains(findText))
                {
                    CurrentAppSettings.FindHistory.Remove(findText);
                }
                CurrentAppSettings.FindHistory.Insert(0, findText);
                if (CurrentAppSettings.FindHistory.Count > 20)
                {
                    CurrentAppSettings.FindHistory.RemoveAt(20);
                }
            }

            // Cập nhật Replace History (chỉ khi là hành động Replace)
            if (replaceText != null)
            {
                if (CurrentAppSettings.ReplaceHistory.Contains(replaceText))
                {
                    CurrentAppSettings.ReplaceHistory.Remove(replaceText);
                }
                CurrentAppSettings.ReplaceHistory.Insert(0, replaceText);
                if (CurrentAppSettings.ReplaceHistory.Count > 20)
                {
                    CurrentAppSettings.ReplaceHistory.RemoveAt(20);
                }
            }

            // Lưu lại cài đặt
            SaveAppSettings();
        }
        #region UI Event Handlers

        private void TglShowOpenRouterApiKey_Checked(object sender, RoutedEventArgs e)
        {
            _isProgrammaticallyChangingOpenRouterApiKey = true;
            txtOpenRouterApiKey.Text = pwdOpenRouterApiKey.Password;
            txtOpenRouterApiKey.Visibility = Visibility.Visible;
            pwdOpenRouterApiKey.Visibility = Visibility.Collapsed;
            _isProgrammaticallyChangingOpenRouterApiKey = false;
        }

        private void TglShowOpenRouterApiKey_Unchecked(object sender, RoutedEventArgs e)
        {
            _isProgrammaticallyChangingOpenRouterApiKey = true;
            pwdOpenRouterApiKey.Password = txtOpenRouterApiKey.Text;
            txtOpenRouterApiKey.Visibility = Visibility.Collapsed;
            pwdOpenRouterApiKey.Visibility = Visibility.Visible;
            _isProgrammaticallyChangingOpenRouterApiKey = false;
        }

        private void PwdOpenRouterApiKey_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isProgrammaticallyChangingOpenRouterApiKey) return;
            if (tglShowOpenRouterApiKey.IsChecked == false)
            {
                txtOpenRouterApiKey.Text = pwdOpenRouterApiKey.Password;
            }
        }

        private void TxtOpenRouterApiKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isProgrammaticallyChangingOpenRouterApiKey) return;
            if (tglShowOpenRouterApiKey.IsChecked == true)
            {
                pwdOpenRouterApiKey.Password = txtOpenRouterApiKey.Text;
            }
        }

        private void BtnAddOpenRouterModel_Click(object sender, RoutedEventArgs e)
        {
            AddModel(ApiProviderType.OpenRouter);
        }

        private void BtnRemoveOpenRouterModel_Click(object sender, RoutedEventArgs e)
        {
            RemoveModel(ApiProviderType.OpenRouter);
        }
        // SỬA ĐỔI HÀM CanExecuteTranslationAsync (dành cho API bên thứ 3)
        private async Task<(bool canProceed, string reason)> CanExecuteTranslationAsync(ApiProviderType selectedProvider)
        {
            var user = App.User;
            (bool success, int remaining, string message) = await ApiService.PreTranslateCheckAsync(selectedProvider);

            if (success)
            {
                user.RemainingRequests = remaining;
                return (true, string.Empty);
            }
            else
            {
                user.RemainingRequests = remaining;
                return (false, message);
            }
        }
        private void UpdateUiBasedOnPermissions()
        {
            var user = App.User;
            if (user == null) return;
            if (!user.IsLoggedIn)
            {
                cmbApiProvider.IsEnabled = false;
                cmbApiProvider.ToolTip = "Vui lòng đăng nhập để sử dụng tính năng này.";
                return;
            }
            cmbApiProvider.IsEnabled = true;
            cmbApiProvider.ToolTip = null;
            AllowedApis finalPermissions;

            if (user.AllowedApiAccess != AllowedApis.None)
            {
                finalPermissions = user.AllowedApiAccess;
            }
            else
            {
                if (user.Tier == "Lifetime")
                {
                    finalPermissions = AllowedApis.ChutesAI | AllowedApis.Gemini | AllowedApis.OpenRouter;
                }
                else if (user.Tier == "Free")
                {
                    finalPermissions = AllowedApis.OpenRouter;
                }
                else 
                {
                    finalPermissions = AllowedApis.ChutesAI | AllowedApis.Gemini | AllowedApis.OpenRouter;
                }
            }
            bool canUseChutes = finalPermissions.HasFlag(AllowedApis.ChutesAI);
            bool canUseGemini = finalPermissions.HasFlag(AllowedApis.Gemini);
            bool canUseOpenRouter = finalPermissions.HasFlag(AllowedApis.OpenRouter);
            int currentSelection = cmbApiProvider.SelectedIndex;
            bool selectionIsInvalid = (currentSelection == 0 && !canUseChutes) ||
                                      (currentSelection == 1 && !canUseGemini) ||
                                      (currentSelection == 2 && !canUseOpenRouter);

            if (selectionIsInvalid)
            {
                if (canUseOpenRouter)
                {
                    cmbApiProvider.SelectedIndex = 2; 
                }
                else if (canUseChutes)
                {
                    cmbApiProvider.SelectedIndex = 0; 
                }
                else if (canUseGemini)
                {
                    cmbApiProvider.SelectedIndex = 1; 
                }
                else
                {
                    cmbApiProvider.IsEnabled = false;
                    cmbApiProvider.ToolTip = "Tài khoản của bạn không có quyền truy cập bất kỳ API nào.";
                }
            }
            UpdateStatusLabel();
        }

        private void TglShowChutesApiKey_Checked(object sender, RoutedEventArgs e)
        {
            _isProgrammaticallyChangingChutesApiKey = true;
            txtChutesApiKey.Text = pwdChutesApiKey.Password;
            txtChutesApiKey.Visibility = Visibility.Visible;
            pwdChutesApiKey.Visibility = Visibility.Collapsed;
            _isProgrammaticallyChangingChutesApiKey = false;
        }

        private void TglShowChutesApiKey_Unchecked(object sender, RoutedEventArgs e)
        {
            _isProgrammaticallyChangingChutesApiKey = true;
            pwdChutesApiKey.Password = txtChutesApiKey.Text;
            txtChutesApiKey.Visibility = Visibility.Collapsed;
            pwdChutesApiKey.Visibility = Visibility.Visible;
            _isProgrammaticallyChangingChutesApiKey = false;
        }

        private void PwdChutesApiKey_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isProgrammaticallyChangingChutesApiKey) return;
            if (tglShowChutesApiKey.IsChecked == false)
            {
                txtChutesApiKey.Text = pwdChutesApiKey.Password;
            }
        }

        private void TxtChutesApiKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isProgrammaticallyChangingChutesApiKey) return;
            if (tglShowChutesApiKey.IsChecked == true)
            {
                pwdChutesApiKey.Password = txtChutesApiKey.Text;
            }
        }

        private void TglShowGeminiApiKey_Checked(object sender, RoutedEventArgs e)
        {
            _isProgrammaticallyChangingGeminiApiKey = true;
            txtGeminiApiKey.Text = pwdGeminiApiKey.Password;
            txtGeminiApiKey.Visibility = Visibility.Visible;
            pwdGeminiApiKey.Visibility = Visibility.Collapsed;
            _isProgrammaticallyChangingGeminiApiKey = false;
        }

        private void TglShowGeminiApiKey_Unchecked(object sender, RoutedEventArgs e)
        {
            _isProgrammaticallyChangingGeminiApiKey = true;
            pwdGeminiApiKey.Password = txtGeminiApiKey.Text;
            txtGeminiApiKey.Visibility = Visibility.Collapsed;
            pwdGeminiApiKey.Visibility = Visibility.Visible;
            _isProgrammaticallyChangingGeminiApiKey = false;
        }

        private void PwdGeminiApiKey_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isProgrammaticallyChangingGeminiApiKey) return;
            if (tglShowGeminiApiKey.IsChecked == false)
            {
                txtGeminiApiKey.Text = pwdGeminiApiKey.Password;
            }
        }
        private void ChkGeminiUseMultiKey_Changed(object sender, RoutedEventArgs e)
        {
            bool isMultiKey = chkGeminiUseMultiKey.IsChecked == true;

            // Toggle visibility
            pnlGeminiSingleKey.Visibility = isMultiKey ? Visibility.Collapsed : Visibility.Visible;
            pnlGeminiMultiKey.Visibility = isMultiKey ? Visibility.Visible : Visibility.Collapsed;

            // Update advanced mode availability
            chkGeminiAdvancedMultiKeyMode.IsEnabled = isMultiKey;
            if (!isMultiKey)
            {
                chkGeminiAdvancedMultiKeyMode.IsChecked = false;
            }
        }

        private void ChkGeminiAdvancedMultiKeyMode_Changed(object sender, RoutedEventArgs e)
        {
            bool isAdvanced = chkGeminiAdvancedMultiKeyMode.IsChecked == true;
            pnlGeminiAdvancedMultiKeySettings.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
        }
        private void TxtGeminiApiKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isProgrammaticallyChangingGeminiApiKey) return;
            if (tglShowGeminiApiKey.IsChecked == true)
            {
                pwdGeminiApiKey.Password = txtGeminiApiKey.Text;
            }
        }
        private void BtnAddChutesModel_Click(object sender, RoutedEventArgs e) { AddModel(ApiProviderType.ChutesAI); }
        private void BtnRemoveChutesModel_Click(object sender, RoutedEventArgs e) { RemoveModel(ApiProviderType.ChutesAI); }
        private void BtnAddGeminiModel_Click(object sender, RoutedEventArgs e) { AddModel(ApiProviderType.Gemini); }
        private void BtnRemoveGeminiModel_Click(object sender, RoutedEventArgs e) { RemoveModel(ApiProviderType.Gemini); }

        private void AddModel(ApiProviderType provider)
        {
            var inputDialog = new InputDialog($"Nhập tên Model API mới:", "")
            {
                Owner = this
            };
            if (inputDialog.ShowDialog() == true)
            {
                string newModelName = inputDialog.Answer?.Trim();
                if (!string.IsNullOrWhiteSpace(newModelName))
                {
                    ObservableCollection<string> modelCollection;
                    System.Windows.Controls.ComboBox comboBox; 
                    switch (provider)
                    {
                        case ApiProviderType.ChutesAI:
                            modelCollection = CurrentAppSettings.AvailableChutesApiModels;
                            comboBox = cmbChutesApiModel;
                            break;
                        case ApiProviderType.Gemini:
                            modelCollection = CurrentAppSettings.AvailableGeminiApiModels;
                            comboBox = cmbGeminiApiModel;
                            break;
                        case ApiProviderType.OpenRouter:
                            modelCollection = CurrentAppSettings.AvailableOpenRouterModels;
                            comboBox = cmbOpenRouterModel;
                            break;
                        default:
                            return;
                    }

                    if (!modelCollection.Contains(newModelName))
                    {
                        modelCollection.Add(newModelName);
                        comboBox.Text = newModelName; 
                    }
                    if (provider == ApiProviderType.OpenRouter)
                    {
                        if (!CurrentAppSettings.AvailableOpenRouterModels.Contains(newModelName))
                        {
                            CurrentAppSettings.AvailableOpenRouterModels.Add(newModelName);
                            var displayModels = new ObservableCollection<string>();
                            foreach (var techName in CurrentAppSettings.AvailableOpenRouterModels)
                            {
                                displayModels.Add(GetDisplayModelName(techName));
                            }
                            cmbOpenRouterModel.ItemsSource = displayModels;
                            cmbOpenRouterModel.SelectedItem = newModelName;
                        }
                    }
                }
            }
        }
        private async Task ProcessWithAioLauncherAsync(string contentToTranslate, string[] filesToTranslate, CancellationToken token)
        {
            var user = App.User;
            if (user == null || !user.IsLoggedIn)
            {
                Dispatcher.Invoke(() => lblStatus.Text = "Lỗi: Yêu cầu đăng nhập để dùng tính năng này.");
                return;
            }

            string targetLanguage = (cmbLanguage.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Tiếng Việt";
            string selectedGenreValue = _selectedGenre;
            long totalCharsToTranslate = 0;
            if (!string.IsNullOrEmpty(contentToTranslate))
            {
                totalCharsToTranslate = contentToTranslate.Length;
            }
            else if (filesToTranslate.Any())
            {
                try
                {
                    Dispatcher.Invoke(() => lblStatus.Text = "Đang tính toán tổng số ký tự...");
                    foreach (var file in filesToTranslate)
                    {
                        if (token.IsCancellationRequested) return;
                        totalCharsToTranslate += (await File.ReadAllTextAsync(file, token)).Length;
                    }
                }
                catch (OperationCanceledException) { Dispatcher.Invoke(() => lblStatus.Text = "Đã dừng bởi người dùng."); return; }
                catch (Exception ex) { Dispatcher.Invoke(() => lblStatus.Text = $"Lỗi khi đọc file để tính ký tự: {ex.Message}"); return; }
            }
            long remainingChars = user.AioCharacterLimit - user.AioCharactersUsedToday;
            if (totalCharsToTranslate > remainingChars)
            {
                Dispatcher.Invoke(() => CustomMessageBox.Show($"Không đủ ký tự để dịch.\n\nYêu cầu: {totalCharsToTranslate:N0}\nCòn lại: {remainingChars:N0}", "Lỗi Giới Hạn", MessageBoxButton.OK, MessageBoxImage.Warning));
                return;
            }

            if (!string.IsNullOrEmpty(contentToTranslate))
            {
                string result = await TranslateSingleContentWithAio(contentToTranslate, token, "UI Input", (cmbLanguage.SelectedItem as ComboBoxItem)?.Content.ToString(), _selectedGenre);
                await RefreshUserSessionAsync();

                if (!result.StartsWith("Lỗi:"))
                {
                    Dispatcher.Invoke(() =>
                    {
                        ChatHistory.Add(new ChatMessage { MessageContent = result, IsUserInput = false, IsExpanded = true });
                        lblStatus.Text = "Hoàn tất dịch từ UI.";
                    });
                }
            }
            else if (filesToTranslate.Any())
            {
                string folder = Path.GetDirectoryName(filesToTranslate[0]);
                string translatedFolder = Path.Combine(folder, "Đã Dịch");
                Directory.CreateDirectory(translatedFolder);
                int rpm = 10;
                var fileSemaphore = new SemaphoreSlim(rpm, rpm);
                var tasks = new List<Task>();
                int successCount = 0;
                int failCount = 0;

                try
                {
                    foreach (var file in filesToTranslate)
                    {
                        if (token.IsCancellationRequested) break;
                        await fileSemaphore.WaitAsync(token);

                        tasks.Add(Task.Run(async () =>
                        {
                            string originalContent = "";
                            string originalFileName = Path.GetFileName(file);
                            try
                            {
                                token.ThrowIfCancellationRequested();
                                originalContent = await File.ReadAllTextAsync(file, token);

                                string translatedContent = await TranslateSingleContentWithAio(originalContent, token, originalFileName, targetLanguage, selectedGenreValue);
                                bool isError = translatedContent.StartsWith("Lỗi:");
                                if (isError)
                                {
                                    failCount++;
                                    return;
                                }
                                string cleanContent = CleanGenericContent(translatedContent);
                                string chapterTitle = GetChapterFromText(cleanContent);
                                string originalFileNameNoExt = Path.GetFileNameWithoutExtension(file);
                                string finalFileNameBase = SanitizeFileName(string.IsNullOrWhiteSpace(chapterTitle) || chapterTitle == "UnknownChapter" ? originalFileNameNoExt : chapterTitle);

                                string finalOutputPath = Path.Combine(translatedFolder, $"{finalFileNameBase}.txt");
                                int counter = 1;
                                while (File.Exists(finalOutputPath))
                                {
                                    finalOutputPath = Path.Combine(translatedFolder, $"{finalFileNameBase}_{counter++}.txt");
                                }
                                await File.WriteAllTextAsync(finalOutputPath, cleanContent, Encoding.UTF8, token);
                                successCount++;
                                Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage { MessageContent = $"Lưu thành công: {Path.GetFileName(finalOutputPath)}" }));
                            }
                            catch (OperationCanceledException) { /* Bỏ qua */ }
                            catch (Exception ex)
                            {
                                failCount++;
                                Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage { MessageContent = $"Lỗi nghiêm trọng khi xử lý file {originalFileName}: {ex.Message}" }));
                            }
                            finally
                            {
                                fileSemaphore.Release();
                            }
                        }, token));
                    }
                    await Task.WhenAll(tasks);
                    Dispatcher.Invoke(() => lblStatus.Text = $"Hoàn tất. Thành công: {successCount}, Lỗi: {failCount}.");
                }
                catch (OperationCanceledException)
                {
                    Dispatcher.Invoke(() => lblStatus.Text = "Tác vụ dịch hàng loạt đã được dừng.");
                }
                await RefreshUserSessionAsync();
            }
        }
        private async Task<string> TranslateSingleContentWithAio(string content, CancellationToken token, string fileNameForLog = "UI Input", string targetLanguage = null, string selectedGenre = null)
        {
            Dispatcher.Invoke(() => lblStatus.Text = $"Đang gửi yêu cầu cho '{fileNameForLog}'...");
            string finalLanguage = targetLanguage ?? ((cmbLanguage.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Tiếng Việt");
            
            // Get the effective Gemini prompts (systemInstruction and userPrompt)
            GetEffectiveGeminiPrompts(content, out string systemInstruction, out string userPrompt);
            
            // Send systemInstruction and the content (userPrompt) to server
            var (success, response, error) = await ApiService.StartAioTranslationJobAsync(
                systemInstruction,
                userPrompt,  // This includes the preamble + actual content
                finalLanguage
            );

            if (!success)
            {
                string errorMessage = $"Lỗi: Không thể bắt đầu dịch cho '{fileNameForLog}': {error}";
                Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage { MessageContent = errorMessage, IsUserInput = false, IsExpanded = true }));
                return errorMessage;
            }
            await RefreshUserSessionAsync();
            string sessionId = response.SessionId;
            Dispatcher.Invoke(() => lblStatus.Text = $"Đang chờ kết quả cho '{fileNameForLog}' (ID: ...{sessionId.Substring(sessionId.Length - 6)})");

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMinutes < 10 && !token.IsCancellationRequested)
            {
                var result = await ApiService.GetAioJobResultAsync(sessionId);
                string status = result.Status.ToLower();

                if (status == "completed")
                {
                    return result.TranslatedContent;
                }
                if (status == "failed")
                {
                    string errorMessage = $"Lỗi: Dịch '{fileNameForLog}' thất bại: {result.ErrorMessage}";
                    Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage { MessageContent = errorMessage, IsUserInput = false, IsExpanded = true }));
                    return errorMessage;
                }

                try
                {
                    await Task.Delay(5000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return $"Lỗi: Tác vụ dịch cho '{fileNameForLog}' đã bị hủy hoặc hết thời gian chờ.";
        }
        private void CmbApiProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (gbAioLauncherSettings == null || gbChutesAiSettings == null || gbGeminiSettings == null || gbOpenRouterSettings == null) return;
            gbAioLauncherSettings.Visibility = Visibility.Collapsed;
            gbChutesAiSettings.Visibility = Visibility.Collapsed;
            gbGeminiSettings.Visibility = Visibility.Collapsed;
            gbOpenRouterSettings.Visibility = Visibility.Collapsed;
            switch (cmbApiProvider.SelectedIndex)
            {
                case 0: 
                    gbAioLauncherSettings.Visibility = Visibility.Visible;
                    break;
                case 1: 
                    gbChutesAiSettings.Visibility = Visibility.Visible;
                    break;
                case 2: 
                    gbGeminiSettings.Visibility = Visibility.Visible;
                    break;
                case 3: 
                    gbOpenRouterSettings.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void BtnAddFile_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Text Files (*.txt)|*.txt|All files (*.*)|*.*", Multiselect = false };
            if (ofd.ShowDialog() == true)
            {
                selectedFilePath = ofd.FileName;
                if (!string.IsNullOrEmpty(selectedFilePath))
                {
                    try
                    {
                        txtInput.Text = ""; 
                        txtInput.Tag = Path.GetFileName(selectedFilePath); 
                        lblFilePath.Text = $"Sẽ dịch hàng loạt file từ thư mục của: {Path.GetFileName(selectedFilePath)}";
                    }
                    catch (Exception ex)
                    {
                        selectedFilePath = "";
                        lblFilePath.Text = "Lỗi đọc file...";
                        CustomMessageBox.Show($"Không thể đọc file: {ex.Message}", "Lỗi File", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (btnRun.Visibility == Visibility.Visible && btnRun.IsEnabled)
                {
                    BtnRun_Click(btnRun, new RoutedEventArgs());
                    e.Handled = true; 
                }
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (btnStop.Visibility == Visibility.Visible)
                {
                    BtnStop_Click(btnStop, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
        }
        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            SaveAppSettings();
            _selectedLanguage = (cmbLanguage.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Tiếng Việt";
            string contentToTranslate = txtInput.Text;
            if (string.IsNullOrEmpty(selectedFilePath) && string.IsNullOrWhiteSpace(contentToTranslate))
            {
                CustomMessageBox.Show("Vui lòng nhập nội dung hoặc chọn một file để dịch.", "Chưa có nội dung", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApiProviderType currentProvider;
            switch (cmbApiProvider.SelectedIndex)
            {
                case 0: currentProvider = ApiProviderType.AIOLauncher; break;
                case 1: currentProvider = ApiProviderType.ChutesAI; break;
                case 2: currentProvider = ApiProviderType.Gemini; break;
                case 3: currentProvider = ApiProviderType.OpenRouter; break;
                default:
                    CustomMessageBox.Show("Vui lòng chọn một API dịch từ danh sách.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
            }

            if (!string.IsNullOrEmpty(selectedFilePath) || !string.IsNullOrWhiteSpace(contentToTranslate))
            {
                ChatHistory.Clear();
            }

            if (!string.IsNullOrEmpty(contentToTranslate))
            {
                ChatHistory.Add(new ChatMessage { MessageContent = contentToTranslate, IsUserInput = true, IsExpanded = false });
            }
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _geminiApiRequestCount = 0;
            _stopProcessingDueToLimit = false;

            btnRun.Visibility = Visibility.Collapsed;
            btnStop.Visibility = Visibility.Visible;
            txtInput.Clear();

            await TranslateAndProcess(currentProvider, _cts.Token);

            btnRun.Visibility = Visibility.Visible;
            btnStop.Visibility = Visibility.Collapsed;
            ChatScrollViewer.ScrollToEnd();
        }
        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();

            selectedFilePath = "";
            _stopProcessingDueToLimit = false;

            Dispatcher.Invoke(() => {
                lblStatus.Text = "Đã dừng.";
                lblFilePath.Text = "Chưa chọn file";
                btnRun.Visibility = Visibility.Visible;
                btnStop.Visibility = Visibility.Collapsed;
            });
        }
        private void TogglePromptEditor_Click(object sender, RoutedEventArgs e)
        {
            if (_isPromptEditorVisible)
            {
                CurrentAppSettings.UserPrompt = txtPromptEditor.Text;
                SaveAppSettings();
                gridPromptEditorPanel.Visibility = Visibility.Collapsed;
                _isPromptEditorVisible = false;
                lblStatus.Text = "Đã lưu prompt hệ thống.";
            }
            else
            {
                txtPromptEditor.Text = CurrentAppSettings.UserPrompt;
                gridPromptEditorPanel.Visibility = Visibility.Visible;
                _isPromptEditorVisible = true;
            }
        }
        private void BtnResetPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (CustomMessageBox.Show("Bạn có chắc muốn đặt lại prompt về mặc định không?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                txtPromptEditor.Text = "";
                CurrentAppSettings.UserPrompt = "";
                SaveAppSettings();
            }
        }
        private void RemoveModel(ApiProviderType provider)
        {
            var comboBox = provider == ApiProviderType.ChutesAI ? cmbChutesApiModel : cmbGeminiApiModel;
            if (comboBox.SelectedItem is string selectedModel)
            {
                var modelCollection = provider == ApiProviderType.ChutesAI ? CurrentAppSettings.AvailableChutesApiModels : CurrentAppSettings.AvailableGeminiApiModels;
                string defaultModel = provider == ApiProviderType.ChutesAI ? AppSettings.DEFAULT_CHUTES_MODEL_TECHNICAL_NAME_CONST : AppSettings.DEFAULT_GEMINI_MODEL_TECHNICAL_NAME_CONST;

                if (selectedModel == defaultModel)
                {
                    CustomMessageBox.Show("Không thể xóa model mặc định.", "Không thể xóa", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (CustomMessageBox.Show($"Bạn có chắc chắn muốn xóa model '{selectedModel}' không?", "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    modelCollection.Remove(selectedModel);
                    comboBox.SelectedItem = modelCollection.FirstOrDefault() ?? defaultModel;
                }
            }
            if (provider == ApiProviderType.OpenRouter)
            {
                string selectedDisplayName = cmbOpenRouterModel.SelectedItem as string;
                if (selectedDisplayName != null)
                {
                    string technicalNameToRemove = GetTechnicalModelName(selectedDisplayName);
                    CurrentAppSettings.AvailableOpenRouterModels.Remove(technicalNameToRemove);
                    var displayModels = new ObservableCollection<string>();
                    foreach (var techName in CurrentAppSettings.AvailableOpenRouterModels)
                    {
                        displayModels.Add(GetDisplayModelName(techName));
                    }
                    cmbOpenRouterModel.ItemsSource = displayModels;
                    cmbOpenRouterModel.SelectedItem = GetDisplayModelName(CurrentAppSettings.AvailableOpenRouterModels.FirstOrDefault());
                }
            }
        }

        private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, "^[0-9]*$");
        }

        private void DecimalNumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as System.Windows.Controls.TextBox;
            string futureText = textBox.Text.Insert(textBox.CaretIndex, e.Text);
            e.Handled = !Regex.IsMatch(futureText, @"^[0-2]?(\.[0-9]*)?$");
        }

        private void SettingsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
        }
        private void BtnCrawl_Click(object sender, RoutedEventArgs e)
        {
        }

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
        }

        private void btnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string pathToOpen = "";
            if (!string.IsNullOrEmpty(selectedFilePath))
            {
                string baseDir = Path.GetDirectoryName(selectedFilePath);
                if (baseDir != null)
                {
                    string translatedDir = Path.Combine(baseDir, "Đã Dịch");
                    if (Directory.Exists(translatedDir))
                    {
                        pathToOpen = translatedDir;
                    }
                    else if (Directory.Exists(baseDir))
                    {
                        pathToOpen = baseDir;
                    }
                }
            }

            if (string.IsNullOrEmpty(pathToOpen))
            {
                pathToOpen = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            }

            try
            {
                Process.Start("explorer.exe", pathToOpen);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Không thể mở thư mục: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Core Translation Logic

        private async Task TranslateAndProcess(ApiProviderType currentProvider, CancellationToken token)
        {
            if (currentProvider == ApiProviderType.AIOLauncher)
            {
                string content = string.IsNullOrEmpty(selectedFilePath) ? ChatHistory.LastOrDefault(m => m.IsUserInput)?.MessageContent ?? "" : null;
                string[] files = string.IsNullOrEmpty(selectedFilePath) ? Array.Empty<string>() : Directory.GetFiles(Path.GetDirectoryName(selectedFilePath), "*.txt");

                await ProcessWithAioLauncherAsync(content, files, token);
                return;
            }
            if (currentProvider == ApiProviderType.Gemini)
            {
                _geminiKeyManager = new GeminiApiKeyManager(CurrentAppSettings);
            }

            if (currentProvider == ApiProviderType.ChutesAI && string.IsNullOrWhiteSpace(CurrentAppSettings.ChutesApiKey))
            {
                Dispatcher.Invoke(() => { lblStatus.Text = "Lỗi: Chutes AI API Key trống."; ChatHistory.Add(new ChatMessage { MessageContent = "Lỗi: Chutes AI API Key trống.", IsUserInput = false }); });
                return;
            }
            if (currentProvider == ApiProviderType.Gemini)
            {
                // Luôn build key manager dựa trên UI của CỬA SỔ hiện tại
                string runtimeKeys = GetGeminiApiKeysForCurrentWindow();
                _geminiKeyManager = new GeminiApiKeyManager(CurrentAppSettings, runtimeKeys);

                if (_geminiKeyManager == null || !_geminiKeyManager.HasAvailableKeys())
                {
                    Dispatcher.Invoke(() =>
                    {
                        lblStatus.Text = "Lỗi: Gemini API Key trống hoặc không hợp lệ.";
                        ChatHistory.Add(new ChatMessage
                        {
                            MessageContent = "Vui lòng cấu hình ít nhất một Gemini API key hợp lệ trong cài đặt.",
                            IsUserInput = false
                        });
                        CustomMessageBox.Show(
                            "Vui lòng cấu hình ít nhất một Gemini API key hợp lệ trong cài đặt.",
                            "Thiếu API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return;
                }
            }
            else if (currentProvider == ApiProviderType.OpenRouter && string.IsNullOrWhiteSpace(CurrentAppSettings.OpenRouterApiKey))
            {
                Dispatcher.Invoke(() => { lblStatus.Text = "Lỗi: OpenRouter API Key trống."; ChatHistory.Add(new ChatMessage { MessageContent = "Lỗi: OpenRouter API Key trống.", IsUserInput = false }); });
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(selectedFilePath))
                {
                    string inputText = ChatHistory.LastOrDefault(m => m.IsUserInput)?.MessageContent ?? "";

                    if (string.IsNullOrWhiteSpace(inputText))
                    {
                        Dispatcher.Invoke(() => lblStatus.Text = "Chưa có nội dung.");
                        return;
                    }

                    Dispatcher.Invoke(() => { lblStatus.Text = $"Đang dịch ({currentProvider})..."; });

                    string translatedText;
                    switch (currentProvider)
                    {
                        case ApiProviderType.AIOLauncher:
                            translatedText = await ProcessWithAioLauncher(inputText, token);
                            break;
                        case ApiProviderType.Gemini: translatedText = await TranslateLongTextWithGeminiApi("UI_Input", inputText, token); break;
                        case ApiProviderType.OpenRouter: translatedText = await TranslateLongTextWithOpenRouterApi("UI_Input", inputText, token); break;
                        case ApiProviderType.ChutesAI: default: translatedText = await TranslateLongTextWithChutesApi("UI_Input", inputText, token); break;
                    }

                    bool isError = string.IsNullOrWhiteSpace(translatedText) || translatedText.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase);
                    if (isError)
                    {
                        Dispatcher.Invoke(() => {
                            ChatHistory.Add(new ChatMessage
                            {
                                MessageContent = "Lỗi Dịch từ UI:\n" + translatedText,
                                IsUserInput = false,
                                IsExpanded = true 
                            });
                            lblStatus.Text = "Lỗi Dịch từ UI";
                        });
                    }
                    else
                    {
                        Dispatcher.Invoke(() => {
                            ChatHistory.Add(new ChatMessage
                            {
                                MessageContent = translatedText,
                                IsUserInput = false,
                                IsExpanded = true 
                            });
                            lblStatus.Text = "Dịch từ UI hoàn tất.";
                        });
                    }
                    return;
                }
                string folder = Path.GetDirectoryName(selectedFilePath);
                string translatedFolder = Path.Combine(folder, "Đã Dịch");
                Directory.CreateDirectory(translatedFolder);
                var files = Directory.GetFiles(folder, "*.txt")
                                     .Where(f => !Path.GetDirectoryName(f).Equals(translatedFolder, StringComparison.OrdinalIgnoreCase))
                                     .OrderBy(f => ExtractChapterNumberFromFileName(Path.GetFileName(f)))
                                     .ToArray();

                if (!files.Any())
                {
                    Dispatcher.Invoke(() => lblStatus.Text = "Không có file .txt nào trong thư mục.");
                    return;
                }

                Dispatcher.Invoke(() => { ChatHistory.Clear(); lblStatus.Text = $"Chuẩn bị dịch {files.Length} file bằng {currentProvider}..."; });

                if (currentProvider == ApiProviderType.Gemini)
                {
                    // NEW: Khởi tạo rate limiter RPM cho riêng tab này nếu được bật
                    _geminiWindowRpmLimiter = null; // Reset trước mỗi lần chạy
                    if (_geminiEnableRpmLimit_Setting && _geminiRpmLimit_Setting > 0)
                    {
                        // Math.Max(1, ...) để tránh lỗi chia cho 0 và đảm bảo limiter luôn hoạt động
                        int effectiveRpm = Math.Max(1, _geminiRpmLimit_Setting);
                        _geminiWindowRpmLimiter = new SlidingRateLimiter(effectiveRpm, TimeSpan.FromMinutes(1));

                        // Gọi hàm xử lý file mới có sử dụng limiter của tab
                        await ProcessFilesWithWindowRpmLimiterAsync(files, translatedFolder, token);
                    }
                    else
                    {
                        // Nếu không bật RPM, chạy song song như các provider khác
                        await ProcessFilesConcurrent(files, translatedFolder, currentProvider, token);
                    }
                }

                else if (currentProvider == ApiProviderType.AIOLauncher)
                {
                    await ProcessFilesWithAioLauncher(files, translatedFolder, token);
                }
                else
                {
                    await ProcessFilesConcurrent(files, translatedFolder, currentProvider, token);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => {
                    lblStatus.Text = $"Lỗi: {ex.Message}";
                    ChatHistory.Add(new ChatMessage { MessageContent = $"Lỗi nghiêm trọng trong Process:\n{ex}", IsUserInput = false });
                });
            }
        }
        private async Task<string> ProcessWithAioLauncher(string content, CancellationToken token)
        {
            var user = App.User;
            if (user == null || !user.IsLoggedIn) return "Lỗi: Cần đăng nhập.";
            long remainingChars = user.AioCharacterLimit - user.AioCharactersUsedToday;
            if (content.Length > remainingChars)
            {
                return $"Lỗi: Không đủ ký tự dịch. Yêu cầu: {content.Length:N0}, còn lại: {remainingChars:N0}.";
            }
            string language = (cmbLanguage.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Tiếng Việt";
            
            // Get the effective Gemini prompts (systemInstruction and userPrompt)
            GetEffectiveGeminiPrompts(content, out string systemInstruction, out string userPrompt);
            
            var (startSuccess, startResponse, startError) = await ApiService.StartAioTranslationJobAsync(systemInstruction, userPrompt, language);

            if (!startSuccess)
            {
                return $"Lỗi khi tạo job: {startError}";
            }
            user.AioCharactersUsedToday += content.Length;
            string sessionId = startResponse.SessionId;
            Dispatcher.Invoke(() => lblStatus.Text = $"Đã gửi yêu cầu. Đang chờ kết quả từ server (Session: ...{sessionId.Substring(sessionId.Length - 6)})...");
            while (!token.IsCancellationRequested)
            {
                var result = await ApiService.GetAioJobResultAsync(sessionId);

                switch (result.Status?.ToLower())
                {
                    case "completed":
                        Dispatcher.Invoke(() => lblStatus.Text = "Hoàn tất dịch từ server.");
                        return result.TranslatedContent;
                    case "failed":
                        return $"Lỗi từ server: {result.ErrorMessage}";
                    case "pending":
                    case "processing":
                        await Task.Delay(20000, token); 
                        break;
                    default:
                        return $"Trạng thái không xác định từ server: {result.Status}";
                }
            }

            return "Lỗi: Người dùng đã hủy tác vụ.";
        }

        private async Task ProcessFilesWithAioLauncher(string[] files, string translatedFolder, CancellationToken token)
        {
            var user = App.User;
            if (user == null) return;

            long totalCharsInFiles = 0;
            try
            {
                foreach (var file in files)
                {
                    totalCharsInFiles += new FileInfo(file).Length;
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage { MessageContent = $"Lỗi khi tính toán kích thước file: {ex.Message}" }));
                return;
            }

            long remainingChars = user.AioCharacterLimit - user.AioCharactersUsedToday;
            if (totalCharsInFiles > remainingChars)
            {
                Dispatcher.Invoke(() => CustomMessageBox.Show($"Không đủ ký tự để dịch toàn bộ các file.\nYêu cầu: {totalCharsInFiles:N0} ký tự\nCòn lại: {remainingChars:N0} ký tự", "Lỗi Giới Hạn", MessageBoxButton.OK, MessageBoxImage.Warning));
                return;
            }

            int successCount = 0;
            int failCount = 0;

            for (int i = 0; i < files.Length; i++)
            {
                if (token.IsCancellationRequested) break;

                string file = files[i];
                string fileName = Path.GetFileName(file);

                Dispatcher.Invoke(() => lblStatus.Text = $"Đang xử lý file {i + 1}/{files.Length}: {fileName}");

                try
                {
                    string content = await File.ReadAllTextAsync(file, token);
                    string translatedContent = await ProcessWithAioLauncher(content, token);

                    if (translatedContent.StartsWith("Lỗi:"))
                    {
                        failCount++;
                        Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage { MessageContent = $"Lỗi dịch file {fileName}: {translatedContent}" }));
                    }
                    else
                    {
                        // Lưu file đã dịch (logic tương tự các hàm khác)
                        string cleanContent = CleanGenericContent(translatedContent);
                        string chapterTitle = GetChapterFromText(cleanContent);
                        string finalFileNameBase = SanitizeFileName(string.IsNullOrWhiteSpace(chapterTitle) || chapterTitle == "UnknownChapter" ? Path.GetFileNameWithoutExtension(file) : chapterTitle);
                        string finalOutputPath = Path.Combine(translatedFolder, $"{finalFileNameBase}.txt");
                        int counter = 1;
                        while (File.Exists(finalOutputPath))
                        {
                            finalOutputPath = Path.Combine(translatedFolder, $"{finalFileNameBase}_{counter++}.txt");
                        }
                        await File.WriteAllTextAsync(finalOutputPath, cleanContent, Encoding.UTF8, token);
                        successCount++;
                        Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage { MessageContent = $"Lưu thành công: {Path.GetFileName(finalOutputPath)}" }));
                    }
                }
                catch (Exception ex)
                {
                    failCount++;
                    Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage { MessageContent = $"Lỗi nghiêm trọng khi xử lý file {fileName}: {ex.Message}" }));
                }
                if (i < files.Length - 1)
                {
                    await Task.Delay(1000, token);
                }
            }

            Dispatcher.Invoke(() => lblStatus.Text = $"Hoàn tất. Thành công: {successCount}, Lỗi: {failCount}.");
        }
        private async Task ProcessFilesConcurrent(string[] files, string translatedFolder, ApiProviderType provider, CancellationToken token)
        {
            var tasks = new List<Task<bool>>();
            int total = files.Length;
            // Lấy độ trễ từ cài đặt tương ứng của provider
            int delayBetweenFilesMs = provider switch
            {
                ApiProviderType.Gemini => _geminiDelayBetweenFileStartsMs_Setting,
                ApiProviderType.OpenRouter => _openRouterDelayBetweenFileStartsMs_Setting,
                _ => _chutesDelayBetweenFileStartsMs_Setting
            };

            for (int i = 0; i < files.Length; i++)
            {
                if (_stopProcessingDueToLimit || token.IsCancellationRequested)
                {
                    break;
                }

                string currentFile = files[i];
                await _fileProcessingSemaphore.WaitAsync(token);
                Task<bool> task = Task.Run(async () =>
                {
                    try
                    {
                        return await ProcessSingleFileWithPermissionCheckAsync(currentFile, translatedFolder, provider, token);
                    }
                    finally
                    {
                        _fileProcessingSemaphore.Release();
                    }
                }, token);

                tasks.Add(task);

                string nameOnly = Path.GetFileName(currentFile);
                Dispatcher.Invoke(() => {
                    ChatHistory.Add(new ChatMessage { MessageContent = $"Đưa file '{nameOnly}' vào hàng đợi.\n" });
                });

                if (i < files.Length - 1 && delayBetweenFilesMs > 0)
                {
                    try { await Task.Delay(delayBetweenFilesMs, token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            // Phần xử lý kết quả cuối cùng giữ nguyên
            if (tasks.Any())
            {
                var results = await Task.WhenAll(tasks);
                int success = results.Count(r => r);
                int failed = tasks.Count - success;
                Dispatcher.Invoke(() => {
                    lblStatus.Text = failed == 0 ? $"Hoàn tất dịch {total} file" : $"Hoàn tất với {success} OK, {failed} lỗi.";
                    ChatHistory.Add(new ChatMessage { MessageContent = $"\nKết quả: {success}/{total} OK, {failed} lỗi.\n",
                        IsUserInput = false,
                        IsExpanded = true
                    });
                });
            }
        }
        private async Task<bool> ProcessSingleFileWithPermissionCheckAsync(string filePath, string translatedFolder, ApiProviderType provider, CancellationToken token)
        {
            var originalFileName = Path.GetFileName(filePath);

            var (canProceed, reason) = await CanExecuteTranslationAsync(provider);
            if (!canProceed)
            {
                // <<< BẮT ĐẦU THAY ĐỔI QUAN TRỌNG >>>
                // Thay vì hủy, chúng ta bật cờ lên và thông báo
                _stopProcessingDueToLimit = true;

                Dispatcher.Invoke(() => {
                    lblStatus.Text = "Đã đạt giới hạn. Đang hoàn thành các tác vụ cuối cùng...";
                    ChatHistory.Add(new ChatMessage { MessageContent = $"\n!!! ĐÃ ĐẠT GIỚI HẠN. Sẽ không gửi thêm file mới. Đang chờ hoàn thành các file đã gửi. Lý do: {reason}\n" });
                });

                // Tác vụ này thất bại, nhưng không hủy các tác vụ khác
                return false;
                // <<< KẾT THÚC THAY ĐỔI QUAN TRỌNG >>>
            }

            // BƯỚC 2: Nếu được phép, gọi hàm dịch tương ứng với provider (giữ nguyên)
            switch (provider)
            {
                case ApiProviderType.OpenRouter:
                    return await ProcessFileOpenRouter(filePath, translatedFolder, token);
                case ApiProviderType.Gemini:
                    return await ProcessFileGemini(filePath, translatedFolder, token);
                case ApiProviderType.ChutesAI:
                default:
                    return await ProcessFileChutes(filePath, translatedFolder, token);
            }
        }
        private async Task SaveOriginalFileOnFailureAsync(string originalFileName, string originalFileNameNoExt, string translatedFolder, string originalContent, string failureReason, CancellationToken token)
        {
            string failedFilesFolder = Path.Combine(translatedFolder, "Dịch Lỗi Lưu Gốc");
            Directory.CreateDirectory(failedFilesFolder);
            string saveFileNameBase = SanitizeFileName($"{originalFileNameNoExt}_gốc_lỗi_Gemini");
            if (string.IsNullOrWhiteSpace(saveFileNameBase))
            {
                saveFileNameBase = $"error_Gemini_orig_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            }
            string outputPath = Path.Combine(failedFilesFolder, $"{saveFileNameBase}.txt");
            int counter = 1;
            string tempPath = outputPath;
            string tempFileNameBase = saveFileNameBase;
            string tempExtension = ".txt";
            while (File.Exists(tempPath))
            {
                string baseWithoutSuffix = tempFileNameBase;
                var match = Regex.Match(tempFileNameBase, @"^(.*)_(\d+)$");
                if (match.Success && int.TryParse(match.Groups[2].Value, out _))
                {
                    baseWithoutSuffix = match.Groups[1].Value;
                }
                tempPath = Path.Combine(failedFilesFolder, $"{baseWithoutSuffix}_{counter++}{tempExtension}");
            }
            outputPath = tempPath;
            try
            {
                string contentToSave = originalContent;
                await File.WriteAllTextAsync(outputPath, contentToSave, Encoding.UTF8, token);
                Dispatcher.Invoke(() =>
                {
                    ChatHistory.Add(new ChatMessage { MessageContent =$"Đã lưu file gốc của '{originalFileName}' vào '{Path.GetFileName(outputPath)}' do dịch thất bại.\n" });
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    ChatHistory.Add(new ChatMessage { MessageContent =$"Lỗi khi cố gắng lưu file gốc của '{originalFileName}': {ex.Message}\n" });
                });
            }
        }
        private async Task<bool> ProcessFileGemini(string filePath, string translatedFolder, CancellationToken token)
        {
            if (token.IsCancellationRequested) return false;
            var originalFileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
            var originalFileName = Path.GetFileName(filePath);
            Dispatcher.Invoke(() => lblStatus.Text = $"Đọc file : {originalFileName}");
            string inputText;
            try
            {
                inputText = await File.ReadAllTextAsync(filePath, token);
            }
            catch (OperationCanceledException) { if (token.IsCancellationRequested) throw; return false; }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { lblStatus.Text = $"Lỗi đọc : {originalFileName}"; ChatHistory.Add(new ChatMessage {MessageContent = $"Lỗi đọc file {originalFileName}: {ex.Message}\n", IsUserInput = false, IsExpanded = true }); });
                return false;
            }

            if (token.IsCancellationRequested) return false;
            Dispatcher.Invoke(() => { lblStatus.Text = $"Dịch Gemini: {originalFileName}"; ChatHistory.Add(new ChatMessage {MessageContent = $"Bắt đầu dịch file : {originalFileName}...\n", IsUserInput = false, IsExpanded = true }); });

            string translatedContent;
            try
            {
                translatedContent = await TranslateLongTextWithGeminiApi(originalFileName, inputText, token);
            }
            catch (GeminiRequestLimitExceededException ex)
            {
                Dispatcher.Invoke(() =>
                {
                    lblStatus.Text = $"Dừng do giới hạn: {originalFileName}";
                    ChatHistory.Add(new ChatMessage {MessageContent = $"Dịch file {originalFileName} bị dừng: {ex.Message}.\n", IsUserInput = false, IsExpanded = true });
                });
                return false;
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    Dispatcher.Invoke(() => { lblStatus.Text = $"Dịch {originalFileName} hủy."; ChatHistory.Add(new ChatMessage {MessageContent = $"Dịch file {originalFileName} đã bị hủy.\n", IsUserInput = false, IsExpanded = true }); });
                }
                return false;
            }
            catch (Exception ex)
            {
                // --- BẮT ĐẦU MÃ GỠ LỖI ---
                // 1. Ghi lại thông tin lỗi gốc và luồng hiện tại vào cửa sổ Output của Visual Studio.
                System.Diagnostics.Debug.WriteLine("================ LỖI XẢY RA TRÊN LUỒNG NỀN ================");
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Thread ID: {System.Threading.Thread.CurrentThread.ManagedThreadId}");
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Tên file đang xử lý: {originalFileName}");
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Loại Exception (LỖI GỐC): {ex.GetType().FullName}");
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Thông báo (LỖI GỐC): {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Stack Trace:\n{ex.StackTrace}");
                System.Diagnostics.Debug.WriteLine("============================================================");

                // 2. Tách thông báo lỗi ra một biến riêng biệt TRƯỚC KHI gọi Dispatcher
                string statusMessageForUi = $"Lỗi dịch (ex): {originalFileName}";
                // Sử dụng ex.GetType().Name để biết chính xác loại lỗi là gì
                string chatMessageContent = $"Lỗi nghiêm trọng khi dịch file {originalFileName} ({ex.GetType().Name}): {ex.Message}\n";

                // 3. Thực hiện cập nhật UI một cách an toàn
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        lblStatus.Text = statusMessageForUi;
                        ChatHistory.Add(new ChatMessage { MessageContent = chatMessageContent, IsUserInput = false, IsExpanded = true });
                    }
                    catch (Exception uiEx)
                    {
                        // Nếu chính việc cập nhật UI gây ra lỗi, chúng ta cũng sẽ thấy nó
                        System.Diagnostics.Debug.WriteLine($"[DEBUG] LỖI KHI CẬP NHẬT UI: {uiEx.Message}");
                    }
                });

                // --- KẾT THÚC MÃ GỠ LỖI ---

                if (!string.IsNullOrWhiteSpace(inputText))
                {
                    // Phương thức này đã có sẵn Dispatcher.Invoke an toàn bên trong
                    await SaveOriginalFileOnFailureAsync(originalFileName, originalFileNameNoExt, translatedFolder, inputText, $"Lỗi nghiêm trọng: {ex.Message}", token);
                }
                return false;
            }

            if (token.IsCancellationRequested) return false;

            bool isTranslationError = string.IsNullOrWhiteSpace(translatedContent) ||
                                      translatedContent.StartsWith("Lỗi Gemini", StringComparison.OrdinalIgnoreCase) ||
                                      translatedContent.StartsWith("Gemini: Nội dung bị chặn", StringComparison.OrdinalIgnoreCase);

            if (isTranslationError)
            {
                if (!string.IsNullOrWhiteSpace(inputText))
                {
                    Dispatcher.Invoke(() =>
                    {
                        lblStatus.Text = $"Lỗi Dịch Gemini: {originalFileName}";
                        ChatHistory.Add(new ChatMessage {MessageContent = $"Lỗi Dịch Gemini: {originalFileName} - {translatedContent?.Substring(0, Math.Min(translatedContent.Length, 150)) ?? "Nội dung lỗi rỗng"}. Đang lưu file gốc...\n", IsUserInput = false, IsExpanded = true });
                    });
                    await SaveOriginalFileOnFailureAsync(originalFileName, originalFileNameNoExt, translatedFolder, inputText, translatedContent ?? "Lỗi không rõ nội dung", token);
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        lblStatus.Text = $"Lỗi Dịch Gemini (file rỗng): {originalFileName}";
                        ChatHistory.Add(new ChatMessage {MessageContent = $"Lỗi Dịch Gemini: {originalFileName} nhưng file gốc rỗng. {translatedContent?.Substring(0, Math.Min(translatedContent.Length, 150)) ?? "Nội dung lỗi rỗng"}\n", IsUserInput = false, IsExpanded = true });
                    });
                }
                return false;
            }

            string cleanContent = CleanGenericContent(translatedContent);
            string chapterTitle = GetChapterFromText(cleanContent);
            string finalFileNameBase = SanitizeFileName(string.IsNullOrWhiteSpace(chapterTitle) || chapterTitle == "UnknownChapter" ? originalFileNameNoExt : chapterTitle);
            if (string.IsNullOrWhiteSpace(finalFileNameBase))
            {
                finalFileNameBase = $"dich_Gemini_file_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            }

            string finalOutputPath = Path.Combine(translatedFolder, $"{finalFileNameBase}.txt");
            int counter = 1;
            string tempPath = finalOutputPath;
            string tempFileNameBase = finalFileNameBase;
            string tempExtension = Path.GetExtension(finalOutputPath);
            if (string.IsNullOrEmpty(tempExtension)) tempExtension = ".txt";

            while (File.Exists(tempPath))
            {
                string baseWithoutSuffix = tempFileNameBase;
                var match = Regex.Match(tempFileNameBase, @"^(.*)_(\d+)$");
                if (match.Success && int.TryParse(match.Groups[2].Value, out _))
                {
                    baseWithoutSuffix = match.Groups[1].Value;
                }
                tempPath = Path.Combine(translatedFolder, $"{baseWithoutSuffix}_{counter++}{tempExtension}");
            }
            finalOutputPath = tempPath;

            try
            {
                await File.WriteAllTextAsync(finalOutputPath, cleanContent, Encoding.UTF8, token);
                Dispatcher.Invoke(() => { lblStatus.Text = $"Hoàn thành Gemini: {Path.GetFileName(finalOutputPath)}"; ChatHistory.Add(new ChatMessage {MessageContent = $"Lưu Gemini OK: {Path.GetFileName(finalOutputPath)}\n", IsUserInput = false, IsExpanded = true }); });
            }
            catch (OperationCanceledException) { if (token.IsCancellationRequested) throw; return false; }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { lblStatus.Text = $"Lỗi lưu Gemini: {Path.GetFileName(finalOutputPath)}"; ChatHistory.Add(new ChatMessage {MessageContent = $"Lỗi lưu file dịch Gemini {Path.GetFileName(finalOutputPath)}: {ex.Message}\n", IsUserInput = false, IsExpanded = true }); });
                return false;
            }
            return true;
        }
        private async Task ProcessFilesWithWindowRpmLimiterAsync(string[] files, string translatedFolder, CancellationToken token)
        {
            var tasks = new List<Task<bool>>();
            int total = files.Length;

            for (int i = 0; i < files.Length; i++)
            {
                if (_stopProcessingDueToLimit || token.IsCancellationRequested)
                {
                    break;
                }

                string currentFile = files[i];

                // Đợi để lấy "suất" gửi file trong phút này (limiter của riêng tab)
                await _geminiWindowRpmLimiter.WaitAsync(token);

                // Sau khi có suất, khởi chạy tác vụ xử lý file
                tasks.Add(Task.Run(() => ProcessFileGemini(currentFile, translatedFolder, token), token));

                string nameOnly = Path.GetFileName(currentFile);
                Dispatcher.Invoke(() => {
                    ChatHistory.Add(new ChatMessage { MessageContent = $"Đưa file '{nameOnly}' vào hàng đợi (RPM Limiter).\n" });
                });
            }

            // Đợi tất cả các tác vụ đã khởi chạy hoàn thành và báo cáo kết quả
            if (tasks.Any())
            {
                var results = await Task.WhenAll(tasks);
                int success = results.Count(r => r);
                int failed = tasks.Count - success;
                Dispatcher.Invoke(() => {
                    lblStatus.Text = failed == 0 ? $"Hoàn tất dịch {total} file" : $"Hoàn tất với {success} OK, {failed} lỗi.";
                    ChatHistory.Add(new ChatMessage
                    {
                        MessageContent = $"\nKết quả: {success}/{total} OK, {failed} lỗi.\n",
                        IsUserInput = false,
                        IsExpanded = true
                    });
                });
            }
        }
        private async Task ProcessFilesTimedBatch(string[] files, string translatedFolder, CancellationToken token)
        {
            var fileQueue = new Queue<string>(files);
            var allLaunchedTasks = new List<Task<bool>>();
            int totalFiles = files.Length;
            int filesLaunched = 0;

            while (fileQueue.Count > 0 && !token.IsCancellationRequested)
            {
                var batchStopwatch = Stopwatch.StartNew();
                int filesToLaunchThisTick = Math.Min(_geminiRpmLimit_Setting, fileQueue.Count);

                Dispatcher.Invoke(() => lblStatus.Text = $"Chuẩn bị gửi lô {filesToLaunchThisTick} file... ({filesLaunched}/{totalFiles})");

                for (int i = 0; i < filesToLaunchThisTick; i++)
                {
                    if (token.IsCancellationRequested) break;
                    if (fileQueue.TryDequeue(out string fileToProcess))
                    {
                        filesLaunched++;
                        allLaunchedTasks.Add(Task.Run(() => ProcessFileGemini(fileToProcess, translatedFolder, token), token));
                    }
                }

                if (token.IsCancellationRequested) break;

                Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage { MessageContent =$"\nĐã gửi {filesToLaunchThisTick} file lên API. Tổng số đã gửi: {filesLaunched}/{totalFiles}.\n"})) ;

                if (fileQueue.Count > 0)
                {
                    batchStopwatch.Stop();
                    long elapsedMs = batchStopwatch.ElapsedMilliseconds;
                    long waitTimeMs = 60000 - elapsedMs;

                    if (waitTimeMs > 0)
                    {
                        Dispatcher.Invoke(() => lblStatus.Text = $"Đợi {waitTimeMs / 1000.0:F1}s trước khi gửi lô tiếp theo...");
                        try
                        {
                            await Task.Delay((int)waitTimeMs, token);
                        }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }

            if (allLaunchedTasks.Any())
            {
                Dispatcher.Invoke(() => lblStatus.Text = "Tất cả các lô đã được gửi. Đang chờ các file hoàn thành...");
                var finalResults = await Task.WhenAll(allLaunchedTasks);
                int totalSuccess = finalResults.Count(r => r);
                int totalFailed = finalResults.Length - totalSuccess;
                Dispatcher.Invoke(() => {
                    lblStatus.Text = $"Hoàn tất toàn bộ. Thành công: {totalSuccess}, Lỗi: {totalFailed}.";
                    ChatHistory.Add(new ChatMessage { MessageContent =$"\n--- KẾT THÚC --- \nTổng thành công: {totalSuccess}/{totalFiles}, Lỗi: {totalFailed}.\n" });
                });
            }
        }

        // --- Chutes AI Translation Methods ---
        private async Task<string> TranslateLongTextWithChutesApi(string fileNameForLog, string inputText, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(inputText)) return string.Empty;

            if (inputText.Length <= _chutesDirectSendThreshold_Setting)
            {
                Dispatcher.Invoke(() => lblStatus.Text = $"Dịch trực tiếp: {Path.GetFileName(fileNameForLog)} ({inputText.Length} chars)");
                // Retry logic for single chunk/direct send
                string translation = "";
                for (int r = 1; r <= _chutesMaxContentRetries_Setting + 1; r++)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    translation = await TranslateSingleWithChutesApi(fileNameForLog, inputText, 0, token);
                    bool isError = translation.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase);
                    if (isError)
                    {
                        if (r <= _chutesMaxContentRetries_Setting)
                        {
                            Dispatcher.Invoke(() => lblStatus.Text = $"File {Path.GetFileName(fileNameForLog)} bị lỗi. Thử lại {r}/{_chutesMaxContentRetries_Setting}...");
                            await Task.Delay(_chutesContentRetryBaseDelayMs_Setting * r, token);
                            continue;
                        }
                        else break;
                    }
                    else break;
                }
                return translation;
            }

            Dispatcher.Invoke(() => lblStatus.Text = $"Chia chunks: {Path.GetFileName(fileNameForLog)} ({inputText.Length} chars)");
            List<string> chunks = SplitTextIntoChunks(inputText, _chutesChunkSize_Setting);
            if (!chunks.Any()) return "[Lỗi: Không thể chia chunks.]";

            int totalChunks = chunks.Count;
            var chunkTasks = new List<Task<string>>();

            for (int i = 0; i < totalChunks; i++)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                if (i > 0 && _chutesInterChunkDelayMs_Setting > 0) await Task.Delay(_chutesInterChunkDelayMs_Setting, token);

                string currentChunk = chunks[i];
                string chunkFileName = $"{Path.GetFileNameWithoutExtension(fileNameForLog)}_chunk{i + 1}";
                chunkTasks.Add(Task.Run(async () => await TranslateSingleWithChutesApi(chunkFileName, currentChunk, i + 1, token), token));
            }

            string[] translatedChunks = await Task.WhenAll(chunkTasks);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < totalChunks; i++)
            {
                if (translatedChunks[i].StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Lỗi chunk {i + 1}/{totalChunks}: {translatedChunks[i]}";
                }
                sb.Append(translatedChunks[i]);
                if (i < totalChunks - 1) sb.Append("\n\n");
            }

            Dispatcher.Invoke(() => lblStatus.Text = $"Hoàn tất ghép chunks cho: {Path.GetFileName(fileNameForLog)}");
            return sb.ToString().Trim();
        }

        private async Task<string> TranslateSingleWithChutesApi(string fileNameForLog, string input, int chunkNumber, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            if (string.IsNullOrWhiteSpace(CurrentAppSettings.ChutesApiKey)) return "Lỗi: Chutes AI API Key chưa được cấu hình.";

            await _apiSemaphore.WaitAsync(token);
            try
            {
                string systemPrompt = GetEffectiveChutesSystemPrompt();
                var requestData = new
                {
                    model = CurrentAppSettings.SelectedChutesApiModel,
                    messages = new[] {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = input }
                    },
                    stream = false,
                    temperature = 0.0,
                    max_tokens = 10000
                };
                string jsonPayload = JsonConvert.SerializeObject(requestData);

                for (int attempt = 1; attempt <= _chutesMaxApiRetries_Setting + 1; attempt++)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);

                    string chunkInfo = chunkNumber > 0 ? $" (Chunk {chunkNumber})" : "";
                    Dispatcher.Invoke(() => lblStatus.Text = $"Đang dịch: {fileNameForLog}{chunkInfo}, thử lần {attempt - 1}/{_chutesMaxApiRetries_Setting}...");

                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://llm.chutes.ai/v1/chat/completions") { Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json") };
                    request.Headers.Add("Authorization", $"Bearer {CurrentAppSettings.ChutesApiKey}");

                    try
                    {
                        HttpResponseMessage response = await httpClient.SendAsync(request, token);
                        string responseBody = await response.Content.ReadAsStringAsync(token);

                        if (!response.IsSuccessStatusCode)
                        {
                            if (attempt <= _chutesMaxApiRetries_Setting)
                            {
                                await Task.Delay(_chutesApiRetryBaseDelayMs_Setting * attempt, token);
                                continue;
                            }
                            return $"Lỗi API Chutes: HTTP {(int)response.StatusCode}.";
                        }

                        dynamic parsedBody = JsonConvert.DeserializeObject(responseBody);
                        if (parsedBody?.error != null)
                        {
                            return $"Lỗi API Chutes (JSON): {parsedBody.error.message}";
                        }
                        if (parsedBody?.choices == null || parsedBody.choices.Count == 0)
                        {
                            if (attempt <= _chutesMaxApiRetries_Setting)
                            {
                                await Task.Delay(_chutesApiRetryBaseDelayMs_Setting * attempt, token);
                                continue;
                            }
                            return "Lỗi API Chutes: Phản hồi không chứa nội dung hợp lệ.";
                        }

                        return parsedBody.choices[0].message.content.ToString();
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                    {
                        if (token.IsCancellationRequested) throw new OperationCanceledException();
                        if (attempt <= _chutesMaxApiRetries_Setting)
                        {
                            await Task.Delay(_chutesApiRetryBaseDelayMs_Setting * attempt, token);
                            continue;
                        }
                        return $"Lỗi mạng hoặc Timeout khi gọi API Chutes: {ex.Message}";
                    }
                }
                return $"Lỗi API Chutes: Không thể nhận phản hồi sau {_chutesMaxApiRetries_Setting} lần thử.";
            }
            finally
            {
                _apiSemaphore.Release();
            }
        }

        // --- Gemini Translation Methods ---
        private async Task<string> TranslateLongTextWithGeminiApi(string fileNameForLog, string inputText, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(inputText)) return string.Empty;

            int directSendThreshold = _geminiDirectSendThreshold_Setting;
            int chunkSize = _geminiChunkSize_Setting;
            int interChunkDelay = _geminiInterChunkDelayMs_Setting;

            // ===== DIRECT SEND (không chia chunk) =====
            if (inputText.Length <= directSendThreshold)
            {
                Dispatcher.Invoke(() => lblStatus.Text = $"Dịch trực tiếp: {Path.GetFileName(fileNameForLog)} ({inputText.Length} chars)");
                string translation = await TranslateSingleWithGeminiApi(fileNameForLog, inputText, token);
                return translation;
            }

            // ===== CHIA CHUNKS =====
            Dispatcher.Invoke(() => lblStatus.Text = $"Chia chunks: {Path.GetFileName(fileNameForLog)} ({inputText.Length} chars)");
            List<string> chunks = SplitTextIntoChunks(inputText, chunkSize);

            if (!chunks.Any())
            {
                Dispatcher.Invoke(() => lblStatus.Text = $"Lỗi chia chunks: {Path.GetFileName(fileNameForLog)}");
                return "[Lỗi: Không thể chia chunks.]";
            }

            int totalChunks = chunks.Count;
            Dispatcher.Invoke(() =>
            {
                lblStatus.Text = $"Dịch {totalChunks} chunks cho {Path.GetFileName(fileNameForLog)}...";
                ChatHistory.Add(new ChatMessage
                {
                    MessageContent = $"📦 Chia {Path.GetFileName(fileNameForLog)} thành {totalChunks} chunks. Bắt đầu dịch...\n",
                    IsUserInput = false,
                    IsExpanded = true
                });
            });

            // ===== DỰA VÀO CHẾ ĐỘ: SEQUENTIAL hoặc PARALLEL =====
            bool useChunkIsolation = CurrentAppSettings.GeminiEnableChunkIsolation;
            string[] translatedChunks;

            if (useChunkIsolation)
            {
                // ===== SEQUENTIAL MODE (Chunk Isolation Enabled) =====
                // Mỗi chunk gửi lần lượt, đảm bảo mỗi chunk dùng key riêng
                Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage
                {
                    MessageContent = $"ℹ️ Chế độ Chunk Isolation: Mỗi chunk sẽ dùng API key riêng, gửi tuần tự.\n",
                    IsUserInput = false
                }));

                translatedChunks = new string[totalChunks];

                for (int i = 0; i < totalChunks; i++)
                {
                    if (token.IsCancellationRequested)
                        throw new OperationCanceledException(token);

                    // Delay giữa các chunks (trừ chunk đầu tiên)
                    if (i > 0 && interChunkDelay > 0)
                    {
                        try
                        {
                            Dispatcher.Invoke(() => lblStatus.Text = $"Đợi {interChunkDelay}ms trước chunk {i + 1}/{totalChunks}...");
                            await Task.Delay(interChunkDelay, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                    }

                    string currentChunk = chunks[i];
                    string chunkFileName = $"{SanitizeFileName(Path.GetFileNameWithoutExtension(fileNameForLog))}_chunk{i + 1}_of_{totalChunks}";

                    Dispatcher.Invoke(() => lblStatus.Text = $"Dịch chunk {i + 1}/{totalChunks} của {Path.GetFileName(fileNameForLog)}...");

                    try
                    {
                        string translatedChunk = await TranslateSingleWithGeminiApi(chunkFileName, currentChunk, token);
                        translatedChunks[i] = translatedChunk;

                        // Log progress
                        Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage
                        {
                            MessageContent = $"✓ Chunk {i + 1}/{totalChunks} hoàn thành ({currentChunk.Length} chars)\n",
                            IsUserInput = false
                        }));
                    }
                    catch (GeminiRequestLimitExceededException)
                    {
                        // Giới hạn đạt - throw để stop
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Log lỗi nhưng tiếp tục (hoặc throw tùy logic)
                        Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage
                        {
                            MessageContent = $"✗ Lỗi chunk {i + 1}/{totalChunks}: {ex.Message}\n",
                            IsUserInput = false
                        }));

                        translatedChunks[i] = $"[Lỗi chunk {i + 1}: {ex.Message}]";
                    }
                }
            }
            else
            {
                // ===== PARALLEL MODE (Chunk Isolation Disabled) =====
                // Các chunks có thể gửi song song, có thể dùng chung key
                Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage
                {
                    MessageContent = $"ℹ️ Chế độ Parallel: Chunks sẽ gửi song song, có thể chia sẻ keys.\n",
                    IsUserInput = false
                }));

                var chunkTasks = new List<Task<string>>();

                for (int i = 0; i < totalChunks; i++)
                {
                    if (token.IsCancellationRequested)
                        throw new OperationCanceledException(token);

                    // Delay giữa việc KHỞI CHẠY các chunks (không phải giữa việc hoàn thành)
                    if (i > 0 && interChunkDelay > 0)
                    {
                        try
                        {
                            await Task.Delay(interChunkDelay, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                    }

                    string currentChunk = chunks[i];
                    string chunkFileName = $"{SanitizeFileName(Path.GetFileNameWithoutExtension(fileNameForLog))}_chunk{i + 1}_of_{totalChunks}";

                    // Khởi chạy task async
                    chunkTasks.Add(Task.Run(async () =>
                        await TranslateSingleWithGeminiApi(chunkFileName, currentChunk, token),
                        token));
                }

                // Đợi tất cả chunks hoàn thành
                try
                {
                    translatedChunks = await Task.WhenAll(chunkTasks);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (GeminiRequestLimitExceededException)
                {
                    throw;
                }
            }

            if (token.IsCancellationRequested)
                throw new OperationCanceledException(token);

            // ===== GHÉP KẾT QUẢ =====
            StringBuilder sb = new StringBuilder();
            int successChunks = 0;
            int failedChunks = 0;

            for (int i = 0; i < totalChunks; i++)
            {
                string translatedChunk = translatedChunks[i];

                // Kiểm tra lỗi
                if (translatedChunk.StartsWith("Lỗi Gemini", StringComparison.OrdinalIgnoreCase) ||
                    translatedChunk.StartsWith("Gemini: Nội dung bị chặn", StringComparison.OrdinalIgnoreCase) ||
                    translatedChunk.StartsWith("[Lỗi chunk", StringComparison.OrdinalIgnoreCase))
                {
                    failedChunks++;

                    // Quyết định: return lỗi ngay hoặc tiếp tục ghép
                    // Option 1: Return ngay (strict)
                    // return $"Lỗi chunk {i + 1}/{totalChunks} của {Path.GetFileName(fileNameForLog)}: {translatedChunk}";

                    // Option 2: Ghi lỗi vào result và tiếp tục (lenient)
                    sb.Append($"\n\n[!!! CHUNK {i + 1} LỖI: {translatedChunk}]\n\n");
                }
                else
                {
                    successChunks++;
                    sb.Append(translatedChunk);

                    // Thêm separator giữa các chunks (trừ chunk cuối)
                    if (i < totalChunks - 1 && !string.IsNullOrEmpty(translatedChunk))
                    {
                        sb.Append("\n\n");
                    }
                }
            }

            // Log kết quả
            Dispatcher.Invoke(() =>
            {
                lblStatus.Text = $"Hoàn tất {Path.GetFileName(fileNameForLog)}: {successChunks}/{totalChunks} chunks OK";

                if (failedChunks > 0)
                {
                    ChatHistory.Add(new ChatMessage
                    {
                        MessageContent = $"⚠️ Hoàn tất {Path.GetFileName(fileNameForLog)}: {successChunks}/{totalChunks} OK, {failedChunks} lỗi.\n",
                        IsUserInput = false,
                        IsExpanded = true
                    });
                }
                else
                {
                    ChatHistory.Add(new ChatMessage
                    {
                        MessageContent = $"✅ Hoàn tất {Path.GetFileName(fileNameForLog)}: Tất cả {totalChunks} chunks OK.\n",
                        IsUserInput = false,
                        IsExpanded = true
                    });
                }
            });

            return sb.ToString().Trim();
        }
        private async Task<string> TranslateLongTextWithOpenRouterApi(string fileNameForLog, string inputText, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(inputText)) return string.Empty;

            if (inputText.Length <= _openRouterDirectSendThreshold_Setting)
            {
                Dispatcher.Invoke(() => lblStatus.Text = $"Dịch trực tiếp OpenRouter: {Path.GetFileName(fileNameForLog)} ({inputText.Length} chars)");
                return await TranslateSingleWithOpenRouterApi(fileNameForLog, inputText, 0, token);
            }

            // Nếu văn bản dài: chia chunk
            Dispatcher.Invoke(() => lblStatus.Text = $"Chia chunks OpenRouter: {Path.GetFileName(fileNameForLog)} ({inputText.Length} chars)");
            List<string> chunks = SplitTextIntoChunks(inputText, _openRouterChunkSize_Setting);
            if (!chunks.Any()) return "[Lỗi: Không thể chia văn bản thành các chunks.]";

            int totalChunks = chunks.Count;
            var chunkTasks = new List<Task<string>>();
            Dispatcher.Invoke(() => lblStatus.Text = $"Đang dịch {totalChunks} chunks bằng OpenRouter cho {Path.GetFileName(fileNameForLog)}...");

            for (int i = 0; i < totalChunks; i++)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);

                if (i > 0 && _openRouterInterChunkDelayMs_Setting > 0)
                    await Task.Delay(_chutesInterChunkDelayMs_Setting, token);

                string currentChunk = chunks[i];
                string chunkFileNameForLog = $"{SanitizeFileName(Path.GetFileNameWithoutExtension(fileNameForLog))}_chunk{i + 1}";

                chunkTasks.Add(Task.Run(() =>
                    TranslateSingleWithOpenRouterApi(chunkFileNameForLog, currentChunk, i + 1, token), token));
            }

            string[] translatedChunks = await Task.WhenAll(chunkTasks);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < totalChunks; i++)
            {
                string translatedChunk = translatedChunks[i];
                if (translatedChunk.StartsWith("Lỗi API OpenRouter", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Lỗi chunk {i + 1}/{totalChunks} của file {Path.GetFileName(fileNameForLog)}: {translatedChunk}";
                }
                sb.Append(translatedChunk);
                if (i < totalChunks - 1 && !string.IsNullOrEmpty(translatedChunk))
                {
                    sb.Append("\n\n");
                }
            }

            Dispatcher.Invoke(() => lblStatus.Text = $"Hoàn tất ghép chunks cho: {Path.GetFileName(fileNameForLog)}");
            return sb.ToString().Trim();
        }
        private async Task<string> TranslateSingleWithOpenRouterApi(string fileNameForLog, string input, int chunkNumber, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            if (string.IsNullOrWhiteSpace(CurrentAppSettings.OpenRouterApiKey)) return "Lỗi API OpenRouter: API Key chưa được cấu hình.";

            await _apiSemaphore.WaitAsync(token);
            try
            {
                string systemPrompt = GetEffectiveChutesSystemPrompt();

                var messages = new List<object>();
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                {
                    messages.Add(new { role = "system", content = systemPrompt });
                }
                messages.Add(new { role = "user", content = input });

                var requestData = new
                {
                    model = CurrentAppSettings.SelectedOpenRouterModel,
                    messages = messages
                };
                string jsonPayload = JsonConvert.SerializeObject(requestData);


                // Giả sử dùng chung cài đặt retry của ChutesAI
                for (int attempt = 1; attempt <= _openRouterMaxApiRetries_Setting + 1; attempt++)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);

                    string chunkInfoForLog = chunkNumber > 0 ? $" (Chunk {chunkNumber})" : "";
                    Dispatcher.Invoke(() => lblStatus.Text = $"Dịch OpenRouter: {fileNameForLog}{chunkInfoForLog}, thử lần {attempt - 1}/{_chutesMaxApiRetries_Setting}...");

                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
                    {
                        Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                    };

                    request.Headers.Add("Authorization", $"Bearer {CurrentAppSettings.OpenRouterApiKey}");
                    if (!string.IsNullOrWhiteSpace(CurrentAppSettings.HttpReferer))
                    {
                        request.Headers.Add("HTTP-Referer", CurrentAppSettings.HttpReferer);
                    }
                    if (!string.IsNullOrWhiteSpace(CurrentAppSettings.XTitle))
                    {
                        request.Headers.Add("X-Title", CurrentAppSettings.XTitle);
                    }

                    try
                    {
                        HttpResponseMessage response = await httpClient.SendAsync(request, token);
                        string responseBody = await response.Content.ReadAsStringAsync(token);

                        if (!response.IsSuccessStatusCode)
                        {
                            string errorDetail = $"HTTP {(int)response.StatusCode}. Phản hồi: {responseBody.Substring(0, Math.Min(responseBody.Length, 150))}";
                            if (attempt <= _chutesMaxApiRetries_Setting)
                            {
                                await Task.Delay(_chutesApiRetryBaseDelayMs_Setting * attempt, token);
                                continue;
                            }
                            return $"Lỗi API OpenRouter: {errorDetail}";
                        }

                        dynamic parsedBody = JsonConvert.DeserializeObject(responseBody);
                        if (parsedBody?.error != null)
                        {
                            return $"Lỗi API OpenRouter (JSON): {parsedBody.error.message}";
                        }
                        if (parsedBody?.choices == null || parsedBody.choices.Count == 0 || parsedBody.choices[0].message?.content == null)
                        {
                            if (attempt <= _openRouterMaxApiRetries_Setting)
                            {
                                await Task.Delay(_openRouterApiRetryBaseDelayMs_Setting * attempt, token);
                                continue;
                            }
                            return "Lỗi API OpenRouter: Phản hồi không chứa nội dung hợp lệ.";
                        }

                        return parsedBody.choices[0].message.content.ToString();
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                    {
                        if (token.IsCancellationRequested) throw new OperationCanceledException();
                        if (attempt <= _openRouterMaxContentRetries_Setting)
                        {
                            await Task.Delay(_openRouterContentRetryBaseDelayMs_Setting * attempt, token);
                            continue;
                        }
                        return $"Lỗi API OpenRouter: Lỗi mạng hoặc Timeout - {ex.Message}";
                    }
                }
                return $"Lỗi API OpenRouter: Không thể nhận phản hồi sau {_chutesMaxApiRetries_Setting} lần thử.";
            }
            finally
            {
                _apiSemaphore.Release();
            }
        }

        private async Task<string> TranslateSingleWithGeminiApi(string fileNameForLog, string inputTextForTranslation, CancellationToken token)
        {
            ApiKeyInfo apiKeyInfo = null;
            string originalKey = null;
            int retryWithSameKey = 0;
            int retryWithDifferentKey = 0;
            const int MAX_RETRIES_SAME_KEY = 3;
            const int MAX_RETRIES_DIFFERENT_KEY = 2;

            try
            {
                // Mỗi cửa sổ có 1 GeminiApiKeyManager riêng, khởi tạo từ key của chính cửa sổ đó.
                if (_geminiKeyManager == null)
                {
                    string runtimeKeys = GetGeminiApiKeysForCurrentWindow();

                    if (string.IsNullOrWhiteSpace(runtimeKeys) &&
                        string.IsNullOrWhiteSpace(CurrentAppSettings.GeminiApiKey))
                    {
                        throw new InvalidOperationException("Chưa cấu hình Gemini API key cho cửa sổ hiện tại.");
                    }

                    _geminiKeyManager = new GeminiApiKeyManager(CurrentAppSettings, runtimeKeys);
                }

                apiKeyInfo = await _geminiKeyManager.GetNextAvailableKeyAsync(token, fileNameForLog);
                originalKey = apiKeyInfo.Key;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage
                {
                    MessageContent = $"Lỗi lấy API key: {ex.Message}\n"
                }));

                // Nếu là lỗi limit hoặc không có key thì cho propagate để dừng đúng cách
                if (ex is GeminiRequestLimitExceededException || ex is InvalidOperationException)
                    throw;

                return $"Lỗi lấy API key: {ex.Message}";
            }

            string geminiApiKey = apiKeyInfo.Key;
            GetEffectiveGeminiPrompts(inputTextForTranslation, out string systemInstructionText, out string userPromptText);
            string modelName = CurrentAppSettings.SelectedGeminiApiModel;

            // ===== MAIN RETRY LOOP =====
            while (retryWithSameKey < MAX_RETRIES_SAME_KEY && !token.IsCancellationRequested)
            {
                string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={geminiApiKey}";

                // Build request payload
                var requestPayload = new
                {
                    contents = new[]
                    {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userPromptText } }
                }
            },
                    systemInstruction = string.IsNullOrWhiteSpace(systemInstructionText)
                        ? null
                        : new { parts = new[] { new { text = systemInstructionText } } },
                    generationConfig = BuildGeminiGenerationConfig()
                };

                string jsonPayload = JsonConvert.SerializeObject(requestPayload, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.None
                });

                int maxRetries = _geminiMaxApiRetries_Setting;
                int baseDelayMs = _geminiApiRetryBaseDelayMs_Setting;
                int attempt = 1;

                HttpResponseMessage response = null;

                try
                {
                    // Cập nhật UI
                    Dispatcher.Invoke(() =>
                    {
                        string keyInfo = $" [Key: {apiKeyInfo.GetMaskedKey()}]";
                        string retryInfo = retryWithSameKey > 0 ? $" (Retry #{retryWithSameKey})" : "";
                        lblStatus.Text = $"Dịch Gemini: {Path.GetFileName(fileNameForLog)}{keyInfo}{retryInfo}";
                    });

                    using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                    {
                        Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                    };

                    response = await httpClient.SendAsync(request, token);
                    string body = await response.Content.ReadAsStringAsync(token);

                    if (token.IsCancellationRequested)
                        throw new OperationCanceledException(token);

                    // ===== PARSE RESPONSE =====
                    JObject parsedBody = null;
                    try
                    {
                        parsedBody = JObject.Parse(body);
                    }
                    catch (JsonException jsonEx)
                    {
                        Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage
                        {
                            MessageContent = $"Lỗi parse JSON từ Gemini cho {fileNameForLog}: {jsonEx.Message}\n"
                        }));

                        retryWithSameKey++;
                        if (retryWithSameKey < MAX_RETRIES_SAME_KEY)
                        {
                            await Task.Delay(baseDelayMs * retryWithSameKey, token);
                            continue; // Retry với cùng key
                        }

                        return $"Lỗi Gemini: Không thể parse JSON sau {MAX_RETRIES_SAME_KEY} lần thử.";
                    }

                    // ===== CHECK FOR API ERRORS =====
                    if (parsedBody?["error"] != null)
                    {
                        var errorObj = parsedBody["error"];
                        string apiErrorMessage = errorObj["message"]?.ToString() ?? "Lỗi không xác định";
                        string apiErrorCode = errorObj["code"]?.ToString() ?? "N/A";
                        string apiErrorStatus = errorObj["status"]?.ToString() ?? "UNKNOWN";

                        Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage
                        {
                            MessageContent = $"Lỗi Gemini API cho {fileNameForLog}: Code {apiErrorCode}, Status {apiErrorStatus} - {apiErrorMessage}\n"
                        }));

                        // ===== AUTO RETRY WITH DIFFERENT KEY ON 429 OR QUOTA =====
                        bool isRateLimitError = apiErrorCode == "429" ||
                                               apiErrorStatus == "RESOURCE_EXHAUSTED" ||
                                               apiErrorMessage.Contains("quota", StringComparison.OrdinalIgnoreCase);

                        if (isRateLimitError && CurrentAppSettings.GeminiEnableAutoRetryWithNewKey)
                        {
                            // Đánh dấu key hiện tại tạm thời unavailable
                            await _geminiKeyManager.MarkKeyTemporarilyUnavailableAsync(
                                geminiApiKey,
                                TimeSpan.FromMinutes(1));

                            // Thử lấy key khác
                            if (retryWithDifferentKey < MAX_RETRIES_DIFFERENT_KEY)
                            {
                                try
                                {
                                    var alternativeKey = await _geminiKeyManager.GetAlternativeKeyForRetryAsync(
                                        geminiApiKey,
                                        token);

                                    geminiApiKey = alternativeKey.Key;
                                    apiKeyInfo = alternativeKey;
                                    retryWithDifferentKey++;
                                    retryWithSameKey = 0; // Reset same-key retry counter

                                    Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage
                                    {
                                        MessageContent = $"🔄 Retry với key khác [{alternativeKey.GetMaskedKey()}] cho {fileNameForLog} do rate limit...\n",
                                        IsUserInput = false,
                                        IsExpanded = true
                                    }));

                                    await Task.Delay(baseDelayMs, token);
                                    continue; // Retry với key mới
                                }
                                catch (InvalidOperationException)
                                {
                                    // Không có key thay thế
                                    return $"Lỗi Gemini: Tất cả keys đều bị rate limit. {apiErrorMessage}";
                                }
                            }
                        }

                        // ===== NORMAL RETRY LOGIC =====
                        bool isRetryableError = apiErrorCode == "500" ||
                                               apiErrorStatus == "INTERNAL" ||
                                               apiErrorStatus == "UNAVAILABLE" ||
                                               apiErrorStatus == "INVALID_ARGUMENT" ||
                                               apiErrorStatus == "FAILED_PRECONDITION";

                        if (isRetryableError)
                        {
                            retryWithSameKey++;
                            if (retryWithSameKey < MAX_RETRIES_SAME_KEY)
                            {
                                int delayMs = baseDelayMs * retryWithSameKey;
                                await Task.Delay(delayMs, token);
                                continue;
                            }
                        }

                        return $"Lỗi Gemini API: Code {apiErrorCode}, Status {apiErrorStatus} - {apiErrorMessage.Substring(0, Math.Min(apiErrorMessage.Length, 150))}";
                    }

                    // ===== CHECK HTTP STATUS =====
                    if (!response.IsSuccessStatusCode)
                    {
                        Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage
                        {
                            MessageContent = $"Lỗi HTTP Gemini cho {fileNameForLog}: {(int)response.StatusCode} {response.ReasonPhrase}\n"
                        }));

                        bool isRetryableHttpStatus =
                            response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                            response.StatusCode == System.Net.HttpStatusCode.InternalServerError ||
                            response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                            response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                            response.StatusCode == System.Net.HttpStatusCode.BadGateway;

                        if (isRetryableHttpStatus)
                        {
                            retryWithSameKey++;
                            if (retryWithSameKey < MAX_RETRIES_SAME_KEY)
                            {
                                int delayMs = baseDelayMs * retryWithSameKey;

                                // Check Retry-After header
                                if (response.Headers.RetryAfter != null)
                                {
                                    if (response.Headers.RetryAfter.Delta.HasValue)
                                        delayMs = (int)Math.Max(delayMs, response.Headers.RetryAfter.Delta.Value.TotalMilliseconds);
                                    else if (response.Headers.RetryAfter.Date.HasValue)
                                        delayMs = (int)Math.Max(delayMs, (response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow).TotalMilliseconds);
                                }

                                await Task.Delay(Math.Max(1000, delayMs), token);
                                continue;
                            }
                        }

                        return $"Lỗi Gemini HTTP: {(int)response.StatusCode} ({response.ReasonPhrase})";
                    }

                    // ===== EXTRACT CONTENT =====
                    var candidate = parsedBody?["candidates"]?[0];
                    string candidateText = candidate?["content"]?["parts"]?[0]?["text"]?.ToString();
                    string finishReason = candidate?["finishReason"]?.ToString();

                    if (candidateText == null)
                    {
                        if (GEMINI_FINISH_REASON_MAX_TOKENS.Equals(finishReason, StringComparison.OrdinalIgnoreCase))
                        {
                            retryWithSameKey++;
                            if (retryWithSameKey < MAX_RETRIES_SAME_KEY)
                            {
                                await Task.Delay(baseDelayMs * retryWithSameKey, token);
                                continue;
                            }
                            return $"Lỗi Gemini: MAX_TOKENS sau {MAX_RETRIES_SAME_KEY} lần thử.";
                        }

                        if (GEMINI_FINISH_REASON_SAFETY.Equals(finishReason, StringComparison.OrdinalIgnoreCase) ||
                            GEMINI_FINISH_REASON_OTHER.Equals(finishReason, StringComparison.OrdinalIgnoreCase))
                        {
                            return $"Gemini: Nội dung bị chặn/kết thúc sớm do: {finishReason}.";
                        }

                        retryWithSameKey++;
                        if (retryWithSameKey < MAX_RETRIES_SAME_KEY)
                        {
                            await Task.Delay(baseDelayMs * retryWithSameKey, token);
                            continue;
                        }

                        return $"Lỗi Gemini: Không có nội dung hợp lệ. FinishReason: {finishReason ?? "N/A"}";
                    }

                    // ===== SUCCESS =====
                    response?.Dispose();
                    return candidateText;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    response?.Dispose();
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    response?.Dispose();

                    if (token.IsCancellationRequested)
                        throw new OperationCanceledException();

                    Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage
                    {
                        MessageContent = $"Lỗi network/timeout cho {fileNameForLog}: {ex.Message}\n"
                    }));

                    retryWithSameKey++;
                    if (retryWithSameKey < MAX_RETRIES_SAME_KEY)
                    {
                        await Task.Delay(baseDelayMs * retryWithSameKey * 2, token);
                        continue;
                    }

                    return $"Lỗi Gemini: Network/Timeout - {ex.Message}";
                }
            }

            return $"Lỗi Gemini: Không thể hoàn thành sau {MAX_RETRIES_SAME_KEY} lần thử với cùng key và {retryWithDifferentKey} lần thử với keys khác.";
        }
        private object BuildGeminiGenerationConfig()
        {
            var config = new Dictionary<string, object>
            {
                ["temperature"] = _geminiTemperature_Setting,
                ["maxOutputTokens"] = _geminiMaxOutputTokens_Setting
            };

            if (_geminiEnableThinkingBudget_Setting && _geminiThinkingBudget_Setting > 0)
            {
                config["thinkingConfig"] = new { thinkingBudget = _geminiThinkingBudget_Setting };
            }

            return config;
        }

        #endregion

        #region File and Content Processing

        private async Task<bool> ProcessFileChutes(string filePath, string translatedFolder, CancellationToken token)
        {
            if (token.IsCancellationRequested) return false;
            var oFNNoExt = Path.GetFileNameWithoutExtension(filePath);
            var oFN = Path.GetFileName(filePath);
            Dispatcher.Invoke(() => lblStatus.Text = $"Đọc file : {oFN}");
            string iTxt;
            try { iTxt = await File.ReadAllTextAsync(filePath, token); }
            catch (OperationCanceledException) { if (token.IsCancellationRequested) throw; return false; }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { lblStatus.Text = $"Lỗi đọc : {oFN}"; ChatHistory.Add(new ChatMessage { MessageContent =$"Lỗi đọc file {oFN}: {ex.Message}\n",
                    IsUserInput = false,
                    IsExpanded = true
                }); });
                return false;
            }

            if (token.IsCancellationRequested) return false;
            Dispatcher.Invoke(() => { lblStatus.Text = $"Dịch : {oFN}"; ChatHistory.Add(new ChatMessage { MessageContent =$"Dịch file : {oFN}...\n",
                IsUserInput = false,
                IsExpanded = true
            }); });

            string trContent;
            try
            {
                trContent = await TranslateLongTextWithChutesApi(oFN, iTxt, token);
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    Dispatcher.Invoke(() => { lblStatus.Text = $"Dịch{oFN} hủy."; ChatHistory.Add(new ChatMessage { MessageContent =$"Dịch file {oFN} đã bị hủy.\n",
                        IsUserInput = false,
                        IsExpanded = true
                    }); });
                }
                return false;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { lblStatus.Text = $"Lỗi dịch (ex): {oFN}"; ChatHistory.Add(new ChatMessage { MessageContent =$"Lỗi nghiêm trọng khi dịch file {oFN}: {ex.Message}\n",
                    IsUserInput = false,
                    IsExpanded = true
                }); });
                return false;
            }

            if (token.IsCancellationRequested) return false;

            bool isTranslationError = string.IsNullOrWhiteSpace(trContent) ||
                                      trContent.StartsWith("Lỗi") ||
                                      trContent.StartsWith("API Lỗi") ||
                                      trContent.StartsWith("API trả về lỗi") ||
                                      trContent.StartsWith(": Không nhận được phản hồi hợp lệ") ||
                                      trContent.StartsWith("Thất bại ");

            if (isTranslationError)
            {
                Dispatcher.Invoke(() =>
                {
                    lblStatus.Text = $"Lỗi Dịch: {oFN}";
                    ChatHistory.Add(new ChatMessage {MessageContent = $"Lỗi Dịch: {oFN} - {trContent?.Substring(0, Math.Min(trContent.Length, 150)) ?? "Nội dung lỗi rỗng"}\n", IsUserInput = false, IsExpanded = true });
                });
                return false;
            }

            string cleanContentGeneric = CleanGenericContent(trContent);
            string finalCleanContent = CleanThinkTags(cleanContentGeneric);

            string chapTitle = GetChapterFromText(finalCleanContent);
            string fFNBase = SanitizeFileName(string.IsNullOrWhiteSpace(chapTitle) || chapTitle == "UnknownChapter" ? oFNNoExt : chapTitle);
            if (string.IsNullOrWhiteSpace(fFNBase)) fFNBase = $"dich_Chutes_file_{Guid.NewGuid().ToString("N").Substring(0, 6)}";

            string fOutPath = Path.Combine(translatedFolder, $"{fFNBase}.txt");
            int cnt = 1;
            string tmpPath = fOutPath;
            string tmpFNBase = fFNBase;
            string tmpExt = Path.GetExtension(fOutPath);
            if (string.IsNullOrEmpty(tmpExt)) tmpExt = ".txt";

            while (File.Exists(tmpPath))
            {
                string baseNoSuffix = tmpFNBase;
                var match = Regex.Match(tmpFNBase, @"^(.*)_(\d+)$");
                if (match.Success && int.TryParse(match.Groups[2].Value, out _))
                {
                    baseNoSuffix = match.Groups[1].Value;
                }
                tmpPath = Path.Combine(translatedFolder, $"{baseNoSuffix}_{cnt++}{tmpExt}");
            }
            fOutPath = tmpPath;

            try
            {
                await File.WriteAllTextAsync(fOutPath, finalCleanContent, Encoding.UTF8, token);
                Dispatcher.Invoke(() => { lblStatus.Text = $"Hoàn thành : {Path.GetFileName(fOutPath)}"; ChatHistory.Add(new ChatMessage { MessageContent =$"Lưu OK: {Path.GetFileName(fOutPath)}\n" }); });
            }
            catch (OperationCanceledException) { if (token.IsCancellationRequested) throw; return false; }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { lblStatus.Text = $"Lỗi lưu : {Path.GetFileName(fOutPath)}"; ChatHistory.Add(new ChatMessage { MessageContent =$"Lỗi lưu file dịch {Path.GetFileName(fOutPath)}: {ex.Message}\n",
                    IsUserInput = false,
                    IsExpanded = true
                }); });
                return false;
            }
            return true;
        }

        private async Task<bool> SaveTranslatedFile(string originalFilePath, string translatedFolder, string content, CancellationToken token)
        {
            string originalFileNameNoExt = Path.GetFileNameWithoutExtension(originalFilePath);
            string chapterTitle = GetChapterFromText(content);
            string finalFileNameBase = SanitizeFileName(chapterTitle != "UnknownChapter" ? chapterTitle : originalFileNameNoExt);

            string outputPath = Path.Combine(translatedFolder, $"{finalFileNameBase}.txt");
            int counter = 1;
            while (File.Exists(outputPath))
            {
                outputPath = Path.Combine(translatedFolder, $"{finalFileNameBase}_{counter++}.txt");
            }

            try
            {
                await File.WriteAllTextAsync(outputPath, content, Encoding.UTF8, token);
                Dispatcher.Invoke(() => {
                    lblStatus.Text = $"Hoàn thành: {Path.GetFileName(outputPath)}";
                    ChatHistory.Add(new ChatMessage { MessageContent =$"Lưu OK: {Path.GetFileName(outputPath)}\n" });
                });
                return true;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => {
                    lblStatus.Text = $"Lỗi lưu: {Path.GetFileName(outputPath)}";
                    ChatHistory.Add(new ChatMessage { MessageContent =$"Lỗi lưu file {Path.GetFileName(outputPath)}: {ex.Message}\n" });
                });
                return false;
            }
        }

        private async Task SaveOriginalFileOnFailureAsync(string originalFilePath, string translatedFolder, string originalContent, string failureReason, CancellationToken token)
        {
            try
            {
                string failedFilesFolder = Path.Combine(translatedFolder, "Dịch Lỗi Lưu Gốc");
                Directory.CreateDirectory(failedFilesFolder);
                string saveFileName = $"{Path.GetFileNameWithoutExtension(originalFilePath)}_gốc_lỗi.txt";
                string outputPath = Path.Combine(failedFilesFolder, SanitizeFileName(saveFileName));

                await File.WriteAllTextAsync(outputPath, originalContent, Encoding.UTF8, token);
                Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage { MessageContent =$"Đã lưu file gốc của '{Path.GetFileName(originalFilePath)}' vào thư mục lỗi.\n" }));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ChatHistory.Add(new ChatMessage { MessageContent =$"Lỗi khi cố gắng lưu file gốc bị lỗi: {ex.Message}\n" }));
            }
        }

        #endregion

        private readonly Dictionary<string, string> _openRouterTechnicalToDisplayMap = new Dictionary<string, string>
{
    { "tngtech/deepseek-r1t-chimera:free", "deepseek-r1t-chimera" },
    { "deepseek/deepseek-r1-0528:free", "deepseek-r1-0528" }
};
        private string GetTechnicalModelName(string displayName)
        {
            // Tìm key (tên kỹ thuật) dựa trên value (tên hiển thị)
            var mapping = _openRouterTechnicalToDisplayMap.FirstOrDefault(kvp => kvp.Value == displayName);
            if (mapping.Key != null)
            {
                return mapping.Key;
            }
            return displayName; // Là model tùy chỉnh
        }
        private string GetDisplayModelName(string technicalName)
        {
            if (_openRouterTechnicalToDisplayMap.TryGetValue(technicalName, out string displayName))
            {
                return displayName;
            }
            return technicalName; // Là model tùy chỉnh
        }
        #region Helpers and Prompt Generation

        private string GetEffectiveChutesSystemPrompt()
        {
            // Lấy ngôn ngữ đích được chọn từ ComboBox
            string targetLanguage = "Tiếng Việt"; // Giá trị mặc định
            if (cmbLanguage.SelectedItem is ComboBoxItem selectedItem)
            {
                targetLanguage = selectedItem.Content.ToString();
            }

            string baseSystemPrompt = "";
            if (UseSystemBasePrompt)
            {
                switch (_selectedGenre)
                {
                    case "Huyền Huyễn Tiên Hiệp":
                        baseSystemPrompt = $"Dịch đoạn truyện tiên hiệp sau sang {targetLanguage} cổ trang, huyền huyễn. Quy tắc: 1) Tên riêng (nhân vật, địa danh, môn phái, thần thú - linh thú), công pháp, tu vi, danh hiệu, bí tịch, thuật pháp, pháp bảo, đan dược: BẮT BUỘC giữ nguyên Hán-Việt, viết hoa chữ cái đầu mỗi âm tiết. TUYỆT ĐỐI không dịch nghĩa đen. 2) Văn miêu tả, lời thoại thường: Dịch thuần {targetLanguage} tự nhiên, mượt mà. 3) Đại từ: CHỈ SỬ DỤNG: ngươi, ta, hắn, nàng, y, sư tỷ, sư tôn, sư đệ, sư muội, chàng, muội muội, tỷ tỷ, huynh đệ, đại ca, sư tổ, tiên tử, thánh nữ, ma nữ, tiểu yêu, đệ, huynh, tỷ, thúc thúc, tẩu tẩu, bá bá, lão gia, nha đầu, thiếp, tiểu thư, công tử, các vị, bản tọa, lão phu, tại hạ, đạo hữu, tiền bối, vãn bối, tiểu bối, tiểu tử, cô nương, nô tỳ. CẤM TUYỆT ĐỐI: tôi, anh, em, mày, tao, ông, bà, bố, mẹ, và các đại từ hiện đại khác. 4) Tiêu đề chương ở đầu truyện định dạng 'Chương X: tiêu đề chương', nếu không có thì không tự thêm. 5) Chỉ xuất văn bản thô, không markdown, không giải thích.";
                        break;
                    case "Ngôn Tình":
                        baseSystemPrompt = $"Bạn là một dịch giả chuyên nghiệp, am hiểu văn học thể loại ngôn tình. Hãy dịch đoạn văn sau từ tiếng Trung sang {targetLanguage}, đặc biệt là TOÀN BỘ tên nhân vật PHẢI dịch Hán việt và Không dịch nghĩa đen các thành phần của tên riêng nhân vật. Dịch đúng và thống nhất các đại từ nhân xưng. Tiêu đề chương ở đầu truyện định dạng 'Chương X: tiêu đề chương', nếu không có thì không tự thêm. Chỉ xuất văn bản thuần túy, không markdown, không chú thích.";
                        break;
                    case "Đô Thị":
                        baseSystemPrompt = $"Bạn là một dịch giả chuyên nghiệp, am hiểu văn học thể loại đô thị hiện đại. Hãy dịch đoạn văn sau từ tiếng Trung thành {targetLanguage}, KHÔNG ĐƯỢC DỊCH SÓT TỪ TIẾNG TRUNG NÀO, đặc biệt là TOÀN BỘ tên nhân vật PHẢI dịch Hán việt và Không dịch nghĩa đen các thành phần của tên riêng nhân vật. Dịch đúng và thống nhất các đại từ nhân xưng. Tiêu đề chương ở đầu truyện định dạng 'Chương X: tiêu đề chương', nếu không có thì không tự thêm. Chỉ xuất văn bản thuần túy, không markdown, không chú thích.";
                        break;
                    default:
                        baseSystemPrompt = $"Bạn là một dịch giả chuyên nghiệp. Hãy dịch đoạn văn sau từ tiếng Trung sang {targetLanguage} một cách chính xác và tự nhiên. Chỉ xuất văn bản thuần túy.";
                        break;
                }
            }

            return !string.IsNullOrWhiteSpace(UserSuppliedPrompt)
                ? (UseSystemBasePrompt ? baseSystemPrompt + "\n\n" + UserSuppliedPrompt : UserSuppliedPrompt)
                : baseSystemPrompt;
        }

        public void GetEffectiveGeminiPrompts(string inputTextForTranslation, out string systemInstruction, out string userPrompt)
        {
            string targetLanguage = _selectedLanguage;

            systemInstruction = "";
            string userPreamble = $"Đây là câu truyện giả tưởng, không liên quan đến tôn giáo hay chính trị hay bạo lực - tình dục. Hãy dịch câu truyện tiếng Trung sau đây sang {targetLanguage} theo các hướng dẫn đã cung cấp, Chỉ xuất bản dịch dạng văn bản thuần túy. Không dùng in đậm, in nghiêng, markdown, gạch đầu dòng, không chú thích, không giải thích.:\n\n";

            if (UseSystemBasePrompt)
            {
                switch (_selectedGenre)
                {
                    case "Huyền Huyễn Tiên Hiệp":
                        systemInstruction = $"Dịch đoạn truyện tiên hiệp sau sang {targetLanguage} cổ trang, huyền huyễn. Quy tắc: 1) Xác định các cụm từ Tên riêng (nhân vật, địa danh, môn phái), công pháp, tu vi: BẮT BUỘC giữ nguyên Hán-Việt, viết hoa chữ cái đầu mỗi âm tiết. 2) Văn miêu tả, lời thoại: Dịch thuần {targetLanguage} tự nhiên. 3) Đại từ: Dịch đúng các đại từ sau nếu có: ngươi, ta, hắn, nàng, y, sư tỷ, sư tôn, sư đệ, sư muội, chàng, muội muội, tỷ tỷ, mẫu thân, huynh đệ, đại ca, sư tổ, tiên tử, thánh nữ, ma nữ, tiểu yêu, đệ, huynh, tỷ, thúc thúc, tẩu tẩu, bá bá, lão gia, nha đầu, thiếp, tiểu thư, công tử, các vị, bản tọa, lão phu, tại hạ, đạo hữu, tiền bối, vãn bối, tiểu bối, tiểu tử, cô nương, nô tỳ, đệ tử. CẤM TUYỆT ĐỐI sử dụng: tôi, anh, em, mày, tao, ông, bà, bố, mẹ, chú, bác, cha, dì, cháu, bạn, cậu, má và các đại từ hiện đại khác. 4) Tiêu đề chương định dạng 'Chương X: tiêu đề chương', nếu văn bản gửi lên không có thì không tự ý thêm. 5) Chỉ xuất bản văn bản đã dịch.";
                        break;
                    case "Ngôn Tình":
                        systemInstruction = $"Bạn là một dịch giả chuyên nghiệp, am hiểu văn học thể loại ngôn tình. Hãy dịch đoạn văn sau từ tiếng Trung sang {targetLanguage}, TOÀN BỘ tên nhân vật PHẢI dịch Hán việt và Không dịch nghĩa đen. Dịch đúng và thống nhất các đại từ nhân xưng. Chỉ xuất văn bản thuần túy. Không dùng markdown, không chú thích. Định dạng tiêu đề: 'Chương X: tiêu đề chương'.";
                        userPreamble = $"Dịch câu chuyện ngôn tình này sang {targetLanguage}:\n\n";
                        break;
                    case "Đô Thị":
                        systemInstruction = $"Bạn là một dịch giả chuyên nghiệp, am hiểu văn học thể loại đô thị hiện đại. Hãy dịch đoạn văn sau từ tiếng Trung, không được dịch sót. Toàn bộ tên nhân vật PHẢI dịch Hán việt và không dịch nghĩa đen. Dịch đúng và thống nhất các đại từ nhân xưng. Chỉ xuất văn bản thuần túy. Không markdown, không chú thích. Định dạng tiêu đề: 'Chương X: tiêu đề chương'.";
                        userPreamble = $"Dịch câu chuyện đô thị này sang {targetLanguage}, mang phong cách truyện hiện đại:\n\n";
                        break;
                    default:
                        systemInstruction = $"Bạn là một trợ lý dịch thuật AI. Hãy dịch văn bản sau sang {targetLanguage} một cách chính xác và tự nhiên. Định dạng tiêu đề: 'Chương X: tiêu đề chương'.";
                        break;
                }
            }

            string finalUserContent = userPreamble + inputTextForTranslation;
            if (!string.IsNullOrWhiteSpace(UserSuppliedPrompt))
            {
                if (UseSystemBasePrompt)
                {
                    systemInstruction += "\n\n" + UserSuppliedPrompt;
                }
                else
                {
                    systemInstruction = UserSuppliedPrompt;
                    finalUserContent = inputTextForTranslation;
                }
            }
            userPrompt = finalUserContent;
        }
        private List<string> SplitTextIntoChunks(string text, int maxChunkSize) { List<string> cks = new List<string>(); if (string.IsNullOrEmpty(text)) return cks; int sIdx = 0; while (sIdx < text.Length) { int len = Math.Min(maxChunkSize, text.Length - sIdx); int eIdx = sIdx + len; if (eIdx < text.Length) { int tEIdx = eIdx - 1; bool fSP = false; int sDist = Math.Min(len / 4, 500); for (int k = 0; k < sDist; ++k) { if (tEIdx - k <= sIdx + 1) break; if (text[tEIdx - k] == '\n' && text[tEIdx - k - 1] == '\n') { eIdx = tEIdx - k + 1; fSP = true; break; } } if (!fSP) { sDist = Math.Min(len / 3, 300); for (int k = 0; k < sDist; ++k) { if (tEIdx - k <= sIdx) break; if (text[tEIdx - k] == '\n') { eIdx = tEIdx - k + 1; fSP = true; break; } } } if (!fSP) { char[] sEnds = { '.', '?', '!', '。', '？', '！', ';', '；', '"', '”', '』', '」' }; sDist = Math.Min(len / 2, 300); int splAt = text.LastIndexOfAny(sEnds, tEIdx, Math.Min(sDist, tEIdx - (sIdx + maxChunkSize / 3))); if (splAt > sIdx + (maxChunkSize / 3)) eIdx = splAt + 1; } } cks.Add(text.Substring(sIdx, eIdx - sIdx).Trim()); sIdx = eIdx; } return cks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList(); }
        private int ExtractChapterNumberFromFileName(string fileName) { if (string.IsNullOrWhiteSpace(fileName)) return int.MaxValue; var pts = new[] { @"(?:[Cc]hương|[Cc]hap(?:ter)?|[Qq]uyển|[Cc]|[Cc][Tt])\s*[:.\-_ ]?\s*(\d+)", @"^(\d+)[\s.\-_]", @"[\s.\-_](\d+)[\s.\-_]", @"[\s\-_](\d+)(?=\.txt$|$)", @"(\d+)" }; foreach (var p in pts) { var m = Regex.Match(fileName, p); if (m.Success && m.Groups.Count > 1 && m.Groups[1].Success) { if (int.TryParse(m.Groups[1].Value, out int cN)) return cN; } } return int.MaxValue; }
        private string GetChapterFromText(string text) { if (string.IsNullOrWhiteSpace(text)) return "UnknownChapter"; string p = @"(?<=\n|^)\s*(?:Chương|chương|[Cc]|CTS|cts)\s*(\d+)\s*[:.\-–—]?\s*(.+?)(?=\n|\r|$)"; Match m = Regex.Match(text, p, RegexOptions.Multiline); if (m.Success) { string cN = m.Groups[1].Value; string cName = m.Groups[2].Value.Trim(); cName = Regex.Replace(cName, @"[\*#\s]+$", "").Trim(); cName = Regex.Replace(cName, @"^\s*[:.\-–—]\s*", "").Trim(); if (string.IsNullOrWhiteSpace(cName)) return $"Chương {cN}"; return $"Chương {cN}: {cName}"; } p = @"(?<=\n|^)\s*第\s*(\d+)\s*[章章节篇]\s*(.*?)(?=\n|\r|$)"; m = Regex.Match(text, p, RegexOptions.Multiline); if (m.Success) { string cN = m.Groups[1].Value; string cName = m.Groups[2].Value.Trim(); cName = Regex.Replace(cName, @"[\*#\s]+$", "").Trim(); if (string.IsNullOrWhiteSpace(cName)) return $"Chương {cN}"; return $"Chương {cN}: {cName}"; } var fL = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim(); if (!string.IsNullOrWhiteSpace(fL) && fL.Length < 100 && fL.Length > 1 && !Regex.IsMatch(fL, @"\w\s+\w\s+\w") && !fL.EndsWith(".") && !fL.EndsWith("。") && !fL.EndsWith("?") && !fL.EndsWith("!")) { var nM = Regex.Match(fL, @"^\s*(\d+)\s*[:.\-–—]?\s*(.*)"); if (nM.Success && !string.IsNullOrWhiteSpace(nM.Groups[1].Value)) { string num = nM.Groups[1].Value; string name = nM.Groups[2].Value.Trim(); if (string.IsNullOrWhiteSpace(name)) return $"Chương {num}"; return $"Chương {num}: {name}"; } return SanitizeFileName(fL, "_"); } return "UnknownChapter"; }
        private string CleanGenericContent(string text) { if (string.IsNullOrWhiteSpace(text)) return text; text = Regex.Replace(text, @"\*\*(.*?)\*\*", "$1"); text = Regex.Replace(text, @"\*(.*?)\*", "$1"); text = Regex.Replace(text, @"__(.*?)__", "$1"); text = Regex.Replace(text, @"_(.*?)_", "$1"); text = Regex.Replace(text, @"^\s*#+\s*(.*?)[\r\n]*", "$1" + Environment.NewLine, RegexOptions.Multiline); text = Regex.Replace(text, @"\(\s*\d+\s*/\s*\d+\s*\)\s*$", "", RegexOptions.Multiline); text = Regex.Replace(text, @"\[Dịch bởi .*?\]", "", RegexOptions.IgnoreCase); text = Regex.Replace(text, @"^\s*\[.*?\]\s*$", "", RegexOptions.Multiline); text = Regex.Replace(text, @"<\|.*?\|>", "", RegexOptions.IgnoreCase); text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine); var lns = text.Split(new[] { Environment.NewLine }, StringSplitOptions.None).Select(l => l.Trim()); text = string.Join(Environment.NewLine, lns).Trim(); text = Regex.Replace(text, $"({Regex.Escape(Environment.NewLine)}){{3,}}", Environment.NewLine + Environment.NewLine); string[] phr = { "Chỉ xuất văn bản thuần túy.", "Không dùng in đậm, in nghiêng, markdown, gạch đầu dòng, không chú thích, giải thích.", "Dưới đây là bản dịch:", "Đây là bản dịch:", "Bản dịch:" }; foreach (var ph in phr) { text = Regex.Replace(text, $@"^\s*{Regex.Escape(ph)}\s*[\r\n]*", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline); text = Regex.Replace(text, $@"[\r\n]+\s*{Regex.Escape(ph)}\s*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline); } lns = text.Split(new[] { Environment.NewLine }, StringSplitOptions.None).Select(l => l.Trim()); text = string.Join(Environment.NewLine, lns).Trim(); text = Regex.Replace(text, $"({Regex.Escape(Environment.NewLine)}){{2,}}", Environment.NewLine + Environment.NewLine); return text; }

        private string CleanThinkTags(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
            string cleanedText = Regex.Replace(text, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return cleanedText.Trim();
        }
        public static string SanitizeFileName(string name, string replacement = "_") { if (string.IsNullOrWhiteSpace(name)) return Guid.NewGuid().ToString("N").Substring(0, 8); string inv = new string(Path.GetInvalidFileNameChars()); string escInv = Regex.Escape(inv); string invRegex = string.Format(@"([{0}]*\.+$)|([{0}]+)", escInv); string sName = Regex.Replace(name, invRegex, replacement); int maxL = 100; if (sName.Length > maxL) { string ext = Path.GetExtension(sName); string nameNoExt = Path.GetFileNameWithoutExtension(sName); if (!string.IsNullOrEmpty(ext) && ext.Length < 10 && nameNoExt.Length > (maxL - ext.Length)) { nameNoExt = nameNoExt.Substring(0, maxL - ext.Length - 1); sName = nameNoExt + ext; } else if (string.IsNullOrEmpty(ext) && sName.Length > maxL) sName = sName.Substring(0, maxL); if (sName.Length > maxL) sName = sName.Substring(0, maxL); } sName = sName.TrimEnd('.', ' ', replacement.Length > 0 ? replacement[0] : ' '); sName = sName.TrimStart(' ', replacement.Length > 0 ? replacement[0] : ' '); string[] resNames = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }; if (resNames.Contains(Path.GetFileNameWithoutExtension(sName).ToUpperInvariant())) sName = "renamed_" + sName; if (string.IsNullOrWhiteSpace(sName) || sName == "." || sName == "..") return Guid.NewGuid().ToString("N").Substring(0, 8); return sName; }


        #endregion

        private void txtOutput_TextChanged(object sender, TextChangedEventArgs e)
        {

        }

        private void BtnEditPrompt_Click(object sender, RoutedEventArgs e)
        {
            PromptEditorWindow promptEditor = new PromptEditorWindow(CurrentAppSettings.UserPrompt, CurrentAppSettings.UseSystemBasePrompt);
            promptEditor.Owner = this;

            if (promptEditor.ShowDialog() == true)
            {
                UserSuppliedPrompt = promptEditor.UserDefinedPromptText;
                UseSystemBasePrompt = promptEditor.UseSystemBasePromptSetting;
                CurrentAppSettings.UserPrompt = UserSuppliedPrompt;
                CurrentAppSettings.UseSystemBasePrompt = UseSystemBasePrompt;
            }
        }
        private async Task<bool> ProcessFileOpenRouter(string filePath, string translatedFolder, CancellationToken token)
        {
            if (token.IsCancellationRequested) return false;
            var originalFileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
            var originalFileName = Path.GetFileName(filePath);
            Dispatcher.Invoke(() => lblStatus.Text = $"Đọc file: {originalFileName}");
            string inputText;
            try
            {
                inputText = await File.ReadAllTextAsync(filePath, token);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { lblStatus.Text = $"Lỗi đọc: {originalFileName}"; ChatHistory.Add(new ChatMessage { MessageContent =$"Lỗi đọc file {originalFileName}: {ex.Message}\n" }); });
                return false;
            }

            if (token.IsCancellationRequested) return false;
            Dispatcher.Invoke(() => { lblStatus.Text = $"Dịch OpenRouter: {originalFileName}"; ChatHistory.Add(new ChatMessage { MessageContent =$"Bắt đầu dịch file OpenRouter: {originalFileName}...\n" }); });

            string translatedContent;
            try
            {
                // Gọi đúng hàm dịch của OpenRouter
                translatedContent = await TranslateLongTextWithOpenRouterApi(originalFileName, inputText, token);
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    Dispatcher.Invoke(() => { ChatHistory.Add(new ChatMessage { MessageContent =$"Dịch file {originalFileName} đã bị hủy.\n" }); });
                }
                return false;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { ChatHistory.Add(new ChatMessage { MessageContent =$"Lỗi nghiêm trọng khi dịch OpenRouter file {originalFileName}: {ex.Message}\n" }); });
                // Vẫn có thể lưu file gốc nếu muốn
                await SaveOriginalFileOnFailureAsync(originalFileName, originalFileNameNoExt, translatedFolder, inputText, $"Lỗi nghiêm trọng OpenRouter: {ex.Message}", token);
                return false;
            }

            if (token.IsCancellationRequested) return false;

            // Kiểm tra thông báo lỗi của OpenRouter
            bool isTranslationError = string.IsNullOrWhiteSpace(translatedContent) ||
                                      translatedContent.StartsWith("Lỗi API OpenRouter", StringComparison.OrdinalIgnoreCase);

            if (isTranslationError)
            {
                Dispatcher.Invoke(() => { ChatHistory.Add(new ChatMessage { MessageContent =$"Lỗi Dịch OpenRouter: {originalFileName} - {translatedContent?.Substring(0, Math.Min(translatedContent.Length, 150)) ?? "Nội dung lỗi rỗng"}.\n" }); });
                await SaveOriginalFileOnFailureAsync(originalFileName, originalFileNameNoExt, translatedFolder, inputText, translatedContent ?? "Lỗi OpenRouter không rõ nội dung", token);
                return false;
            }

            // Nếu thành công, lưu file dịch
            string cleanContent = CleanGenericContent(translatedContent);
            string chapterTitle = GetChapterFromText(cleanContent);
            string finalFileNameBase = SanitizeFileName(string.IsNullOrWhiteSpace(chapterTitle) || chapterTitle == "UnknownChapter" ? originalFileNameNoExt : chapterTitle);

            // Đảm bảo tên file không bị trùng
            string finalOutputPath = Path.Combine(translatedFolder, $"{finalFileNameBase}.txt");
            int counter = 1;
            while (File.Exists(finalOutputPath))
            {
                finalOutputPath = Path.Combine(translatedFolder, $"{finalFileNameBase}_{counter++}.txt");
            }

            try
            {
                await File.WriteAllTextAsync(finalOutputPath, cleanContent, Encoding.UTF8, token);
                Dispatcher.Invoke(() => { lblStatus.Text = $"Hoàn thành OpenRouter: {Path.GetFileName(finalOutputPath)}"; ChatHistory.Add(new ChatMessage { MessageContent =$"Lưu OpenRouter OK: {Path.GetFileName(finalOutputPath)}\n" }); });
                return true;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { ChatHistory.Add(new ChatMessage { MessageContent =$"Lỗi lưu file dịch OpenRouter {Path.GetFileName(finalOutputPath)}: {ex.Message}\n" }); });
                return false;
            }
        }
        private void BtnSelectFile_Click(object sender, RoutedEventArgs e) { var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Text (*.txt)|*.txt|All (*.*)|*.*", Multiselect = false }; if (ofd.ShowDialog() == true) { selectedFilePath = ofd.FileName; if (!string.IsNullOrEmpty(selectedFilePath)) { try { _isProgrammaticallyChangingText = true; using (var sr = new StreamReader(selectedFilePath)) { char[] buf = new char[2000]; int read = sr.ReadBlock(buf, 0, buf.Length); txtInput.Text = new string(buf, 0, read) + (read == buf.Length ? "..." : ""); } lblFilePath.Text = $"Sẽ dịch thư mục của: {selectedFilePath}"; } catch { selectedFilePath = ""; txtInput.Clear(); lblFilePath.Text = "Đường dẫn file..."; } finally { _isProgrammaticallyChangingText = false; } } } }
    }
}