using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubPhim.Server.Services;
using System.Security.Claims;

namespace SubPhim.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class VipTranslationController : ControllerBase
    {
        private readonly IVipTranslationService _vipTranslationService;
        private readonly ILogger<VipTranslationController> _logger;

        public VipTranslationController(IVipTranslationService vipTranslationService, ILogger<VipTranslationController> logger)
        {
            _vipTranslationService = vipTranslationService;
            _logger = logger;
        }

        [HttpPost("start")]
        public async Task<IActionResult> StartTranslation([FromBody] VipTranslationRequest request)
        {
            var userIdString = User.FindFirstValue("id");
            if (!int.TryParse(userIdString, out int userId))
            {
                return Unauthorized("Token không hợp lệ.");
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Content))
            {
                return BadRequest("Nội dung dịch không được để trống.");
            }

            if (string.IsNullOrWhiteSpace(request.SystemInstruction))
            {
                return BadRequest("System Instruction không được để trống.");
            }

            _logger.LogInformation("Received VIP translation request from User ID {UserId}", userId);

            var result = await _vipTranslationService.CreateJobAsync(userId, request);

            if (result.IsSuccess)
            {
                return Accepted(new { result.SessionId, result.Message });
            }
            else
            {
                // Return appropriate status code based on error type
                if (result.Message.Contains("Vượt quá giới hạn") || result.Message.Contains("quota"))
                {
                    return StatusCode(429, new { result.Message });
                }
                return BadRequest(new { result.Message });
            }
        }

        [HttpGet("result/{sessionId}")]
        public async Task<IActionResult> GetResult(string sessionId)
        {
            var userIdString = User.FindFirstValue("id");
            if (!int.TryParse(userIdString, out int userId))
            {
                return Unauthorized("Token không hợp lệ.");
            }

            if (string.IsNullOrEmpty(sessionId))
            {
                return BadRequest("Session ID không được để trống.");
            }

            var result = await _vipTranslationService.GetJobResultAsync(sessionId, userId);

            return Ok(result);
        }
    }
}
