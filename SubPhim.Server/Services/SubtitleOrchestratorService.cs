using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubPhim.Server.Services
{
    /// <summary>
    /// Service để điều phối dịch phụ đề phân tán đến nhiều server fly.io
    /// </summary>
    public interface ISubtitleOrchestratorService
    {
        Task<SubtitleJobResponse> SubmitJobAsync(SubtitleTranslationRequest request, int? userId = null, string? externalApiKeyPrefix = null);
        Task<SubtitleJobStatusResponse> GetJobStatusAsync(string sessionId);
        Task<SubtitleJobResultsResponse> GetJobResultsAsync(string sessionId);
        Task ProcessCallbackAsync(ServerCallbackData callback);
    }

    public class SubtitleOrchestratorService : ISubtitleOrchestratorService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IEncryptionService _encryptionService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<SubtitleOrchestratorService> _logger;

        // Track cooldown keys in memory
        private static readonly ConcurrentDictionary<int, DateTime> _apiKeyCooldowns = new();

        public SubtitleOrchestratorService(
    IServiceScopeFactory scopeFactory,
    IEncryptionService encryptionService,
    IHttpClientFactory httpClientFactory,
    ILogger<SubtitleOrchestratorService> logger)
        {
            _scopeFactory = scopeFactory;
            _encryptionService = encryptionService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<SubtitleJobResponse> SubmitJobAsync(SubtitleTranslationRequest request, int? userId = null, string? externalApiKeyPrefix = null)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Load settings
            var settings = await context.SubtitleApiSettings.FindAsync(1) ?? new SubtitleApiSetting();

            // Validate
            if (request.Lines == null || !request.Lines.Any())
            {
                throw new ArgumentException("Không có dòng phụ đề nào để dịch.");
            }

            // Check if session already exists
            if (await context.SubtitleTranslationJobs.AnyAsync(j => j.SessionId == request.SessionId))
            {
                throw new InvalidOperationException($"Session {request.SessionId} đã tồn tại.");
            }

            // Get available servers
            var availableServers = await context.SubtitleTranslationServers
                .Where(s => s.IsEnabled && !s.IsBusy)
                .OrderBy(s => s.Priority)
                .ThenBy(s => s.FailureCount)
                .ToListAsync();

            if (!availableServers.Any())
            {
                throw new InvalidOperationException("Không có server dịch nào khả dụng.");
            }

            // Get available API keys (not in cooldown)
            var now = DateTime.UtcNow;
            var availableKeys = await context.SubtitleApiKeys
                .Where(k => k.IsEnabled && (k.CooldownUntil == null || k.CooldownUntil <= now))
                .OrderBy(k => k.TotalSuccessRequests) // Load balance
                .ToListAsync();

            if (!availableKeys.Any())
            {
                throw new InvalidOperationException("Không có API key nào khả dụng.");
            }

            // Calculate smart batching
            var batchPlan = CalculateBatchPlan(request.Lines.Count, settings, availableServers.Count);

            // Create job
            var job = new SubtitleTranslationJob
            {
                SessionId = request.SessionId,
                UserId = userId,
                ExternalApiKeyPrefix = externalApiKeyPrefix,
                Status = SubtitleJobStatus.Pending,
                TotalLines = request.Lines.Count,
                CompletedLines = 0,
                Progress = 0,
                SystemInstruction = request.SystemInstruction,
                Prompt = request.Prompt,
                Model = request.Model ?? settings.DefaultModel,
                ThinkingBudget = request.ThinkingBudget ?? (settings.ThinkingBudget > 0 ? settings.ThinkingBudget : null),
                CallbackUrl = request.CallbackUrl,
                OriginalLinesJson = JsonSerializer.Serialize(request.Lines),
                CreatedAt = DateTime.UtcNow
            };

            context.SubtitleTranslationJobs.Add(job);
            await context.SaveChangesAsync();

            _logger.LogInformation("Job {SessionId} created: {Lines} lines, {Batches} batches",
                request.SessionId, request.Lines.Count, batchPlan.Count);

            // Start distribution in background
            _ = Task.Run(async () => await DistributeJobAsync(job.SessionId, request.Lines, batchPlan, settings, availableServers, availableKeys));

            return new SubtitleJobResponse
            {
                SessionId = job.SessionId,
                Status = "pending",
                TotalLines = job.TotalLines,
                BatchCount = batchPlan.Count,
                ServersAssigned = Math.Min(batchPlan.Count, availableServers.Count),
                Message = "Job đã được tạo và đang phân phối đến các server."
            };
        }

        private List<BatchInfo> CalculateBatchPlan(int totalLines, SubtitleApiSetting settings, int availableServerCount)
        {
            var batches = new List<BatchInfo>();
            int linesPerServer = settings.LinesPerServer;
            int mergeThreshold = settings.MergeBatchThreshold;

            int currentIndex = 0;
            int batchIndex = 0;

            while (currentIndex < totalLines)
            {
                int remainingLines = totalLines - currentIndex;
                int batchSize = Math.Min(linesPerServer, remainingLines);

                // Smart merge: if last batch is too small, merge with previous
                if (remainingLines <= linesPerServer + mergeThreshold && batches.Any())
                {
                    // Merge into last batch
                    var lastBatch = batches.Last();
                    lastBatch.LineCount += remainingLines;
                    _logger.LogDebug("Merged {Lines} lines into batch {BatchIndex}", remainingLines, lastBatch.BatchIndex);
                    break;
                }

                batches.Add(new BatchInfo
                {
                    BatchIndex = batchIndex,
                    StartIndex = currentIndex,
                    LineCount = batchSize
                });

                currentIndex += batchSize;
                batchIndex++;
            }

            return batches;
        }

        private async Task DistributeJobAsync(
            string sessionId,
            List<SubtitleLine> lines,
            List<BatchInfo> batchPlan,
            SubtitleApiSetting settings,
            List<SubtitleTranslationServer> servers,
            List<SubtitleApiKey> apiKeys)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var job = await context.SubtitleTranslationJobs.FindAsync(sessionId);
            if (job == null) return;

            job.Status = SubtitleJobStatus.Distributing;
            await context.SaveChangesAsync();

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(settings.ServerTimeoutSeconds);

            int serverIndex = 0;
            int keyIndex = 0;
            var tasks = new List<Task>();
            var semaphore = new SemaphoreSlim(servers.Count); // Limit concurrent server calls

            foreach (var batch in batchPlan)
            {
                var server = servers[serverIndex % servers.Count];
                serverIndex++;

                var keysForServer = new List<string>();
                for (int i = 0; i < settings.ApiKeysPerServer && keyIndex < apiKeys.Count * 2; i++)
                {
                    var key = apiKeys[keyIndex % apiKeys.Count];
                    keyIndex++;
                    if (_apiKeyCooldowns.TryGetValue(key.Id, out var cooldownUntil) && cooldownUntil > DateTime.UtcNow)
                        continue;
                    try
                    {
                        keysForServer.Add(_encryptionService.Decrypt(key.EncryptedApiKey, key.Iv));
                    }
                    catch { /* ... */ }
                }
                if (!keysForServer.Any()) continue;

                var serverTask = new SubtitleServerTask
                {
                    SessionId = sessionId,
                    ServerId = server.Id,
                    BatchIndex = batch.BatchIndex,
                    StartLineIndex = batch.StartIndex,
                    LineCount = batch.LineCount,
                    Status = ServerTaskStatus.Pending,
                    CreatedAt = DateTime.UtcNow
                };

                context.SubtitleServerTasks.Add(serverTask);

                // === THAY ĐỔI QUAN TRỌNG: LƯU VÀO DB NGAY LẬP TỨC ĐỂ LẤY ID THẬT ===
                await context.SaveChangesAsync();
                // Sau dòng này, serverTask.Id sẽ có giá trị thật (ví dụ: 5, 6, 7...)

                var batchLines = lines.Skip(batch.StartIndex).Take(batch.LineCount).ToList();

                tasks.Add(SendToServerAsync( // Bây giờ SendToServerAsync nhận được task.Id có giá trị đúng
                    httpClient,
                    server,
                    serverTask,
                    job,
                    batchLines,
                    keysForServer,
                    settings,
                    semaphore
                ));

                if (settings.DelayBetweenServerBatchesMs > 0)
                {
                    await Task.Delay(settings.DelayBetweenServerBatchesMs);
                }
            }

            // Không cần SaveChanges() ở đây nữa vì đã lưu trong loop

            job.Status = SubtitleJobStatus.Processing;
            await context.SaveChangesAsync();

            // Wait for all tasks
            await Task.WhenAll(tasks);

            // Aggregate results
            await AggregateResultsAsync(sessionId);
        }

        private async Task SendToServerAsync(
    HttpClient httpClient,
    SubtitleTranslationServer server,
    SubtitleServerTask task,
    SubtitleTranslationJob job,
    List<SubtitleLine> lines,
    List<string> apiKeys,
    SubtitleApiSetting settings,
    SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync();

            // === THÊM DEBUG LOG: Ghi lại thông tin trước khi gửi ===
            _logger.LogInformation(
                "Attempting to send task for SessionId: {SessionId}, BatchIndex: {BatchIndex} to Server: {ServerUrl}",
                job.SessionId, task.BatchIndex, server.ServerUrl);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var taskInDb = await context.SubtitleServerTasks.FindAsync(task.Id);
                var serverInDb = await context.SubtitleTranslationServers.FindAsync(server.Id);

                if (taskInDb == null || serverInDb == null)
                {
                    _logger.LogError("Could not find TaskId {TaskId} or ServerId {ServerId} in DB before sending.", task.Id, server.Id);
                    return;
                }

                taskInDb.Status = ServerTaskStatus.Sent;
                taskInDb.SentAt = DateTime.UtcNow;
                serverInDb.IsBusy = true;
                serverInDb.CurrentSessionId = job.SessionId;
                await context.SaveChangesAsync();

                string? callbackUrl = null;
                if (settings.EnableCallback && !string.IsNullOrWhiteSpace(settings.MainServerUrl))
                {
                    callbackUrl = $"{settings.MainServerUrl.TrimEnd('/')}/api/subtitle/callback/{job.SessionId}/{task.BatchIndex}";
                }

                var serverRequest = new
                {
                    model = job.Model,
                    prompt = job.Prompt,
                    lines = lines.Select(l => new { index = l.Index, text = l.Text }),
                    systemInstruction = job.SystemInstruction,
                    sessionId = $"{job.SessionId}_batch{task.BatchIndex}",
                    apiKeys = apiKeys,
                    batchSize = settings.BatchSizePerServer,
                    thinkingBudget = job.ThinkingBudget,
                    callbackUrl = callbackUrl
                };

                var startTime = DateTime.UtcNow;
                var requestJson = JsonSerializer.Serialize(serverRequest);

                // === THÊM DEBUG LOG: Ghi lại payload và URL sẽ gọi ===
                _logger.LogInformation("Sending POST to {ServerUrl} with payload: {RequestPayload}", server.ServerUrl, requestJson);

                try
                {
                    var response = await httpClient.PostAsJsonAsync($"{server.ServerUrl}/translate", serverRequest);

                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadFromJsonAsync<ServerTranslateResponse>();
                        taskInDb.Status = ServerTaskStatus.Processing;
                        _logger.LogInformation("Successfully sent Batch {BatchIndex} to server {ServerId}. Server responded OK.", task.BatchIndex, server.Id);
                    }
                    else
                    {
                        var error = await response.Content.ReadAsStringAsync();
                        // === SỬA LỖI QUAN TRỌNG: CẬP NHẬT TRẠNG THÁI FAILED KHI HTTP LỖI ===
                        taskInDb.Status = ServerTaskStatus.Failed;
                        taskInDb.ErrorMessage = $"HTTP Error {(int)response.StatusCode}: {error}";
                        serverInDb.FailureCount++;
                        serverInDb.LastFailedAt = DateTime.UtcNow;
                        serverInDb.LastFailureReason = taskInDb.ErrorMessage;
                        _logger.LogError("Failed to send Batch {BatchIndex}. Server {ServerUrl} responded with {StatusCode}: {Error}", task.BatchIndex, server.ServerUrl, (int)response.StatusCode, error);
                    }
                }
                catch (Exception ex)
                {
                    // === SỬA LỖI QUAN TRỌNG: CẬP NHẬT TRẠNG THÁI FAILED KHI CÓ EXCEPTION (VD: TIMEOUT, CONNECTION REFUSED) ===
                    taskInDb.Status = ServerTaskStatus.Failed;
                    taskInDb.ErrorMessage = ex.Message; // Ghi lại lỗi thực tế
                    serverInDb.FailureCount++;
                    serverInDb.LastFailedAt = DateTime.UtcNow;
                    serverInDb.LastFailureReason = ex.Message;
                    _logger.LogError(ex, "CRITICAL EXCEPTION sending Batch {BatchIndex} to server {ServerId}", task.BatchIndex, server.Id);
                }
                finally
                {
                    // Code này sẽ luôn chạy dù thành công hay thất bại
                    taskInDb.ProcessingTimeMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
                    serverInDb.IsBusy = false;
                    serverInDb.CurrentSessionId = null;
                    serverInDb.LastUsedAt = DateTime.UtcNow;
                    // Chỉ tăng UsageCount nếu gửi thành công
                    if (taskInDb.Status == ServerTaskStatus.Processing)
                    {
                        serverInDb.UsageCount++;
                    }
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                // Ghi log nếu có lỗi ngoài cả khối try-catch trên
                _logger.LogError(ex, "Unhandled exception in SendToServerAsync for SessionId {SessionId}", job.SessionId);
            }
            finally
            {
                semaphore.Release();
            }
        }
        public async Task ProcessCallbackAsync(ServerCallbackData callback)
        {
            // === THÊM DEBUG LOG: Ghi lại toàn bộ callback nhận được ===
            var callbackJson = JsonSerializer.Serialize(callback);
            _logger.LogInformation("Processing callback data: {CallbackJson}", callbackJson);

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var parts = callback.SessionId.Split("_batch");
            if (parts.Length != 2)
            {
                _logger.LogWarning("Invalid callback session format: {SessionId}", callback.SessionId);
                return;
            }

            var originalSessionId = parts[0];
            if (!int.TryParse(parts[1], out var batchIndex))
            {
                _logger.LogWarning("Could not parse batch index from callback session: {SessionId}", callback.SessionId);
                return;
            }

            // === THÊM DEBUG LOG: Ghi lại thông tin đã parse được ===
            _logger.LogInformation("Callback received for SessionId: {OriginalSessionId}, BatchIndex: {BatchIndex}", originalSessionId, batchIndex);

            var task = await context.SubtitleServerTasks
                .FirstOrDefaultAsync(t => t.SessionId == originalSessionId && t.BatchIndex == batchIndex);

            if (task == null)
            {
                // === THÊM DEBUG LOG: Cảnh báo khi không tìm thấy task tương ứng ===
                _logger.LogWarning("Callback received but no matching task found in DB for SessionId: {OriginalSessionId}, BatchIndex: {BatchIndex}", originalSessionId, batchIndex);
                return;
            }

            task.Status = callback.Status == "completed" ? ServerTaskStatus.Completed : ServerTaskStatus.Failed;
            task.CompletedAt = DateTime.UtcNow;
            task.ErrorMessage = callback.Error;

            // Store results
            if (callback.Status == "completed" && callback.Results != null)
            {
                task.ResultJson = JsonSerializer.Serialize(callback.Results);
            }

            // Process API key usage
            if (callback.ApiKeyUsage != null)
            {
                foreach (var usage in callback.ApiKeyUsage)
                {
                    // Update key stats
                    if (usage.FailureCount > 0)
                    {
                        // Check if it's 429 error
                        var key = await context.SubtitleApiKeys
                            .FirstOrDefaultAsync(k => k.EncryptedApiKey.StartsWith(usage.MaskedKey.Substring(0, 8)));

                        if (key != null)
                        {
                            key.TotalFailedRequests += usage.FailureCount;
                            key.TotalSuccessRequests += usage.SuccessCount;

                            // Put in cooldown if needed
                            if (usage.FailureCount > 0)
                            {
                                var settings = await context.SubtitleApiSettings.FindAsync(1);
                                key.CooldownUntil = DateTime.UtcNow.AddMinutes(settings?.ApiKeyCooldownMinutes ?? 5);
                                key.Consecutive429Count++;
                                _apiKeyCooldowns[key.Id] = key.CooldownUntil.Value;
                            }
                        }
                    }
                }
            }

            await context.SaveChangesAsync();

            // Check if all tasks are complete
            await AggregateResultsAsync(originalSessionId);
        }

        private async Task AggregateResultsAsync(string sessionId)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var job = await context.SubtitleTranslationJobs
                .Include(j => j.ServerTasks)
                .FirstOrDefaultAsync(j => j.SessionId == sessionId);

            if (job == null) return;

            var allTasks = job.ServerTasks.ToList();
            var completedTasks = allTasks.Where(t => t.Status == ServerTaskStatus.Completed).ToList();
            var failedTasks = allTasks.Where(t => t.Status == ServerTaskStatus.Failed).ToList();
            var pendingTasks = allTasks.Where(t => t.Status != ServerTaskStatus.Completed && t.Status != ServerTaskStatus.Failed).ToList();

            // Update progress
            int completedLines = completedTasks.Sum(t => t.LineCount);
            job.CompletedLines = completedLines;
            job.Progress = job.TotalLines > 0 ? (float)completedLines / job.TotalLines * 100 : 0;

            // Check if all done
            if (!pendingTasks.Any())
            {
                job.Status = failedTasks.Any()
                    ? (completedTasks.Any() ? SubtitleJobStatus.PartialCompleted : SubtitleJobStatus.Failed)
                    : SubtitleJobStatus.Completed;

                job.CompletedAt = DateTime.UtcNow;

                // Aggregate results
                var allResults = new List<TranslatedLineResult>();
                foreach (var task in completedTasks.OrderBy(t => t.StartLineIndex))
                {
                    if (!string.IsNullOrEmpty(task.ResultJson))
                    {
                        try
                        {
                            var taskResults = JsonSerializer.Deserialize<List<TranslatedLineResult>>(task.ResultJson);
                            if (taskResults != null)
                            {
                                allResults.AddRange(taskResults);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error parsing results for task {TaskId}", task.Id);
                        }
                    }
                }

                job.ResultsJson = JsonSerializer.Serialize(allResults.OrderBy(r => r.Index).ToList());

                // Log errors if any
                if (failedTasks.Any())
                {
                    job.ErrorMessage = string.Join("; ", failedTasks.Select(t => $"Batch {t.BatchIndex}: {t.ErrorMessage}"));
                }

                _logger.LogInformation("Job {SessionId} {Status}: {Completed}/{Total} lines",
                    sessionId, job.Status, job.CompletedLines, job.TotalLines);
            }

            await context.SaveChangesAsync();

            // Send callback to client if configured
            if (!string.IsNullOrEmpty(job.CallbackUrl) && job.Status != SubtitleJobStatus.Processing)
            {
                await SendClientCallbackAsync(job);
            }
        }

        private async Task SendClientCallbackAsync(SubtitleTranslationJob job)
        {
            try
            {
                var httpClient = _httpClientFactory.CreateClient();
                var payload = new
                {
                    sessionId = job.SessionId,
                    status = job.Status.ToString().ToLower(),
                    totalLines = job.TotalLines,
                    completedLines = job.CompletedLines,
                    progress = job.Progress,
                    error = job.ErrorMessage
                };

                await httpClient.PostAsJsonAsync(job.CallbackUrl, payload);
                _logger.LogInformation("Callback sent to {Url} for job {SessionId}", job.CallbackUrl, job.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send callback for job {SessionId}", job.SessionId);
            }
        }

        public async Task<SubtitleJobStatusResponse> GetJobStatusAsync(string sessionId)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var job = await context.SubtitleTranslationJobs
                .Include(j => j.ServerTasks)
                .FirstOrDefaultAsync(j => j.SessionId == sessionId);

            if (job == null)
            {
                throw new KeyNotFoundException($"Job {sessionId} không tồn tại.");
            }

            var taskStats = job.ServerTasks.GroupBy(t => t.Status).ToDictionary(g => g.Key, g => g.Count());

            return new SubtitleJobStatusResponse
            {
                SessionId = job.SessionId,
                Status = job.Status.ToString().ToLower(),
                Progress = Math.Round(job.Progress, 2),
                TotalLines = job.TotalLines,
                CompletedLines = job.CompletedLines,
                Error = job.ErrorMessage,
                TaskStats = taskStats.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
            };
        }

        public async Task<SubtitleJobResultsResponse> GetJobResultsAsync(string sessionId)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var job = await context.SubtitleTranslationJobs.FirstOrDefaultAsync(j => j.SessionId == sessionId);

            if (job == null)
            {
                throw new KeyNotFoundException($"Job {sessionId} không tồn tại.");
            }

            var results = new List<TranslatedLineResult>();
            if (!string.IsNullOrEmpty(job.ResultsJson))
            {
                results = JsonSerializer.Deserialize<List<TranslatedLineResult>>(job.ResultsJson) ?? new();
            }

            return new SubtitleJobResultsResponse
            {
                SessionId = job.SessionId,
                Status = job.Status.ToString().ToLower(),
                TotalLines = job.TotalLines,
                CompletedLines = job.CompletedLines,
                Results = results,
                Error = job.ErrorMessage,
                CreatedAt = job.CreatedAt,
                CompletedAt = job.CompletedAt
            };
        }
    }

    #region DTOs
    public class SubtitleTranslationRequest
    {
        public string SessionId { get; set; }
        public string Prompt { get; set; }
        public string SystemInstruction { get; set; }
        public List<SubtitleLine> Lines { get; set; }
        public string? Model { get; set; }
        public int? ThinkingBudget { get; set; }
        public string? CallbackUrl { get; set; }
    }

    public class SubtitleLine
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; }
    }

    public class SubtitleJobResponse
    {
        public string SessionId { get; set; }
        public string Status { get; set; }
        public int TotalLines { get; set; }
        public int BatchCount { get; set; }
        public int ServersAssigned { get; set; }
        public string Message { get; set; }
    }

    public class SubtitleJobStatusResponse
    {
        public string SessionId { get; set; }
        public string Status { get; set; }
        public double Progress { get; set; }
        public int TotalLines { get; set; }
        public int CompletedLines { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, int> TaskStats { get; set; } = new();
    }

    public class SubtitleJobResultsResponse
    {
        public string SessionId { get; set; }
        public string Status { get; set; }
        public int TotalLines { get; set; }
        public int CompletedLines { get; set; }
        public List<TranslatedLineResult> Results { get; set; } = new();
        public string? Error { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class TranslatedLineResult
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("original")]
        public string Original { get; set; }

        [JsonPropertyName("translated")]
        public string Translated { get; set; }
    }

    public class BatchInfo
    {
        public int BatchIndex { get; set; }
        public int StartIndex { get; set; }
        public int LineCount { get; set; }
    }

    public class ServerTranslateResponse
    {
        public string SessionId { get; set; }
        public string Status { get; set; }
        public int TotalLines { get; set; }
        public string Message { get; set; }
    }

    public class ServerCallbackData
    {
        public string SessionId { get; set; }
        public string Status { get; set; }
        public int TotalLines { get; set; }
        public int CompletedLines { get; set; }
        public string? Error { get; set; }
        public List<ApiKeyUsageInfo>? ApiKeyUsage { get; set; }
        public List<TranslatedLineResult>? Results { get; set; }
    }

    public class ApiKeyUsageInfo
    {
        public string ApiKey { get; set; }
        public string MaskedKey { get; set; }
        public int RequestCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
    }
    #endregion
}
