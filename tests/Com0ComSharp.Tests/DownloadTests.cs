using System.Net;
using System.Net.Http;
using Xunit;

namespace Com0ComSharp.Tests;

public sealed class DownloadTests
{
    [Fact]
    public async Task HashMismatchPreservesExistingDestinationAndRemovesPartialFile()
    {
        var directory = RuntimeCompatibility.CreateTemporaryDirectory("Com0ComSharp-download-tests-");
        try
        {
            var destination = Path.Combine(directory, "installer.exe");
            File.WriteAllText(destination, "existing file");
            using var client = new HttpClient(new FixtureHandler(new ByteArrayContent(new byte[] { 1, 2, 3 })));
            await Assert.ThrowsAsync<InvalidDataException>(() => DriverDownload.DownloadInstallerAsync(destination, client));
            Assert.Equal("existing file", File.ReadAllText(destination));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeCreatingInstallerFile()
    {
        var directory = RuntimeCompatibility.CreateTemporaryDirectory("Com0ComSharp-download-tests-");
        try
        {
            var content = new ByteArrayContent(new byte[] { 1 });
            content.Headers.ContentLength = 5_000_001;
            using var client = new HttpClient(new FixtureHandler(content));
            await Assert.ThrowsAsync<InvalidDataException>(() => DriverDownload.DownloadInstallerAsync(Path.Combine(directory, "installer.exe"), client));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CancellationStopsWaitingEvenWhenFrameworkStreamIgnoresToken()
    {
        var directory = RuntimeCompatibility.CreateTemporaryDirectory("Com0ComSharp-download-tests-");
        try
        {
            using var stream = new UncancellableStream();
            using var client = new HttpClient(new FixtureHandler(new StreamContent(stream)));
            using var cancellation = new CancellationTokenSource();
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var download = DriverDownload.DownloadInstallerAsync(Path.Combine(directory, "installer.exe"), client, cancellation.Token);
            await RuntimeCompatibility.WaitAsync(stream.ReadStarted.Task, bound.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeCompatibility.WaitAsync(download, bound.Token));
            Assert.True(download.IsCanceled);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FixtureHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class UncancellableStream : MemoryStream
    {
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult(true);
            return completion.Task;
        }
        protected override void Dispose(bool disposing)
        {
            completion.TrySetException(new ObjectDisposedException(nameof(UncancellableStream)));
            base.Dispose(disposing);
        }
    }
}
