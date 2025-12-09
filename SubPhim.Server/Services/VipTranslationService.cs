using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SubPhim.Server.Data;
using SubPhim.Server.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace SubPhim.Server.Services
{
    public class VipTranslationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<VipTranslationService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEncryptionService _encryptionService;
        private readonly ProxyService _proxyService;
        private readonly ProxyRateLimiterService _proxyRateLimiter;

        // Session storage
        private static readonly ConcurrentDictionary<string, VipTranslationSession> _sessions = new();
        
        // RPM Limiter per API Key
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _keyRpmLimiters = new();
        
        private const int RPM_WAIT_TIMEOUT_MS = 100;
        private const int PROXY_RPM_WAIT_TIMEOUT_MS = 500;
        private const int MAX_PROXY_SEARCH_ATTEMPTS = 10;
        private const int MAX_SRT_LINE_LENGTH = 3000;
        private const int DEFAULT_SETTINGS_ID = 1;

        public VipTranslationService(
            IServiceProvider serviceProvider,
            ILogger<VipTranslationService> logger,
            IHttpClientFactory httpClientFactory,
            IEncryptionService encryptionService,
            ProxyService proxyService,
            ProxyRateLimiterService proxyRateLimiter)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _encryptionService = encryptionService;
            _proxyService = proxyService;
            _proxyRateLimiter = proxyRateLimiter;
        }

        public async Task<VipCreateJobResult> CreateJobAsync(int userId, string targetLanguage, List<SrtLine> lines, string systemInstruction)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            // Check if this is an API key request (negative userId indicates external API key)
            bool isApiKeyRequest = userId < 0;
            
            if (!isApiKeyRequest)
            {
                // Regular user validation and quota checks
                var user = await context.Users.FindAsync(userId);
                if (user == null)
                    return new VipCreateJobResult { Status = "Error", Message = "Tài khoản không tồn tại." };

                // Reset quota if needed (12:00 AM Vietnam time)
                var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
                var lastResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastVipSrtResetUtc, vietnamTimeZone);

                if (lastResetInVietnam.Date < vietnamNow.Date)
                {
                    user.VipSrtLinesUsedToday = 0;
                    user.LastVipSrtResetUtc = DateTime.UtcNow; // Keep full DateTime
                    await context.SaveChangesAsync();
                }

                // Check quota
                int remainingLines = user.DailyVipSrtLimit - user.VipSrtLinesUsedToday;
                if (remainingLines <= 0)
                {
                    return new VipCreateJobResult 
                    { 
                        Status = "Error", 
                        Message = $"Bạn đã hết lượt dịch VIP hôm nay. Giới hạn: {user.DailyVipSrtLimit} dòng/ngày." 
                    };
                }

                if (lines.Count > remainingLines)
                {
                    return new VipCreateJobResult 
                    { 
                        Status = "Error", 
                        Message = $"Không đủ lượt dịch. Yêu cầu: {lines.Count} dòng, còn lại: {remainingLines} dòng." 
                    };
                }

                // Deduct quota
                user.VipSrtLinesUsedToday += lines.Count;
                await context.SaveChangesAsync();
            }

            // Validate line length (reject if any line > 3000 characters) - applies to both users and API keys
            foreach (var line in lines)
            {
                if (line.OriginalText.Length > MAX_SRT_LINE_LENGTH)
                {
                    return new VipCreateJobResult
                    {
                        Status = "Error",
                        Message = $"Dòng {line.Index} vượt quá giới hạn {MAX_SRT_LINE_LENGTH} ký tự. Vui lòng kiểm tra lại file SRT."
                    };
                }
            }

            // Create session
            var sessionId = Guid.NewGuid().ToString();
            var session = new VipTranslationSession
            {
                SessionId = sessionId,
                UserId = userId,
                TargetLanguage = targetLanguage,
                SystemInstruction = systemInstruction,
                Status = VipJobStatus.Processing,
                TotalLines = lines.Count,
                TranslatedLines = new ConcurrentBag<TranslatedSrtLine>(),
                CreatedAt = DateTime.UtcNow,
                Cts = new CancellationTokenSource()
            };

            _sessions[sessionId] = session;

            // Start translation in background
            _ = Task.Run(async () => await ProcessTranslationAsync(sessionId, lines));

            return new VipCreateJobResult 
            { 
                Status = "Accepted", 
                SessionId = sessionId 
            };
        }

        private async Task ProcessTranslationAsync(string sessionId, List<SrtLine> lines)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                var settings = await context.VipTranslationSettings.FindAsync(DEFAULT_SETTINGS_ID);
                if (settings == null)
                {
                    settings = new VipTranslationSetting { Id = DEFAULT_SETTINGS_ID };
                    context.VipTranslationSettings.Add(settings);
                    await context.SaveChangesAsync();
                }

                // Get active model
                var activeModel = await context.VipAvailableApiModels
                    .FirstOrDefaultAsync(m => m.IsActive);
                
                if (activeModel == null)
                {
                    session.Status = VipJobStatus.Failed;
                    session.ErrorMessage = "Không tìm thấy model đang hoạt động.";
                    return;
                }

                // Batch processing
                int batchSize = settings.BatchSize;
                var batches = lines.Select((line, index) => new { line, index })
                    .GroupBy(x => x.index / batchSize)
                    .Select(g => g.Select(x => x.line).ToList())
                    .ToList();

                foreach (var batch in batches)
                {
                    if (session.Cts.Token.IsCancellationRequested)
                        break;

                    await TranslateBatchAsync(session, batch, activeModel.ModelName, settings, context);
                    
                    if (batch != batches.Last())
                        await Task.Delay(settings.DelayBetweenBatchesMs, session.Cts.Token);
                }

                session.Status = VipJobStatus.Completed;
                session.CompletedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing VIP translation session {SessionId}", sessionId);
                session.Status = VipJobStatus.Failed;
                session.ErrorMessage = ex.Message;
            }
        }

        private async Task TranslateBatchAsync(VipTranslationSession session, List<SrtLine> batch, 
            string modelName, VipTranslationSetting settings, AppDbContext context)
        {
            var inputLines = batch.Select(line => new { index = line.Index, text = line.OriginalText }).ToList();
            string inputJson = JsonConvert.SerializeObject(inputLines);

            int retryCount = 0;
            while (retryCount <= settings.MaxRetries)
            {
                if (session.Cts.Token.IsCancellationRequested)
                    break;

                try
                {
                    // Get API key
                    var apiKey = await GetAvailableKeyAsync(context, settings.Rpm);
                    if (apiKey == null)
                    {
                        _logger.LogError("No available VIP API keys");
                        await Task.Delay(settings.RetryDelayMs);
                        retryCount++;
                        continue;
                    }

                    // Get proxy
                    var proxy = await _proxyService.GetNextProxyAsync();
                    if (proxy == null)
                    {
                        _logger.LogWarning("No proxy available, using direct connection");
                    }

                    // Attempt to acquire proxy RPM slot if proxy exists
                    if (proxy != null)
                    {
                        string requestId = Guid.NewGuid().ToString();
                        string? slotId = await _proxyRateLimiter.TryAcquireSlotAsync(
                            proxy.Id, requestId, session.Cts.Token);
                        
                        if (slotId == null)
                        {
                            _logger.LogWarning("Proxy {ProxyId} RPM limit reached, retrying", proxy.Id);
                            await Task.Delay(settings.RetryDelayMs);
                            retryCount++;
                            continue;
                        }
                    }

                    // Translate
                    var result = await CallGeminiApiAsync(apiKey, modelName, inputJson, session, settings, proxy);
                    
                    if (result.Success)
                    {
                        // Parse results and add to session
                        var translatedLines = ParseTranslationResult(result.ResponseText, batch);
                        foreach (var line in translatedLines)
                        {
                            session.TranslatedLines.Add(line);
                        }

                        // Update API key usage
                        apiKey.RequestsToday++;
                        apiKey.TotalTokensUsed += result.TokensUsed;
                        await context.SaveChangesAsync();
                        
                        return; // Success
                    }
                    else
                    {
                        _logger.LogWarning("Translation failed: {Error}", result.ErrorMessage);
                        retryCount++;
                        if (retryCount <= settings.MaxRetries)
                            await Task.Delay(settings.RetryDelayMs);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error translating batch in session {SessionId}", session.SessionId);
                    retryCount++;
                    if (retryCount <= settings.MaxRetries)
                        await Task.Delay(settings.RetryDelayMs);
                }
            }

            // If we reach here, all retries failed - mark lines as failed
            foreach (var line in batch)
            {
                session.TranslatedLines.Add(new TranslatedSrtLine
                {
                    Index = line.Index,
                    TranslatedText = line.OriginalText,
                    Success = false
                });
            }
        }

        private async Task<VipApiKey?> GetAvailableKeyAsync(AppDbContext context, int rpm)
        {
            var now = DateTime.UtcNow;
            
            var keys = await context.VipApiKeys
                .Where(k => k.IsEnabled)
                .Where(k => k.TemporaryCooldownUntil == null || k.TemporaryCooldownUntil < now)
                .OrderBy(k => k.RequestsToday)
                .ToListAsync();

            foreach (var key in keys)
            {
                var limiter = _keyRpmLimiters.GetOrAdd(key.Id, _ => new SemaphoreSlim(rpm, rpm));
                
                if (await limiter.WaitAsync(RPM_WAIT_TIMEOUT_MS))
                {
                    // Release after 1 minute
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(60000);
                        limiter.Release();
                    });
                    
                    return key;
                }
            }

            return null;
        }

        private async Task<GeminiCallResult> CallGeminiApiAsync(VipApiKey apiKey, string modelName, 
            string inputJson, VipTranslationSession session, VipTranslationSetting settings, Proxy? proxy)
        {
            try
            {
                var decryptedKey = _encryptionService.Decrypt(apiKey.EncryptedApiKey, apiKey.Iv);
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={decryptedKey}";

                // Build generationConfig using JObject for snake_case field names (matching Gemini API format)
                var generationConfig = new JObject 
                { 
                    ["temperature"] = (double)settings.Temperature, 
                    ["maxOutputTokens"] = settings.MaxOutputTokens 
                };
                if (settings.EnableThinkingBudget) 
                {
                    generationConfig["thinking_config"] = new JObject { ["thinking_budget"] = settings.ThinkingBudget };
                }

                // Build user content with input JSON
                string userContent = $"Dịch sang {session.TargetLanguage}:\n{inputJson}";
                
                // Use proper Gemini API format with system_instruction as separate field
                // This matches the AioLauncherService implementation
                var requestBody = new
                {
                    contents = new[] { new { role = "user", parts = new[] { new { text = userContent } } } },
                    system_instruction = new { parts = new[] { new { text = session.SystemInstruction } } },
                    generationConfig
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpClient httpClient;
                if (proxy != null)
                {
                    httpClient = _proxyService.CreateHttpClientWithProxy(proxy);
                    httpClient.Timeout = TimeSpan.FromMinutes(5);
                }
                else
                {
                    httpClient = _httpClientFactory.CreateClient();
                    httpClient.Timeout = TimeSpan.FromMinutes(5);
                }

                var response = await httpClient.PostAsync(url, content);
                var responseText = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Gemini API error: {Status} - {Response}", response.StatusCode, responseText);
                    return new GeminiCallResult 
                    { 
                        Success = false, 
                        ErrorMessage = $"API Error: {response.StatusCode}" 
                    };
                }

                // Check if response is valid JSON before parsing
                // Proxies may return HTML error pages even with HTTP 200 status
                var trimmedResponse = responseText.TrimStart();
                if (string.IsNullOrWhiteSpace(trimmedResponse) || (!trimmedResponse.StartsWith("{") && !trimmedResponse.StartsWith("[")))
                {
                    // Log the first 500 characters of the non-JSON response for debugging
                    var logPreview = responseText.Length > 500 ? responseText[..500] + "..." : responseText;
                    _logger.LogError("Gemini API returned non-JSON response (possibly proxy/firewall HTML page): {Response}", logPreview);
                    return new GeminiCallResult
                    {
                        Success = false,
                        ErrorMessage = "API returned non-JSON response (proxy/firewall issue)"
                    };
                }

                var responseJson = JObject.Parse(responseText);
                var textResult = responseJson["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
                var tokensUsed = responseJson["usageMetadata"]?["totalTokenCount"]?.Value<int>() ?? 0;

                return new GeminiCallResult
                {
                    Success = true,
                    ResponseText = textResult ?? "",
                    TokensUsed = tokensUsed
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception calling Gemini API");
                return new GeminiCallResult 
                { 
                    Success = false, 
                    ErrorMessage = ex.Message 
                };
            }
        }

        private List<TranslatedSrtLine> ParseTranslationResult(string responseText, List<SrtLine> originalBatch)
        {
            var result = new List<TranslatedSrtLine>();
            
            try
            {
                // Try to parse as JSON array
                var cleanedText = responseText.Trim();
                if (cleanedText.StartsWith("```json"))
                    cleanedText = cleanedText.Substring(7);
                if (cleanedText.StartsWith("```"))
                    cleanedText = cleanedText.Substring(3);
                if (cleanedText.EndsWith("```"))
                    cleanedText = cleanedText.Substring(0, cleanedText.Length - 3);
                
                cleanedText = cleanedText.Trim();
                
                var parsed = JsonConvert.DeserializeObject<List<TranslationItem>>(cleanedText);
                if (parsed != null)
                {
                    foreach (var item in parsed)
                    {
                        result.Add(new TranslatedSrtLine
                        {
                            Index = item.Index,
                            TranslatedText = item.Text,
                            Success = true
                        });
                    }
                }
            }
            catch
            {
                // Fallback: return original text
                foreach (var line in originalBatch)
                {
                    result.Add(new TranslatedSrtLine
                    {
                        Index = line.Index,
                        TranslatedText = line.OriginalText,
                        Success = false
                    });
                }
            }

            return result;
        }

        public async Task<List<TranslatedSrtLine>?> GetResultsAsync(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return null;

            // Update last polled time for auto-cleanup tracking
            session.LastPolledAt = DateTime.UtcNow;
            
            return session.TranslatedLines.OrderBy(l => l.Index).ToList();
        }

        public async Task<(bool IsCompleted, string? ErrorMessage)> GetStatusAsync(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return (true, "Session không tồn tại hoặc đã hết hạn.");

            // Update last polled time for auto-cleanup tracking
            session.LastPolledAt = DateTime.UtcNow;
            
            bool isCompleted = session.Status == VipJobStatus.Completed || session.Status == VipJobStatus.Failed;
            return (isCompleted, session.ErrorMessage);
        }

        public async Task<bool> CancelJobAsync(string sessionId, int userId)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return false;

            if (session.UserId != userId)
                return false;

            session.Cts.Cancel();
            session.Status = VipJobStatus.Failed;
            session.ErrorMessage = "Job đã bị hủy bởi người dùng.";

            // Refund unused lines (only for regular users, not API keys)
            bool isApiKeyRequest = userId < 0;
            if (!isApiKeyRequest)
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var user = await context.Users.FindAsync(userId);
                
                if (user != null)
                {
                    int translatedCount = session.TranslatedLines.Count(l => l.Success);
                    int refundLines = session.TotalLines - translatedCount;
                    
                    if (refundLines > 0)
                    {
                        user.VipSrtLinesUsedToday -= refundLines;
                        await context.SaveChangesAsync();
                        _logger.LogInformation("Refunded {Count} lines to user {UserId}", refundLines, userId);
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Cleanup stale API key sessions that have completed but not been polled for 5 minutes.
        /// For API key requests (userId < 0), sessions are auto-deleted if:
        /// - The job is completed or failed, AND
        /// - Either never polled after completion, OR not polled for 5 minutes since completion
        /// </summary>
        /// <returns>Number of sessions cleaned up</returns>
        public int CleanupStaleApiKeySessions()
        {
            var now = DateTime.UtcNow;
            var staleThreshold = TimeSpan.FromMinutes(5);
            int cleanedCount = 0;
            
            // Snapshot of session keys for thread-safe iteration
            // ConcurrentDictionary methods (TryGetValue, TryRemove) are atomic
            var sessionIds = _sessions.Keys.ToList();
            
            foreach (var sessionId in sessionIds)
            {
                if (!_sessions.TryGetValue(sessionId, out var session))
                    continue;
                    
                // Only process API key sessions (negative userId)
                if (session.UserId >= 0)
                    continue;
                    
                // Only process completed or failed sessions
                if (session.Status != VipJobStatus.Completed && session.Status != VipJobStatus.Failed)
                    continue;
                
                // Session must have CompletedAt set for proper cleanup timing
                // If CompletedAt is null for a completed/failed session, it's a data inconsistency - skip and log
                if (session.CompletedAt == null)
                {
                    _logger.LogWarning(
                        "API key session {SessionId} is {Status} but has no CompletedAt timestamp, skipping cleanup",
                        sessionId, session.Status);
                    continue;
                }
                    
                // Check if session is stale (not polled for 5 minutes after completion)
                // Use LastPolledAt if available, otherwise use CompletedAt
                var lastActivity = session.LastPolledAt ?? session.CompletedAt.Value;
                
                if (now - lastActivity >= staleThreshold)
                {
                    // Remove the session
                    if (_sessions.TryRemove(sessionId, out var removed))
                    {
                        cleanedCount++;
                        _logger.LogInformation(
                            "Cleaned up stale API key session {SessionId} (ApiKeyId={ApiKeyId}, CompletedAt={CompletedAt}, LastPolledAt={LastPolledAt})",
                            sessionId, -removed.UserId, removed.CompletedAt, removed.LastPolledAt);
                            
                        // Dispose the CancellationTokenSource
                        try 
                        { 
                            removed.Cts?.Dispose(); 
                        } 
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error disposing CancellationTokenSource for session {SessionId}", sessionId);
                        }
                    }
                }
            }
            
            return cleanedCount;
        }

        private class TranslationItem
        {
            [JsonProperty("index")]
            public int Index { get; set; }
            
            [JsonProperty("text")]
            public string Text { get; set; }
        }

        private class GeminiCallResult
        {
            public bool Success { get; set; }
            public string ResponseText { get; set; }
            public int TokensUsed { get; set; }
            public string ErrorMessage { get; set; }
        }
    }

    public class VipTranslationSession
    {
        public string SessionId { get; set; }
        public int UserId { get; set; }
        public string TargetLanguage { get; set; }
        public string SystemInstruction { get; set; }
        public VipJobStatus Status { get; set; }
        public int TotalLines { get; set; }
        public ConcurrentBag<TranslatedSrtLine> TranslatedLines { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public CancellationTokenSource Cts { get; set; }
        
        /// <summary>
        /// Timestamp of the last time this session was polled for results.
        /// Used for auto-cleanup of API key sessions that are not polled after completion.
        /// </summary>
        public DateTime? LastPolledAt { get; set; }
    }

    public enum VipJobStatus
    {
        Processing,
        Completed,
        Failed
    }

    public class VipCreateJobResult
    {
        public string Status { get; set; } // "Accepted", "Error"
        public string? Message { get; set; }
        public string? SessionId { get; set; }
    }
}
