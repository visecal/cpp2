using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using SubPhim.Server.Services;
using System.Security.Claims;

namespace SubPhim.Server.Controllers
{
    [ApiController]
    [Route("api/aiolauncher-tts")]
    [Authorize]
    public class AioLauncherTtsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly AioTtsDispatcherService _dispatcher;
        private readonly ILogger<AioLauncherTtsController> _logger;
        private readonly IWebHostEnvironment _env;

        public AioLauncherTtsController(AppDbContext context, AioTtsDispatcherService dispatcher, ILogger<AioLauncherTtsController> logger, IWebHostEnvironment env)
        {
            _context = context;
            _dispatcher = dispatcher;
            _logger = logger;
            _env = env;
        }

        public record TtsApiRequest(
            string Language,
            string VoiceId,
            double Rate,
            string Text
        );

        [HttpPost("generate")]
        public async Task<IActionResult> Generate([FromBody] TtsApiRequest request)
        {
            if (!int.TryParse(User.FindFirstValue("id"), out int userId))
            {
                return Unauthorized();
            }

            var characterCount = request.Text?.Length ?? 0;
            if (characterCount == 0)
            {
                return BadRequest(new { message = "Nội dung văn bản không được để trống." });
            }

            // BỎ GIỚI HẠN 4900 BYTE Ở ĐÂY ĐỂ SERVER TỰ CHIA BATCH TRONG Dispatcher
            // const int MaxByteLimit = 4900;
            // var byteCount = System.Text.Encoding.UTF8.GetByteCount(request.Text);
            // if (byteCount > MaxByteLimit)
            // {
            //     return StatusCode(413, new { message = $"Nội dung văn bản quá lớn ({byteCount} bytes). Vui lòng chia nhỏ yêu cầu dưới {MaxByteLimit} bytes." });
            // }

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return NotFound(new { message = "Tài khoản không tồn tại." });
            }

            long remainingChars = user.TtsCharacterLimit - user.TtsCharactersUsed;
            if (characterCount > remainingChars)
            {
                return StatusCode(429, new { message = $"Không đủ ký tự TTS. Yêu cầu: {characterCount}, còn lại: {remainingChars}." });
            }

            // Trừ quota theo tổng ký tự đầu vào. Dispatcher sẽ reserve lại theo SA tương ứng.
            user.TtsCharactersUsed += characterCount;
            await _context.SaveChangesAsync();

            var result = await _dispatcher.SynthesizeAsync(request.Language, request.VoiceId, request.Rate, request.Text);

            if (result.IsSuccess)
            {
                return File(result.AudioContent, "audio/mpeg");
            }
            else
            {
                // Hoàn lại quota cho user nếu thất bại
                user.TtsCharactersUsed -= characterCount;
                if (user.TtsCharactersUsed < 0) user.TtsCharactersUsed = 0;
                await _context.SaveChangesAsync();

                _logger.LogWarning("Tạo AIOLauncher TTS cho user {UserId} thất bại: {Error}", userId, result.ErrorMessage);
                return StatusCode(503, new { message = result.ErrorMessage });
            }
        }

        [HttpPost("batch/upload"), DisableRequestSizeLimit] // Cho phép upload file lớn
        public async Task<IActionResult> UploadSrtBatch([FromForm] IFormFile srtFile, [FromForm] string language, [FromForm] string voiceId, [FromForm] double rate, [FromForm] string audioFormat)
        {
            const long maxFileSize = 50 * 1024 * 1024;

            if (srtFile == null || srtFile.Length == 0)
                return BadRequest(new { message = "Chưa cung cấp file SRT." });
            if (srtFile.Length > maxFileSize)
            {
                return StatusCode(413, new { message = $"Kích thước file quá lớn. Vui lòng upload file SRT dưới {maxFileSize / 1024 / 1024}MB." });
            }

            if (!int.TryParse(User.FindFirstValue("id"), out int userId)) return Unauthorized();
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound(new { message = "Tài khoản không tồn tại." });

            var tempUploadDir = Path.Combine(_env.ContentRootPath, "TempUploads");
            Directory.CreateDirectory(tempUploadDir);
            var originalFilePath = Path.Combine(tempUploadDir, $"{Guid.NewGuid()}_{srtFile.FileName}");

            await using (var stream = new FileStream(originalFilePath, FileMode.Create))
            {
                await srtFile.CopyToAsync(stream);
            }

            var newJob = new AioTtsBatchJob
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Status = AioTtsJobStatus.Pending,
                Language = language,
                VoiceId = voiceId,
                Rate = rate,
                AudioFormat = audioFormat,
                OriginalSrtFilePath = originalFilePath
            };

            _context.AioTtsBatchJobs.Add(newJob);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Đã tạo TTS Batch Job {JobId} cho User {UserId}.", newJob.Id, userId);

            return Ok(new { jobId = newJob.Id });
        }

        [HttpGet("batch/status/{jobId}")]
        public async Task<IActionResult> GetBatchStatus(Guid jobId)
        {
            if (!int.TryParse(User.FindFirstValue("id"), out int userId)) return Unauthorized();

            var job = await _context.AioTtsBatchJobs.AsNoTracking()
                                    .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId);

            if (job == null) return NotFound(new { message = "Không tìm thấy tác vụ hoặc bạn không có quyền truy cập." });

            return Ok(new
            {
                jobId = job.Id,
                status = job.Status.ToString(),
                totalLines = job.TotalLines,
                processedLines = job.ProcessedLines,
                errorMessage = job.ErrorMessage,
                createdAt = job.CreatedAt,
                completedAt = job.CompletedAt
            });
        }

        [HttpGet("batch/download/{jobId}")]
        public async Task<IActionResult> DownloadBatchResult(Guid jobId)
        {
            if (!int.TryParse(User.FindFirstValue("id"), out int userId)) return Unauthorized();

            var job = await _context.AioTtsBatchJobs.AsNoTracking()
                                .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId);

            if (job == null) return NotFound(new { message = "Không tìm thấy tác vụ hoặc bạn không có quyền truy cập." });

            if (job.Status != AioTtsJobStatus.Completed || string.IsNullOrEmpty(job.ResultZipFilePath) || !System.IO.File.Exists(job.ResultZipFilePath))
            {
                return BadRequest(new { message = "Tác vụ chưa hoàn thành hoặc file kết quả không tồn tại." });
            }

            var filePath = job.ResultZipFilePath;
            var fileName = $"AioTtsResult_{job.Id}.zip";
            var fileInfo = new FileInfo(filePath);

            // Cho phép resume và stream trực tiếp từ đĩa, tránh dồn RAM
            Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
            Response.Headers["Content-Length"] = fileInfo.Length.ToString();

            // Đăng ký xóa file sau khi response hoàn tất (thành công hoặc không)
            HttpContext.Response.OnCompleted(() =>
            {
                try { System.IO.File.Delete(filePath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Không thể xóa file zip tạm thời: {FilePath}", filePath); }
                return Task.CompletedTask;
            });

            return PhysicalFile(filePath, "application/zip", enableRangeProcessing: true);
        }

    }
}