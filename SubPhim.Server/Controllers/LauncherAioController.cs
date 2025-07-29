using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubPhim.Server.Models;
using SubPhim.Server.Services;
using System.Security.Claims;

[ApiController]
[Route("api/launcheraio")]
[Authorize]
public class LauncherAioController : ControllerBase
{
    private readonly TranslationOrchestratorService _orchestrator;
    private readonly ILogger<LauncherAioController> _logger;

    public LauncherAioController(TranslationOrchestratorService orchestrator, ILogger<LauncherAioController> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public record StartTranslationRequest(string Genre, string TargetLanguage, List<SrtLine> Lines, bool AcceptPartial = false);
    public class StartTranslationResponse
    {
        // Status: "Accepted", "PartialContent", "Error"
        public string Status { get; set; }
        public string Message { get; set; }
        public string SessionId { get; set; }
        public int RemainingLines { get; set; }
    }
    public record GetResultsResponse(List<TranslatedSrtLine> NewLines, bool IsCompleted, string ErrorMessage);

    [HttpPost("start-translation")]
public async Task<IActionResult> StartTranslation([FromBody] StartTranslationRequest request)
{
    try
    {
        if (!int.TryParse(User.FindFirstValue("id"), out int userId)) return Unauthorized();

        _logger.LogInformation("Received translation job from User ID {UserId} with {LineCount} lines. AcceptPartial={AcceptPartial}", 
            userId, request.Lines.Count, request.AcceptPartial);

        var result = await _orchestrator.CreateJobAsync(userId, request.Genre, request.TargetLanguage, request.Lines, request.AcceptPartial);

        // Dựa vào kết quả từ service để trả về các mã trạng thái khác nhau
        switch (result.Status)
        {
            case "Accepted":
                // 200 OK: Job được chấp nhận hoàn toàn
                return Ok(new StartTranslationResponse { Status = result.Status, SessionId = result.SessionId });
            
            case "PartialContent":
                // 202 Accepted: Job chưa được tạo, server đang chờ xác nhận từ client
                return StatusCode(202, new StartTranslationResponse { Status = result.Status, Message = result.Message, RemainingLines = result.RemainingLines });

            default: // "Error"
                // 429 Too Many Requests: Hết sạch lượt, không thể dịch
                return StatusCode(429, new StartTranslationResponse { Status = result.Status, Message = result.Message });
        }
    }
    catch (Exception ex)
    {
        _logger.LogCritical(ex, "!!!!!!!! CRITICAL ERROR in StartTranslation endpoint. !!!!!!!");
        return StatusCode(500, new StartTranslationResponse { Status = "Error", Message = $"Lỗi nghiêm trọng khi tạo job: {ex.Message}" });
    }
}

    [HttpGet("get-results/{sessionId}")]
    public async Task<IActionResult> GetResults(string sessionId)
    {
        var newLines = await _orchestrator.GetJobResultsAsync(sessionId);
        var (isCompleted, errorMessage) = await _orchestrator.GetJobStatusAsync(sessionId);

        if (newLines == null && isCompleted)
        {
            return NotFound("Session không hợp lệ hoặc đã hết hạn.");
        }

        return Ok(new GetResultsResponse(newLines, isCompleted, errorMessage));
    }
}