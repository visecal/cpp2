using Newtonsoft.Json;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.DirectoryServices.ActiveDirectory;

namespace subphimv1
{
    public enum ApiProviderType
    {
        AIOLauncher,
        ChutesAI,    
        Gemini,
        OpenRouter
    }

    public class AppSettings : INotifyPropertyChanged
    {
        // --- PHẦN MỚI: ADVANCED MULTI-KEY SETTINGS ---
        private bool _geminiAdvancedMultiKeyMode = false;
        [JsonProperty]
        public bool GeminiAdvancedMultiKeyMode
        {
            get => _geminiAdvancedMultiKeyMode;
            set { _geminiAdvancedMultiKeyMode = value; OnPropertyChanged(nameof(GeminiAdvancedMultiKeyMode)); }
        }

        private int _geminiRequestsPerDayPerKey = 1500;
        [JsonProperty]
        public int GeminiRequestsPerDayPerKey
        {
            get => _geminiRequestsPerDayPerKey;
            set { _geminiRequestsPerDayPerKey = value; OnPropertyChanged(nameof(GeminiRequestsPerDayPerKey)); }
        }

        private bool _geminiEnableChunkIsolation = true;
        [JsonProperty]
        public bool GeminiEnableChunkIsolation
        {
            get => _geminiEnableChunkIsolation;
            set { _geminiEnableChunkIsolation = value; OnPropertyChanged(nameof(GeminiEnableChunkIsolation)); }
        }

        private bool _geminiEnableAutoRetryWithNewKey = true;
        [JsonProperty]
        public bool GeminiEnableAutoRetryWithNewKey
        {
            get => _geminiEnableAutoRetryWithNewKey;
            set { _geminiEnableAutoRetryWithNewKey = value; OnPropertyChanged(nameof(GeminiEnableAutoRetryWithNewKey)); }
        }
        // --- KẾT THÚC PHẦN MỚI ---
        private int _aioLauncherDirectSendThreshold = 7000;
        [JsonProperty]
        public int AioLauncherDirectSendThreshold
        {
            get => _aioLauncherDirectSendThreshold;
            set { _aioLauncherDirectSendThreshold = value; OnPropertyChanged(nameof(AioLauncherDirectSendThreshold)); }
        }

        private int _aioLauncherChunkSize = 3500;
        [JsonProperty]
        public int AioLauncherChunkSize
        {
            get => _aioLauncherChunkSize;
            set { _aioLauncherChunkSize = value; OnPropertyChanged(nameof(AioLauncherChunkSize)); }
        }
        private ApiProviderType _selectedApiProvider = ApiProviderType.OpenRouter;
        [JsonProperty]
        public ApiProviderType SelectedApiProvider
        {
            get => _selectedApiProvider;
            set { _selectedApiProvider = value; OnPropertyChanged(nameof(SelectedApiProvider)); }
        }
        [JsonIgnore] public const string DEFAULT_CHUTES_MODEL_TECHNICAL_NAME_CONST = "deepseek-ai/DeepSeek-V3-0324";
        private string _userPrompt = "";
        [JsonProperty]
        public string UserPrompt
        {
            get => _userPrompt;
            set { _userPrompt = value; OnPropertyChanged(nameof(UserPrompt)); }
        }

        private bool _useSystemBasePrompt = true;
        [JsonProperty]
        public bool UseSystemBasePrompt
        {
            get => _useSystemBasePrompt;
            set { _useSystemBasePrompt = value; OnPropertyChanged(nameof(UseSystemBasePrompt)); }
        }

        private string _selectedLanguage = "Tiếng Việt";
        [JsonProperty]
        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set { _selectedLanguage = value; OnPropertyChanged(nameof(SelectedLanguage)); }
        }
        // --- OpenRouter Settings --- 
        private string _openRouterApiKey = "";
        [JsonProperty]
        public string OpenRouterApiKey
        {
            get => _openRouterApiKey;
            set { _openRouterApiKey = value; OnPropertyChanged(nameof(OpenRouterApiKey)); }
        }

        private bool _openRouterShouldSaveApiKey = false;
        [JsonProperty]
        public bool OpenRouterShouldSaveApiKey
        {
            get => _openRouterShouldSaveApiKey;
            set { _openRouterShouldSaveApiKey = value; OnPropertyChanged(nameof(OpenRouterShouldSaveApiKey)); }
        }

        // Thêm các trường cho Header tùy chọn
        private string _httpReferer = "";
        [JsonProperty]
        public string HttpReferer
        {
            get => _httpReferer;
            set { _httpReferer = value; OnPropertyChanged(nameof(HttpReferer)); }
        }

        private string _xTitle = "";
        [JsonProperty]
        public string XTitle
        {
            get => _xTitle;
            set { _xTitle = value; OnPropertyChanged(nameof(XTitle)); }
        }

        [JsonIgnore] public const string DEFAULT_OPENROUTER_MODEL = "tngtech/deepseek-r1t-chimera:free";

        private string _selectedOpenRouterModel = DEFAULT_OPENROUTER_MODEL;
        [JsonProperty]
        public string SelectedOpenRouterModel
        {
            get => _selectedOpenRouterModel;
            set { _selectedOpenRouterModel = value; OnPropertyChanged(nameof(SelectedOpenRouterModel)); }
        }

        // Khởi tạo danh sách model mặc định với TÊN KỸ THUẬT
        private ObservableCollection<string> _availableOpenRouterModels = new ObservableCollection<string>
{
    "tngtech/deepseek-r1t-chimera:free",
    "deepseek/deepseek-r1-0528:free"
};
        [JsonProperty]
        public ObservableCollection<string> AvailableOpenRouterModels
        {
            get => _availableOpenRouterModels;
            set { _availableOpenRouterModels = value; OnPropertyChanged(nameof(AvailableOpenRouterModels)); }
        }
        private int _openRouterRateLimitPerMinute = 60; // Giá trị mặc định
        [JsonProperty]
        public int OpenRouterRateLimitPerMinute
        {
            get => _openRouterRateLimitPerMinute;
            set { _openRouterRateLimitPerMinute = value; OnPropertyChanged(nameof(OpenRouterRateLimitPerMinute)); }
        }

        private int _openRouterMaxApiRetries = 3;
        [JsonProperty]
        public int OpenRouterMaxApiRetries
        {
            get => _openRouterMaxApiRetries;
            set { _openRouterMaxApiRetries = value; OnPropertyChanged(nameof(OpenRouterMaxApiRetries)); }
        }

        private int _openRouterMaxContentRetries = 2;
        [JsonProperty]
        public int OpenRouterMaxContentRetries
        {
            get => _openRouterMaxContentRetries;
            set { _openRouterMaxContentRetries = value; OnPropertyChanged(nameof(OpenRouterMaxContentRetries)); }
        }

        private int _openRouterApiRetryBaseDelayMs = 2000;
        [JsonProperty]
        public int OpenRouterApiRetryBaseDelayMs
        {
            get => _openRouterApiRetryBaseDelayMs;
            set { _openRouterApiRetryBaseDelayMs = value; OnPropertyChanged(nameof(OpenRouterApiRetryBaseDelayMs)); }
        }

        private int _openRouterContentRetryBaseDelayMs = 5000;
        [JsonProperty]
        public int OpenRouterContentRetryBaseDelayMs
        {
            get => _openRouterContentRetryBaseDelayMs;
            set { _openRouterContentRetryBaseDelayMs = value; OnPropertyChanged(nameof(OpenRouterContentRetryBaseDelayMs)); }
        }

        private int _openRouterDelayBetweenFileStartsMs = 3000;
        [JsonProperty]
        public int OpenRouterDelayBetweenFileStartsMs
        {
            get => _openRouterDelayBetweenFileStartsMs;
            set { _openRouterDelayBetweenFileStartsMs = value; OnPropertyChanged(nameof(OpenRouterDelayBetweenFileStartsMs)); }
        }

        private int _openRouterInterChunkDelayMs = 1000;
        [JsonProperty]
        public int OpenRouterInterChunkDelayMs
        {
            get => _openRouterInterChunkDelayMs;
            set { _openRouterInterChunkDelayMs = value; OnPropertyChanged(nameof(OpenRouterInterChunkDelayMs)); }
        }

        private int _openRouterDirectSendThreshold = 3000;
        [JsonProperty]
        public int OpenRouterDirectSendThreshold
        {
            get => _openRouterDirectSendThreshold;
            set { _openRouterDirectSendThreshold = value; OnPropertyChanged(nameof(OpenRouterDirectSendThreshold)); }
        }

        private int _openRouterChunkSize = 800;
        [JsonProperty]
        public int OpenRouterChunkSize
        {
            get => _openRouterChunkSize;
            set { _openRouterChunkSize = value; OnPropertyChanged(nameof(OpenRouterChunkSize)); }
        }
        // --- ChutesAI Settings ---
        private string _chutesApiKey = "";
        [JsonProperty]
        public string ChutesApiKey
        {
            get => _chutesApiKey;
            set { _chutesApiKey = value; OnPropertyChanged(nameof(ChutesApiKey)); }
        }

        private bool _chutesShouldSaveApiKey = false;
        [JsonProperty]
        public bool ChutesShouldSaveApiKey
        {
            get => _chutesShouldSaveApiKey;
            set { _chutesShouldSaveApiKey = value; OnPropertyChanged(nameof(ChutesShouldSaveApiKey)); }
        }

        private string _selectedChutesApiModel = DEFAULT_CHUTES_MODEL_TECHNICAL_NAME_CONST;
        [JsonProperty]
        public string SelectedChutesApiModel
        {
            get => _selectedChutesApiModel;
            set { _selectedChutesApiModel = value; OnPropertyChanged(nameof(SelectedChutesApiModel)); }
        }

        private ObservableCollection<string> _availableChutesApiModels = new ObservableCollection<string> { DEFAULT_CHUTES_MODEL_TECHNICAL_NAME_CONST };
        [JsonProperty]
        public ObservableCollection<string> AvailableChutesApiModels
        {
            get => _availableChutesApiModels;
            set { _availableChutesApiModels = value; OnPropertyChanged(nameof(AvailableChutesApiModels)); }
        }

        private int _chutesRateLimitPerMinute = DEFAULT_CHUTES_RATE_LIMIT_PER_MINUTE;
        [JsonProperty]
        public int ChutesRateLimitPerMinute
        {
            get => _chutesRateLimitPerMinute;
            set { _chutesRateLimitPerMinute = value; OnPropertyChanged(nameof(ChutesRateLimitPerMinute)); }
        }

        private int _chutesMaxApiRetries = DEFAULT_CHUTES_MAX_API_RETRIES;
        [JsonProperty]
        public int ChutesMaxApiRetries
        {
            get => _chutesMaxApiRetries;
            set { _chutesMaxApiRetries = value; OnPropertyChanged(nameof(ChutesMaxApiRetries)); }
        }

        private int _chutesMaxContentRetries = DEFAULT_CHUTES_MAX_CONTENT_RETRIES;
        [JsonProperty]
        public int ChutesMaxContentRetries
        {
            get => _chutesMaxContentRetries;
            set { _chutesMaxContentRetries = value; OnPropertyChanged(nameof(ChutesMaxContentRetries)); }
        }

        private int _chutesDelayBetweenFileStartsMs = DEFAULT_CHUTES_DELAY_BETWEEN_FILE_STARTS_MS;
        [JsonProperty]
        public int ChutesDelayBetweenFileStartsMs
        {
            get => _chutesDelayBetweenFileStartsMs;
            set { _chutesDelayBetweenFileStartsMs = value; OnPropertyChanged(nameof(ChutesDelayBetweenFileStartsMs)); }
        }

        private int _chutesInterChunkDelayMs = DEFAULT_CHUTES_INTER_CHUNK_DELAY_MS;
        [JsonProperty]
        public int ChutesInterChunkDelayMs
        {
            get => _chutesInterChunkDelayMs;
            set { _chutesInterChunkDelayMs = value; OnPropertyChanged(nameof(ChutesInterChunkDelayMs)); }
        }

        private int _chutesApiRetryBaseDelayMs = DEFAULT_CHUTES_API_RETRY_BASE_DELAY_MS;
        [JsonProperty]
        public int ChutesApiRetryBaseDelayMs
        {
            get => _chutesApiRetryBaseDelayMs;
            set { _chutesApiRetryBaseDelayMs = value; OnPropertyChanged(nameof(ChutesApiRetryBaseDelayMs)); }
        }

        private int _chutesContentRetryBaseDelayMs = DEFAULT_CHUTES_CONTENT_RETRY_BASE_DELAY_MS;
        [JsonProperty]
        public int ChutesContentRetryBaseDelayMs
        {
            get => _chutesContentRetryBaseDelayMs;
            set { _chutesContentRetryBaseDelayMs = value; OnPropertyChanged(nameof(ChutesContentRetryBaseDelayMs)); }
        }

        private int _chutesDirectSendThreshold = DEFAULT_CHUTES_DIRECT_SEND_THRESHOLD;
        [JsonProperty]
        public int ChutesDirectSendThreshold
        {
            get => _chutesDirectSendThreshold;
            set { _chutesDirectSendThreshold = value; OnPropertyChanged(nameof(ChutesDirectSendThreshold)); }
        }

        private int _chutesChunkSize = DEFAULT_CHUTES_CHUNK_SIZE;
        [JsonProperty]
        public int ChutesChunkSize
        {
            get => _chutesChunkSize;
            set { _chutesChunkSize = value; OnPropertyChanged(nameof(ChutesChunkSize)); }
        }

        // --- Gemini Settings ---
        private string _geminiApiKey = "";
        [JsonProperty]
        public string GeminiApiKey
        {
            get => _geminiApiKey;
            set { _geminiApiKey = value; OnPropertyChanged(nameof(GeminiApiKey)); }
        }

        private bool _geminiShouldSaveApiKey = false;
        [JsonProperty]
        public bool GeminiShouldSaveApiKey
        {
            get => _geminiShouldSaveApiKey;
            set { _geminiShouldSaveApiKey = value; OnPropertyChanged(nameof(GeminiShouldSaveApiKey)); }
        }

        private string _selectedGeminiApiModel = DEFAULT_GEMINI_MODEL_TECHNICAL_NAME_CONST;
        [JsonProperty]
        public string SelectedGeminiApiModel
        {
            get => _selectedGeminiApiModel;
            set { _selectedGeminiApiModel = value; OnPropertyChanged(nameof(SelectedGeminiApiModel)); }
        }

        private ObservableCollection<string> _availableGeminiApiModels = new ObservableCollection<string> { DEFAULT_GEMINI_MODEL_TECHNICAL_NAME_CONST };
        [JsonProperty]
        public ObservableCollection<string> AvailableGeminiApiModels
        {
            get => _availableGeminiApiModels;
            set { _availableGeminiApiModels = value; OnPropertyChanged(nameof(AvailableGeminiApiModels)); }
        }

        private int _geminiRateLimitPerMinute = DEFAULT_GEMINI_RATE_LIMIT_PER_MINUTE;
        [JsonProperty]
        public int GeminiRateLimitPerMinute
        {
            get => _geminiRateLimitPerMinute;
            set { _geminiRateLimitPerMinute = value; OnPropertyChanged(nameof(GeminiRateLimitPerMinute)); }
        }

        private int _geminiMaxApiRetries = DEFAULT_GEMINI_MAX_API_RETRIES;
        [JsonProperty]
        public int GeminiMaxApiRetries
        {
            get => _geminiMaxApiRetries;
            set { _geminiMaxApiRetries = value; OnPropertyChanged(nameof(GeminiMaxApiRetries)); }
        }

        private int _geminiApiRetryBaseDelayMs = DEFAULT_GEMINI_API_RETRY_BASE_DELAY_MS;
        [JsonProperty]
        public int GeminiApiRetryBaseDelayMs
        {
            get => _geminiApiRetryBaseDelayMs;
            set { _geminiApiRetryBaseDelayMs = value; OnPropertyChanged(nameof(GeminiApiRetryBaseDelayMs)); }
        }

        private double _geminiTemperature = DEFAULT_GEMINI_TEMPERATURE;
        [JsonProperty]
        public double GeminiTemperature
        {
            get => _geminiTemperature;
            set { _geminiTemperature = value; OnPropertyChanged(nameof(GeminiTemperature)); }
        }

        private bool _geminiEnableThinkingBudget = false;
        [JsonProperty]
        public bool GeminiEnableThinkingBudget
        {
            get => _geminiEnableThinkingBudget;
            set { _geminiEnableThinkingBudget = value; OnPropertyChanged(nameof(GeminiEnableThinkingBudget)); }
        }

        private int _geminiThinkingBudget = DEFAULT_GEMINI_THINKING_BUDGET;
        [JsonProperty]
        public int GeminiThinkingBudget
        {
            get => _geminiThinkingBudget;
            set { _geminiThinkingBudget = value; OnPropertyChanged(nameof(GeminiThinkingBudget)); }
        }

        private int _geminiMaxOutputTokens = DEFAULT_GEMINI_MAX_OUTPUT_TOKENS;
        [JsonProperty]
        public int GeminiMaxOutputTokens
        {
            get => _geminiMaxOutputTokens;
            set { _geminiMaxOutputTokens = value; OnPropertyChanged(nameof(GeminiMaxOutputTokens)); }
        }

        private bool _geminiEnableRequestLimit = true;
        [JsonProperty]
        public bool GeminiEnableRequestLimit
        {
            get => _geminiEnableRequestLimit;
            set { _geminiEnableRequestLimit = value; OnPropertyChanged(nameof(GeminiEnableRequestLimit)); }
        }

        private int _geminiRequestLimit = DEFAULT_GEMINI_REQUEST_LIMIT_VALUE;
        [JsonProperty]
        public int GeminiRequestLimit
        {
            get => _geminiRequestLimit;
            set { _geminiRequestLimit = value; OnPropertyChanged(nameof(GeminiRequestLimit)); }
        }

        private bool _geminiEnableRpmLimit = false;
        [JsonProperty]
        public bool GeminiEnableRpmLimit
        {
            get => _geminiEnableRpmLimit;
            set { _geminiEnableRpmLimit = value; OnPropertyChanged(nameof(GeminiEnableRpmLimit)); }
        }

        // --- THUỘC TÍNH MỚI ĐƯỢC THÊM VÀO ---
        private bool _geminiUseMultiKey = false;
        [JsonProperty]
        public bool GeminiUseMultiKey
        {
            get => _geminiUseMultiKey;
            set { _geminiUseMultiKey = value; OnPropertyChanged(nameof(GeminiUseMultiKey)); }
        }
        // --- KẾT THÚC PHẦN THÊM MỚI ---

        private int _geminiRpmLimit = 60; // Giá trị mặc định là 60 RPM
        [JsonProperty]
        public int GeminiRpmLimit
        {
            get => _geminiRpmLimit;
            set { _geminiRpmLimit = value; OnPropertyChanged(nameof(GeminiRpmLimit)); }
        }

        private int _geminiDirectSendThreshold = DEFAULT_GEMINI_DIRECT_SEND_THRESHOLD;
        [JsonProperty]
        public int GeminiDirectSendThreshold
        {
            get => _geminiDirectSendThreshold;
            set { _geminiDirectSendThreshold = value; OnPropertyChanged(nameof(GeminiDirectSendThreshold)); }
        }

        private int _geminiChunkSize = DEFAULT_GEMINI_CHUNK_SIZE;
        [JsonProperty]
        public int GeminiChunkSize
        {
            get => _geminiChunkSize;
            set { _geminiChunkSize = value; OnPropertyChanged(nameof(GeminiChunkSize)); }
        }

        private int _geminiDelayBetweenFileStartsMs = DEFAULT_GEMINI_DELAY_BETWEEN_FILE_STARTS_MS;
        [JsonProperty]
        public int GeminiDelayBetweenFileStartsMs
        {
            get => _geminiDelayBetweenFileStartsMs;
            set { _geminiDelayBetweenFileStartsMs = value; OnPropertyChanged(nameof(GeminiDelayBetweenFileStartsMs)); }
        }

        private int _geminiInterChunkDelayMs = DEFAULT_GEMINI_INTER_CHUNK_DELAY_MS;
        [JsonProperty]
        public int GeminiInterChunkDelayMs
        {
            get => _geminiInterChunkDelayMs;
            set { _geminiInterChunkDelayMs = value; OnPropertyChanged(nameof(GeminiInterChunkDelayMs)); }
        }
        [JsonProperty]
        public ObservableCollection<string> FindHistory { get; set; } = new ObservableCollection<string>();

        [JsonProperty]
        public ObservableCollection<string> ReplaceHistory { get; set; } = new ObservableCollection<string>();

        // --- WhisperNet Settings ---
        private string _whisperNetLastSelectedModel = "base";
        [JsonProperty]
        public string WhisperNetLastSelectedModel
        {
            get => _whisperNetLastSelectedModel;
            set { _whisperNetLastSelectedModel = value; OnPropertyChanged(nameof(WhisperNetLastSelectedModel)); }
        }

        private string _whisperNetLastSelectedLanguage = "auto";
        [JsonProperty]
        public string WhisperNetLastSelectedLanguage
        {
            get => _whisperNetLastSelectedLanguage;
            set { _whisperNetLastSelectedLanguage = value; OnPropertyChanged(nameof(WhisperNetLastSelectedLanguage)); }
        }

        // --- Constants ---
        public const int DEFAULT_GEMINI_REQUESTS_PER_DAY_PER_KEY = 1500;
        [JsonIgnore] public static int DEFAULT_CHUTES_RATE_LIMIT_PER_MINUTE { get; } = 60;
        [JsonIgnore] public static int DEFAULT_CHUTES_MAX_API_RETRIES { get; } = 3;
        [JsonIgnore] public static int DEFAULT_CHUTES_MAX_CONTENT_RETRIES { get; } = 2;
        [JsonIgnore] public static int DEFAULT_CHUTES_DELAY_BETWEEN_FILE_STARTS_MS { get; } = 3000;
        [JsonIgnore] public static int DEFAULT_CHUTES_INTER_CHUNK_DELAY_MS { get; } = 1000;
        [JsonIgnore] public static int DEFAULT_CHUTES_API_RETRY_BASE_DELAY_MS { get; } = 2000;
        [JsonIgnore] public static int DEFAULT_CHUTES_CONTENT_RETRY_BASE_DELAY_MS { get; } = 5000;
        public const int DEFAULT_CHUTES_LARGE_FILE_CHUNK_THRESHOLD = 5;
        public const int DEFAULT_CHUTES_DIRECT_SEND_THRESHOLD = 3000;
        public const int DEFAULT_CHUTES_CHUNK_SIZE = 800;
        [JsonIgnore] public static int DEFAULT_GEMINI_RATE_LIMIT_PER_MINUTE { get; } = 10;
        [JsonIgnore] public static double DEFAULT_GEMINI_TEMPERATURE { get; } = 0.1;
        [JsonIgnore] public static int DEFAULT_GEMINI_THINKING_BUDGET { get; } = 2000;
        [JsonIgnore] public static int DEFAULT_GEMINI_MAX_OUTPUT_TOKENS { get; } = 15000;
        [JsonIgnore] public static int DEFAULT_GEMINI_DIRECT_SEND_THRESHOLD { get; } = 8000;
        [JsonIgnore] public static int DEFAULT_GEMINI_CHUNK_SIZE { get; } = 3500;
        [JsonIgnore] public static int DEFAULT_GEMINI_MAX_API_RETRIES { get; } = 3;
        [JsonIgnore] public static int DEFAULT_GEMINI_API_RETRY_BASE_DELAY_MS { get; } = 10000;
        [JsonIgnore] public static int DEFAULT_GEMINI_DELAY_BETWEEN_FILE_STARTS_MS { get; } = 10000;
        [JsonIgnore] public static int DEFAULT_GEMINI_INTER_CHUNK_DELAY_MS { get; } = 5000;
        public const int DEFAULT_GEMINI_REQUEST_LIMIT_VALUE = 500;
        public const string DEFAULT_GEMINI_MODEL_TECHNICAL_NAME_CONST = "gemini-2.5-flash";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}