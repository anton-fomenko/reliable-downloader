namespace ReliableDownloader
{
    /// <summary>
    /// Handles reporting download progress to the console, reducing noise by only reporting whole percentage changes.
    /// </summary>
    internal sealed class ConsoleProgressReporter
    {
        private int _lastReportedPercentage = -1; // Initial state
        private readonly Action<string> _outputWriter;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConsoleProgressReporter"/> class.
        /// Writes output using Console.WriteLine by default.
        /// </summary>
        public ConsoleProgressReporter() : this(Console.WriteLine)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConsoleProgressReporter"/> class with a specific output writer.
        /// Used primarily for testing.
        /// </summary>
        /// <param name="outputWriter">The action to call for writing output lines.</param>
        /// <exception cref="ArgumentNullException">Thrown if outputWriter is null.</exception>
        internal ConsoleProgressReporter(Action<string> outputWriter)
        {
            _outputWriter = outputWriter ?? throw new ArgumentNullException(nameof(outputWriter));
        }

        /// <summary>
        /// Handles a progress update, writing to the configured output writer if necessary.
        /// </summary>
        /// <param name="progress">The current file progress.</param>
        public void HandleProgress(FileProgress progress)
        {
            // Check if progress percentage is available and valid
            if (progress.ProgressPercent.HasValue && progress.ProgressPercent.Value >= 0)
            {
                int currentPercentage = (int)Math.Floor(progress.ProgressPercent.Value);

                // Only report if it's a new whole percentage point or exactly 100%
                if (currentPercentage > _lastReportedPercentage || (currentPercentage == 100 && _lastReportedPercentage != 100))
                {
                    string timeString = progress.EstimatedRemaining.HasValue && progress.EstimatedRemaining.Value > TimeSpan.Zero
                        ? progress.EstimatedRemaining.Value.ToString(@"hh\:mm\:ss")
                        : "Calculating...";

                    if (currentPercentage >= 100)
                    {
                        _outputWriter($"[PROGRESS] 100% complete. Download finished.");
                    }
                    else
                    {
                        _outputWriter($"[PROGRESS] {currentPercentage}% complete. Estimated time remaining: {timeString}");
                    }
                    _lastReportedPercentage = currentPercentage;
                }
            }
            // Handle case where percentage might not be calculated yet (report initial state once)
            else if (_lastReportedPercentage == -1)
            {
                _outputWriter($"[PROGRESS] Starting download (Size: {progress.TotalFileSize ?? 0} bytes)...");
                _lastReportedPercentage = 0; // Mark initial message as sent
            }
        }
    }
}