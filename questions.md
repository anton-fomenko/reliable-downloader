# Questions

## How did you approach solving the problem?

The .NET solution uses a `FileDownloader` class implementing `IFileDownloader`. Key approaches include:
* **Resilience:** Implemented retries with exponential backoff for network errors, configured to handle disconnections exceeding two minutes.
* **Download Modes:** Supports partial downloads by checking `Accept-Ranges: bytes` and resuming from the correct offset if a partial file exists. Falls back to full download if ranges aren't supported or if the existing file is invalid.
* **Integrity:** Verifies downloads using the `Content-MD5` header provided by the server. The file is deleted if the MD5 check fails.
* **Progress & Cancellation:** Uses a callback for progress updates (reported on whole percentage changes via `ConsoleProgressReporter`) and supports graceful cancellation via `CancellationToken`.
* **Testing:** Leveraged interfaces (`IWebSystemCalls`) for mocking dependencies in unit tests.

## How did you verify your solution works correctly?

Verification included:
* **Unit Testing:** NUnit and Moq were used to test core logic, including header fetching, download modes (full, partial, resume), error handling (retries, timeouts), MD5 integrity checks (success and failure/deletion), cancellation, and progress reporting logic.
* **Manual Testing:** Simulated network disruptions (brief and ~2 minutes) using OS network controls to confirm resilience and resume capabilities.
* **Code Review:** Reviewed for logic, error handling, readability, and adherence to requirements.

## How long did you spend on the exercise?

3-4 hours

## What would you add if you had more time and how?

* **Structured Logging:** Implement a library like Serilog/NLog for better diagnostics instead of Console writes.
* **Configurable Retries:** Allow retry parameters to be passed in or configured externally.
* **Refactor `FileDownloader`:** Further break down large methods for clarity.
* **Improved Cancellation UX:** Add interactive cancellation (e.g., key press).
* **More Edge Case Tests:** Cover scenarios like zero-byte files or specific interruption points.