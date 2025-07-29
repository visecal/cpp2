using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SubPhim.Server.Data;
using SubPhim.Server.Models;
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
        public record CreateJobResult(string Status, string Message, string SessionId = null, int RemainingLines = 0);
        public TranslationOrchestratorService(IServiceProvider serviceProvider, ILogger<TranslationOrchestratorService> logger, IHttpClientFactory httpClientFactory)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<CreateJobResult> CreateJobAsync(int userId, string genre, string targetLanguage, List<SrtLine> allLines, bool acceptPartial)
        {
            _logger.LogInformation("GATEKEEPER: Job creation request for User ID {UserId}. AcceptPartial={AcceptPartial}", userId, acceptPartial);

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var user = await context.Users.FindAsync(userId);
            if (user == null) throw new InvalidOperationException("User not found.");

            // Logic reset hàng ngày (đảm bảo dữ liệu là mới nhất)
            var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
            var lastResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastLocalSrtResetUtc, vietnamTimeZone);
            if (lastResetInVietnam.Date < vietnamNow.Date)
            {
                user.LocalSrtLinesUsedToday = 0;
                user.LastLocalSrtResetUtc = DateTime.UtcNow.Date;
                // Lưu ngay thay đổi này để đảm bảo các request đồng thời khác cũng thấy
                await context.SaveChangesAsync();
            }

            int remainingLines = user.DailyLocalSrtLimit - user.LocalSrtLinesUsedToday;
            int requestedLines = allLines.Count;

            // --- BẮT ĐẦU LOGIC PHÂN LUỒNG MỚI ---

            // TRƯỜNG HỢP 1: User đủ lượt
            if (requestedLines <= remainingLines)
            {
                user.LocalSrtLinesUsedToday += requestedLines;
                var sessionId = await CreateJobInDb(user, genre, targetLanguage, allLines, context);
                _ = ProcessJob(sessionId, user.Tier);
                return new CreateJobResult("Accepted", "OK", sessionId);
            }

            // TRƯỜNG HỢP 2: User không đủ lượt, nhưng vẫn còn một ít
            if (remainingLines > 0)
            {
                // 2a: User đã đồng ý dịch số lượng còn lại
                if (acceptPartial)
                {
                    var partialLines = allLines.Take(remainingLines).ToList();
                    user.LocalSrtLinesUsedToday += partialLines.Count; // Trừ đúng số lượng dịch
                    var sessionId = await CreateJobInDb(user, genre, targetLanguage, partialLines, context);
                    _ = ProcessJob(sessionId, user.Tier);
                    return new CreateJobResult("Accepted", "OK", sessionId);
                }
                // 2b: Đây là lần hỏi đầu tiên, đưa ra gợi ý
                else
                {
                    string message = $"Bạn không đủ lượt dịch Local API. Yêu cầu: {requestedLines} dòng, còn lại: {remainingLines} dòng.\n\nBạn có muốn dịch {remainingLines} dòng đầu tiên không?";
                    return new CreateJobResult("PartialContent", message, RemainingLines: remainingLines);
                }
            }

            // TRƯỜNG HỢP 3: User đã hết sạch lượt
            string errorMessage = $"Bạn đã hết {user.DailyLocalSrtLimit} lượt dịch Local API trong ngày.";
            return new CreateJobResult("Error", errorMessage);
        }

        // Tách logic tạo Job ra một hàm riêng để tái sử dụng
        private async Task<string> CreateJobInDb(User user, string genre, string targetLanguage, List<SrtLine> linesToProcess, AppDbContext context)
        {
            var sessionId = Guid.NewGuid().ToString();
            var newJob = new TranslationJobDb
            {
                SessionId = sessionId,
                UserId = user.Id,
                Genre = genre,
                TargetLanguage = targetLanguage,
                Status = JobStatus.Pending,
                OriginalLines = linesToProcess.Select(l => new OriginalSrtLineDb { LineIndex = l.Index, OriginalText = l.OriginalText }).ToList()
            };
            context.TranslationJobs.Add(newJob);
            await context.SaveChangesAsync();
            _logger.LogInformation("[DB] Created Job {SessionId} with {LineCount} lines for user {Username}", sessionId, linesToProcess.Count, user.Username);
            return sessionId;
        }
        #region Unchanged Methods
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
        #endregion

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

                var enabledKeys = await context.ManagedApiKeys.AsNoTracking().Where(k => k.IsEnabled && k.PoolType == poolToUse).ToListAsync(cts.Token);
                if (!enabledKeys.Any()) throw new Exception($"Không có API key nào đang hoạt động cho nhóm '{poolToUse}'.");

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

                foreach (var batch in batches)
                {
                    if (cts.IsCancellationRequested) break;
                    await rpmLimiter.WaitAsync(cts.Token);
                    processingTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var apiKeyRecord = enabledKeys[Random.Shared.Next(enabledKeys.Count)];
                            var apiKey = encryptionService.Decrypt(apiKeyRecord.EncryptedApiKey, apiKeyRecord.Iv);
                            var (translatedBatch, tokensUsed) = await TranslateBatchAsync(batch, job, settings, activeModel.ModelName, apiKey, cts.Token);
                            await SaveResultsToDb(sessionId, translatedBatch);
                            await UpdateUsageInDb(apiKeyRecord.Id, tokensUsed);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi xử lý batch cho job {SessionId}", sessionId);
                            var errorResults = batch.Select(l => new TranslatedSrtLineDb { SessionId = sessionId, LineIndex = l.LineIndex, TranslatedText = "[LỖI EXCEPTION]", Success = false }).ToList();
                            await SaveResultsToDb(sessionId, errorResults);
                        }
                    }, cts.Token));
                }

                await Task.WhenAll(processingTasks);
                _logger.LogInformation("All batches for job {SessionId} completed. Setting status to 'Completed'.", sessionId);
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
        private async Task<(List<TranslatedSrtLineDb> results, int tokensUsed)> TranslateBatchAsync(
            List<OriginalSrtLineDb> batch, TranslationJobDb job, LocalApiSetting settings,
            string modelName, string apiKey, CancellationToken token)
        {
            var payloadBuilder = new StringBuilder();
            foreach (var line in batch)
            {
                payloadBuilder.AppendLine($"{line.LineIndex}: {line.OriginalText.Replace("\r\n", " ").Replace("\n", " ")}");
            }
            string payload = payloadBuilder.ToString().TrimEnd();

            // 1. XÂY DỰNG PAYLOAD VỚI CÁC THAM SỐ ĐÃ ĐƯỢC CHỨNG MINH LÀ ỔN ĐỊNH
            var generationConfig = new JObject
            {
                ["temperature"] = 1, // GIÁ TRỊ TỪ MÃ THAM KHẢO
                ["topP"] = 0.95,     // GIÁ TRỊ TỪ MÃ THAM KHẢO, LUÔN CÓ
                ["maxOutputTokens"] = 15000 // GIÁ TRỊ TỪ MÃ THAM KHẢO
            };

            // Vẫn giữ lại logic thinking_budget nếu được bật
            if (settings.EnableThinkingBudget && settings.ThinkingBudget > 0)
            {
                generationConfig["thinking_config"] = new JObject { ["thinking_budget"] = settings.ThinkingBudget };
            }

            var requestPayloadObject = new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = $"Dịch các câu thoại sau sang {job.TargetLanguage}:\n\n{payload}" } } } },
                system_instruction = new { parts = new[] { new { text = GetSystemInstructionForGeminiSrtTranslation(job.Genre, job.TargetLanguage) } } },
                generationConfig
            };

            string jsonPayload = JsonConvert.SerializeObject(requestPayloadObject, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";

            // 2. GỌI PHƯƠNG THỨC GỌI API CHUYÊN DỤNG VỚI LOGIC RETRY MẠNH MẼ
            var (responseText, tokensUsed) = await CallApiWithRetryAsync(apiUrl, jsonPayload, settings, token);

            var results = new List<TranslatedSrtLineDb>();
            if (responseText != null && !responseText.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase))
            {
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
                        results.Add(new TranslatedSrtLineDb { SessionId = job.SessionId, LineIndex = line.LineIndex, TranslatedText = string.IsNullOrWhiteSpace(translated) ? "[API DỊCH RỖNG]" : translated, Success = true });
                    else
                        results.Add(new TranslatedSrtLineDb { SessionId = job.SessionId, LineIndex = line.LineIndex, TranslatedText = "[API KHÔNG TRẢ VỀ DÒNG NÀY]", Success = false });
                }
            }
            else
            {
                // Nếu có lỗi, ghi lỗi cho tất cả các dòng trong batch
                foreach (var line in batch)
                {
                    results.Add(new TranslatedSrtLineDb { SessionId = job.SessionId, LineIndex = line.LineIndex, TranslatedText = $"[LỖI: {responseText}]", Success = false });
                }
            }
            return (results, tokensUsed);
        }

        /// <summary>
        /// Phương thức chuyên dụng để gọi API, tích hợp logic Exponential Backoff từ mã nguồn tham khảo.
        /// </summary>
        private async Task<(string responseText, int tokensUsed)> CallApiWithRetryAsync(string url, string jsonPayload, LocalApiSetting settings, CancellationToken token)
        {
            for (int attempt = 1; attempt <= settings.MaxRetries; attempt++)
            {
                if (token.IsCancellationRequested) return ("Lỗi: Tác vụ đã bị hủy.", 0);

                try
                {
                    // Dùng HttpClientFactory để tối ưu, nếu đã đăng ký trong Program.cs
                    // Hoặc tạo mới như mã tham khảo
                    using var httpClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(5) };
                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                    };

                    _logger.LogInformation("Attempt {Attempt}/{MaxRetries}: Sending request to {Url}", attempt, settings.MaxRetries, url);
                    using HttpResponseMessage response = await httpClient.SendAsync(request, token);
                    string responseBody = await response.Content.ReadAsStringAsync(token);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("HTTP Error {StatusCode} from {Url}. Retrying in {Delay}ms...", response.StatusCode, url, settings.RetryDelayMs * attempt);
                        if (attempt < settings.MaxRetries)
                        {
                            // 3. ÁP DỤNG EXPONENTIAL BACKOFF
                            await Task.Delay(settings.RetryDelayMs * attempt, token);
                            continue; // Chuyển sang lần thử tiếp theo
                        }
                        return ($"Lỗi API: {response.StatusCode}", 0);
                    }

                    JObject parsedBody = JObject.Parse(responseBody);
                    if (parsedBody?["error"] != null) return ($"Lỗi API: {parsedBody["error"]?["message"]}", 0);
                    if (parsedBody?["promptFeedback"]?["blockReason"] != null) return ($"Lỗi: Nội dung bị chặn. Lý do: {parsedBody["promptFeedback"]["blockReason"]}", 0);

                    int tokens = parsedBody?["usageMetadata"]?["totalTokenCount"]?.Value<int>() ?? 0;
                    string responseText = parsedBody?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();

                    if (responseText == null)
                    {
                        // Xử lý trường hợp Gemini trả về 200 OK nhưng không có nội dung
                        _logger.LogWarning("API returned OK but content is empty. Retrying...");
                        if (attempt < settings.MaxRetries)
                        {
                            await Task.Delay(settings.RetryDelayMs * attempt, token);
                            continue;
                        }
                        return ("Lỗi: API trả về phản hồi rỗng.", 0);
                    }

                    return (responseText, tokens);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception during API call. Retrying in {Delay}ms...", settings.RetryDelayMs * attempt);
                    if (attempt >= settings.MaxRetries) return ($"Lỗi Exception: {ex.Message}", 0);
                    await Task.Delay(settings.RetryDelayMs * attempt, token);
                }
            }
            return ("Lỗi API: Hết số lần thử lại.", 0);
        }

        // === CÁC PHƯƠNG THỨC HỖ TRỢ KHÁC ===
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

                // Tìm key API tương ứng trong database
                var apiKey = await context.ManagedApiKeys.FindAsync(apiKeyId);
                if (apiKey == null)
                {
                    _logger.LogWarning("Không thể cập nhật sử dụng: Không tìm thấy API Key ID {ApiKeyId}", apiKeyId);
                    return;
                }

                // === LOGIC RESET HÀNG NGÀY THEO GIỜ VIỆT NAM ===
                var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
                var lastResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(apiKey.LastRequestCountResetUtc, vietnamTimeZone);

                // Nếu ngày reset cuối cùng nhỏ hơn ngày hiện tại (theo giờ VN), reset bộ đếm
                if (lastResetInVietnam.Date < vietnamNow.Date)
                {
                    _logger.LogInformation("Resetting daily request count for API Key ID {ApiKeyId}", apiKeyId);
                    apiKey.RequestsToday = 0;
                    // Ghi lại thời điểm reset là đầu ngày UTC hôm nay
                    apiKey.LastRequestCountResetUtc = DateTime.UtcNow.Date;
                }

                // === CẬP NHẬT CÁC BỘ ĐẾM ===
                apiKey.RequestsToday++; // Mỗi lần gọi là một request

                if (tokensUsed > 0)
                {
                    apiKey.TotalTokensUsed += tokensUsed;
                }

                // Lưu các thay đổi vào database
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật sử dụng cho API Key ID {ApiKeyId}", apiKeyId);
            }
        }
        private string GetSystemInstructionForGeminiSrtTranslation(string selectedGenre, string targetLanguage)
        {
            // (Giữ nguyên phương thức này từ mã nguồn cũ của bạn vì nó đã chi tiết và tốt)
            string baseFormattingAndCoreRules = $@"QUY TẮC CHUNG:
1. Dịch toàn bộ sang {targetLanguage}, KHÔNG bỏ dòng nào.
2. Kết quả là văn bản thuần túy, KHÔNG thêm lời dẫn, chú thích, markdown, in đậm/in nghiêng.
3. Tên riêng (nhân vật, địa danh, pháp bảo...) giữ nguyên Hán-Việt, KHÔNG dịch nghĩa từng chữ. Đảm bảo nhất quán.
4. Mỗi dòng phải giữ nguyên số thứ tự. Ví dụ: 123: Nội dung dịch. Nếu không có gì để dịch, trả về: 123:
5. KHÔNG tự ý thêm dấu chấm, dấu phẩy ở đầu/cuối câu.";

            string genreSpecificConstraints = "";

            if (selectedGenre == "H.Huyễn Tiên Hiệp")
            {
                genreSpecificConstraints = $@"QUY TẮC CHO PHIM THỂ LOẠI TU TIÊN / HUYỀN HUYỄN:
- Văn phong vẫn dễ hiểu, phù hợp với đối thoại trong phim tu tiên - tiên hiệp.
- Lời thoại phải mượt mà, ngắn gọn, không rườm rà.
- Dùng đúng các đại từ cổ phù hợp hoàn cảnh như ví dụ: ngươi (汝), ta (我吾), hắn (他), nàng (她), y (他她伊), sư tỷ (师姐), sư tôn (师尊), sư đệ (师弟), sư muội (师妹), chàng (郎君他), muội muội (妹妹), tỷ tỷ (姐姐), huynh đệ (兄弟), đại ca (大哥), sư tổ (师祖), tiên tử (仙子), thánh nữ (圣女), ma nữ (魔女), tiểu yêu (小妖), đệ (弟), đệ tử (弟子) huynh (兄), tỷ (姐), thúc thúc (叔叔), tẩu tẩu (嫂嫂嫂子), bá bá (伯伯), lão gia (老爷), nha đầu (丫头), tiểu nha đầu (小丫头), đệ đệ (弟弟), thiếp (妾), tiểu thư (小姐), công tử (公子), các vị (各位), bản tọa (本座), lão phu (老夫), tại hạ (在下), đạo hữu (道友), tiền bối (前辈), vãn bối (晚辈), tiểu bối (小辈), tiểu tử (小子), tiểu nha đầu (小丫头), cô nương (姑娘), nô tỳ (奴婢), không tự tiện sửa thành đại từ khác nếu như không rõ ràng trong ngữ cảnh.
- TRÁNH TUYỆT ĐỐI các đại từ hiện đại như: tôi, mày, tao, bạn, ông, bà, cha mẹ...
- Ưu tiên dịch sát nghĩa, không được cường điệu hay “văn vẻ hóa” lời thoại.
";
            }
            else if (selectedGenre == "Ngôn Tình")
            {
                genreSpecificConstraints = @"QUY TẮC CHO PHIM NGÔN TÌNH:
- Giữ nguyên tên riêng Hán-Việt, không dịch nghĩa từng chữ.
- Văn phong hiện đại, lời thoại tự nhiên, biểu cảm phù hợp ngữ cảnh.
- Sử dụng đại từ hiện đại như: tôi, anh, em, cậu, bạn… tùy mối quan hệ.";
            }
            else if (selectedGenre == "Khoa học lịch sử")
            {
                genreSpecificConstraints = @"QUY TẮC CHO PHIM KHOA HỌC / LỊCH SỬ:
- Dịch chính xác và nhất quán các thuật ngữ khoa học, kỹ thuật, tên riêng và các sự kiện lịch sử.
- Văn phong trang trọng, rõ ràng, phù hợp với bối cảnh học thuật hoặc lịch sử.
- Tránh sử dụng ngôn ngữ quá đời thường hoặc tiếng lóng.";
            }
            else // Đô Thị Hiện Đại
            {
                genreSpecificConstraints = @"QUY TẮC CHO PHIM ĐÔ THỊ / HIỆN ĐẠI:
- Tên riêng giữ nguyên Hán-Việt. KHÔNG dịch nghĩa từng chữ.
- Sử dụng đại từ hiện đại tự nhiên như: tôi, anh, em, chị, bạn, v.v.
- Lời thoại mạch lạc, gần gũi, dễ hiểu.";
            }

            return !string.IsNullOrWhiteSpace(genreSpecificConstraints)
                ? $"{baseFormattingAndCoreRules}\n\n{genreSpecificConstraints}"
                : baseFormattingAndCoreRules;
        }
    }
}