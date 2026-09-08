namespace Arbitarr.Core.Ai;

/// <summary>
/// #89: the process-wide cache of the Ollama base URL in force, and the mechanism that makes a
/// change take effect WITHOUT A RESTART.
///
/// <para><b>The problem this solves.</b> Before #89 the base URL was read once at start-up into a
/// singleton <c>OllamaOptions</c> (Program.cs), so it could only ever change across a process
/// restart — the same shape <c>ResolvedSourceConfiguration</c> has, and the reason the Sources
/// section has to tell operators that disabling a source needs a restart. #89's acceptance criteria
/// require the opposite ("changes take effect without a restart, or the setting says plainly that a
/// restart is required and why"), so the value is resolved PER USE instead of per process.</para>
///
/// <para><b>Why a cache and not a plain DB read per call.</b> <c>IOllamaClient</c> is resolved on
/// the classification path, which runs per candidate release; reading a settings row on every
/// construction would put a SQLite query in front of work that is otherwise pure in-memory setup.
/// So the value is held here and re-read only when <see cref="Invalidate"/> says it is stale. The
/// write path calls that immediately after a successful write, so the window between "saved" and
/// "in force" is one request, not one restart.</para>
///
/// <para><b>Why this is not <see cref="Arbitarr.Core.Ai"/> reading the database itself.</b> Core
/// references no persistence layer (AC6), so this type holds a value and a staleness flag and
/// nothing else. The caller that CAN read the database (<c>OllamaBaseUrlResolver</c>, which has the
/// scoped DbContext) supplies the value through <see cref="TrySetIfGenerationMatches"/>. Keeping
/// the read outside means this stays a plain, synchronously-testable holder rather than a second
/// settings-reading path that could drift from <c>SettingsReader</c>'s.</para>
///
/// <para><b>THERE IS EXACTLY ONE WAY TO PUBLISH A VALUE, DELIBERATELY.</b> An earlier revision also
/// carried an unguarded <c>SetIfStale</c> (plus <c>Current</c> and <c>IsStale</c> readers) that
/// published whatever it was handed whenever the cache was stale. Nothing in production used it —
/// only its own tests did — and leaving it available was the hazard: a later resolution site wired
/// to it would compile, pass, and silently reintroduce the race
/// <see cref="TrySetIfGenerationMatches"/> exists to close, because a read that began BEFORE a
/// settings write would be free to republish the pre-write value afterwards and pin the old address
/// until the next write. It was deleted rather than left for a future caller to find, so any new
/// publisher has to go through the generation check.</para>
///
/// <para><b>Thread safety.</b> A lock rather than a volatile field: reading the triple (value,
/// staleness and generation) and writing it must not interleave, or a request could observe a stale
/// flag cleared while the value it guards is still the old one — which would pin the previous
/// address until the NEXT write rather than this one. Contention is nil in practice (the AI path is
/// capped at one in-flight call and the settings write is an operator action).</para>
/// </summary>
public sealed class OllamaBaseUrlCache
{
    private readonly Lock _gate = new();
    private string? _baseUrl;
    private bool _stale = true;
    private long _generation;

    /// <summary>
    /// Returns the fresh cached value, or null when the next use must re-read from the database,
    /// together with the generation against which such a read may safely publish. Starts stale, so
    /// the first use after start-up reads the row rather than serving a value nobody has loaded.
    ///
    /// <para>The generation is handed out WITH the value rather than exposed as a separate reader
    /// because the pair must be observed under one lock acquisition: read apart, a write could land
    /// between the two and hand the caller a generation that no longer describes the state it just
    /// decided on.</para>
    /// </summary>
    public string? GetCurrentIfFresh(out long generation)
    {
        lock (_gate)
        {
            generation = _generation;
            return _stale ? null : _baseUrl;
        }
    }

    /// <summary>
    /// Marks the cache stale, so the next resolution re-reads the stored value. Called by the write
    /// path the moment a new base URL is persisted — that call is the whole "no restart required"
    /// mechanism, and removing it silently restores the restart requirement without failing a test
    /// that only checks the value was written.
    ///
    /// <para>Advancing the generation is the other half of it: that invalidates any database read
    /// already in flight, so such a read cannot publish its pre-write value after this point.</para>
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _stale = true;
            _generation++;
        }
    }

    /// <summary>
    /// Publishes a database read only when no settings write invalidated the cache since the read
    /// began. Returns false when the caller must re-read because its value may be stale.
    /// </summary>
    public bool TrySetIfGenerationMatches(string baseUrl, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        lock (_gate)
        {
            if (_generation != generation)
            {
                return false;
            }

            _baseUrl = baseUrl;
            _stale = false;
            return true;
        }
    }
}
