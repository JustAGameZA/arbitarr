namespace Arbitarr.Ai.Tests;

/// <summary>
/// AC10: classifier accuracy must be validated against a stratified labeled corpus
/// (docs/ac10-corpus/) covering junk/legitimate releases across both protocols and a range of
/// obfuscation levels. Corpus capture is step-0 owner-gated work (M5-1/M5-2/M5-6), explicitly out of
/// scope for this M5 AI-layer implementation pass.
///
/// These tests are intentionally skipped (not deleted, not vacuously passing) so the AC10 gate stays
/// visible in test output until the corpus exists — a green suite here would otherwise look like
/// "already covered" when the corpus is still owner-gated in progress.
/// </summary>
public class Ac10CorpusStrataTests
{
    private const string CorpusDirectory = "docs/ac10-corpus";

    private const string SkipReason =
        "AC10 corpus not yet captured (M5-1/M5-2/M5-6 are owner-gated, out of scope for this pass). " +
        "Skipped, not deleted, so this gate stays visible until docs/ac10-corpus/ exists.";

    [Fact(Skip = SkipReason)]
    public void Corpus_TorrentJunkStratum_ClassifierMeetsAccuracyTarget()
    {
        throw new InvalidOperationException(
            $"AC10 corpus directory '{CorpusDirectory}' does not exist yet — this test must remain skipped, not implemented, until it does.");
    }

    [Fact(Skip = SkipReason)]
    public void Corpus_TorrentLegitimateStratum_ClassifierMeetsAccuracyTarget()
    {
        throw new InvalidOperationException(
            $"AC10 corpus directory '{CorpusDirectory}' does not exist yet — this test must remain skipped, not implemented, until it does.");
    }

    [Fact(Skip = SkipReason)]
    public void Corpus_UsenetJunkStratum_ClassifierMeetsAccuracyTarget()
    {
        throw new InvalidOperationException(
            $"AC10 corpus directory '{CorpusDirectory}' does not exist yet — this test must remain skipped, not implemented, until it does.");
    }

    [Fact(Skip = SkipReason)]
    public void Corpus_UsenetLegitimateObfuscatedStratum_ClassifierMeetsAccuracyTarget()
    {
        throw new InvalidOperationException(
            $"AC10 corpus directory '{CorpusDirectory}' does not exist yet — this test must remain skipped, not implemented, until it does.");
    }
}
