using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using SubPhim.Server.Models;
using System.Text.RegularExpressions;

namespace SubPhim.Server.Services
{
    public class VipTranslationService : IVipTranslationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<VipTranslationService> _logger;

        public VipTranslationService(IServiceProvider serviceProvider, ILogger<VipTranslationService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task<VipJobResult> CreateJobAsync(int userId, VipTranslationRequest request)
        {
            _logger.LogInformation("VIP Translation job creation request for User ID {UserId}", userId);

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var user = await context.Users.FindAsync(userId);
            if (user == null)
            {
                return new VipJobResult { IsSuccess = false, Message = "User not found" };
            }

            // Reset quota nếu cần (theo giờ Việt Nam)
            var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            var vietnamNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
            var lastResetInVietnam = TimeZoneInfo.ConvertTimeFromUtc(user.LastVipTranslationResetUtc, vietnamTimeZone);
            
            if (lastResetInVietnam.Date < vietnamNow.Date)
            {
                user.VipTranslationLinesUsedToday = 0;
                user.LastVipTranslationResetUtc = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }

            // Xác định limit (override hoặc từ tier)
            int dailyLimit = user.DailyVipTranslationLimitOverride >= 0 
                ? user.DailyVipTranslationLimitOverride 
                : user.DailyVipTranslationLimit;

            // Parse SRT content
            var srtLines = ParseSrtContent(request.Content);
            if (srtLines == null || !srtLines.Any())
            {
                return new VipJobResult { IsSuccess = false, Message = "Invalid SRT content" };
            }

            // Kiểm tra nếu có dòng > 3000 ký tự thì từ chối
            var longLine = srtLines.FirstOrDefault(line => line.OriginalText.Length > 3000);
            if (longLine != null)
            {
                return new VipJobResult 
                { 
                    IsSuccess = false, 
                    Message = $"Dòng {longLine.Index} vượt quá 3000 ký tự ({longLine.OriginalText.Length} ký tự). Vui lòng chia nhỏ file SRT." 
                };
            }

            int requestedLines = srtLines.Count;
            int remainingLines = dailyLimit - user.VipTranslationLinesUsedToday;

            if (requestedLines > remainingLines)
            {
                return new VipJobResult 
                { 
                    IsSuccess = false, 
                    Message = $"Vượt quá giới hạn. Còn lại: {remainingLines}/{dailyLimit} dòng/ngày" 
                };
            }

            // Tạo job
            var sessionId = Guid.NewGuid().ToString();
            var job = new VipTranslationJob
            {
                SessionId = sessionId,
                UserId = userId,
                SystemInstruction = request.SystemInstruction,
                Status = JobStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                TotalLines = requestedLines,
                ProcessedLines = 0
            };

            context.VipTranslationJobs.Add(job);

            // Add original lines
            int lineIndex = 1;
            foreach (var line in srtLines)
            {
                context.VipOriginalSrtLines.Add(new VipOriginalSrtLine
                {
                    SessionId = sessionId,
                    LineNumber = lineIndex++,
                    TimeCode = "", // SrtLine doesn't have TimeCode in this model
                    OriginalText = line.OriginalText
                });
            }

            // Update user usage
            user.VipTranslationLinesUsedToday += requestedLines;
            await context.SaveChangesAsync();

            // Start background processing
            _ = Task.Run(() => ProcessJobAsync(sessionId));

            return new VipJobResult 
            { 
                IsSuccess = true, 
                SessionId = sessionId, 
                Message = "Job created successfully" 
            };
        }

        public async Task<VipResultResponse> GetJobResultAsync(string sessionId, int userId)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var job = await context.VipTranslationJobs
                .FirstOrDefaultAsync(j => j.SessionId == sessionId && j.UserId == userId);

            if (job == null)
            {
                return new VipResultResponse 
                { 
                    Status = "NotFound", 
                    IsCompleted = true, 
                    ErrorMessage = "Job not found" 
                };
            }

            var translatedLines = await context.VipTranslatedSrtLines
                .Where(l => l.SessionId == sessionId)
                .OrderBy(l => l.LineNumber)
                .Select(l => new VipTranslatedLine
                {
                    LineNumber = l.LineNumber,
                    TimeCode = l.TimeCode,
                    TranslatedText = l.TranslatedText
                })
                .ToListAsync();

            return new VipResultResponse
            {
                Status = job.Status.ToString(),
                NewLines = translatedLines,
                IsCompleted = job.Status == JobStatus.Completed || job.Status == JobStatus.Failed,
                ErrorMessage = job.ErrorMessage
            };
        }

        private List<SrtLine> ParseSrtContent(string content)
        {
            var lines = new List<SrtLine>();
            var blocks = content.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

            int index = 1;
            foreach (var block in blocks)
            {
                var parts = block.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
                if (parts.Length < 3) continue;

                // Skip the line number
                // Skip the timecode
                var text = string.Join(" ", parts.Skip(2).Where(p => !string.IsNullOrWhiteSpace(p)));

                lines.Add(new SrtLine { Index = index++, OriginalText = text });
            }

            return lines;
        }

        private async Task ProcessJobAsync(string sessionId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var job = await context.VipTranslationJobs
                    .Include(j => j.OriginalLines)
                    .FirstOrDefaultAsync(j => j.SessionId == sessionId);

                if (job == null) return;

                job.Status = JobStatus.Processing;
                await context.SaveChangesAsync();

                // TODO: Implement actual translation logic using Gemini API
                // For now, just mark as completed
                _logger.LogWarning("VIP Translation processing not fully implemented yet for session {SessionId}", sessionId);

                // Simulate processing
                await Task.Delay(1000);

                job.Status = JobStatus.Completed;
                job.CompletedAt = DateTime.UtcNow;
                job.ProcessedLines = job.TotalLines;
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing VIP translation job {SessionId}", sessionId);

                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var job = await context.VipTranslationJobs.FindAsync(sessionId);
                if (job != null)
                {
                    job.Status = JobStatus.Failed;
                    job.ErrorMessage = ex.Message;
                    await context.SaveChangesAsync();
                }
            }
        }
    }
}
