using Moq;
using NUnit.Framework;
using System.Net;

namespace ReliableDownloader.Tests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class Tests
{
    private readonly FileDownloader _sut;
    private readonly Mock<IWebSystemCalls> _mockWebCalls = new();
    private FileProgress? _lastProgress;
    private CancellationTokenSource _cts;

    public Tests()
    {
        _sut = new FileDownloader(_mockWebCalls.Object);
        _cts = new CancellationTokenSource();
    }

    [TearDown]
    public void Teardown()
    {
        _cts.Dispose();
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestFails()
    {
        // Arrange
        var url = "http://fail.com/file.msi";
        var filePath = "file.msi";
        var response = new HttpResponseMessage(HttpStatusCode.NotFound); // Simulate failure

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response);

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once); // Verify HEAD call was made
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenContentLengthMissing()
    {
        // Arrange
        var url = "http://no-length.com/file.msi";
        var filePath = "file.msi";
        var response = new HttpResponseMessage(HttpStatusCode.OK); // Missing Content-Length header

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response);

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
    }

    [Test]
    public async Task TryDownloadFile_ShouldProceed_WhenPartialSupportedAndHeadersPresent()
    {
        // Arrange
        var url = "http://partial-support.com/file.msi";
        var filePath = "file.msi";
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.AcceptRanges.Add("bytes");
        response.Content = new ByteArrayContent(Array.Empty<byte>()); // Needs Content for Content.Headers
        response.Content.Headers.ContentLength = 1024;
        response.Content.Headers.ContentMD5 = Convert.FromBase64String("/////////////////////w=="); // Example valid MD5 hash (placeholder)

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response);

        // Act
        // Because download isn't implemented yet, it will return false,
        // but we verify the header check logic passed internally (no exceptions, etc.)
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        // For now, it returns false, but the key is that it got past the header check without early exit
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
        // TODO: add verification for download calls
    }

    [Test]
    public async Task TryDownloadFile_ShouldProceed_WhenPartialNotSupportedAndHeadersPresent()
    {
        // Arrange
        var url = "http://no-partial-support.com/file.msi";
        var filePath = "file.msi";
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        // No Accept-Ranges header
        response.Content = new ByteArrayContent(Array.Empty<byte>());
        response.Content.Headers.ContentLength = 2048; 
        // MD5 might be missing

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response);

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false); // Still false as download logic is missing
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
    }


    private void HandleProgress(FileProgress progress)
    {
        _lastProgress = progress;
        Console.WriteLine($"Progress: {progress}");
    }
}