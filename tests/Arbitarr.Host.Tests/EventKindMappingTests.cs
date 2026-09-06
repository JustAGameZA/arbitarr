using Arbitarr.Core.Diagnostics;
using Arbitarr.Data.Entities;
using Arbitarr.Host.Diagnostics;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// #55 step 2: <see cref="RecordedEventKind"/> (Arbitarr.Core) mirrors <see cref="EventKind"/>
/// (Arbitarr.Data) because Core may not reference Data —
/// <c>Arbitarr.Architecture.Tests.CoreIsolationTests</c> requires Core to reference no other
/// Arbitarr project, and Core is where the emitting refresh worker lives.
///
/// A hand-maintained mirror drifts unless something checks it, and the drift is silent: a kind
/// added to one enum and not the other either throws at runtime deep inside a best-effort sink that
/// swallows its own exceptions, or quietly files events under the wrong kind. These tests make that
/// drift a build-time failure instead, which is the only reason the mirror is acceptable at all.
/// </summary>
public sealed class EventKindMappingTests
{
    [Fact]
    public void Every_recorded_kind_maps_to_a_defined_entity_kind()
    {
        foreach (var kind in Enum.GetValues<RecordedEventKind>())
        {
            var mapped = EventKindMapping.ToEntityKind(kind);

            Assert.True(
                Enum.IsDefined(mapped),
                $"{nameof(RecordedEventKind)}.{kind} maps to undefined {nameof(EventKind)} value {(int)mapped}.");
        }
    }

    /// <summary>
    /// The mapping must be onto as well as total: an <see cref="EventKind"/> with no
    /// <see cref="RecordedEventKind"/> counterpart is a kind the emission layer can never produce,
    /// which would be a store column nothing can fill rather than an outright bug — still worth
    /// failing on, because it means one of the two enums was extended alone.
    /// </summary>
    [Fact]
    public void Every_entity_kind_is_reachable_from_some_recorded_kind()
    {
        var reachable = Enum.GetValues<RecordedEventKind>()
            .Select(EventKindMapping.ToEntityKind)
            .ToHashSet();

        foreach (var kind in Enum.GetValues<EventKind>())
        {
            Assert.True(
                reachable.Contains(kind),
                $"{nameof(EventKind)}.{kind} has no {nameof(RecordedEventKind)} that maps onto it.");
        }
    }

    [Fact]
    public void Mapping_is_injective_so_two_recorded_kinds_never_collapse_into_one()
    {
        var mapped = Enum.GetValues<RecordedEventKind>()
            .Select(EventKindMapping.ToEntityKind)
            .ToList();

        Assert.Equal(mapped.Count, mapped.Distinct().Count());
    }

    [Fact]
    public void Names_agree_across_the_two_enums()
    {
        // Beyond arity: this catches a rename on one side, and a mapping that pairs the wrong two
        // members while still being total, onto and injective.
        foreach (var kind in Enum.GetValues<RecordedEventKind>())
        {
            Assert.Equal(kind.ToString(), EventKindMapping.ToEntityKind(kind).ToString());
        }
    }
}
