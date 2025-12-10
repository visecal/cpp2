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
        private readonly ApiKeyCooldownService _cooldownService;
        private readonly JobCancellationService _cancellationService;
        private readonly GlobalRequestRateLimiterService _globalRateLimiter;
        private readonly ProxyService _proxyService;
        private readonly ProxyRateLimiterService _proxyRateLimiter;

        // === RPM Limiter per API Key - Đảm bảo mỗi key tôn trọng RPM riêng ===
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _keyRpmLimiters = new();
        private static readonly ConcurrentDictionary<int, int> _keyRpmCapacities = new(); // Track capacity per key
        
        // === Round-Robin Index per Pool - Đảm bảo phân bổ đều request giữa các key ===
        private static int _paidKeyRoundRobinIndex = 0;
        private static int _freeKeyRoundRobinIndex = 0;
        private static readonly object _roundRobinLock = new();
        
        // === Constants ===
        private const int RPM_WAIT_TIMEOUT_MS = 100; // Thời gian chờ khi kiểm tra RPM slot khả dụng
        private const int PROXY_RPM_WAIT_TIMEOUT_MS = 500; // Thời gian chờ khi kiểm tra proxy RPM slot
        private const int MAX_PROXY_SEARCH_ATTEMPTS = 10; // Số lần thử tìm proxy có RPM slot
        private const int FINAL_KEY_WAIT_TIMEOUT_MS = 30000; // Thời gian chờ tối đa khi tất cả keys bận (30 giây)
        private const int RETRY_RESULT_TIMEOUT_SECONDS = 30; // Timeout khi thử dịch lại trước khi trả kết quả
        private const int DEFAULT_LOCAL_API_SETTING_ID = 1;
        private const int MIN_BATCH_SIZE = 1;
        
        // Chrome-based templates use {0}=major, {1}=build, {2}=patch
        // Firefox templates only use {0}=version (extra args are safely ignored by string.Format)
        private static readonly string[] _chromeTemplates = new[]
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.{1}.{2} Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.{1}.{2} Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.{1}.{2} Safari/537.36 Edg/{0}.0.{1}.{2}",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.{1}.{2} Safari/537.36"
        };
        
        private static readonly string[] _firefoxTemplates = new[]
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{0}.0) Gecko/20100101 Firefox/{0}.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:{0}.0) Gecko/20100101 Firefox/{0}.0",
            "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:{0}.0) Gecko/20100101 Firefox/{0}.0"
        };

        /// <summary>
        /// Tạo User-Agent ngẫu nhiên cho mỗi request để tránh bị rate limit
        /// </summary>
        private static string GenerateRandomUserAgent()
        {
            var random = new Random(Guid.NewGuid().GetHashCode()); // Random seed cho mỗi request
            
            // Chọn ngẫu nhiên giữa Chrome và Firefox
            bool useChrome = random.Next(2) == 0;
            
            if (useChrome)
            {
                var template = _chromeTemplates[random.Next(_chromeTemplates.Length)];
                var majorVersion = random.Next(100, 131); // Chrome versions 100-130
                var buildNumber = random.Next(1000, 9999);
                var patchNumber = random.Next(100, 999);
                return string.Format(template, majorVersion, buildNumber, patchNumber);
            }
            else
            {
                var template = _firefoxTemplates[random.Next(_firefoxTemplates.Length)];
                var majorVersion = random.Next(100, 135); // Firefox versions 100-134
                return string.Format(template, majorVersion);
            }
        }
        
        /// <summary>
        /// Helper method để chọn key theo round-robin (sync, có thể dùng lock)
        /// </summary>
        private ManagedApiKey GetNextKeyRoundRobin(List<ManagedApiKey> eligibleKeys, ApiPoolType poolType)
        {
            lock (_roundRobinLock)
            {
                int currentIndex;
                if (poolType == ApiPoolType.Paid)
                {
                    if (_paidKeyRoundRobinIndex >= eligibleKeys.Count)
                        _paidKeyRoundRobinIndex = 0;
                    currentIndex = _paidKeyRoundRobinIndex;
                    _paidKeyRoundRobinIndex++;
                }
                else
                {
                    if (_freeKeyRoundRobinIndex >= eligibleKeys.Count)
                        _freeKeyRoundRobinIndex = 0;
                    currentIndex = _freeKeyRoundRobinIndex;
                    _freeKeyRoundRobinIndex++;
                }
                return eligibleKeys[currentIndex];
            }
        }
        
        /// <summary>
        /// Đảm bảo key có RPM limiter với capacity đúng. Tạo mới nếu cần.
        /// </summary>
        private void EnsureKeyRpmLimiter(int keyId, int rpmCapacity)
        {
            // Kiểm tra capacity đã lưu
            if (_keyRpmCapacities.TryGetValue(keyId, out int currentCapacity) && currentCapacity == rpmCapacity)
            {
                // Capacity không thay đổi, không cần làm gì
                return;
            }
            
            // Capacity thay đổi hoặc chưa có, cần tạo/cập nhật semaphore
            lock (_roundRobinLock) // Sử dụng lock để tránh race condition
            {
                // Double-check sau khi lấy lock
                if (_keyRpmCapacities.TryGetValue(keyId, out currentCapacity) && currentCapacity == rpmCapacity)
                    return;
                
                // Dispose old semaphore nếu có
                if (_keyRpmLimiters.TryRemove(keyId, out var oldSemaphore))
                {
                    try { oldSemaphore.Dispose(); }
                    catch { /* Ignore dispose errors */ }
                }
                
                // Tạo semaphore mới
                _keyRpmLimiters[keyId] = new SemaphoreSlim(rpmCapacity, rpmCapacity);
                _keyRpmCapacities[keyId] = rpmCapacity;
            }
        }

        public record CreateJobResult(string Status, string Message, string SessionId = null, int RemainingLines = 0);

        public TranslationOrchestratorService(
            IServiceProvider serviceProvider, 
            ILogger<TranslationOrchestratorService> logger, 
            IHttpClientFactory httpClientFactory,
            ApiKeyCooldownService cooldownService,
            JobCancellationService cancellationService,
            GlobalRequestRateLimiterService globalRateLimiter,
            ProxyService proxyService,
            ProxyRateLimiterService proxyRateLimiter)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cooldownService = cooldownService;
            _cancellationService = cancellationService;
            _globalRateLimiter = globalRateLimiter;
            _proxyService = proxyService;
            _proxyRateLimiter = proxyRateLimiter;
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
                _ = ProcessJob(sessionId, user.Id, user.Tier);
                return new CreateJobResult("Accepted", "OK", sessionId);
            }

            if (remainingLines > 0)
            {
                if (acceptPartial)
                {
                    var partialLines = allLines.Take(remainingLines).ToList();
                    user.LocalSrtLinesUsedToday += partialLines.Count;
                    var sessionId = await CreateJobInDb(user, genre, targetLanguage, systemInstruction, partialLines, context);
                    _ = ProcessJob(sessionId, user.Id, user.Tier);
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

            var translatedLines = resultsDb
                .Select(l => new TranslatedSrtLine
                {
                    Index = l.LineIndex,
                    TranslatedText = l.TranslatedText,
                    Success = l.Success
                })
                .ToList();
            var translatedLookup = translatedLines
                .GroupBy(l => l.Index)
                .ToDictionary(g => g.Key, g => g.First());

            // Auto-retry failed lines before returning to client (do not charge extra user quota; API key usage is still tracked)
            var retryIndexes = resultsDb
                .Where(l => !l.Success)
                .Select(l => l.LineIndex)
                .Distinct()
                .ToList();

            if (retryIndexes.Any())
            {
                try
                {
                    _logger.LogInformation("Retrying {RetryCount} failed lines for session {SessionId} before returning results.", retryIndexes.Count, sessionId);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RETRY_RESULT_TIMEOUT_SECONDS));
                    var retryResults = await RetranslateLinesAsync(sessionId, retryIndexes, cts.Token);

                    foreach (var retried in retryResults)
                    {
                        if (translatedLookup.TryGetValue(retried.Key, out var line))
                        {
                            // Update the same instances that will be returned to the client
                            line.TranslatedText = retried.Value.TranslatedText;
                            line.Success = retried.Value.Success;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Retry translation failed for session {SessionId}", sessionId);
                }
            }

            return translatedLines;
        }

        private async Task<Dictionary<int, TranslatedSrtLine>> RetranslateLinesAsync(
            string sessionId,
            List<int> lineIndexes,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<int, TranslatedSrtLine>();
            if (lineIndexes == null || !lineIndexes.Any())
                return result;

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var encryptionService = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

            var job = await context.TranslationJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.SessionId == sessionId, cancellationToken);
            if (job == null) return result;

            var user = await context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == job.UserId, cancellationToken);
            if (user == null) return result;

            var settings = await GetLocalApiSettingsAsync(context, cancellationToken);

            var poolToUse = (user.Tier == SubscriptionTier.Free) ? ApiPoolType.Free : ApiPoolType.Paid;

            var activeModel = await context.AvailableApiModels.AsNoTracking()
                .FirstOrDefaultAsync(m => m.IsActive && m.PoolType == poolToUse, cancellationToken);
            if (activeModel == null) return result;

            var availableKeys = await context.ManagedApiKeys.AsNoTracking()
                .Where(k => k.IsEnabled && k.PoolType == poolToUse)
                .ToListAsync(cancellationToken);
            availableKeys = availableKeys.Where(k => !_cooldownService.IsInCooldown(k.Id)).ToList();
            if (!availableKeys.Any()) return result;

            foreach (var key in availableKeys)
            {
                if (!_keyRpmCapacities.TryGetValue(key.Id, out var capacity) || capacity != settings.Rpm)
                {
                    EnsureKeyRpmLimiter(key.Id, settings.Rpm);
                }
            }

            var originalLines = await context.OriginalSrtLines.AsNoTracking()
                .Where(l => l.SessionId == sessionId && lineIndexes.Contains(l.LineIndex))
                .OrderBy(l => l.LineIndex)
                .ToListAsync(cancellationToken);
            if (!originalLines.Any()) return result;

            int batchSize = GetValidBatchSize(settings);

            var batches = originalLines
                .Select((line, index) => new { line, index })
                .GroupBy(x => x.index / batchSize)
                .Select(g => g.Select(x => x.line).ToList())
                .ToList();

            for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                string? rateLimitSlotId = null;
                try
                {
                    rateLimitSlotId = await _globalRateLimiter.AcquireSlotAsync(
                        $"{sessionId}_retry_batch{batchIndex}", cancellationToken);

                    var (translatedBatch, tokensUsed, usedKeyId) = await TranslateBatchAsync(
                        batches[batchIndex],
                        job,
                        settings,
                        activeModel.ModelName,
                        job.SystemInstruction,
                        poolToUse,
                        encryptionService,
                        availableKeys,
                        settings.Rpm,
                        cancellationToken);

                    foreach (var item in translatedBatch)
                    {
                        result[item.LineIndex] = new TranslatedSrtLine
                        {
                            Index = item.LineIndex,
                            TranslatedText = item.TranslatedText,
                            Success = item.Success
                        };
                    }

                    if (usedKeyId.HasValue)
                    {
                        await UpdateUsageInDb(usedKeyId.Value, tokensUsed);
                        await _cooldownService.ResetCooldownAsync(usedKeyId.Value);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Retry translation cancelled for session {SessionId}", sessionId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while retrying missing lines for session {SessionId}", sessionId);
                }
                finally
                {
                    if (rateLimitSlotId != null)
                    {
                        _globalRateLimiter.ReleaseSlot(rateLimitSlotId);
                    }
                }
            }

            return result;
        }

        private int GetValidBatchSize(LocalApiSetting settings)
        {
            int? configuredBatchSize = settings?.BatchSize;
            if (configuredBatchSize.HasValue && configuredBatchSize.Value > 0) return configuredBatchSize.Value;
            _logger.LogWarning("Invalid BatchSize {BatchSize} detected. Falling back to {Fallback}.", configuredBatchSize, MIN_BATCH_SIZE);
            return MIN_BATCH_SIZE;
        }

        private async Task<LocalApiSetting> GetLocalApiSettingsAsync(AppDbContext context, CancellationToken cancellationToken)
        {
            var settings = await context.LocalApiSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == DEFAULT_LOCAL_API_SETTING_ID, cancellationToken);

            if (settings == null)
            {
                _logger.LogWarning("LocalApiSettings with Id {Id} not found. Falling back to defaults.", DEFAULT_LOCAL_API_SETTING_ID);
                return new LocalApiSetting();
            }

            return settings;
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

        private async Task ProcessJob(string sessionId, int userId, SubscriptionTier userTier)
        {
            _logger.LogInformation("Starting HIGH-SPEED processing for job {SessionId} using {Tier} tier API pool", sessionId, userTier);
            
            // === SỬA ĐỔI: Sử dụng JobCancellationService thay vì tạo CTS mới ===
            var cancellationToken = _cancellationService.RegisterJob(sessionId, userId, timeoutMinutes: 15);
            
            ApiPoolType poolToUse = (userTier == SubscriptionTier.Free) ? ApiPoolType.Free : ApiPoolType.Paid;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var encryptionService = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

                var job = await context.TranslationJobs.FindAsync(new object[] { sessionId }, cancellationToken);
                if (job == null)
                {
                    _logger.LogError("Job {SessionId} not found in database at the start of processing.", sessionId);
                    return;
                }

                job.Status = JobStatus.Processing;
                await context.SaveChangesAsync(cancellationToken);

                var settings = await GetLocalApiSettingsAsync(context, cancellationToken);
                var activeModel = await context.AvailableApiModels.AsNoTracking().FirstOrDefaultAsync(m => m.IsActive && m.PoolType == poolToUse, cancellationToken);
                if (activeModel == null) throw new Exception($"Không có model nào đang hoạt động cho nhóm '{poolToUse}'.");

                // === SỬA ĐỔI: Load tất cả keys enabled và filter cooldown ===
                var enabledKeys = await context.ManagedApiKeys.AsNoTracking()
                    .Where(k => k.IsEnabled && k.PoolType == poolToUse)
                    .ToListAsync(cancellationToken);
                
                // Filter out keys in cooldown
                enabledKeys = enabledKeys.Where(k => !_cooldownService.IsInCooldown(k.Id)).ToList();
                
                if (!enabledKeys.Any()) throw new Exception($"Không có API key nào đang hoạt động cho nhóm '{poolToUse}' (có thể tất cả đang trong cooldown).");
                // === KẾT THÚC SỬA ĐỔI ===

                // === MỚI: Lấy RPM từ Admin/LocalApi settings thay vì hardcode ===
                int rpmPerKey = settings.Rpm; // RPM được cài đặt trên Admin panel
                
                // Đảm bảo mỗi key có SemaphoreSlim riêng để tuân thủ RPM
                foreach (var key in enabledKeys)
                {
                    // Kiểm tra capacity đã lưu và tạo/cập nhật semaphore nếu cần
                    EnsureKeyRpmLimiter(key.Id, rpmPerKey);
                }
                
                _logger.LogInformation("Job {SessionId}: Using {KeyCount} API keys, each with {Rpm} RPM (from Admin settings)", 
                    sessionId, enabledKeys.Count, rpmPerKey);

                var allLines = await context.OriginalSrtLines.AsNoTracking()
                    .Where(l => l.SessionId == sessionId)
                    .OrderBy(l => l.LineIndex)
                    .ToListAsync(cancellationToken);

                int batchSize = GetValidBatchSize(settings);

                var batches = allLines
                    .Select((line, index) => new { line, index })
                    .GroupBy(x => x.index / batchSize)
                    .Select(g => g.Select(x => x.line).ToList())
                    .ToList();

                var processingTasks = new List<Task>();
                
                // Log global rate limiter status
                var (maxReqs, windowMins, availSlots, activeReqs) = _globalRateLimiter.GetCurrentStatus();
                _logger.LogInformation("Job {SessionId}: Processing {BatchCount} batches. Global rate limit: {MaxReqs}/{WindowMins}min (Available: {AvailSlots})",
                    sessionId, batches.Count, maxReqs, windowMins, availSlots);

                for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
                {
                    // === THÊM MỚI: Kiểm tra cancellation trước mỗi batch ===
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Job {SessionId}: Cancellation requested, stopping at batch {BatchIndex}/{TotalBatches}",
                            sessionId, batchIndex + 1, batches.Count);
                        break;
                    }
                    
                    var batch = batches[batchIndex];
                    
                    // === BẮT ĐẦU THÊM: Delay giữa các batch theo cài đặt ===
                    if (batchIndex > 0 && settings.DelayBetweenBatchesMs > 0)
                    {
                        _logger.LogInformation("Job {SessionId}: Waiting {DelayMs}ms before batch {BatchIndex}/{TotalBatches}", 
                            sessionId, settings.DelayBetweenBatchesMs, batchIndex + 1, batches.Count);
                        await Task.Delay(settings.DelayBetweenBatchesMs, cancellationToken);
                    }
                    // === KẾT THÚC THÊM ===
                    
                    // Capture batch index for closure - cần thiết vì batchIndex được thay đổi trong loop
                    int currentBatchIndex = batchIndex;
                    
                    // === MỚI: Áp dụng Global Rate Limiter trước khi xử lý batch ===
                    processingTasks.Add(Task.Run(async () =>
                    {
                        string? rateLimitSlotId = null;
                        try
                        {
                            // === GLOBAL RATE LIMIT: Đợi slot khả dụng trước khi gửi API request ===
                            rateLimitSlotId = await _globalRateLimiter.AcquireSlotAsync(
                                $"{sessionId}_batch{currentBatchIndex}", cancellationToken);
                            
                            // === SỬA ĐỔI: Truyền thêm enabledKeys và rpmPerKey để hỗ trợ round-robin và per-key RPM ===
                            var (translatedBatch, tokensUsed, usedKeyId) = await TranslateBatchAsync(
                                batch, job, settings, activeModel.ModelName, job.SystemInstruction, 
                                poolToUse, encryptionService, enabledKeys, rpmPerKey, cancellationToken);
                            
                            await SaveResultsToDb(sessionId, translatedBatch);
                            
                            if (usedKeyId.HasValue)
                            {
                                await UpdateUsageInDb(usedKeyId.Value, tokensUsed);
                                
                                // Reset cooldown nếu batch thành công
                                await _cooldownService.ResetCooldownAsync(usedKeyId.Value);
                            }
                            // === KẾT THÚC SỬA ĐỔI ===
                        }
                        catch (OperationCanceledException) 
                        { 
                            _logger.LogInformation("Batch processing cancelled for job {SessionId}", sessionId);
                        }
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
                        finally
                        {
                            // === GLOBAL RATE LIMIT: Giải phóng slot sau khi hoàn thành ===
                            if (rateLimitSlotId != null)
                            {
                                _globalRateLimiter.ReleaseSlot(rateLimitSlotId);
                            }
                        }
                    }, cancellationToken));
                }

                await Task.WhenAll(processingTasks);
                _logger.LogInformation("All batches completed for job {SessionId}", sessionId);

                // ✅ Kiểm tra status trước khi làm gì
                using (var checkScope = _serviceProvider.CreateScope())
                {
                    var checkContext = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var currentJob = await checkContext.TranslationJobs.AsNoTracking()
                        .FirstOrDefaultAsync(j => j.SessionId == sessionId);

                    // Nếu job đã bị cancel hoặc completed, thoát sớm
                    if (currentJob == null ||
                        currentJob.Status == JobStatus.Failed ||
                        currentJob.Status == JobStatus.Completed)
                    {
                        _logger.LogInformation("Job {SessionId} đã xử lý trước đó, bỏ qua.", sessionId);
                        return;
                    }
                }

                try { await CheckAndRefundFailedLinesAsync(sessionId); }
                catch (Exception ex) { _logger.LogError(ex, "CheckAndRefund failed"); }

                await UpdateJobStatus(sessionId, JobStatus.Completed);
                _logger.LogInformation("🎉 Job {SessionId} COMPLETED!", sessionId);

            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Job {SessionId} đã bị hủy (timeout hoặc user request).", sessionId);
                await CheckAndRefundFailedLinesAsync(sessionId);
                await UpdateJobStatus(sessionId, JobStatus.Failed, "Job đã bị hủy.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi nghiêm trọng trong quá trình xử lý job {SessionId}", sessionId);
                await UpdateJobStatus(sessionId, JobStatus.Failed, ex.Message);
            }
            finally
            {
                // === THÊM MỚI: Hủy đăng ký job khi hoàn thành ===
                _cancellationService.UnregisterJob(sessionId, userId);
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
            IEncryptionService encryptionService, List<ManagedApiKey> availableKeys, int rpmPerKey, CancellationToken token)
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

            // === MỚI: Sử dụng round-robin và per-key RPM limiter ===
            HashSet<int> triedKeyIds = new HashSet<int>();
            int? successfulKeyId = null;
            
            for (int attempt = 1; attempt <= settings.MaxRetries; attempt++)
            {
                ManagedApiKey selectedKey = null;
                
                try
                {
                    // === MỚI: Chọn key bằng round-robin và chờ per-key RPM limiter ===
                    selectedKey = await GetAvailableKeyWithRpmLimitAsync(availableKeys, poolType, triedKeyIds, rpmPerKey, token);
                    
                    if (selectedKey == null)
                    {
                        _logger.LogWarning("Batch: Không còn key nào khả dụng sau {Attempts} lần thử với {TriedKeys} keys",
                            attempt, triedKeyIds.Count);
                        break; // Không còn key nào để thử
                    }

                    triedKeyIds.Add(selectedKey.Id);
                    
                    var apiKey = encryptionService.Decrypt(selectedKey.EncryptedApiKey, selectedKey.Iv);
                    string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";

                    _logger.LogInformation("Batch attempt {Attempt}/{MaxRetries}: Using Key ID {KeyId} (round-robin)", 
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
                            await Task.Delay(settings.RetryDelayMs, token);
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
                        int delayMs = settings.RetryDelayMs * attempt;
                        
                        _logger.LogWarning("Batch attempt {Attempt} failed with Key ID {KeyId}. Error: {Error}. Retrying after {Delay}ms...",
                            attempt, selectedKey.Id, errorType, delayMs);
                        
                        await Task.Delay(delayMs, token);
                        continue;
                    }

                }
                catch (OperationCanceledException)
                {
                    // Operation was cancelled - this is expected when timeout occurs or cancellation requested
                    if (selectedKey != null)
                    {
                        _logger.LogInformation("Batch processing cancelled for job {JobId} at attempt {Attempt} with Key ID {KeyId}", 
                            job.SessionId, attempt, selectedKey.Id);
                    }
                    else
                    {
                        _logger.LogInformation("Batch processing cancelled for job {JobId} at attempt {Attempt} (no key was selected - all keys may be busy or in cooldown)", 
                            job.SessionId, attempt);
                    }
                    break; // Exit retry loop on cancellation
                }
                catch (Exception ex)
                {
                    if (selectedKey != null)
                    {
                        _logger.LogError(ex, "Exception during batch translation attempt {Attempt} with Key ID {KeyId}", 
                            attempt, selectedKey.Id);
                    }
                    else
                    {
                        _logger.LogError(ex, "Exception during batch translation attempt {Attempt} (no key was selected - all keys may be busy or in cooldown). Available keys: {KeyCount}, Tried keys: {TriedCount}", 
                            attempt, availableKeys.Count, triedKeyIds.Count);
                    }
                    
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
        }

        /// <summary>
        /// Chọn key bằng round-robin và đợi per-key RPM limiter
        /// </summary>
        private async Task<ManagedApiKey> GetAvailableKeyWithRpmLimitAsync(
            List<ManagedApiKey> availableKeys, ApiPoolType poolType, HashSet<int> excludeKeyIds, int rpmPerKey, CancellationToken token)
        {
            // Lọc keys chưa thử và không trong cooldown
            var eligibleKeys = availableKeys
                .Where(k => !excludeKeyIds.Contains(k.Id) && !_cooldownService.IsInCooldown(k.Id))
                .ToList();
            
            if (!eligibleKeys.Any()) 
            {
                var totalKeys = availableKeys.Count;
                var excludedKeys = excludeKeyIds.Count;
                var cooldownKeys = availableKeys.Count(k => _cooldownService.IsInCooldown(k.Id));
                
                _logger.LogWarning(
                    "No eligible keys available. Total: {Total}, Excluded: {Excluded}, In Cooldown: {Cooldown}, Pool: {PoolType}",
                    totalKeys, excludedKeys, cooldownKeys, poolType);
                    
                return null;
            }
            
            // === ROUND-ROBIN SELECTION: Đảm bảo phân bổ đều ===
            ManagedApiKey selectedKey = GetNextKeyRoundRobin(eligibleKeys, poolType);
            
            // === PER-KEY RPM LIMITER: Đảm bảo mỗi key tuân thủ RPM riêng ===
            var semaphore = _keyRpmLimiters.GetOrAdd(selectedKey.Id, _ => new SemaphoreSlim(rpmPerKey, rpmPerKey));
            
            // Thử lấy slot từ semaphore (không chờ vô hạn)
            if (await semaphore.WaitAsync(RPM_WAIT_TIMEOUT_MS, token))
            {
                // Tự động release sau 1 phút (60 giây = 1 phút trong context RPM)
                ScheduleSemaphoreRelease(semaphore, TimeSpan.FromMinutes(1));
                
                _logger.LogDebug("Key ID {KeyId} selected via round-robin. RPM slots remaining: {Remaining}/{Total}", 
                    selectedKey.Id, semaphore.CurrentCount, rpmPerKey);
                
                return selectedKey;
            }
            
            // Nếu key đã đạt RPM limit, thử key tiếp theo
            _logger.LogWarning("Key ID {KeyId} đã đạt giới hạn {Rpm} RPM, thử key khác", selectedKey.Id, rpmPerKey);
            
            // Thử các key còn lại
            foreach (var key in eligibleKeys.Where(k => k.Id != selectedKey.Id))
            {
                var keySemaphore = _keyRpmLimiters.GetOrAdd(key.Id, _ => new SemaphoreSlim(rpmPerKey, rpmPerKey));
                if (await keySemaphore.WaitAsync(RPM_WAIT_TIMEOUT_MS, token))
                {
                    ScheduleSemaphoreRelease(keySemaphore, TimeSpan.FromMinutes(1));
                    
                    _logger.LogDebug("Alternative Key ID {KeyId} selected. RPM slots remaining: {Remaining}/{Total}", 
                        key.Id, keySemaphore.CurrentCount, rpmPerKey);
                    
                    return key;
                }
            }
            
            // Nếu tất cả key đều đạt RPM limit, chờ key đầu tiên với timeout
            _logger.LogInformation("Tất cả keys đều đạt giới hạn RPM, đợi key ID {KeyId} với timeout {TimeoutMs}ms...", 
                selectedKey.Id, FINAL_KEY_WAIT_TIMEOUT_MS);
            
            // Sử dụng timeout để tránh chờ vô hạn
            if (await semaphore.WaitAsync(FINAL_KEY_WAIT_TIMEOUT_MS, token))
            {
                ScheduleSemaphoreRelease(semaphore, TimeSpan.FromMinutes(1));
                return selectedKey;
            }
            
            // Timeout - không có key nào khả dụng
            _logger.LogWarning("Timeout khi đợi key khả dụng sau {TimeoutMs}ms. Tất cả {Count} keys đều bận.", 
                FINAL_KEY_WAIT_TIMEOUT_MS, eligibleKeys.Count);
            return null;
        }
        
        /// <summary>
        /// Lên lịch release semaphore sau một khoảng thời gian (để đảm bảo RPM window)
        /// </summary>
        private static void ScheduleSemaphoreRelease(SemaphoreSlim semaphore, TimeSpan delay)
        {
            // Sử dụng object holder để tránh race condition với timer assignment
            var timerHolder = new TimerHolder();
            timerHolder.Timer = new Timer(_ =>
            {
                try 
                { 
                    semaphore.Release(); 
                }
                catch (SemaphoreFullException) 
                { 
                    // Semaphore đã đầy, ignore
                }
                catch (ObjectDisposedException) 
                { 
                    // Semaphore đã bị disposed, ignore 
                }
                finally
                {
                    // Dispose timer sau khi callback hoàn thành
                    try { timerHolder.Timer?.Dispose(); }
                    catch { /* Ignore dispose errors */ }
                }
            }, null, delay, Timeout.InfiniteTimeSpan);
        }
        
        // Helper class để giữ timer reference an toàn
        private class TimerHolder
        {
            public Timer? Timer { get; set; }
        }
        
        // ===== SỬA ĐỔI: Thêm tracking lỗi chi tiết, random User-Agent, PROXY support và PROXY RPM LIMITING ===== 
        private async Task<(string responseText, int tokensUsed, string errorType, string errorDetail, int httpStatusCode)> CallApiWithRetryAsync(
            string url, string jsonPayload, LocalApiSetting settings, int apiKeyId, CancellationToken token)
        {
            // Generate random User-Agent once per request to avoid fingerprinting
            string userAgent = GenerateRandomUserAgent();
            
            // Track failed proxy IDs to exclude them from subsequent attempts within this request
            var failedProxyIds = new HashSet<int>();
            
            // Track current proxy slot for RPM limiting
            string? currentProxySlotId = null;
            Proxy? currentProxy = null;
            
            // Create unique request ID for tracking
            string requestId = $"key{apiKeyId}_{Guid.NewGuid():N}";
            
            for (int attempt = 1; attempt <= settings.MaxRetries; attempt++)
            {
                if (token.IsCancellationRequested)
                    return ("Lỗi: Tác vụ đã bị hủy.", 0, "CANCELLED", "Task was cancelled", 0);

                // === PROXY SELECTION WITH RPM LIMITING ===
                // Release previous proxy slot if switching proxy
                if (currentProxySlotId != null)
                {
                    _proxyRateLimiter.ReleaseSlotEarly(currentProxySlotId);
                    currentProxySlotId = null;
                }
                
                // Get a proxy with available RPM slots
                currentProxy = await GetProxyWithAvailableRpmSlotAsync(failedProxyIds, requestId, token);
                
                // Acquire RPM slot for this proxy (if proxy is available)
                if (currentProxy != null)
                {
                    currentProxySlotId = await _proxyRateLimiter.TryAcquireSlotWithTimeoutAsync(
                        currentProxy.Id, requestId, PROXY_RPM_WAIT_TIMEOUT_MS, token);
                    
                    if (currentProxySlotId == null)
                    {
                        _logger.LogWarning("Proxy {ProxyId} ({Host}:{Port}) đã đạt giới hạn RPM, thử proxy khác",
                            currentProxy.Id, currentProxy.Host, currentProxy.Port);
                        failedProxyIds.Add(currentProxy.Id); // Tạm exclude proxy này
                        
                        // Try to get another proxy
                        currentProxy = await GetProxyWithAvailableRpmSlotAsync(failedProxyIds, requestId, token);
                        if (currentProxy != null)
                        {
                            currentProxySlotId = await _proxyRateLimiter.TryAcquireSlotWithTimeoutAsync(
                                currentProxy.Id, requestId, PROXY_RPM_WAIT_TIMEOUT_MS, token);
                        }
                    }
                }

                try
                {
                    // Create HttpClient with the current proxy (or direct if no proxy)
                    using var httpClient = _proxyService.CreateHttpClientWithProxy(currentProxy);
                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                    };
                    
                    // Add random User-Agent header to avoid rate limiting
                    request.Headers.Add("User-Agent", userAgent);

                    if (currentProxy != null)
                    {
                        var (rpmPerProxy, availSlots, _) = _proxyRateLimiter.GetProxyStatus(currentProxy.Id);
                        _logger.LogDebug("Attempt {Attempt}/{MaxRetries}: Sending request via Proxy {ProxyId} ({Type}://{Host}:{Port}) (Key ID: {KeyId}) RPM slots: {Available}/{Max}", 
                            attempt, settings.MaxRetries, currentProxy.Id, currentProxy.Type, currentProxy.Host, currentProxy.Port, apiKeyId, availSlots, rpmPerProxy);
                    }
                    else
                    {
                        _logger.LogDebug("Attempt {Attempt}/{MaxRetries}: Sending request directly (no proxy) (Key ID: {KeyId})", 
                            attempt, settings.MaxRetries, apiKeyId);
                    }
                    
                    using HttpResponseMessage response = await httpClient.SendAsync(request, token);
                    string responseBody = await response.Content.ReadAsStringAsync(token);

                    // === REQUEST ĐÃ KẾT NỐI THÀNH CÔNG ĐẾN API GEMINI ===
                    // Tại đây, request đã được gửi thành công qua proxy và nhận response từ Gemini
                    // => Đánh dấu slot đã được sử dụng (sẽ tự auto-release sau 1 phút)
                    if (currentProxySlotId != null)
                    {
                        _proxyRateLimiter.MarkSlotAsUsed(currentProxySlotId);
                        currentProxySlotId = null; // Prevent early release
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        int statusCode = (int)response.StatusCode;
                        string errorType = $"HTTP_{statusCode}";
                        string errorMsg = $"HTTP Error {statusCode}";

                        // === THÊM MỚI: Kiểm tra lỗi FAILED_PRECONDITION "location is not supported" từ Gemini API ===
                        // Lỗi này cho biết proxy IP location không được hỗ trợ bởi Gemini API
                        // Cần disable proxy ngay lập tức để tránh tái sử dụng
                        if (statusCode == 400 && currentProxy != null)
                        {
                            try
                            {
                                var errorBody = JObject.Parse(responseBody);
                                var errorStatus = errorBody?["error"]?["status"]?.ToString();
                                var errorMessage = errorBody?["error"]?["message"]?.ToString() ?? "";
                                
                                if (errorStatus == "FAILED_PRECONDITION" && 
                                    errorMessage.Contains("location is not supported", StringComparison.OrdinalIgnoreCase))
                                {
                                    _logger.LogError("🚫 Proxy {ProxyId} ({Host}:{Port}) bị khoá do lỗi FAILED_PRECONDITION: {ErrorMessage}. Disable proxy ngay lập tức.",
                                        currentProxy.Id, currentProxy.Host, currentProxy.Port, errorMessage);
                                    
                                    await _proxyService.DisableProxyImmediatelyAsync(currentProxy.Id, "location is not supported");
                                    failedProxyIds.Add(currentProxy.Id);
                                    
                                    // Thử ngay với proxy khác (không delay)
                                    if (attempt < settings.MaxRetries)
                                    {
                                        continue;
                                    }
                                    
                                    return ($"Lỗi API: {errorMessage}", 0, "FAILED_PRECONDITION", errorMessage, statusCode);
                                }
                            }
                            catch (JsonReaderException)
                            {
                                // Không parse được JSON, tiếp tục xử lý như HTTP error bình thường
                            }
                        }
                        // === KẾT THÚC THÊM MỚI ===

                        _logger.LogWarning("HTTP Error {StatusCode}. Retrying in {Delay}ms... (Attempt {Attempt}/{MaxRetries})",
                            statusCode, settings.RetryDelayMs * attempt, attempt, settings.MaxRetries);

                        // Ghi nhận proxy failure nếu lỗi không phải 429 (429 là do API, không phải proxy)
                        if (currentProxy != null && statusCode != 429)
                        {
                            await _proxyService.RecordProxyFailureAsync(currentProxy.Id, $"HTTP {statusCode}");
                        }

                        if (attempt < settings.MaxRetries)
                        {
                            await Task.Delay(settings.RetryDelayMs * attempt, token);
                            continue;
                        }

                        // Hết số lần retry, trả về lỗi
                        return ($"Lỗi API: {response.StatusCode}", 0, errorType, errorMsg, statusCode);
                    }

                    // === Request thành công, ghi nhận proxy success ===
                    if (currentProxy != null)
                    {
                        await _proxyService.RecordProxySuccessAsync(currentProxy.Id);
                    }

                    // === Parse JSON response với error handling ===
                    JObject parsedBody;
                    try
                    {
                        parsedBody = JObject.Parse(responseBody);
                    }
                    catch (JsonReaderException jsonEx)
                    {
                        // Response không phải JSON (có thể là HTML error page từ proxy hoặc server)
                        var previewBody = responseBody.Length > 200 ? responseBody.Substring(0, 200) + "..." : responseBody;
                        _logger.LogWarning("Response is not valid JSON (possibly HTML error page). Preview: {Preview}. Retrying... (Attempt {Attempt}/{MaxRetries})",
                            previewBody, attempt, settings.MaxRetries);
                        
                        // Nếu response bắt đầu bằng HTML tag, có thể proxy trả về error page
                        // Đây là lỗi INTERMITTENT - proxy có thể hoạt động lần sau
                        if (responseBody.TrimStart().StartsWith("<", StringComparison.Ordinal))
                        {
                            if (currentProxy != null)
                            {
                                // isIntermittent = true: sử dụng threshold cao hơn (10 thay vì 5)
                                await _proxyService.RecordProxyFailureAsync(currentProxy.Id, "Proxy returned HTML instead of JSON", isIntermittent: true);
                                failedProxyIds.Add(currentProxy.Id);
                            }
                        }
                        
                        if (attempt < settings.MaxRetries)
                        {
                            await Task.Delay(settings.RetryDelayMs * attempt, token);
                            continue;
                        }
                        
                        return ("Lỗi: Response không phải JSON hợp lệ", 0, "INVALID_JSON", $"JSON parse error: {jsonEx.Message}", 200);
                    }

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

                    // Success
                    return (responseText, tokens, null, null, 200);
                }
                catch (HttpRequestException ex) when (IsProxyTunnelError(ex))
                {
                    // === PROXY TUNNEL ERROR: Immediately switch to different proxy or direct connection ===
                    // Lỗi kết nối proxy - KHÔNG tính vào RPM (release slot early)
                    if (currentProxySlotId != null)
                    {
                        _proxyRateLimiter.ReleaseSlotEarly(currentProxySlotId);
                        currentProxySlotId = null;
                    }
                    
                    if (currentProxy != null)
                    {
                        failedProxyIds.Add(currentProxy.Id);
                        await _proxyService.RecordProxyFailureAsync(currentProxy.Id, $"Proxy tunnel failed: {ex.Message}");
                        _logger.LogWarning("Proxy {ProxyId} ({Host}:{Port}) tunnel connection failed: {Error}. Excluding and trying another proxy immediately.", 
                            currentProxy.Id, currentProxy.Host, currentProxy.Port, ex.Message);
                    }
                    
                    // Don't count proxy failures as API retry attempts - retry immediately with new proxy
                    // Only add minimal delay to prevent tight loop
                    if (attempt < settings.MaxRetries)
                    {
                        await Task.Delay(500, token); // Short delay before retry with new proxy
                        continue;
                    }
                    
                    return ($"Lỗi Proxy: {ex.Message}", 0, "PROXY_TUNNEL_ERROR", ex.Message, 0);
                }
                catch (Exception ex)
                {
                    // === Lỗi kết nối - KHÔNG tính vào RPM (release slot early) ===
                    if (currentProxySlotId != null)
                    {
                        _proxyRateLimiter.ReleaseSlotEarly(currentProxySlotId);
                        currentProxySlotId = null;
                    }
                    
                    // Check if this is a CRITICAL proxy error (connection timeout, host unreachable, etc.)
                    // These proxies should be disabled IMMEDIATELY and PERMANENTLY
                    if (currentProxy != null && ProxyService.IsCriticalProxyError(ex))
                    {
                        var errorDescription = ProxyService.GetProxyErrorDescription(ex);
                        _logger.LogError("🚫 CRITICAL PROXY ERROR for Proxy {ProxyId} ({Host}:{Port}): {Error}. Disabling proxy PERMANENTLY.", 
                            currentProxy.Id, currentProxy.Host, currentProxy.Port, errorDescription);
                        
                        // Disable proxy immediately - don't wait for consecutive failures
                        await _proxyService.DisableProxyImmediatelyAsync(currentProxy.Id, errorDescription);
                        failedProxyIds.Add(currentProxy.Id);
                        
                        // Retry immediately with new proxy (don't count as retry attempt for critical proxy errors)
                        if (attempt < settings.MaxRetries)
                        {
                            await Task.Delay(500, token); // Short delay before retry
                            continue;
                        }
                    }
                    // Record non-critical proxy failure and switch to a new proxy
                    else if (currentProxy != null && (ex is HttpRequestException || ex is TaskCanceledException))
                    {
                        failedProxyIds.Add(currentProxy.Id);
                        
                        // Check if this is a timeout/cancellation error (very transient)
                        bool isTimeoutError = ProxyService.IsTimeoutOrCancellationError(ex);
                        var errorMessage = ProxyService.GetProxyErrorDescription(ex);
                        
                        await _proxyService.RecordProxyFailureAsync(currentProxy.Id, errorMessage, 
                            isIntermittent: false, isTimeoutError: isTimeoutError);
                        
                        if (isTimeoutError)
                        {
                            _logger.LogDebug("Proxy {ProxyId} ({Host}:{Port}) timeout (transient): {Error}. Switching to another proxy.", 
                                currentProxy.Id, currentProxy.Host, currentProxy.Port, errorMessage);
                        }
                        else
                        {
                            _logger.LogWarning("Proxy {ProxyId} ({Host}:{Port}) connection failed: {Error}. Switching to a new proxy.", 
                                currentProxy.Id, currentProxy.Host, currentProxy.Port, errorMessage);
                        }
                    }
                    
                    _logger.LogError(ex, "Exception during API call. Retrying in {Delay}ms... (Attempt {Attempt}/{MaxRetries})",
                        settings.RetryDelayMs * attempt, attempt, settings.MaxRetries);

                    if (attempt >= settings.MaxRetries)
                        return ($"Lỗi Exception: {ex.Message}", 0, "EXCEPTION", ex.Message, 0);

                    await Task.Delay(settings.RetryDelayMs * attempt, token);
                }
            }

            // Cleanup: release slot if still held
            if (currentProxySlotId != null)
            {
                _proxyRateLimiter.ReleaseSlotEarly(currentProxySlotId);
            }

            return ("Lỗi API: Hết số lần thử lại.", 0, "MAX_RETRIES", "Exceeded maximum retry attempts", 0);
        }
        
        /// <summary>
        /// Lấy proxy có RPM slot khả dụng, loại trừ các proxy đã failed.
        /// </summary>
        private async Task<Proxy?> GetProxyWithAvailableRpmSlotAsync(HashSet<int> excludeProxyIds, string requestId, CancellationToken token)
        {
            // Get list of available proxies
            var proxy = await _proxyService.GetNextProxyAsync(excludeProxyIds);
            if (proxy == null)
            {
                return null;
            }
            
            // Check if this proxy has available RPM slots
            if (_proxyRateLimiter.HasAvailableSlot(proxy.Id))
            {
                return proxy;
            }
            
            // Current proxy is at RPM limit, try to find another one
            var triedProxyIds = new HashSet<int>(excludeProxyIds) { proxy.Id };
            
            for (int i = 0; i < MAX_PROXY_SEARCH_ATTEMPTS; i++)
            {
                var nextProxy = await _proxyService.GetNextProxyAsync(triedProxyIds);
                if (nextProxy == null)
                {
                    // No more proxies available - return the original one (will wait for slot)
                    _logger.LogInformation("All proxies at RPM limit or excluded. Using proxy {ProxyId} and waiting for slot.", proxy.Id);
                    return proxy;
                }
                
                if (_proxyRateLimiter.HasAvailableSlot(nextProxy.Id))
                {
                    return nextProxy;
                }
                
                triedProxyIds.Add(nextProxy.Id);
            }
            
            // All proxies at RPM limit, return the first one
            return proxy;
        }
        
        /// <summary>
        /// Check if the exception is a proxy tunnel error (HTTP 400/407 during CONNECT).
        /// </summary>
        private static bool IsProxyTunnelError(HttpRequestException ex)
        {
            // Check for proxy tunnel error patterns in the exception message
            var message = ex.Message ?? string.Empty;
            return message.Contains("proxy tunnel", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("proxy", StringComparison.OrdinalIgnoreCase) && 
                   (message.Contains("400") || message.Contains("407") || message.Contains("403"));
        }

        private async Task UpdateJobStatus(string sessionId, JobStatus newStatus, string errorMessage = null)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var job = await context.TranslationJobs.FindAsync(sessionId);

                if (job == null)
                {
                    _logger.LogWarning("Job {SessionId} không tồn tại!", sessionId);
                    return;
                }

                // ✅ KHÔNG update nếu đã ở trạng thái cuối
                if (job.Status == JobStatus.Completed || job.Status == JobStatus.Failed)
                {
                    _logger.LogInformation("Job {SessionId} đã ở trạng thái {Status}, bỏ qua.", sessionId, job.Status);
                    return;
                }

                job.Status = newStatus;
                if (errorMessage != null) job.ErrorMessage = errorMessage;
                await context.SaveChangesAsync();

                _logger.LogInformation("✅ Job {SessionId} -> {Status}", sessionId, newStatus);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ UpdateJobStatus FAILED: {SessionId}", sessionId);
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
