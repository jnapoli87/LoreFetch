using System.Collections.Concurrent;

namespace LoreFetch.Tests.Lab.Images;

/// A scriptable `HttpMessageHandler` so `ImageDownloader` can be driven
/// with no network at all. Thread-safe: `ImageDownloader` runs entries
/// under bounded concurrency, so tests that assert request counts need
/// counting that survives concurrent callers.
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
    private int _requestCount;
    private readonly ConcurrentBag<string> _requestedUrls = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public int RequestCount => _requestCount;

    public IReadOnlyCollection<string> RequestedUrls => _requestedUrls;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        _requestedUrls.Add(request.RequestUri!.ToString());
        return await _responder(request, cancellationToken);
    }
}
