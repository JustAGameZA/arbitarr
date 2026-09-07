using System.Reflection;
using Arbitarr.Core.Diagnostics;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// <see cref="RecordedEvent"/>'s own doc says it is "field-for-field identical to
/// <see cref="IEventSink.RecordAsync"/>'s parameters, deliberately: this is the same event in a
/// shape that can go in a list, not a second model of one." That sentence is the thing that keeps
/// the single and batch paths writing the same row -- and until this test existed it was prose with
/// nothing behind it.
///
/// It failed to hold once already. #54 added a shadowMode parameter to RecordAsync and a matching
/// ShadowMode member to RecordedEvent, but the batch path beneath them projected a tuple that
/// omitted it, so a Decision recorded through RecordBatchAsync would have persisted a NULL flag.
/// The shadow-mode filter treats NULL as "no answer" and excludes such rows from BOTH branches, so
/// the row would have been invisible under "Shadow-only" AND "Enforced" while rendering as
/// "Unknown" -- a silent failure of #54 AC1. Nothing caught it because RecordBatchAsync had no
/// production caller yet: dead code cannot fail a test, so a green suite carried no information.
///
/// This is the same technique <c>EventKindMappingTests</c> uses on the RecordedEventKind/EventKind
/// mirror, and for the same reason: a hand-maintained correspondence drifts unless something checks
/// it, and the drift is silent.
/// </summary>
public sealed class RecordedEventCorrespondenceTests
{
    [Fact]
    public void RecordedEvent_carries_exactly_the_fields_RecordAsync_takes()
    {
        // cancellationToken is plumbing, not part of the event, so it is the one parameter with no
        // corresponding member. Everything else must appear on both sides.
        var parameters = typeof(IEventSink)
            .GetMethod(nameof(IEventSink.RecordAsync))!
            .GetParameters()
            .Select(p => p.Name!)
            .Where(name => name != "cancellationToken")
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var members = typeof(RecordedEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(parameters, members, StringComparer.OrdinalIgnoreCase);
    }
}
