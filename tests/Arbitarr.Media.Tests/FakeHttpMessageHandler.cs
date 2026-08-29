namespace ArrSearcher.Media.Tests;

/// <summary>
/// Stub <see cref="HttpMessageHandler"/> that records every request it receives and returns a
/// caller-supplied response via a responder function. Matches the established fakes-only pattern
/// from <c>ArrSearcher.Sources.NzbHydra.Tests</c> (no mocking library referenced by either test
/// project) so <see cref="ArrSearcher.Media.Providers.AnimeListsProvider"/> can be exercised without
/// ever making a live call to GitHub/AniDB.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<Uri> RequestedUris { get; } = new();

    public int RequestCount => RequestedUris.Count;

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
