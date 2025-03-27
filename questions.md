# Questions

## How did you approach solving the problem?

I focused on the .NET implementation, creating a `FileDownloader` class responsible for handling the download logic. The core approach involved:
* **Abstraction:** Using interfaces (`IFileDownloader`, `IWebSystemCalls`) for network operations and the downloader itself to allow for mocking and testing.
* **Resilience:** Implementing a retry mechanism (`ExecuteWithRetryAsync`) with exponential backoff for handling transient network errors and disconnections. The retry attempts and delays were made configurable to handle longer outages, based on the requirement for resilience to disconnections of "any length".
* **Partial Downloads:** Checking the `Accept-Ranges: bytes` header to determine server support. If supported, the download proceeds in chunks, and importantly, it checks for existing partially downloaded files and resumes from the correct byte offset.
* **Full Downloads:** Implementing a fallback method to download the entire file in one go if partial downloads are not supported or if a full restart is deemed necessary (e.g., existing file size mismatch).
* **Integrity:** Retrieving the `Content-MD5` hash from the headers and verifying the downloaded file against it using `VerifyIntegrityAsync`. The file is deleted if the check fails, as per requirements.
* **Progress:** Implementing a callback mechanism (`Action<FileProgress>`) to report the total file size, bytes downloaded, percentage completion, and estimated time remaining. Modifications were considered to make reporting less verbose.
* **Cancellation:** Accepting and propagating a `CancellationToken` throughout the asynchronous operations to allow the download to be cancelled gracefully.
* **Asynchronous Operations:** Utilizing `async`/`await` extensively for non-blocking I/O operations.

## How did you verify your solution works correctly?

Verification involved multiple layers:
* **Unit Testing:** Extensive unit tests were written using NUnit and Moq (`Tests.cs`). These tests cover:
    * Header fetching success and failures (including retries).
    * Detection of partial vs. full download support.
    * Successful full and partial downloads.
    * Resuming partial downloads correctly.
    * Handling of network errors and timeouts during downloads (both full and partial chunks), verifying retry logic.
    * MD5 integrity check success and failure (including file deletion on mismatch).
    * Cancellation during various stages of the download (headers, full, partial).
    * Progress reporting calculations.
* **Manual Testing (Simulated Disconnections):** As suggested in the README, I manually tested the compiled application by:
    * Using a network throttling tool (like NetLimiter) to slow down the download, providing time to intervene.
    * Manually disabling/enabling the network interface (Wi-Fi/Ethernet) to simulate disconnections, specifically testing recovery after ~2 second and ~2 minute outages (after adjusting retry parameters to last > 2 minutes).
    * Observing the console output to confirm that the application retried, didn't terminate, correctly reported errors (like DNS failures during disconnection), resumed progress upon reconnection, and completed successfully.
* **Code Review:** Analyzing the code for logical flow, error handling, and adherence to requirements.

## How long did you spend on the exercise?

3-4 hours

## What would you add if you had more time and how?

* **Refine Retry Strategy:** While the current finite retry strategy covers long durations (e.g., ~10 minutes based on 15 retries / 60s max delay), I would further investigate the "any length" requirement. This could involve implementing truly "endless" retries (changing the retry loop condition in `ExecuteWithRetryAsync` to `while(true)` or similar) while keeping the capped maximum delay and relying solely on the `CancellationToken` for termination. This would require careful testing around cancellation edge cases. Making retry parameters (attempts, delays) more easily configurable via the constructor or external settings would also be beneficial.
* **Improve Logging:** Replace `Console.WriteLine` with a structured logging framework (like Serilog or NLog). This would allow adding timestamps and log levels (Info, Warn, Error) easily, writing logs to files, and controlling verbosity more effectively (e.g., reducing noise from progress updates by logging them only on significant changes or at Trace/Debug level).
* **Refactor `FileDownloader.cs`:** Break down the main `TryDownloadFile` method and the download helper methods (`PerformFullDownloadAsync`, `PerformPartialDownloadAsync`) into smaller, more single-purpose private methods to improve readability, testability, and maintainability.
* **Enhance Cancellation UX:** Modify the example console application (`Program.cs`) to demonstrate a more interactive user cancellation (e.g., "Press 'C' to cancel") rather than just relying on a command-line timeout argument.
* **Test Edge Cases:** Add more unit tests for specific edge cases, such as zero-byte files or scenarios where server headers change unexpectedly between requests (though less likely with HEAD followed by GET/Range).