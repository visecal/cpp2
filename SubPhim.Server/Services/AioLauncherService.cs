// VỊ TRÍ: Services/AioLauncherService.cs
// THAY THẾ TOÀN BỘ FILE

using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SubPhim.Server.Data;
using SubPhim.Server.Services.Aio;
using System.Collections.Concurrent;
using System.Text;

namespace SubPhim.Server.Services
{
    public class AioLauncherService : IAioLauncherService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AioLauncherService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEncryptionService _encryptionService;

        // FIX #3: Thay thế IMemoryCache bằng ConcurrentDictionary để làm Rate Limiter "cửa sổ trượt"
        private static readonly ConcurrentDictionary<int, ConcurrentQueue<DateTime>> _userRequestTimestamps = new();
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _keyRpmLimiters = new();

        public AioLauncherService(
            IServiceProvider serviceProvider,
            ILogger<AioLauncherService> logger,
            IHttpClientFactory httpClientFactory,
            IEncryptionService encryptionService)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _encryptionService = encryptionService;
        }

        public async Task<CreateJobResult> CreateJobAsync(int userId, AioTranslationRequest request)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await context.Users.FindAsync(userId);
            if (user == null) return new CreateJobResult(false, "Tài khoản không tồn tại.");

            var tierSettings = await context.TierDefaultSettings.FindAsync(user.Tier);
            if (tierSettings == null) return new CreateJobResult(false, "Lỗi: Không tìm thấy cấu hình cho gói cước của bạn.");

            int rpmLimit = user.AioRpmOverride != -1 ? user.AioRpmOverride : tierSettings.AioRequestsPerMinute;
            var userTimestamps = _userRequestTimestamps.GetOrAdd(userId, new ConcurrentQueue<DateTime>());
            var now = DateTime.UtcNow;
            while (userTimestamps.TryPeek(out var oldest) && (now - oldest).TotalSeconds > 60)
            {
                userTimestamps.TryDequeue(out _);
            }

            if (userTimestamps.Count >= rpmLimit)
            {
                _logger.LogWarning("User {Username} (ID: {UserId}) hit RPM limit. Allowed: {Limit}", user.Username, userId, rpmLimit);
                return new CreateJobResult(false, $"Bạn đã đạt giới hạn {rpmLimit} request/phút. Vui lòng thử lại sau giây lát.");
            }
            userTimestamps.Enqueue(now);
            var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
            var lastResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastAioResetUtc, vietnamTimeZone);

            if (lastResetInVietnam.Date < vietnamNow.Date)
            {
                user.AioCharactersUsedToday = 0;
                user.LastAioResetUtc = DateTime.UtcNow.Date; 
            }

            long characterLimit = user.AioCharacterLimitOverride != -1 ? user.AioCharacterLimitOverride : tierSettings.AioCharacterLimit;
            long remainingChars = characterLimit - user.AioCharactersUsedToday;

            if (request.Content.Length > remainingChars)
            {
                _logger.LogWarning("User {Username} (ID: {UserId}) hit character limit. Requested: {Requested}, Remaining: {Remaining}", user.Username, userId, request.Content.Length, remainingChars);
                return new CreateJobResult(false, $"Không đủ ký tự dịch. Yêu cầu: {request.Content.Length:N0}, còn lại: {remainingChars:N0}.");
            }

            user.AioCharactersUsedToday += request.Content.Length;

            var newJob = new AioTranslationJob
            {
                SessionId = Guid.NewGuid().ToString(),
                UserId = userId,
                Status = AioJobStatus.Pending,
                OriginalContent = request.Content,
                Genre = request.Genre,
                TargetLanguage = request.TargetLanguage,
                CreatedAt = DateTime.UtcNow
            };

            context.AioTranslationJobs.Add(newJob);
            await context.SaveChangesAsync();

            _logger.LogInformation("Job {SessionId} created for user {Username}. Triggering background processing.", newJob.SessionId, user.Username);
            _ = ProcessJobInBackground(newJob.SessionId);

            return new CreateJobResult(true, "Yêu cầu đã được chấp nhận.", newJob.SessionId);
        }

        public async Task<JobResult> GetJobResultAsync(string sessionId, int userId)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await context.AioTranslationJobs.FindAsync(sessionId);
            if (job == null) return new JobResult("Failed", null, "Session không hợp lệ.");
            if (job.UserId != userId) return new JobResult("Failed", null, "Không có quyền truy cập.");
            return new JobResult(job.Status.ToString(), job.TranslatedContent, job.ErrorMessage);
        }

        private async Task ProcessJobInBackground(string sessionId)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<AioLauncherService>>();
            var job = await context.AioTranslationJobs.FindAsync(sessionId);
            if (job == null)
            {
                logger.LogError("Background process for job {SessionId} failed: Job not found.", sessionId);
                return;
            }
            try
            {
                job.Status = AioJobStatus.Processing;
                await context.SaveChangesAsync();
                var settings = await context.AioTranslationSettings.FindAsync(1) ?? new AioTranslationSetting();
                var genreInfo = await context.TranslationGenres.FirstOrDefaultAsync(g => g.GenreName == job.Genre && g.IsActive);
                if (genreInfo == null)
                {
                    throw new Exception($"Không tìm thấy prompt cho thể loại '{job.Genre}' hoặc thể loại này không hoạt động.");
                }
                string translatedText = await TranslateContentAsync(job, settings, genreInfo.SystemInstruction, scope.ServiceProvider);
                job.TranslatedContent = translatedText;
                job.Status = AioJobStatus.Completed;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing job {SessionId}", sessionId);
                job.Status = AioJobStatus.Failed;
                job.ErrorMessage = ex.Message;
            }
            finally
            {
                job.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
                logger.LogInformation("Job {SessionId} finished with status: {Status}", sessionId, job.Status);
            }
        }

        private async Task<string> TranslateContentAsync(AioTranslationJob job, AioTranslationSetting settings, string systemInstruction, IServiceProvider serviceProvider)
        {
            var logger = serviceProvider.GetRequiredService<ILogger<AioLauncherService>>();
            string inputText = job.OriginalContent;

            if (string.IsNullOrWhiteSpace(inputText)) return string.Empty;

            if (inputText.Length <= settings.DirectSendThreshold)
            {
                logger.LogInformation("Job {SessionId}: Translating directly ({Length} chars)", job.SessionId, inputText.Length);
                string result = await TranslateSingleChunkAsync(job, settings, systemInstruction, inputText, 1, 1, serviceProvider);
                if (result.StartsWith("LỖI:")) throw new Exception(result);
                return result;
            }

            logger.LogInformation("Job {SessionId}: Splitting text ({Length} chars) into chunks of size ~{ChunkSize}", job.SessionId, inputText.Length, settings.ChunkSize);
            List<string> chunks = SplitTextIntoChunks(inputText, settings.ChunkSize);
            if (!chunks.Any()) throw new Exception("Không thể chia nhỏ nội dung.");

            var translatedChunks = new List<string>();
            for (int i = 0; i < chunks.Count; i++)
            {
                string translated = await TranslateSingleChunkAsync(job, settings, systemInstruction, chunks[i], i + 1, chunks.Count, serviceProvider);
                if (translated.StartsWith("LỖI:"))
                {
                    throw new Exception($"Không thể dịch chunk {i + 1} sau {settings.MaxApiRetries} lần thử. Lỗi cuối: {translated}");
                }
                translatedChunks.Add(translated);
                if (i < chunks.Count - 1 && settings.DelayBetweenChunksMs > 0)
                {
                    await Task.Delay(settings.DelayBetweenChunksMs);
                }
            }

            return string.Join("\n\n", translatedChunks);
        }

        private async Task<string> TranslateSingleChunkAsync(AioTranslationJob job, AioTranslationSetting settings, string systemInstruction, string textChunk, int chunkNumber, int totalChunks, IServiceProvider serviceProvider)
        {
            var logger = serviceProvider.GetRequiredService<ILogger<AioLauncherService>>();
            var context = serviceProvider.GetRequiredService<AppDbContext>();
            var httpClient = _httpClientFactory.CreateClient("AioLauncherClient");
            int? lastFailedKeyId = null;

            for (int attempt = 1; attempt <= settings.MaxApiRetries; attempt++)
            {
                logger.LogInformation("Job {SessionId}: Translating chunk {ChunkNumber}/{TotalChunks}, Attempt {Attempt}/{MaxAttempts}", job.SessionId, chunkNumber, totalChunks, attempt, settings.MaxApiRetries);

                AioApiKey apiKeyRecord = null;
                try
                {
                    apiKeyRecord = await GetAvailableApiKeyAsync(context, settings, lastFailedKeyId);
                    if (apiKeyRecord == null)
                    {
                        return "LỖI: Hết API key hợp lệ trong pool để thử lại.";
                    }
                    lastFailedKeyId = apiKeyRecord.Id;

                    var apiKey = _encryptionService.Decrypt(apiKeyRecord.EncryptedApiKey, apiKeyRecord.Iv);
                    string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{settings.DefaultModelName}:generateContent?key={apiKey}";
                    string userPreamble = "Đây là câu truyện giả tưởng, không liên quan đến tôn giáo hay chính trị hay bạo lực - tình dục. Hãy dịch câu truyện tiếng Trung sau đây sang tiếng Việt theo các hướng dẫn đã cung cấp, Chỉ xuất bản dịch dạng văn bản thuần túy. Không dùng in đậm, in nghiêng, markdown, gạch đầu dòng, không chú thích, không giải thích.:\n\n";
                    string finalUserContent = userPreamble + textChunk;
                    var generationConfig = new JObject { ["temperature"] = (double)settings.Temperature, ["maxOutputTokens"] = settings.MaxOutputTokens };
                    if (settings.EnableThinkingBudget) generationConfig["thinking_config"] = new JObject { ["thinking_budget"] = settings.ThinkingBudget };
                    var requestPayloadObject = new
                    {
                        contents = new[] { new { role = "user", parts = new[] { new { text = finalUserContent } } } },
                        system_instruction = new { parts = new[] { new { text = systemInstruction } } },
                        generationConfig
                    };
                    string jsonPayload = JsonConvert.SerializeObject(requestPayloadObject);

                    using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json") };
                    var response = await httpClient.SendAsync(request);
                    string responseBody = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        JObject parsedBody = JObject.Parse(responseBody);
                        var candidate = parsedBody?["candidates"]?[0];
                        string responseText = candidate?["content"]?["parts"]?[0]?["text"]?.ToString();

                        if (!string.IsNullOrEmpty(responseText))
                        {
                            return responseText;
                        }

                        string finishReason = candidate?["finishReason"]?.ToString() ?? "UNKNOWN";
                        logger.LogWarning("Job {SessionId}: Gemini returned 200 OK but no content. FinishReason: {Reason}", job.SessionId, finishReason);
                        return $"LỖI: API không trả về nội dung. Lý do: {finishReason}";
                    }
                    else
                    {
                        logger.LogWarning("Job {SessionId}, KeyID {KeyId}, Attempt {Attempt}: API Error. Status: {StatusCode}, Body: {Body}", job.SessionId, apiKeyRecord.Id, attempt, response.StatusCode, responseBody);
                        if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                        {
                            await DisableKeyAsync(apiKeyRecord.Id, $"Lỗi {(int)response.StatusCode}", context);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Job {SessionId}, KeyID {KeyId}, Attempt {Attempt}: Exception during API call.", job.SessionId, apiKeyRecord?.Id, attempt);
                }
                finally
                {
                    if (apiKeyRecord != null)
                    {
                        await UpdateApiKeyUsageAsync(apiKeyRecord.Id, context);
                    }
                }
                if (attempt < settings.MaxApiRetries)
                {
                    await Task.Delay(settings.RetryApiDelayMs);
                }
            }

            return $"LỖI: Dịch chunk thất bại sau {settings.MaxApiRetries} lần thử.";
        }

        private async Task<AioApiKey> GetAvailableApiKeyAsync(AppDbContext context, AioTranslationSetting settings, int? excludeKeyId = null)
        {
            var query = context.AioApiKeys.Where(k => k.IsEnabled && k.RequestsToday < settings.RpdPerKey);

            if (excludeKeyId.HasValue)
            {
                query = query.Where(k => k.Id != excludeKeyId.Value);
            }

            var eligibleKeys = await query.ToListAsync();
            if (!eligibleKeys.Any()) return null;
            var shuffledKeys = eligibleKeys.OrderBy(k => Guid.NewGuid()).ToList();

            foreach (var key in shuffledKeys)
            {
                var semaphore = _keyRpmLimiters.GetOrAdd(key.Id, _ => new SemaphoreSlim(settings.RpmPerKey, settings.RpmPerKey));
                if (await semaphore.WaitAsync(0))
                {
                    _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(t => semaphore.Release());
                    return key; 
                }
            }
            var firstKey = shuffledKeys.First();
            var firstSemaphore = _keyRpmLimiters[firstKey.Id];
            await firstSemaphore.WaitAsync();
            _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(t => firstSemaphore.Release());

            return firstKey;
        }

        private async Task UpdateApiKeyUsageAsync(int keyId, AppDbContext context)
        {
            try
            {
                var rowsAffected = await context.AioApiKeys
                    .Where(k => k.Id == keyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.RequestsToday, k => k.RequestsToday + 1));

                if (rowsAffected > 0)
                {
                    var settings = await context.AioTranslationSettings.FindAsync(1) ?? new AioTranslationSetting();
                    var keyAfterUpdate = await context.AioApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == keyId);

                    if (keyAfterUpdate != null && keyAfterUpdate.IsEnabled && keyAfterUpdate.RequestsToday >= settings.RpdPerKey)
                    {
                        await DisableKeyAsync(keyId, $"Hit RPD limit ({settings.RpdPerKey})", context);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update API key usage for Key ID {KeyId}", keyId);
            }
        }

        private async Task DisableKeyAsync(int keyId, string reason, AppDbContext context)
        {
            var key = await context.AioApiKeys.FindAsync(keyId);
            if (key != null && key.IsEnabled)
            {
                key.IsEnabled = false;
                key.DisabledReason = reason;
                await context.SaveChangesAsync();
                _logger.LogWarning("AIO API Key ID {KeyId} has been auto-disabled. Reason: {Reason}", keyId, reason);
            }
        }
        private List<string> SplitTextIntoChunks(string text, int maxChunkSize) { List<string> cks = new List<string>(); if (string.IsNullOrEmpty(text)) return cks; int sIdx = 0; while (sIdx < text.Length) { int len = Math.Min(maxChunkSize, text.Length - sIdx); int eIdx = sIdx + len; if (eIdx < text.Length) { int tEIdx = eIdx - 1; bool fSP = false; int sDist = Math.Min(len / 4, 500); for (int k = 0; k < sDist; ++k) { if (tEIdx - k <= sIdx + 1) break; if (text[tEIdx - k] == '\n' && text[tEIdx - k - 1] == '\n') { eIdx = tEIdx - k + 1; fSP = true; break; } } if (!fSP) { sDist = Math.Min(len / 3, 300); for (int k = 0; k < sDist; ++k) { if (tEIdx - k <= sIdx) break; if (text[tEIdx - k] == '\n') { eIdx = tEIdx - k + 1; fSP = true; break; } } } if (!fSP) { char[] sEnds = { '.', '?', '!', '。', '？', '！', ';', '；', '"', '”', '』', '」' }; sDist = Math.Min(len / 2, 300); int splAt = text.LastIndexOfAny(sEnds, tEIdx, Math.Min(sDist, tEIdx - (sIdx + maxChunkSize / 3))); if (splAt > sIdx + (maxChunkSize / 3)) eIdx = splAt + 1; } } cks.Add(text.Substring(sIdx, eIdx - sIdx).Trim()); sIdx = eIdx; } return cks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList(); }
    }
}