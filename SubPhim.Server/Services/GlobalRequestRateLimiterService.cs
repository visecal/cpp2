using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using System.Collections.Concurrent;

namespace SubPhim.Server.Services
{
    /// <summary>
    /// DEPRECATED: Global rate limiting has been removed per requirements.
    /// This service now acts as a pass-through (no-op) for backward compatibility.
    /// Rate limiting is now handled separately for Pro and Flash models.
    /// </summary>
    public class GlobalRequestRateLimiterService
    {
        private readonly ILogger<GlobalRequestRateLimiterService> _logger;

        public GlobalRequestRateLimiterService(
            ILogger<GlobalRequestRateLimiterService> logger)
        {
            _logger = logger;
            _logger.LogInformation("GlobalRequestRateLimiter initialized as NO-OP (global rate limiting disabled)");
        }

        /// <summary>
        /// DEPRECATED: Always returns immediately without blocking.
        /// </summary>
        public Task RefreshSettingsAsync(bool forceRefresh = false)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// DEPRECATED: Always returns success without blocking.
        /// </summary>
        public Task<string> AcquireSlotAsync(string jobId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(jobId);
        }

        /// <summary>
        /// DEPRECATED: No-op method for backward compatibility.
        /// </summary>
        public void ReleaseSlot(string requestId)
        {
            // No-op
        }

        /// <summary>
        /// DEPRECATED: Always returns true.
        /// </summary>
        public bool HasAvailableSlot()
        {
            return true;
        }

        /// <summary>
        /// DEPRECATED: Always returns maximum value.
        /// </summary>
        public int GetAvailableSlots()
        {
            return int.MaxValue;
        }

        /// <summary>
        /// DEPRECATED: Returns dummy status.
        /// </summary>
        public (int maxRequests, int windowMinutes, int availableSlots, int activeRequests) GetCurrentStatus()
        {
            return (int.MaxValue, 0, int.MaxValue, 0);
        }
    }
}
