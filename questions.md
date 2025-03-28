# Questions

## How did you approach solving the problem?

The .NET solution uses a `FileDownloader` class implementing `IFileDownloader`. Key approaches include:
* **Resilience:** Implemented retries with exponential backoff for network errors, configured to handle disconnections exceeding two minutes.
* **Download Modes:** Supports partial downloads by checking `Accept-Ranges: bytes` and resuming from the correct offset if a partial file exists. Falls back to full download if ranges aren't supported or if the existing file is invalid (e.g., larger than server size), preferring to use the partial mechanism even for restarts from byte 0 where supported.
* **Integrity:** Verifies downloads using the `Content-MD5` header provided by the server. The file is deleted if the MD5 check fails.
* **Progress & Cancellation:** Uses a callback for progress updates (reported on whole percentage changes via `ConsoleProgressReporter`) and supports graceful cancellation via `CancellationToken`.
* **Testing:** Leveraged interfaces (`IWebSystemCalls`) for mocking dependencies in unit tests, covering various success, failure, resume, and cancellation scenarios.

## How did you verify your solution works correctly?

Verification included:
* **Unit Testing:** NUnit and Moq were used to test core logic, including header fetching, download modes (full, partial, resume, restart logic), error handling (retries, timeouts), MD5 integrity checks (success and failure/deletion), cancellation, and progress reporting logic.
* **Manual Testing:** Simulated network disruptions (brief and ~2 minutes) using OS network controls to confirm resilience and resume capabilities under realistic conditions.
* **Code Review:** Reviewed for logic, error handling, readability, and adherence to requirements.

## How long did you spend on the exercise?

3-4 hours

## What would you add if you had more time and how?

* **Refactor `FileDownloader` using Strategy Pattern:** The `FileDownloader` class is currently quite large (over 600 lines). To improve adherence to the Single Responsibility Principle (SRP), readability, and maintainability, I would refactor it. I'd introduce an `IDownloadStrategy` interface and create concrete implementations like `PartialDownloadStrategy` and `FullDownloadStrategy`. `FileDownloader` would then delegate the download execution to the appropriate strategy based on server capabilities and download state (resume/restart), making the main class primarily an orchestrator. This would also simplify unit testing of the specific download mechanisms.
* **Structured Logging:** Replace `Console.WriteLine` calls with a structured logging library (like Serilog or NLog). This would enable configurable log levels (e.g., Debug, Info, Warn, Error), consistent formatting (timestamps, context), and outputting logs to files or other sinks for better diagnostics in production environments.
* **Configurable Options:** Make downloader options (`MaxRetries`, `InitialRetryDelay`, `MaxRetryDelay`, `ChunkSize`, `BufferSize`) configurable via an options object passed during `FileDownloader` instantiation or potentially loaded from an external configuration source, instead of relying solely on internal defaults.
* **Improved Cancellation UX:** Enhance the console application (`Program.cs`) to allow interactive cancellation (e.g., "Press 'C' to cancel") rather than just relying on a command-line timeout argument.
* **More Edge Case Tests:** Add further unit tests for specific edge cases, such as zero-byte files, network interruptions exactly *during* an MD5 calculation, or servers unexpectedly changing headers between requests.
* **Introduce DI Container:** While constructor injection is used for testability, introducing a proper Dependency Injection container (like `Microsoft.Extensions.DependencyInjection`) in `Program.cs` would align with modern .NET practices. This would involve registering services (`IFileDownloader`, `IWebSystemCalls`, etc.) and resolving the main application class from the container, simplifying dependency management and configuration, especially if the application grew more complex.