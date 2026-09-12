namespace Arbitarr.Sources.Newznab.Tests;

/// <summary>
/// Stub <see cref="HttpMessageHandler"/> that records every request it receives — URI AND headers —
/// and answers via a caller-supplied responder. Used so tests never make a live network call.
///
/// <para>The whole <see cref="HttpRequestMessage"/> is recorded, not just the URI, because the
/// api-key placement assertions have to be able to see a key in a HEADER as well as one in the
/// path or query string. Recording only the URI would make those assertions blind to exactly one
/// of the two placements they exist to reject.</para>
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<Uri> RequestedUris { get; } = new();

    /// <summary>Every request's headers, flattened to "name: value" lines, in request order.</summary>
    public List<IReadOnlyList<string>> RequestedHeaderLines { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUris.Add(request.RequestUri!);
        RequestedHeaderLines.Add(FlattenHeaders(request));
        var response = _responder(request);
        return Task.FromResult(response);
    }

    internal static IReadOnlyList<string> FlattenHeaders(HttpRequestMessage request) =>
        request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .Select(header => $"{header.Key}: {string.Join(",", header.Value)}")
            .ToArray();
}
