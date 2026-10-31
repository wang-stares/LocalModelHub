using System.Net;
using System.Net.Http.Headers;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class DownloadServiceTests
{
    [Fact]
    public async Task Resumes_part_when_switching_source()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lmh-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "model.gguf");
            await File.WriteAllBytesAsync(destination + ".part", "AAAA"u8.ToArray());
            await File.WriteAllTextAsync(
                destination + ".part.meta",
                """{"Url":"https://old.example/model.gguf","ExpectedBytes":10}""");

            string? range = null;
            var handler = new StubHandler(request =>
            {
                range = request.Headers.Range?.ToString();
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent("BBBBBB"u8.ToArray())
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, 9, 10);
                return response;
            });

            var service = new DownloadService(handler);
            await service.DownloadFileAsync(
                "https://new.example/model.gguf", destination, 10, null, CancellationToken.None);

            Assert.Equal("bytes=4-", range);
            Assert.Equal("AAAABBBBBB", await File.ReadAllTextAsync(destination));
            Assert.False(File.Exists(destination + ".part"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Source_switch_tries_once_without_internal_retry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lmh-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "model.gguf");
            var calls = 0;
            var handler = new StubHandler(_ =>
            {
                calls++;
                throw new HttpRequestException("boom");
            });

            var service = new DownloadService(handler);
            await Assert.ThrowsAsync<IOException>(() => service.DownloadFileAsync(
                "https://broken.example/model.gguf", destination, 10, null,
                CancellationToken.None, allowRetry: false));

            Assert.Equal(1, calls);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_responder(request));
    }
}
