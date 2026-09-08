namespace Arbitarr.Core.Ai;

/// <summary>
/// #112: the process-wide cache of the Ollama MODEL in force, and the mechanism that makes a change
/// take effect WITHOUT A RESTART. The deliberate sibling of <see cref="OllamaBaseUrlCache"/>.
///
/// <para><b>Why a second cache rather than one generic settings cache.</b> The obvious tidy-up is to
/// parameterise <see cref="OllamaBaseUrlCache"/> by <c>SettingKey</c> and hold a dictionary. It was
/// rejected: the generation counter is the thing that closes the publish race (see that type's
/// "THERE IS EXACTLY ONE WAY TO PUBLISH" note), and a shared counter would make ANY write invalidate
/// EVERY key's in-flight read — turning one operator action into a re-read storm — while a
/// per-key counter inside a dictionary is the same code twice with a lookup in front of it. Two
/// small holders that each do one thing keep the race argument readable, which is the property that
/// matters most here.</para>
///
/// <para>Every design note on <see cref="OllamaBaseUrlCache"/> applies verbatim: it starts stale so
/// the first use reads the row; the write path calls <see cref="Invalidate"/> only after a
/// successful write; publishing goes through <see cref="TrySetIfGenerationMatches"/> alone so a read
/// that began before a write cannot republish the pre-write value; and the lock covers the triple
/// (value, staleness, generation) because observing them apart would clear the stale flag while the
/// value it guards is still the old one.</para>
/// </summary>
public sealed class OllamaModelCache
{
    private readonly Lock _gate = new();
    private string? _model;
    private bool _stale = true;
    private long _generation;

    /// <summary>
    /// Returns the fresh cached model name, or null when the next use must re-read from the
    /// database, together with the generation against which such a read may safely publish. Starts
    /// stale, so the first use after start-up reads the row rather than serving a value nobody has
    /// loaded.
    /// </summary>
    public string? GetCurrentIfFresh(out long generation)
    {
        lock (_gate)
        {
            generation = _generation;
            return _stale ? null : _model;
        }
    }

    /// <summary>
    /// Marks the cache stale, so the next resolution re-reads the stored value. Called by the write
    /// path the moment a new model is persisted — that call is the whole "no restart required"
    /// mechanism, and removing it silently restores the restart requirement without failing a test
    /// that only checks the value was written. Advancing the generation invalidates any database
    /// read already in flight, so such a read cannot publish its pre-write value afterwards.
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
    public bool TrySetIfGenerationMatches(string model, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        lock (_gate)
        {
            if (_generation != generation)
            {
                return false;
            }

            _model = model;
            _stale = false;
            return true;
        }
    }
}
