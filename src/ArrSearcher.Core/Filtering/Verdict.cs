namespace ArrSearcher.Core.Filtering;

/// <summary>
/// Outcome of evaluating a release against the filtering pipeline.
/// </summary>
public enum Verdict
{
    Unknown = 0,
    Accept = 1,
    Reject = 2,
    Suppress = 3,
}
