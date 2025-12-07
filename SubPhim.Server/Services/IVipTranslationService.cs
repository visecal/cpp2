using SubPhim.Server.Models;

namespace SubPhim.Server.Services
{
    public interface IVipTranslationService
    {
        Task<VipJobResult> CreateJobAsync(int userId, VipTranslationRequest request);
        Task<VipResultResponse> GetJobResultAsync(string sessionId, int userId);
    }

    public class VipTranslationRequest
    {
        public string Content { get; set; }
        public string SystemInstruction { get; set; }
    }

    public class VipJobResult
    {
        public bool IsSuccess { get; set; }
        public string SessionId { get; set; }
        public string Message { get; set; }
    }

    public class VipResultResponse
    {
        public string Status { get; set; }
        public List<VipTranslatedLine> NewLines { get; set; }
        public bool IsCompleted { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class VipTranslatedLine
    {
        public int LineNumber { get; set; }
        public string TimeCode { get; set; }
        public string TranslatedText { get; set; }
    }
}
