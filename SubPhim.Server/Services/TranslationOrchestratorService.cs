using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SubPhim.Server.Data;
using SubPhim.Server.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SubPhim.Server.Services
{
    public class TranslationOrchestratorService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<TranslationOrchestratorService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        // === BẮT ĐẦU THÊM: Cooldown Service ===
        private readonly ApiKeyCooldownService _cooldownService;
        // === KẾT THÚC THÊM ===

        // === BẮT ĐẦU THÊM: Random User-Agent per API Key ===
        private static readonly ConcurrentDictionary<int, string> _apiKeyUserAgents = new();
        private static readonly string[] _userAgentTemplates = new[]
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.{1}.{2} Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.{1}.{2} Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{0}.0) Gecko/20100101 Firefox/{0}.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:{0}.0) Gecko/20100101 Firefox/{0}.0",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.{1}.{2} Safari/537.36 Edg/{0}.0.{1}.{2}",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.{1}.{2} Safari/537.36",
            "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:{0}.0) Gecko/20100101 Firefox/{0}.0"
        };

        private static string GetUserAgentForApiKey(int apiKeyId)
        {
            return _apiKeyUserAgents.GetOrAdd(apiKeyId, id =>
            {
                var random = new Random(id); // Sử dụng apiKeyId làm seed để đảm bảo cùng key luôn có cùng User-Agent
                var template = _userAgentTemplates[random.Next(_userAgentTemplates.Length)];
                var majorVersion = random.Next(100, 131); // Chrome/Firefox versions
                var buildNumber = random.Next(1000, 9999);
                var patchNumber = random.Next(100, 999);
                return string.Format(template, majorVersion, buildNumber, patchNumber);
            });
        }
        // === KẾT THÚC THÊM ===

        public record CreateJobResult(string Status, string Message, string SessionId = null, int RemainingLines = 0);

        public TranslationOrchestratorService(
            IServiceProvider serviceProvider, 
            ILogger<TranslationOrchestratorService> logger, 
            IHttpClientFactory httpClientFactory,
            ApiKeyCooldownService cooldownService)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cooldownService = cooldownService;
        }

        public async Task<CreateJobResult> CreateJobAsync(int userId, string genre, string targetLanguage, List<SrtLine> allLines, string systemInstruction, bool acceptPartial)
        {
            _logger.LogInformation("GATEKEEPER: Job creation request for User ID {UserId}. AcceptPartial={AcceptPartial}", userId, acceptPartial);

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var user = await context.Users.FindAsync(userId);
            if (user == null) throw new InvalidOperationException("User not found.");

            var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
            var lastResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastLocalSrtResetUtc, vietnamTimeZone);
            if (lastResetInVietnam.Date < vietnamNow.Date)
            {
                user.LocalSrtLinesUsedToday = 0;
                user.LastLocalSrtResetUtc = DateTime.UtcNow.Date;
                await context.SaveChangesAsync();
            }

            int remainingLines = user.DailyLocalSrtLimit - user.LocalSrtLinesUsedToday;
            int requestedLines = allLines.Count;

            if (requestedLines <= remainingLines)
            {
                user.LocalSrtLinesUsedToday += requestedLines;
                var sessionId = await CreateJobInDb(user, genre, targetLanguage, systemInstruction, allLines, context);
                _ = ProcessJob(sessionId, user.Tier);
                return new CreateJobResult("Accepted", "OK", sessionId);
            }

            if (remainingLines > 0)
            {
                if (acceptPartial)
                {
                    var partialLines = allLines.Take(remainingLines).ToList();
                    user.LocalSrtLinesUsedToday += partialLines.Count;
                    var sessionId = await CreateJobInDb(user, genre, targetLanguage, systemInstruction, partialLines, context);
                    _ = ProcessJob(sessionId, user.Tier);
                    return new CreateJobResult("Accepted", "OK", sessionId);
                }
                else
                {
                    string message = $"Bạn không đủ lượt dịch Local API. Yêu cầu: {requestedLines} dòng, còn lại: {remainingLines} dòng.\n\nBạn có muốn dịch {remainingLines} dòng đầu tiên không?";
                    return new CreateJobResult("PartialContent", message, RemainingLines: remainingLines);
                }
            }

            string errorMessage = $"Bạn đã hết {user.DailyLocalSrtLimit} lượt dịch Local API trong ngày.";
            return new CreateJobResult("Error", errorMessage);
        }

        private async Task<string> CreateJobInDb(User user, string genre, string targetLanguage, string systemInstruction, List<SrtLine> linesToProcess, AppDbContext context)
        {
            var sessionId = Guid.NewGuid().ToString();
            var newJob = new TranslationJobDb
            {
                SessionId = sessionId,
                UserId = user.Id,
                Genre = genre,
                TargetLanguage = targetLanguage,
                SystemInstruction = systemInstruction,
                Status = JobStatus.Pending,
                OriginalLines = linesToProcess.Select(l => new OriginalSrtLineDb { LineIndex = l.Index, OriginalText = l.OriginalText }).ToList()
            };
            context.TranslationJobs.Add(newJob);
            await context.SaveChangesAsync();
            _logger.LogInformation("[DB] Created Job {SessionId} with {LineCount} lines for user {Username}", sessionId, linesToProcess.Count, user.Username);
            return sessionId;
        }

        public async Task<List<TranslatedSrtLine>> GetJobResultsAsync(string sessionId)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var resultsDb = await context.TranslatedSrtLines.Where(l => l.SessionId == sessionId).ToListAsync();
            if (resultsDb.Any())
            {
                context.TranslatedSrtLines.RemoveRange(resultsDb);
                await context.SaveChangesAsync();
            }
            await transaction.CommitAsync();
            return resultsDb.Select(l => new TranslatedSrtLine { Index = l.LineIndex, TranslatedText = l.TranslatedText, Success = l.Success }).ToList();
        }

        public async Task<(bool isCompleted, string errorMessage)> GetJobStatusAsync(string sessionId)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await context.TranslationJobs.AsNoTracking().FirstOrDefaultAsync(j => j.SessionId == sessionId);
            if (job == null) return (true, "Session không tồn tại.");
            bool isFinished = job.Status == JobStatus.Completed || job.Status == JobStatus.Failed;
            return (isFinished, job.ErrorMessage);
        }

        private async Task ProcessJob(string sessionId, SubscriptionTier userTier)
        {
            _logger.LogInformation("Starting HIGH-SPEED processing for job {SessionId} using {Tier} tier API pool", sessionId, userTier);
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            ApiPoolType poolToUse = (userTier == SubscriptionTier.Free) ? ApiPoolType.Free : ApiPoolType.Paid;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var encryptionService = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

                var job = await context.TranslationJobs.FindAsync(new object[] { sessionId }, cts.Token);
                if (job == null)
                {
                    _logger.LogError("Job {SessionId} not found in database at the start of processing.", sessionId);
                    return;
                }

                job.Status = JobStatus.Processing;
                await context.SaveChangesAsync(cts.Token);

                var settings = await context.LocalApiSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, cts.Token) ?? new LocalApiSetting();
                var activeModel = await context.AvailableApiModels.AsNoTracking().FirstOrDefaultAsync(m => m.IsActive && m.PoolType == poolToUse, cts.Token);
                if (activeModel == null) throw new Exception($"Không có model nào đang hoạt động cho nhóm '{poolToUse}'.");

                // === SỬA ĐỔI: Lọc bỏ keys đang trong cooldown ===
                var enabledKeys = await context.ManagedApiKeys.AsNoTracking()
                    .Where(k => k.IsEnabled && k.PoolType == poolToUse)
                    .ToListAsync(cts.Token);
                
                // Filter out keys in cooldown
                enabledKeys = enabledKeys.Where(k => !_cooldownService.IsInCooldown(k.Id)).ToList();
                
                if (!enabledKeys.Any()) throw new Exception($"Không có API key nào đang hoạt động cho nhóm '{poolToUse}' (có thể tất cả đang trong cooldown).");
                // === KẾT THÚC SỬA ĐỔI ===

                const int RPM_PER_PAID_KEY = 150;
                const int RPM_PER_FREE_KEY = 15;
                int rpmPerKey = (poolToUse == ApiPoolType.Paid) ? RPM_PER_PAID_KEY : RPM_PER_FREE_KEY;
                int totalRpm = enabledKeys.Count * rpmPerKey;

                using var rpmLimiter = new SemaphoreSlim(totalRpm, totalRpm);
                using var rpmResetTimer = new Timer(_ => {
                    try { if (rpmLimiter.CurrentCount < totalRpm) rpmLimiter.Release(totalRpm - rpmLimiter.CurrentCount); }
                    catch (ObjectDisposedException) { }
                }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

                var allLines = await context.OriginalSrtLines.AsNoTracking()
                    .Where(l => l.SessionId == sessionId)
                    .OrderBy(l => l.LineIndex)
                    .ToListAsync(cts.Token);

                var batches = allLines
                    .Select((line, index) => new { line, index })
                    .GroupBy(x => x.index / settings.BatchSize)
                    .Select(g => g.Select(x => x.line).ToList())
                    .ToList();

                var processingTasks = new List<Task>();
                
                _logger.LogInformation("Job {SessionId}: Processing {BatchCount} batches with {DelayMs}ms delay between batches", 
                    sessionId, batches.Count, settings.DelayBetweenBatchesMs);

                for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
                {
                    if (cts.IsCancellationRequested) break;
                    
                    var batch = batches[batchIndex];
                    
                    // === BẮT ĐẦU THÊM: Delay giữa các batch theo cài đặt ===
                    if (batchIndex > 0 && settings.DelayBetweenBatchesMs > 0)
                    {
                        _logger.LogInformation("Job {SessionId}: Waiting {DelayMs}ms before batch {BatchIndex}/{TotalBatches}", 
                            sessionId, settings.DelayBetweenBatchesMs, batchIndex + 1, batches.Count);
                        await Task.Delay(settings.DelayBetweenBatchesMs, cts.Token);
                    }
                    // === KẾT THÚC THÊM ===
                    
                    await rpmLimiter.WaitAsync(cts.Token);
                    processingTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            // === SỬA ĐỔI: Gọi TranslateBatchAsync với context và settings ===
                            var (translatedBatch, tokensUsed, usedKeyId) = await TranslateBatchAsync(
                                batch, job, settings, activeModel.ModelName, job.SystemInstruction, 
                                poolToUse, encryptionService, cts.Token);
                            
                            await SaveResultsToDb(sessionId, translatedBatch);
                            
                            if (usedKeyId.HasValue)
                            {
                                await UpdateUsageInDb(usedKeyId.Value, tokensUsed);
                                
                                // Reset cooldown nếu batch thành công
                                await _cooldownService.ResetCooldownAsync(usedKeyId.Value);
                            }
                            // === KẾT THÚC SỬA ĐỔI ===
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi xử lý batch cho job {SessionId}", sessionId);
                            var errorResults = batch.Select(l => new TranslatedSrtLineDb
                            {
                                SessionId = sessionId,
                                LineIndex = l.LineIndex,
                                TranslatedText = "[LỖI EXCEPTION]",
                                Success = false,
                                ErrorType = "EXCEPTION",
                                ErrorDetail = ex.Message
                            }).ToList();
                            await SaveResultsToDb(sessionId, errorResults);
                        }
                    }, cts.Token));
                }

                await Task.WhenAll(processingTasks);
                _logger.LogInformation("All batches for job {SessionId} completed. Checking for errors and refunding if needed...", sessionId);

                await CheckAndRefundFailedLinesAsync(sessionId);
                await UpdateJobStatus(sessionId, JobStatus.Completed);

            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Job {SessionId} đã bị hủy do timeout.", sessionId);
                await UpdateJobStatus(sessionId, JobStatus.Failed, "Job timed out.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi nghiêm trọng trong quá trình xử lý job {SessionId}", sessionId);
                await UpdateJobStatus(sessionId, JobStatus.Failed, ex.Message);
            }
        }

        // ===== THÊM MỚI: Phương thức kiểm tra và hoàn trả lượt dịch =====
        private async Task CheckAndRefundFailedLinesAsync(string sessionId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var job = await context.TranslationJobs
                    .Include(j => j.TranslatedLines)
                    .FirstOrDefaultAsync(j => j.SessionId == sessionId);

                if (job == null || job.HasRefunded)
                {
                    _logger.LogWarning("Job {SessionId} not found or already refunded", sessionId);
                    return;
                }

                // Đếm số dòng bị lỗi (Success = false)
                var failedLines = job.TranslatedLines.Where(l => !l.Success).ToList();
                int failedCount = failedLines.Count;

                if (failedCount > 0)
                {
                    _logger.LogWarning("Job {SessionId} has {FailedCount} failed lines. Refunding...", sessionId, failedCount);

                    // Lấy thông tin user
                    var user = await context.Users.FindAsync(job.UserId);
                    if (user != null)
                    {
                        // Hoàn trả số dòng bị lỗi
                        user.LocalSrtLinesUsedToday = Math.Max(0, user.LocalSrtLinesUsedToday - failedCount);

                        _logger.LogInformation("Refunded {FailedCount} lines to User ID {UserId}. New usage: {NewUsage}/{Limit}",
                            failedCount, user.Id, user.LocalSrtLinesUsedToday, user.DailyLocalSrtLimit);
                    }

                    // Cập nhật job với thông tin lỗi
                    job.FailedLinesCount = failedCount;
                    job.HasRefunded = true;

                    // Tạo error details dưới dạng JSON
                    var errorSummary = failedLines
                        .GroupBy(l => l.ErrorType ?? "UNKNOWN")
                        .Select(g => new { ErrorType = g.Key, Count = g.Count() })
                        .ToList();

                    job.ErrorDetails = JsonConvert.SerializeObject(errorSummary);

                    await context.SaveChangesAsync();

                    _logger.LogInformation("Refund completed for Job {SessionId}. Error summary: {ErrorSummary}",
                        sessionId, job.ErrorDetails);
                }
                else
                {
                    _logger.LogInformation("Job {SessionId} completed successfully with no failed lines", sessionId);
                    job.HasRefunded = true; // Đánh dấu đã kiểm tra
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during refund process for Job {SessionId}", sessionId);
            }
        }
        // ===== KẾT THÚC THÊM MỚI =====

        private async Task<(List<TranslatedSrtLineDb> results, int tokensUsed, int? usedKeyId)> TranslateBatchAsync(
            List<OriginalSrtLineDb> batch, TranslationJobDb job, LocalApiSetting settings,
            string modelName, string systemInstruction, ApiPoolType poolType, 
            IEncryptionService encryptionService, CancellationToken token)
        {
            var payloadBuilder = new StringBuilder();
            foreach (var line in batch)
            {
                payloadBuilder.AppendLine($"{line.LineIndex}: {line.OriginalText.Replace("\r\n", " ").Replace("\n", " ")}");
            }
            string payload = payloadBuilder.ToString().TrimEnd();

            var generationConfig = new JObject
            {
                ["temperature"] = 1,
                ["topP"] = 0.95,
                ["maxOutputTokens"] = 15000
            };

            if (settings.EnableThinkingBudget && settings.ThinkingBudget > 0)
            {
                generationConfig["thinking_config"] = new JObject { ["thinking_budget"] = settings.ThinkingBudget };
            }

            var requestPayloadObject = new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = $"Dịch các câu thoại sau sang {job.TargetLanguage}:\n\n{payload}" } } } },
                system_instruction = new { parts = new[] { new { text = systemInstruction } } },
                generationConfig
            };

            string jsonPayload = JsonConvert.SerializeObject(requestPayloadObject, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

            // === BẮT ĐẦU SỬA ĐỔI: Retry với auto key rotation ===
            HashSet<int> triedKeyIds = new HashSet<int>();
            int? successfulKeyId = null;
            
            for (int attempt = 1; attempt <= settings.MaxRetries; attempt++)
            {
                ManagedApiKey selectedKey = null;
                
                try
                {
                    // Lấy key khả dụng, loại trừ những key đã thử
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    
                    selectedKey = await GetAvailableKeyForBatchAsync(context, poolType, triedKeyIds);
                    
                    if (selectedKey == null)
                    {
                        _logger.LogWarning("Batch: Không còn key nào khả dụng sau {Attempts} lần thử với {TriedKeys} keys",
                            attempt, triedKeyIds.Count);
                        break; // Không còn key nào để thử
                    }

                    triedKeyIds.Add(selectedKey.Id);
                    
                    var apiKey = encryptionService.Decrypt(selectedKey.EncryptedApiKey, selectedKey.Iv);
                    string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";

                    _logger.LogInformation("Batch attempt {Attempt}/{MaxRetries}: Using Key ID {KeyId}", 
                        attempt, settings.MaxRetries, selectedKey.Id);

                    var (responseText, tokensUsed, errorType, errorDetail, httpStatusCode) = 
                        await CallApiWithRetryAsync(apiUrl, jsonPayload, settings, selectedKey.Id, token);

                    // === XỬ LÝ LỖI 429 ===
                    if (httpStatusCode == 429)
                    {
                        _logger.LogWarning("Key ID {KeyId} gặp lỗi 429 Rate Limit. Đặt vào cooldown và chờ {Delay}ms trước khi thử key khác.", 
                            selectedKey.Id, settings.RetryDelayMs);
                        
                        await _cooldownService.SetCooldownAsync(selectedKey.Id, $"HTTP 429 on attempt {attempt}");
                        
                        if (attempt < settings.MaxRetries)
                        {
                            // === SỬA LẠI: Delay theo cài đặt từ panel thay vì cố định ===
                            await Task.Delay(settings.RetryDelayMs, token); // Tuân theo cài đặt RetryDelayMs từ Admin panel
                            continue; // Thử lại với key khác
                        }
                    }
                    
                    // === XỬ LÝ CÁC LỖI NGHIÊM TRỌNG KHÁC ===
                    if (httpStatusCode == 401 || httpStatusCode == 403 || 
                        errorType == "INVALID_ARGUMENT" || errorDetail?.Contains("API key") == true)
                    {
                        _logger.LogError("Key ID {KeyId} gặp lỗi nghiêm trọng ({ErrorType}). Vô hiệu hóa vĩnh viễn và thử key khác NGAY.", 
                            selectedKey.Id, errorType);
                        
                        await _cooldownService.DisableKeyPermanentlyAsync(selectedKey.Id, 
                            $"{errorType}: {errorDetail}");
                        
                        if (attempt < settings.MaxRetries)
                        {
                            // Không delay cho lỗi nghiêm trọng - thử ngay với key khác
                            continue;
                        }
                    }

                    // === THÀNH CÔNG ===
                    if (responseText != null && !responseText.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase))
                    {
                        successfulKeyId = selectedKey.Id;
                        
                        var results = new List<TranslatedSrtLineDb>();
                        var translatedLinesDict = new Dictionary<int, string>();
                        var regex = new Regex(@"^\s*(\d+):\s*(.*)$", RegexOptions.Multiline);
                        
                        foreach (Match m in regex.Matches(responseText))
                        {
                            if (int.TryParse(m.Groups[1].Value, out int idx))
                                translatedLinesDict[idx] = m.Groups[2].Value.Trim();
                        }

                        foreach (var line in batch)
                        {
                            if (translatedLinesDict.TryGetValue(line.LineIndex, out string translated))
                                results.Add(new TranslatedSrtLineDb
                                {
                                    SessionId = job.SessionId,
                                    LineIndex = line.LineIndex,
                                    TranslatedText = string.IsNullOrWhiteSpace(translated) ? "[API DỊCH RỖNG]" : translated,
                                    Success = true
                                });
                            else
                                results.Add(new TranslatedSrtLineDb
                                {
                                    SessionId = job.SessionId,
                                    LineIndex = line.LineIndex,
                                    TranslatedText = "[API KHÔNG TRẢ VỀ DÒNG NÀY]",
                                    Success = false,
                                    ErrorType = "MISSING_LINE",
                                    ErrorDetail = "API không trả về dòng này trong response"
                                });
                        }
                        
                        return (results, tokensUsed, successfulKeyId);
                    }
                    
                    // === LỖI KHÁC (không phải 429, không nghiêm trọng) ===
                    if (attempt < settings.MaxRetries)
                    {
                        // Tính delay theo exponential backoff với hệ số từ cài đặt
                        int delayMs = settings.RetryDelayMs * attempt;
                        
                        _logger.LogWarning("Batch attempt {Attempt} failed with Key ID {KeyId}. Error: {Error}. Retrying after {Delay}ms...",
                            attempt, selectedKey.Id, errorType, delayMs);
                        
                        await Task.Delay(delayMs, token);
                        continue;
                    }

                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception during batch translation attempt {Attempt} with Key ID {KeyId}", 
                        attempt, selectedKey?.Id);
                    
                    if (attempt >= settings.MaxRetries) break;
                    await Task.Delay(settings.RetryDelayMs * attempt, token);
                }
            }
            
            // === TẤT CẢ ATTEMPTS ĐỀU THẤT BẠI ===
            _logger.LogError("Batch translation failed after {MaxRetries} attempts with {KeyCount} different keys",
                settings.MaxRetries, triedKeyIds.Count);
            
            var failedResults = batch.Select(l => new TranslatedSrtLineDb
            {
                SessionId = job.SessionId,
                LineIndex = l.LineIndex,
                TranslatedText = "[LỖI: Không thể dịch sau nhiều lần thử]",
                Success = false,
                ErrorType = "MAX_RETRIES_EXCEEDED",
                ErrorDetail = $"Failed after {settings.MaxRetries} attempts with {triedKeyIds.Count} keys"
            }).ToList();
            
            return (failedResults, 0, null);
            // === KẾT THÚC SỬA ĐỔI ===
        }

        /// <summary>
        /// Lấy key khả dụng cho batch, loại trừ những key đã thử và đang trong cooldown
        /// </summary>
        private async Task<ManagedApiKey> GetAvailableKeyForBatchAsync(
            AppDbContext context, ApiPoolType poolType, HashSet<int> excludeKeyIds)
        {
            var query = context.ManagedApiKeys
                .Where(k => k.IsEnabled && k.PoolType == poolType);
            
            if (excludeKeyIds.Any())
            {
                query = query.Where(k => !excludeKeyIds.Contains(k.Id));
            }
            
            var eligibleKeys = await query.ToListAsync();
            
            // Filter out keys in cooldown (in-memory check)
            eligibleKeys = eligibleKeys
                .Where(k => !_cooldownService.IsInCooldown(k.Id))
                .ToList();
            
            if (!eligibleKeys.Any()) return null;
            
            // Random selection từ pool
            return eligibleKeys[Random.Shared.Next(eligibleKeys.Count)];
        }
        
        // ===== SỬA ĐỔI: Thêm tracking lỗi chi tiết và random User-Agent ===== 
        private async Task<(string responseText, int tokensUsed, string errorType, string errorDetail, int httpStatusCode)> CallApiWithRetryAsync(
            string url, string jsonPayload, LocalApiSetting settings, int apiKeyId, CancellationToken token)
        {
            // Lấy User-Agent cố định cho API key này
            string userAgent = GetUserAgentForApiKey(apiKeyId);
            
            for (int attempt = 1; attempt <= settings.MaxRetries; attempt++)
            {
                if (token.IsCancellationRequested)
                    return ("Lỗi: Tác vụ đã bị hủy.", 0, "CANCELLED", "Task was cancelled", 0);

                try
                {
                    using var httpClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(5) };
                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                    };
                    
                    // === THÊM: Random User-Agent header per API key để giảm 429 ===
                    request.Headers.Add("User-Agent", userAgent);

                    _logger.LogInformation("Attempt {Attempt}/{MaxRetries}: Sending request to API with User-Agent: {UserAgent}", 
                        attempt, settings.MaxRetries, userAgent.Substring(0, Math.Min(50, userAgent.Length)) + "...");
                    using HttpResponseMessage response = await httpClient.SendAsync(request, token);
                    string responseBody = await response.Content.ReadAsStringAsync(token);

                    if (!response.IsSuccessStatusCode)
                    {
                        int statusCode = (int)response.StatusCode;
                        string errorType = $"HTTP_{statusCode}";
                        string errorMsg = $"HTTP Error {statusCode}";

                        _logger.LogWarning("HTTP Error {StatusCode}. Retrying in {Delay}ms... (Attempt {Attempt}/{MaxRetries})",
                            statusCode, settings.RetryDelayMs * attempt, attempt, settings.MaxRetries);

                        if (attempt < settings.MaxRetries)
                        {
                            await Task.Delay(settings.RetryDelayMs * attempt, token);
                            continue;
                        }

                        // Hết số lần retry, trả về lỗi
                        return ($"Lỗi API: {response.StatusCode}", 0, errorType, errorMsg, statusCode);
                    }

                    JObject parsedBody = JObject.Parse(responseBody);

                    // Kiểm tra lỗi trong response body
                    if (parsedBody?["error"] != null)
                    {
                        string errorMsg = parsedBody["error"]?["message"]?.ToString() ?? "Unknown error";
                        _logger.LogWarning("API returned error: {ErrorMsg}. Retrying... (Attempt {Attempt}/{MaxRetries})",
                            errorMsg, attempt, settings.MaxRetries);

                        if (attempt < settings.MaxRetries)
                        {
                            await Task.Delay(settings.RetryDelayMs * attempt, token);
                            continue;
                        }

                        return ($"Lỗi API: {errorMsg}", 0, "API_ERROR", errorMsg, 200);
                    }

                    // ===== THÊM MỚI: Kiểm tra blockReason (vi phạm chính sách an toàn) =====
                    if (parsedBody?["promptFeedback"]?["blockReason"] != null)
                    {
                        string blockReason = parsedBody["promptFeedback"]["blockReason"].ToString();
                        string errorMsg = $"Nội dung bị chặn. Lý do: {blockReason}";

                        _logger.LogWarning("Content blocked by safety filters: {BlockReason}. This will NOT be retried.", blockReason);

                        // Vi phạm chính sách không retry
                        return ($"Lỗi: {errorMsg}", 0, "BLOCKED_CONTENT", errorMsg, 200);
                    }
                    // ===== KẾT THÚC THÊM MỚI =====

                    // ===== THÊM MỚI: Kiểm tra finishReason =====
                    var finishReason = parsedBody?["candidates"]?[0]?["finishReason"]?.ToString();
                    if (!string.IsNullOrEmpty(finishReason) && finishReason != "STOP")
                    {
                        string errorMsg = $"FinishReason không hợp lệ: {finishReason}";

                        _logger.LogWarning("Invalid finishReason: {FinishReason}. Possible safety violation. Retrying... (Attempt {Attempt}/{MaxRetries})",
                            finishReason, attempt, settings.MaxRetries);

                        if (attempt < settings.MaxRetries)
                        {
                            await Task.Delay(settings.RetryDelayMs * attempt, token);
                            continue;
                        }

                        return ($"Lỗi: {errorMsg}", 0, "FINISH_REASON", errorMsg, 200);
                    }
                    // ===== KẾT THÚC THÊM MỚI =====

                    int tokens = parsedBody?["usageMetadata"]?["totalTokenCount"]?.Value<int>() ?? 0;
                    string responseText = parsedBody?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();

                    if (responseText == null)
                    {
                        _logger.LogWarning("API returned OK but content is empty. Retrying... (Attempt {Attempt}/{MaxRetries})",
                            attempt, settings.MaxRetries);

                        if (attempt < settings.MaxRetries)
                        {
                            await Task.Delay(settings.RetryDelayMs * attempt, token);
                            continue;
                        }

                        return ("Lỗi: API trả về phản hồi rỗng.", 0, "EMPTY_RESPONSE", "API returned empty response", 200);
                    }

                    // Thành công
                    return (responseText, tokens, null, null, 200);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception during API call. Retrying in {Delay}ms... (Attempt {Attempt}/{MaxRetries})",
                        settings.RetryDelayMs * attempt, attempt, settings.MaxRetries);

                    if (attempt >= settings.MaxRetries)
                        return ($"Lỗi Exception: {ex.Message}", 0, "EXCEPTION", ex.Message, 0);

                    await Task.Delay(settings.RetryDelayMs * attempt, token);
                }
            }

            return ("Lỗi API: Hết số lần thử lại.", 0, "MAX_RETRIES", "Exceeded maximum retry attempts", 0);
        }
        // ===== KẾT THÚC SỬA ĐỔI =====

        private async Task UpdateJobStatus(string sessionId, JobStatus status, string errorMessage = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await context.TranslationJobs.FindAsync(sessionId);
            if (job != null)
            {
                job.Status = status;
                if (errorMessage != null) job.ErrorMessage = errorMessage;
                await context.SaveChangesAsync();
            }
        }

        private async Task SaveResultsToDb(string sessionId, List<TranslatedSrtLineDb> results)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.TranslatedSrtLines.AddRangeAsync(results);
            await context.SaveChangesAsync();
        }

        private async Task UpdateUsageInDb(int apiKeyId, int tokensUsed)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var apiKey = await context.ManagedApiKeys.FindAsync(apiKeyId);
                if (apiKey == null)
                {
                    _logger.LogWarning("Không thể cập nhật sử dụng: Không tìm thấy API Key ID {ApiKeyId}", apiKeyId);
                    return;
                }
                var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
                var lastResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(apiKey.LastRequestCountResetUtc, vietnamTimeZone);
                if (lastResetInVietnam.Date < vietnamNow.Date)
                {
                    _logger.LogInformation("Resetting daily request count for API Key ID {ApiKeyId}", apiKeyId);
                    apiKey.RequestsToday = 0;
                    apiKey.LastRequestCountResetUtc = DateTime.UtcNow.Date;
                }
                apiKey.RequestsToday++;
                if (tokensUsed > 0)
                {
                    apiKey.TotalTokensUsed += tokensUsed;
                }
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật sử dụng cho API Key ID {ApiKeyId}", apiKeyId);
            }
        }
    }
}