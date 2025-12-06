using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using subphimv1.UserView;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using JsonSerializer = System.Text.Json.JsonSerializer;
using JsonSerializerOptions = System.Text.Json.JsonSerializerOptions;


namespace subphimv1.Services
{
    public class AioTranslationRequest
    {
        public string SystemInstruction { get; set; }
        public string Content { get; set; }
        public string TargetLanguage { get; set; } = "Vietnamese";
    }
    public class AioCreateJobResponse
    {
        public string SessionId { get; set; }
        public string Message { get; set; }
    }
    public class AioJobResultResponse
    {
        public string Status { get; set; }
        public string TranslatedContent { get; set; }
        public string ErrorMessage { get; set; }
    }
    public class SrtLine
    {
        public int Index { get; set; }
        public string OriginalText { get; set; }
        public SrtLine(int index, string text) { Index = index; OriginalText = text; }
    }
    public class StartTranslationRequest
    {
        public string Genre { get; set; }
        public string TargetLanguage { get; set; }
        public List<SrtLine> Lines { get; set; }
        public string SystemInstruction { get; set; }
        public bool AcceptPartial { get; set; }
    }
    public class StartTranslationResponse
    {
        public string Status { get; set; } 
        public string Message { get; set; }
        public string SessionId { get; set; }
        public int RemainingLines { get; set; }
    }
    public class TranslatedSrtLine
    {
        public int Index { get; set; }
        public string TranslatedText { get; set; }
        public bool Success { get; set; }
    }
    public class GetResultsResponse
    {
        public List<TranslatedSrtLine> NewLines { get; set; }
        public bool IsCompleted { get; set; }
        public string ErrorMessage { get; set; }
    }
    public class CancelJobResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }
    public class CancelAllJobsResponse
    {
        public bool Success { get; set; }
        public int CancelledJobsCount { get; set; }
        public string Message { get; set; }
    }
    public static class ApiService
    {
        [Flags]
        public enum GrantedFeatures
        {
            None = 0,
            SubPhim = 1,
            DichThuat = 2,
            OcrTruyen = 4,
            EditTruyen = 8,
            Capcut = 16,
            Jianying = 32,
            XemPhim = 64
        }

        public class FeedbackHistoryItem
        {
            public string Username { get; set; }
            public string Message { get; set; }
            public DateTime SubmittedAt { get; set; }
        }

        public record UsageStatusDto(
            bool CanProcessNewVideo,
            int RemainingVideosToday,
            int MaxVideoDurationMinutes,
            DateTime LimitResetTimeUtc,
            string Message
        );
        public record UpdateCheckResponse(string LatestVersion, string DownloadUrl, string ReleaseNotes);
        private static HttpClient client;
        private static HttpClient longRunningClient;
        private static string _apiBaseUrl;
        private const string FallbackConfigUrl = "https://raw.githubusercontent.com/visecal/Updater/refs/heads/main/server_config.json";
        private static string _jwtToken;

        public static async Task<(bool success, UpdateCheckResponse response)> CheckForUpdateAsync()
        {
            try
            {
                var response = await client.GetAsync("api/appupdate/check");
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var updateInfo = JsonSerializer.Deserialize<UpdateCheckResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return (true, updateInfo);
                }
                return (false, null);
            }
            catch (Exception ex)
            {
                return (false, null);
            }
        }

        static ApiService()
        {
            // Load URL đã lưu từ registry khi ứng dụng khởi động
            _apiBaseUrl = CredentialService.LoadServerUrl();

            client = new HttpClient
            {
                BaseAddress = new Uri(_apiBaseUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            longRunningClient = new HttpClient
            {
                BaseAddress = new Uri(_apiBaseUrl),
                Timeout = TimeSpan.FromMinutes(120)
            };
            longRunningClient.DefaultRequestHeaders.Accept.Clear();
            longRunningClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
        public static void UpdateApiBaseUrl(string newBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(newBaseUrl))
            {
                return;
            }
            if (!newBaseUrl.EndsWith("/"))
            {
                newBaseUrl += "/";
            }

            _apiBaseUrl = newBaseUrl;
            try
            {
                var newUri = new Uri(_apiBaseUrl);

                // Tạo mới HttpClient thường
                client = new HttpClient
                {
                    BaseAddress = newUri,
                    Timeout = TimeSpan.FromSeconds(30)
                };
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Tạo mới HttpClient chạy lâu (cho các tác vụ nặng)
                longRunningClient = new HttpClient
                {
                    BaseAddress = newUri,
                    Timeout = TimeSpan.FromMinutes(120)
                };
                longRunningClient.DefaultRequestHeaders.Accept.Clear();
                longRunningClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // QUAN TRỌNG: Nếu đang đăng nhập, cần gắn lại Token cho client mới
                if (!string.IsNullOrEmpty(_jwtToken))
                {
                    var authHeader = new AuthenticationHeaderValue("Bearer", _jwtToken);
                    client.DefaultRequestHeaders.Authorization = authHeader;
                    longRunningClient.DefaultRequestHeaders.Authorization = authHeader;
                }

                // Lưu URL mới vào registry
                CredentialService.SaveServerUrl(newBaseUrl);
            }
            catch (UriFormatException ex)
            {
                Debug.WriteLine($"[ApiService] Lỗi định dạng URL: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] Lỗi khi cập nhật Base URL: {ex.Message}");
            }
        }
        private static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> apiCall, Func<T> errorResponseFactory)
        {
            try
            {
                return await apiCall();
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                bool fallbackSuccess = await TryFallbackServerUrlAsync();
                if (fallbackSuccess)
                {
                    try
                    {
                        return await apiCall();
                    }
                    catch (Exception retryEx)
                    {

                        return errorResponseFactory();
                    }
                }
                else
                {

                    return errorResponseFactory();
                }
            }
        }
        private static async Task ExecuteWithRetryFireAndForgetAsync(Func<Task> apiCall)
        {
            try
            {
                await apiCall();
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                bool fallbackSuccess = await TryFallbackServerUrlAsync();
                if (fallbackSuccess)
                {
                    try
                    {
                        await apiCall();
                    }
                    catch (Exception retryEx){}
                }
                else{ }
            }
        }
        private static async Task<bool> TryFallbackServerUrlAsync()
        {
            try
            {
                using (var fallbackClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    fallbackClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                    string jsonContent = await fallbackClient.GetStringAsync(FallbackConfigUrl);
                    var config = JObject.Parse(jsonContent);
                    string newUrl = config["current_server_url"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(newUrl) && Uri.IsWellFormedUriString(newUrl, UriKind.Absolute))
                    {
                        UpdateApiBaseUrl(newUrl);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                return false;
            }
        }
        public static async Task<(bool success, List<byte[]> audioData, string errorMessage, string fileExtension)> GenerateTtsAsync(string provider, string model, string text, string voiceId = null, string geminiInstruction = null, double elevenLabsStability = 0.7, double elevenLabsSimilarity = 0.75)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, null, "Yêu cầu đăng nhập.", ".error");
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return (false, null, "Văn bản không được để trống.", ".error");
            }
            Func<Task<(bool, List<byte[]>, string, string)>> apiCall = async () =>
            {
                object requestPayload;
                if (provider == "ElevenLabs")
                {
                    requestPayload = new
                    {
                        Provider = provider,
                        Model = model,
                        Text = text,
                        VoiceId = voiceId,
                        VoiceSettings = new { stability = elevenLabsStability, similarity_boost = elevenLabsSimilarity, style = 0.5, use_speaker_boost = true }
                    };
                }
                else // Gemini
                {
                    requestPayload = new { Provider = provider, Model = model, Text = text, VoiceId = voiceId, SystemInstruction = geminiInstruction };
                }

                var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");
                var response = await longRunningClient.PostAsync("api/tts/generate", content);

                if (response.IsSuccessStatusCode)
                {
                    var audioChunks = new List<byte[]>();
                    string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                    string extension = ".wav";

                    if (contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
                    {
                        var jsonBody = await response.Content.ReadAsStringAsync();
                        var jsonDoc = JsonDocument.Parse(jsonBody);
                        var root = jsonDoc.RootElement;
                        string mimeType = root.GetProperty("mimeType").GetString();
                        extension = mimeType.Contains("mpeg") ? ".mp3" : ".wav";
                        foreach (var chunkElement in root.GetProperty("audioChunks").EnumerateArray())
                        {
                            audioChunks.Add(Convert.FromBase64String(chunkElement.GetString()));
                        }
                    }
                    else
                    {
                        byte[] rawAudioData = await response.Content.ReadAsByteArrayAsync();
                        if (contentType.StartsWith("audio/L", StringComparison.OrdinalIgnoreCase))
                        {
                            audioChunks.Add(WavHelper.ConvertToWav(rawAudioData, contentType));
                            extension = ".wav";
                        }
                        else if (contentType.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase))
                        {
                            audioChunks.Add(rawAudioData);
                            extension = ".mp3";
                        }
                    }
                    return (true, audioChunks, null, extension);
                }
                else
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    try
                    {
                        var errorObject = JObject.Parse(errorBody);
                        string message = errorObject["message"]?.ToString() ?? errorBody;
                        return (false, null, $"Lỗi server ({(int)response.StatusCode}): {message}", ".error");
                    }
                    catch (JsonReaderException)
                    {
                        return (false, null, $"Lỗi server ({(int)response.StatusCode}): {errorBody}", ".error");
                    }
                }
            };
            Func<(bool, List<byte[]>, string, string)> errorFactory = () => (false, null, "Lỗi hệ thống: Không thể kết nối đến máy chủ.", ".error");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public static async Task<StartTranslationResponse> StartTranslationJobAsync(string genre, string targetLanguage, List<SrtLine> lines, string systemInstruction, bool acceptPartial = false)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return new StartTranslationResponse { Status = "Error", Message = "Chưa đăng nhập." };
            }

            Func<Task<StartTranslationResponse>> apiCall = async () =>
            {
                var requestPayload = new StartTranslationRequest
                {
                    Genre = genre,
                    TargetLanguage = targetLanguage,
                    Lines = lines,
                    SystemInstruction = systemInstruction,
                    AcceptPartial = acceptPartial
                };
                var jsonPayload = JsonConvert.SerializeObject(requestPayload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await longRunningClient.PostAsync("api/launcheraio/start-translation", content);
                var responseBody = await response.Content.ReadAsStringAsync();
                try
                {
                    var result = JsonConvert.DeserializeObject<StartTranslationResponse>(responseBody);
                    if (!response.IsSuccessStatusCode && string.IsNullOrEmpty(result.Message))
                    {
                        result.Message = $"Lỗi server {(int)response.StatusCode}: {response.ReasonPhrase}";
                    }
                    return result;
                }
                catch (Newtonsoft.Json.JsonException ex)
                {
                    return new StartTranslationResponse { Status = "Error", Message = $"Lỗi phản hồi từ server: {ex.Message}. Body: {responseBody}" };
                }
            };

            Func<StartTranslationResponse> errorFactory = () => new StartTranslationResponse { Status = "Error", Message = "Lỗi kết nối: Không thể gửi yêu cầu tới server." };

            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public static async Task<(bool hasAccess, string message)> CheckApiAccessAsync(string apiName)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, "Yêu cầu đăng nhập.");
            }

            Func<Task<(bool, string)>> apiCall = async () =>
            {
                var requestPayload = new { ApiName = apiName };
                var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("api/auth/check-api-access", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        bool hasAccess = root.GetProperty("hasAccess").GetBoolean();
                        string message = root.GetProperty("message").GetString();
                        return (hasAccess, message);
                    }
                }
                try
                {
                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("message", out var messageElement))
                        {
                            return (false, messageElement.GetString());
                        }
                    }
                }
                catch {}

                return (false, $"Lỗi từ máy chủ: {response.ReasonPhrase}");
            };

            Func<(bool, string)> errorFactory = () => (false, "Lỗi kết nối khi kiểm tra quyền truy cập API.");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public static async Task<GetResultsResponse> GetTranslationResultsAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(_jwtToken))
                return new GetResultsResponse { NewLines = null, IsCompleted = true, ErrorMessage = "Yêu cầu đăng nhập." };

            Func<Task<GetResultsResponse>> apiCall = async () =>
            {
                var response = await client.GetAsync($"api/launcheraio/get-results/{sessionId}");
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        var result = JsonSerializer.Deserialize<GetResultsResponse>(responseBody,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        // FIX: Handle null
                        if (result == null)
                        {
                            return new GetResultsResponse
                            {
                                NewLines = new List<TranslatedSrtLine>(),
                                IsCompleted = false,
                                ErrorMessage = "Không thể parse response."
                            };
                        }

                        result.NewLines ??= new List<TranslatedSrtLine>();
                        return result;
                    }
                    catch (Newtonsoft.Json.JsonException ex)
                    {
                        return new GetResultsResponse
                        {
                            NewLines = new List<TranslatedSrtLine>(),
                            IsCompleted = false,
                            ErrorMessage = $"Lỗi JSON: {ex.Message}"
                        };
                    }
                }

                // 404 = session hết hạn -> completed
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new GetResultsResponse
                    {
                        NewLines = new List<TranslatedSrtLine>(),
                        IsCompleted = true,
                        ErrorMessage = "Session không tồn tại."
                    };
                }

                return new GetResultsResponse
                {
                    NewLines = new List<TranslatedSrtLine>(),
                    IsCompleted = false,
                    ErrorMessage = $"Lỗi server: {response.ReasonPhrase}"
                };
            };

            Func<GetResultsResponse> errorFactory = () => new GetResultsResponse
            {
                NewLines = new List<TranslatedSrtLine>(),
                IsCompleted = false,
                ErrorMessage = "Lỗi kết nối."
            };

            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public static async Task<CancelJobResponse> CancelTranslationJobAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return new CancelJobResponse { Success = false, Message = "Yêu cầu đăng nhập." };
            }
            if (string.IsNullOrEmpty(sessionId))
            {
                return new CancelJobResponse { Success = false, Message = "SessionId không hợp lệ." };
            }

            Func<Task<CancelJobResponse>> apiCall = async () =>
            {
                var response = await client.PostAsync($"api/launcheraio/cancel/{sessionId}", null);
                var responseBody = await response.Content.ReadAsStringAsync();

                try
                {
                    var result = JsonSerializer.Deserialize<CancelJobResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return result ?? new CancelJobResponse { Success = false, Message = "Phản hồi không hợp lệ từ server." };
                }
                catch (System.Text.Json.JsonException)
                {
                    return new CancelJobResponse { Success = false, Message = responseBody };
                }
            };

            Func<CancelJobResponse> errorFactory = () => new CancelJobResponse { Success = false, Message = "Lỗi kết nối khi hủy job." };

            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public static async Task<CancelAllJobsResponse> CancelAllTranslationJobsAsync()
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return new CancelAllJobsResponse { Success = false, CancelledJobsCount = 0, Message = "Yêu cầu đăng nhập." };
            }

            Func<Task<CancelAllJobsResponse>> apiCall = async () =>
            {
                var response = await client.PostAsync("api/launcheraio/cancel-all", null);
                var responseBody = await response.Content.ReadAsStringAsync();

                try
                {
                    var result = JsonSerializer.Deserialize<CancelAllJobsResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return result ?? new CancelAllJobsResponse { Success = false, CancelledJobsCount = 0, Message = "Phản hồi không hợp lệ từ server." };
                }
                catch (System.Text.Json.JsonException)
                {
                    return new CancelAllJobsResponse { Success = false, CancelledJobsCount = 0, Message = responseBody };
                }
            };

            Func<CancelAllJobsResponse> errorFactory = () => new CancelAllJobsResponse { Success = false, CancelledJobsCount = 0, Message = "Lỗi kết nối khi hủy tất cả jobs." };

            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        private static void UpdateToken(string token)
        {
            _jwtToken = token;
            var authHeader = string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", _jwtToken);
            client.DefaultRequestHeaders.Authorization = authHeader;
            longRunningClient.DefaultRequestHeaders.Authorization = authHeader;
        }
        public static async Task<(bool success, string message, UserDto user)> LoginAsync(string username, string password, string hwid, bool isRetry = false)
        {
            if (!isRetry)
            {
                UpdateToken(null);
            }
            var requestPayload = new { Username = username, Password = password, Hwid = hwid };
            var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");
            try
            {
                var response = await client.PostAsync("api/auth/login", content);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var userDto = JsonSerializer.Deserialize<UserDto>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (userDto == null || string.IsNullOrEmpty(userDto.Token))
                    {
                        return (false, "Phản hồi từ máy chủ không hợp lệ. Vui lòng kiểm tra lại.", null);
                    }
                    UpdateToken(userDto.Token);
                    return (true, "Đăng nhập thành công!", userDto);
                }
                else
                {
                    return (false, responseBody, null);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                // KHÔNG tự động chuyển server khi đăng nhập
                // Hướng dẫn người dùng tự chọn server khác
                return (false, "Không thể kết nối tới máy chủ hiện tại.\n\nVui lòng thử:\n1. Kiểm tra kết nối mạng\n2. Chọn server khác từ danh sách server ở trên\n3. Thử lại sau", null);
            }
            catch (Exception ex)
            {
                return (false, $"Đã xảy ra lỗi không xác định: {ex.Message}", null);
            }
        }
        public static async Task<(bool hasAccess, string message)> CheckFeatureAccessAsync(string featureName)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, "Yêu cầu đăng nhập.");
            }
            Func<Task<(bool, string)>> apiCall = async () =>
            {
                var requestPayload = new { FeatureName = featureName };
                var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("api/auth/check-feature-access", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        bool hasAccess = root.GetProperty("hasAccess").GetBoolean();
                        string message = root.GetProperty("message").GetString();
                        return (hasAccess, message);
                    }
                }
                return (false, $"Lỗi từ máy chủ: {response.ReasonPhrase}");
            };
            Func<(bool, string)> errorFactory = () => (false, "Lỗi kết nối khi kiểm tra quyền truy cập.");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public static async Task<(bool success, UserDto refreshedUser, string message)> RefreshUserProfileAsync()
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, null, "Không có token để làm mới.");
            }

            Func<Task<(bool, UserDto, string)>> apiCall = async () =>
            {
                var response = await client.GetAsync("api/auth/refresh-profile");
                var responseBody = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    var userDto = JsonSerializer.Deserialize<UserDto>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return (true, userDto, "Làm mới thành công.");
                }
                return (false, null, responseBody);
            };

            Func<(bool, UserDto, string)> errorFactory = () => (false, null, "Không thể kết nối đến máy chủ.");

            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public static async Task<List<FeedbackHistoryItem>> GetFeedbackHistoryAsync()
        {
            const string FeedbackApiUrl = "https://feedbackservice.fly.dev/api/Feedback";
            try
            {
                var response = await client.GetAsync(FeedbackApiUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<List<FeedbackHistoryItem>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch (Exception)
            {
                
            }
            return new List<FeedbackHistoryItem>();
        }
        public static async Task<(bool success, string message)> SubmitFeedbackAsync(string username, string feedbackType, string feedbackMessage)
        {
            const string FeedbackApiUrl = "https://feedbackservice.fly.dev/api/Feedback";

            if (string.IsNullOrWhiteSpace(feedbackMessage))
            {
                return (false, "Nội dung không được để trống.");
            }

            try
            {
                var feedbackData = new
                {
                    Username = username,
                    FeedbackType = feedbackType,
                    Message = feedbackMessage
                };

                var json = JsonSerializer.Serialize(feedbackData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(FeedbackApiUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    return (true, "Cảm ơn bạn!");
                }
                else
                {
                    return (false, $"Gửi thất bại. Server trả về lỗi: {response.ReasonPhrase}");
                }
            }
            catch (Exception)
            {
                return (false, "Không thể gửi Vui lòng kiểm tra kết nối mạng.");
            }
        }

        public record SrtTranslateCheckRequest(int LineCount);
        public record SrtTranslateCheckResponse(bool CanTranslate, int RemainingLines, string Message);

        public static async Task<(bool success, string message, int remaining)> PreSrtTranslateCheckAsync(int lineCount)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, "Yêu cầu đăng nhập.", 0);
            }

            Func<Task<(bool, string, int)>> apiCall = async () =>
            {
                var requestPayload = new SrtTranslateCheckRequest(lineCount);
                var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("api/auth/pre-srt-translate-check", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var checkResponse = JsonSerializer.Deserialize<SrtTranslateCheckResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return (true, checkResponse.Message, checkResponse.RemainingLines);
                }
                else
                {
                    try
                    {
                        var errorResponse = JsonSerializer.Deserialize<SrtTranslateCheckResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        return (false, errorResponse.Message, errorResponse.RemainingLines);
                    }
                    catch
                    {
                        return (false, responseBody, 0);
                    }
                }
            };
            Func<(bool, string, int)> errorFactory = () => (false, "Lỗi kết nối khi kiểm tra.", 0);
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public static async Task<(bool success, int remaining, string message)> PreTranslateCheckAsync(ApiProviderType provider)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, 0, "Yêu cầu đăng nhập.");
            }
            try
            {
                var requestPayload = new { Provider = provider.ToString() };
                var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("api/auth/pre-translate-check", content);

                if (response.IsSuccessStatusCode)
                {
                    var jsonResponse = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(jsonResponse))
                    {
                        JsonElement root = doc.RootElement;
                        if (root.TryGetProperty("remainingRequests", out JsonElement remainingElement))
                        {
                            int remaining = remainingElement.GetInt32();
                            return (true, remaining, "OK");
                        }
                        return (false, 0, "Phản hồi từ server không hợp lệ.");
                    }
                }

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var message = await response.Content.ReadAsStringAsync();
                    return (false, 0, message);
                }

                return (false, 0, $"Lỗi máy chủ: {response.ReasonPhrase}");
            }
            catch (Exception ex)
            {
                return (false, 0, $"Lỗi kết nối: {ex.Message}");
            }
        }
        public static async Task<(bool success, byte[] audioData, string errorMessage)> GenerateAioTtsAsync(string language, string voiceId, double rate, string text, int modelType = 4)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, null, "Yêu cầu đăng nhập.");
            }

            Func<Task<(bool, byte[], string)>> apiCall = async () =>
            {
                var requestPayload = new { Language = language, VoiceId = voiceId, Rate = rate, Text = text, ModelType = modelType };
                var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");

                var response = await longRunningClient.PostAsync("api/aiolauncher-tts/generate", content);

                if (response.IsSuccessStatusCode)
                {
                    byte[] audioData = await response.Content.ReadAsByteArrayAsync();
                    return (true, audioData, null);
                }
                else
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    try
                    {
                        var errorObject = JObject.Parse(errorBody);
                        string message = errorObject["message"]?.ToString() ?? errorBody;
                        return (false, null, $"Lỗi server ({(int)response.StatusCode}): {message}");
                    }
                    catch (JsonReaderException)
                    {
                        return (false, null, $"Lỗi server ({(int)response.StatusCode}): {errorBody}");
                    }
                }
            };

            Func<(bool, byte[], string)> errorFactory = () => (false, null, "Lỗi hệ thống: Không thể kết nối đến máy chủ.");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }

        public static async Task<(bool success, ListVoicesResponse data, string errorMessage)> ListAioTtsVoicesAsync(string languageCode = null, int? modelType = null)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, null, "Yêu cầu đăng nhập.");
            }

            Func<Task<(bool, ListVoicesResponse, string)>> apiCall = async () =>
            {
                var queryParams = new List<string>();
                if (!string.IsNullOrEmpty(languageCode))
                {
                    queryParams.Add($"languageCode={Uri.EscapeDataString(languageCode)}");
                }
                if (modelType.HasValue)
                {
                    queryParams.Add($"modelType={modelType.Value}");
                }

                string queryString = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";
                var response = await client.GetAsync($"api/aiolauncher-tts/list-voices{queryString}");

                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<ListVoicesResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return (true, data, null);
                }
                else
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    try
                    {
                        var errorObject = JObject.Parse(errorBody);
                        string message = errorObject["message"]?.ToString() ?? errorBody;
                        return (false, null, $"Lỗi server ({(int)response.StatusCode}): {message}");
                    }
                    catch (JsonReaderException)
                    {
                        return (false, null, $"Lỗi server ({(int)response.StatusCode}): {errorBody}");
                    }
                }
            };

            Func<(bool, ListVoicesResponse, string)> errorFactory = () => (false, null, "Lỗi hệ thống: Không thể kết nối đến máy chủ.");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }

        public class AioTtsBatchJobStatus
        {
            public string JobId { get; set; }
            public string Status { get; set; }
            public int TotalLines { get; set; }
            public int ProcessedLines { get; set; }
            public string ErrorMessage { get; set; }
        }

        // Models for list-voices API
        public class TtsVoiceInfo
        {
            public string Name { get; set; }
            public List<string> LanguageCodes { get; set; }
            public string SsmlGender { get; set; }
            public int NaturalSampleRateHertz { get; set; }
            public string ModelType { get; set; }
            public string VoiceId { get; set; }
        }

        public class ListVoicesResponse
        {
            public List<TtsVoiceInfo> Voices { get; set; }
            public int TotalCount { get; set; }
            public ListVoicesFilter FilteredBy { get; set; }
        }

        public class ListVoicesFilter
        {
            public string LanguageCode { get; set; }
            public string ModelType { get; set; }
        }

        public static async Task<(bool success, string jobId, string errorMessage)> StartAioTtsBatchJobAsync(string srtContent, string language, string voiceId, double rate, string audioFormat = "mp3", int modelType = 4)
        {
            if (string.IsNullOrEmpty(_jwtToken)) return (false, null, "Yêu cầu đăng nhập.");

            Func<Task<(bool, string, string)>> apiCall = async () =>
            {
                using var formData = new MultipartFormDataContent();
                formData.Add(new StringContent(language), "language");
                formData.Add(new StringContent(voiceId), "voiceId");
                formData.Add(new StringContent(rate.ToString(CultureInfo.InvariantCulture)), "rate");
                formData.Add(new StringContent(audioFormat), "audioFormat");
                formData.Add(new StringContent(modelType.ToString()), "modelType");

                var srtBytes = Encoding.UTF8.GetBytes(srtContent);
                var srtStreamContent = new ByteArrayContent(srtBytes);
                srtStreamContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-subrip");
                formData.Add(srtStreamContent, "srtFile", "subtitle.srt");

                var response = await longRunningClient.PostAsync("api/aiolauncher-tts/batch/upload", formData);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JObject.Parse(responseBody);
                    return (true, result["jobId"]?.ToString(), null);
                }
                else
                {
                    var errorResult = JObject.Parse(responseBody);
                    return (false, null, errorResult["message"]?.ToString() ?? responseBody);
                }
            };
            Func<(bool, string, string)> errorFactory = () => (false, null, "Lỗi kết nối khi bắt đầu tác vụ TTS hàng loạt.");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }

        public static async Task<(bool success, AioTtsBatchJobStatus status, string errorMessage)> GetAioTtsBatchJobStatusAsync(string jobId)
        {
            if (string.IsNullOrEmpty(_jwtToken)) return (false, null, "Yêu cầu đăng nhập.");

            Func<Task<(bool, AioTtsBatchJobStatus, string)>> apiCall = async () =>
            {
                var response = await client.GetAsync($"api/aiolauncher-tts/batch/status/{jobId}");
                var responseBody = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    var status = JsonSerializer.Deserialize<AioTtsBatchJobStatus>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return (true, status, null);
                }
                else
                {
                    var errorResult = JObject.Parse(responseBody);
                    return (false, null, errorResult["message"]?.ToString() ?? responseBody);
                }
            };
            Func<(bool, AioTtsBatchJobStatus, string)> errorFactory = () => (false, null, "Lỗi kết nối khi kiểm tra trạng thái.");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }

        public static async Task<(bool success, byte[] zipData, string errorMessage)> DownloadAioTtsBatchResultAsync(string jobId)
        {
            if (string.IsNullOrEmpty(_jwtToken)) return (false, null, "Yêu cầu đăng nhập.");

            Func<Task<(bool, byte[], string)>> apiCall = async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"api/aiolauncher-tts/batch/download/{jobId}");
                request.Headers.Accept.Clear();
                request.Headers.Accept.ParseAdd("application/zip");

                using var response = await longRunningClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead 
                );

                if (response.IsSuccessStatusCode)
                {
                    await using var responseStream = await response.Content.ReadAsStreamAsync();
                    using var ms = new MemoryStream();
                    await responseStream.CopyToAsync(ms);
                    return (true, ms.ToArray(), null);
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    try
                    {
                        var errorResult = JObject.Parse(responseBody);
                        return (false, null, errorResult["message"]?.ToString() ?? responseBody);
                    }
                    catch
                    {
                        return (false, null, responseBody);
                    }
                }
            };

            Func<(bool, byte[], string)> errorFactory = () => (false, null, "Lỗi kết nối khi tải kết quả.");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }


        public static async Task ReportViolationAsync(string reason, string hwid)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return;
            }

            Func<Task> apiCall = async () =>
            {
                var reportData = new { Reason = reason, Hwid = hwid };
                var content = new StringContent(JsonSerializer.Serialize(reportData), Encoding.UTF8, "application/json");
                await client.PostAsync("api/auth/report-violation", content);
            };
            await ExecuteWithRetryFireAndForgetAsync(apiCall);
        }
        public static void ClearSession()
        {
            UpdateToken(null);
        }

        public static async Task<(bool success, string message)> RegisterAsync(string username, string password, string email, string hwid)
        {
            var requestPayload = new { Username = username, Password = password, Email = email, Hwid = hwid };
            var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync("api/auth/register", content);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return (true, responseBody);
                }
                else
                {
                    return (false, responseBody);
                }
            }
            catch (HttpRequestException)
            {
                return (false, "Không thể kết nối tới máy chủ. Vui lòng kiểm tra kết nối mạng và địa chỉ server.");
            }
            catch (Exception ex)
            {
                return (false, $"Đã xảy ra lỗi không xác định: {ex.Message}");
            }
        }
        public static async Task<(bool success, UsageStatusDto status, string message)> GetUsageStatusAsync()
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, null, "Người dùng chưa đăng nhập.");
            }

            Func<Task<(bool, UsageStatusDto, string)>> apiCall = async () =>
            {
                var response = await client.GetAsync("api/auth/usage-status");
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var dto = JsonSerializer.Deserialize<UsageStatusDto>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return (true, dto, dto.Message);
                }
                return (false, null, responseBody);
            };

            Func<(bool, UsageStatusDto, string)> errorFactory = () => (false, null, "Lỗi kết nối khi lấy trạng thái sử dụng.");

            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public static async Task<(bool success, string message)> TryStartProcessingAsync()
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, "Người dùng chưa đăng nhập.");
            }
            Func<Task<(bool, string)>> apiCall = async () =>
            {
                var response = await client.PostAsync("api/auth/start-processing", null);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    return (true, "Server đã chấp thuận.");
                }
                else
                {
                    return (false, responseBody);
                }
            };
            Func<(bool, string)> errorFactory = () => (false, "Lỗi kết nối khi xin phép xử lý.");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
    
    public static async Task<(bool success, AioCreateJobResponse response, string error)> StartAioTranslationJobAsync(string systemInstruction, string content, string targetLanguage = "Vietnamese")
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, null, "Chưa đăng nhập.");
            }

            Func<Task<(bool, AioCreateJobResponse, string)>> apiCall = async () =>
            {
                var requestPayload = new AioTranslationRequest
                {
                    SystemInstruction = systemInstruction,
                    Content = content,
                    TargetLanguage = targetLanguage
                };
                var jsonPayload = JsonSerializer.Serialize(requestPayload);
                var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var response = await longRunningClient.PostAsync("api/aiolauncher/start-translation", httpContent);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == System.Net.HttpStatusCode.Accepted) // 202
                {
                    var result = JsonSerializer.Deserialize<AioCreateJobResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return (true, result, null);
                }
                else
                {
                    try
                    {
                        var errorObj = JObject.Parse(responseBody);
                        return (false, null, errorObj["message"]?.ToString() ?? responseBody);
                    }
                    catch { return (false, null, responseBody); }
                }
            };
            Func<(bool, AioCreateJobResponse, string)> errorFactory = () => (false, null, "Lỗi kết nối: Không thể gửi yêu cầu tới server.");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public static async Task<AioJobResultResponse> GetAioJobResultAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return new AioJobResultResponse { Status = "Failed", ErrorMessage = "Chưa đăng nhập." };
            }

            Func<Task<AioJobResultResponse>> apiCall = async () =>
            {
                var response = await client.GetAsync($"api/aiolauncher/get-result/{sessionId}");
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return JsonSerializer.Deserialize<AioJobResultResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                return new AioJobResultResponse { Status = "Failed", ErrorMessage = $"Lỗi server: {response.ReasonPhrase}" };
            };

            Func<AioJobResultResponse> errorFactory = () => new AioJobResultResponse { Status = "Failed", ErrorMessage = "Lỗi kết nối khi lấy kết quả." };

            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public record ForgotPasswordRequest(string Email);
        public record ResetDevicesResponse(string Message);
        public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

        public static async Task<(bool success, string message)> ForgotPasswordAsync(string email)
        {
            try
            {
                var requestPayload = new ForgotPasswordRequest(email);
                var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("api/auth/forgot-password", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JObject.Parse(responseBody);
                    return (true, result["message"]?.ToString() ?? "Yêu cầu đã được gửi.");
                }
                else
                {
                    return (false, responseBody);
                }
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi kết nối: {ex.Message}");
            }
        }

        public static async Task<(bool success, string message)> ResetDevicesAsync()
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, "Yêu cầu đăng nhập.");
            }

            Func<Task<(bool, string)>> apiCall = async () =>
            {
                var response = await client.PostAsync("api/auth/reset-devices", null);
                var responseBody = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(responseBody);
                var message = result["message"]?.ToString() ?? responseBody;

                if (response.IsSuccessStatusCode)
                {
                    return (true, message);
                }
                else
                {
                    return (false, message);
                }
            };

            Func<(bool, string)> errorFactory = () => (false, "Lỗi kết nối khi reset thiết bị.");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }

        public static async Task<(bool success, string message)> ChangePasswordAsync(string currentPassword, string newPassword)
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, "Yêu cầu đăng nhập.");
            }

            Func<Task<(bool, string)>> apiCall = async () =>
            {
                var requestPayload = new ChangePasswordRequest(currentPassword, newPassword);
                var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("api/auth/change-password", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JObject.Parse(responseBody);
                    return (true, result["message"]?.ToString() ?? "Đổi mật khẩu thành công.");
                }
                else
                {
                    return (false, responseBody);
                }
            };

            Func<(bool, string)> errorFactory = () => (false, "Lỗi kết nối khi đổi mật khẩu.");
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public class SaOcrWorkerDto
        {
            public string JsonKey { get; set; }
            public string FolderId { get; set; }
        }

        public static async Task<(bool success, List<SaOcrWorkerDto> workers, string errorMessage)> GetSaOcrWorkersAsync()
        {
            if (string.IsNullOrEmpty(_jwtToken))
            {
                return (false, new List<SaOcrWorkerDto>(), "Yêu cầu đăng nhập.");
            }
            Func<Task<(bool, List<SaOcrWorkerDto>, string)>> apiCall = async () =>
            {
                var response = await client.GetAsync("api/sa-ocr/keys");
                var body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    var arr = System.Text.Json.JsonSerializer.Deserialize<List<SaOcrWorkerDto>>(
                        body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<SaOcrWorkerDto>();
                    return (true, arr, null);
                }
                else
                {
                    return (false, new List<SaOcrWorkerDto>(), body);
                }
            };

            Func<(bool, List<SaOcrWorkerDto>, string)> errorFactory = () =>
            {
                return (false, new List<SaOcrWorkerDto>(), "Lỗi kết nối khi lấy SA OCR.");
            };
            return await ExecuteWithRetryAsync(apiCall, errorFactory);
        }
        public static async Task<bool> HasSaOcrWorkersAsync()
        {
            var (ok, list, _) = await GetSaOcrWorkersAsync();
            return ok && list != null && list.Count > 0;
        }

    }
}