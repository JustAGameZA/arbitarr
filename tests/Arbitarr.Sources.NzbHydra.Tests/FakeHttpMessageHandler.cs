namespace Arbitarr.Sources.NzbHydra.Tests;

/// <summary>
/// Stub <see cref="HttpMessageHandler"/> that records every request it receives and returns
/// caller-supplied responses in order (or via a responder function). Used so tests never make a
/// live network call.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<Uri> RequestedUris { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUris.Add(request.RequestUri!);
        var response = _responder(request);
        return Task.FromResult(response);
    }
}
