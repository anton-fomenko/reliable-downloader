# Questions

## How did you approach solving the problem?

* **Understanding Requirements:** I first thoroughly reviewed the README to grasp the core problem (reliable downloads over unreliable networks) and the specific functional requirements (resilience, partial/full download modes, integrity checks, progress reporting, cancellation). I also noted the non-functional requirements regarding code quality and testing.

* **Modular Design & Key Decisions:** I aimed for a modular design to separate concerns. The core download logic resides in `FileDownloader`. Other distinct responsibilities were separated into helper classes: `ConsoleProgressReporter` for progress output, `DownloadSpeedEstimator` for calculating remaining time, and `FileDownloaderOptions` for configuration. Key decisions included: using a retry mechanism with exponential backoff for resilience; prioritizing partial downloads for efficiency, with logic for resumption and fallbacks; using `Content-MD5` for integrity checks; implementing progress via callbacks and cancellation using `CancellationToken`.

* **Iterative Implementation & Testing:** Development followed an iterative, test-driven approach. I focused on implementing features incrementally, writing unit tests (using NUnit and Moq) for each piece of functionality alongside its implementation. This involved mocking `IWebSystemCalls` to simulate various scenarios and validate logic without network dependencies at each step. Within `FileDownloader`, the logic was further broken down into distinct methods for different stages like fetching prerequisites (`GetDownloadPrerequisitesAsync`), performing full (`PerformFullDownloadAsync`) and partial (`PerformPartialDownloadAsync`, `DownloadChunkAsync`) downloads, verifying integrity (`VerifyIntegrityAsync`), and handling retries (`ExecuteWithRetryAsync`), enhancing internal modularity.

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