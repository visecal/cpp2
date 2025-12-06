using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace subphimv1.Services
{
    /// <summary>
    /// Provides a rate limiter based on the sliding window algorithm.
    /// </summary>
    public class SlidingRateLimiter : IDisposable
    {
        private readonly int _maxRequests;
        private readonly TimeSpan _timeWindow;
        private readonly Queue<DateTime> _requestTimestamps;
        private readonly SemaphoreSlim _semaphore;

        public SlidingRateLimiter(int maxRequests, TimeSpan timeWindow)
        {
            if (maxRequests <= 0) throw new ArgumentOutOfRangeException(nameof(maxRequests), "Max requests must be positive.");
            if (timeWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeWindow), "Time window must be positive.");

            _maxRequests = maxRequests;
            _timeWindow = timeWindow;
            _requestTimestamps = new Queue<DateTime>(maxRequests);
            _semaphore = new SemaphoreSlim(1, 1);
        }

        public async Task WaitAsync(CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var now = DateTime.UtcNow;

                    // Remove old timestamps
                    while (_requestTimestamps.Count > 0 && (now - _requestTimestamps.Peek()) > _timeWindow)
                    {
                        _requestTimestamps.Dequeue();
                    }

                    if (_requestTimestamps.Count < _maxRequests)
                    {
                        _requestTimestamps.Enqueue(now);
                        return; // Allowed to proceed
                    }

                    // Wait until the oldest request timestamp is outside the window
                    DateTime oldestTimestamp = _requestTimestamps.Peek();
                    TimeSpan waitTime = _timeWindow - (now - oldestTimestamp) + TimeSpan.FromMilliseconds(10); // Add a small buffer

                    if (waitTime > TimeSpan.Zero)
                    {
                        await Task.Delay(waitTime, cancellationToken);
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            _semaphore?.Dispose();
        }
    }
}