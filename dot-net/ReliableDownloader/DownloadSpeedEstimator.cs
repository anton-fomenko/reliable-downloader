using System.Diagnostics;

namespace ReliableDownloader
{
    /// <summary>
    /// Estimates download speed and remaining time.
    /// </summary>
    internal class DownloadSpeedEstimator
    {
        private readonly Stopwatch _stopwatch = new();
        private long _startTimestampBytes = 0;
        private long _totalFileSize = 0;
        private long _totalBytesDownloaded = 0;

        /// <summary>
        /// Starts or restarts the estimation timer.
        /// </summary>
        /// <param name="totalFileSize">The total expected size of the file in bytes.</param>
        /// <param name="initialBytesDownloaded">The number of bytes already downloaded when starting.</param>
        public void Start(long totalFileSize, long initialBytesDownloaded)
        {
            _totalFileSize = totalFileSize;
            _totalBytesDownloaded = initialBytesDownloaded;
            _startTimestampBytes = initialBytesDownloaded;
            _stopwatch.Restart();
        }

        /// <summary>
        /// Updates the total number of bytes downloaded so far.
        /// </summary>
        /// <param name="totalBytesDownloaded">The current total bytes downloaded.</param>
        public void UpdateBytesDownloaded(long totalBytesDownloaded)
        {
            _totalBytesDownloaded = totalBytesDownloaded;
        }

        /// <summary>
        /// Stops the estimation timer.
        /// </summary>
        public void Stop()
        {
            _stopwatch.Stop();
        }

        /// <summary>
        /// Estimates the remaining download time based on progress since Start was called.
        /// </summary>
        /// <returns>An estimated TimeSpan, TimeSpan.Zero if complete, or null if estimation is not reliable yet.</returns>
        public TimeSpan? EstimateRemainingTime()
        {
            if (!_stopwatch.IsRunning || _totalFileSize <= 0) return null;

            double elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
            if (elapsedSeconds < 0.5) return null; // Avoid estimates based on very short intervals

            long bytesDownloadedSinceStart = _totalBytesDownloaded - _startTimestampBytes;
            if (bytesDownloadedSinceStart <= 0) return null; // Cannot estimate speed if no progress

            double bytesPerSecond = bytesDownloadedSinceStart / elapsedSeconds;
            if (bytesPerSecond < 1) return null; // Effectively stalled or too slow

            long remainingBytes = _totalFileSize - _totalBytesDownloaded;
            if (remainingBytes <= 0) return TimeSpan.Zero; // Complete

            try
            {
                double secondsRemaining = remainingBytes / bytesPerSecond;

                // Cap estimate to avoid excessively large values due to low speed
                if (secondsRemaining > TimeSpan.MaxValue.TotalSeconds)
                {
                    return TimeSpan.MaxValue; // Effectively infinite
                }
                if (secondsRemaining < 0)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromSeconds(secondsRemaining);
            }
            catch (OverflowException)
            {
                return TimeSpan.MaxValue; // Handle potential overflow
            }
        }
    }
}