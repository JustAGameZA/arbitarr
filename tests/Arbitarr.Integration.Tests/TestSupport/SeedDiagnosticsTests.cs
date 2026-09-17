using Microsoft.Data.Sqlite;
using Xunit;

namespace Arbitarr.Integration.Tests.TestSupport;

/// <summary>
/// arb-tdc4: pins that <see cref="SeedDiagnostics.Wrap"/> actually carries the fields the next CI
/// sighting of the intermittent seeding "database is locked" needs, without inventing a production
/// fix ahead of a failing baseline (see the bead's comments for why not).
///
/// <para><b>Every maintenance-state assertion below pins the FULL rendered phrase</b>
/// (<c>before=X, after=Y</c>), never a bare substring such as <c>"completed"</c> — that substring
/// matches "not completed" too, so a switch arm swapped between <c>true</c> and <c>false</c> would
/// still pass. Pinning the whole phrase is what makes each of the three tri-state values, and both a
/// transition and no transition between the before/after samples, individually provable.</para>
/// </summary>
public sealed class SeedDiagnosticsTests
{
    private const string ConfigDirectory = "arbitarr-tdc4-tests-config-directory";

    /// <summary>
    /// SQLite's own "database is locked" code, matching the bead's real-world sighting.
    /// </summary>
    private const int DatabaseLockedErrorCode = 5;

    [Fact]
    public void A_wrapped_SqliteException_carries_the_error_codes_and_elapsed_time()
    {
        var original = new SqliteException("database is locked", DatabaseLockedErrorCode, extendedErrorCode: 261);
        var elapsed = TimeSpan.FromMilliseconds(1234);

        var wrapped = SeedDiagnostics.Wrap(original, elapsed, before: true, after: true, ConfigDirectory);

        Assert.Contains("SqliteErrorCode=5", wrapped.Message);
        Assert.Contains("SqliteExtendedErrorCode=261", wrapped.Message);
        Assert.Contains("1234ms", wrapped.Message);
    }

    [Fact]
    public void A_wrapped_SqliteException_names_the_database_file_name_but_not_the_config_directory_path()
    {
        var original = new SqliteException("database is locked", DatabaseLockedErrorCode);

        var wrapped = SeedDiagnostics.Wrap(original, TimeSpan.Zero, before: false, after: false, ConfigDirectory);

        Assert.Contains("arbitarr.db", wrapped.Message);
        Assert.DoesNotContain(ConfigDirectory, wrapped.Message);
    }

    [Fact]
    public void Before_and_after_both_true_renders_completed_on_both_sides_with_no_transition()
    {
        var original = new SqliteException("database is locked", DatabaseLockedErrorCode);

        var wrapped = SeedDiagnostics.Wrap(original, TimeSpan.Zero, before: true, after: true, ConfigDirectory);

        Assert.Contains("before=completed, after=completed", wrapped.Message);
    }

    [Fact]
    public void Before_and_after_both_false_renders_not_completed_on_both_sides_with_no_transition()
    {
        var original = new SqliteException("database is locked", DatabaseLockedErrorCode);

        var wrapped = SeedDiagnostics.Wrap(original, TimeSpan.Zero, before: false, after: false, ConfigDirectory);

        Assert.Contains("before=not completed, after=not completed", wrapped.Message);
    }

    [Fact]
    public void Before_and_after_both_null_renders_unknown_on_both_sides_with_no_transition()
    {
        var original = new SqliteException("database is locked", DatabaseLockedErrorCode);

        var wrapped = SeedDiagnostics.Wrap(original, TimeSpan.Zero, before: null, after: null, ConfigDirectory);

        Assert.Contains("before=unknown, after=unknown", wrapped.Message);
    }

    [Fact]
    public void A_pass_that_completes_between_the_two_samples_renders_the_transition()
    {
        var original = new SqliteException("database is locked", DatabaseLockedErrorCode);

        var wrapped = SeedDiagnostics.Wrap(original, TimeSpan.Zero, before: false, after: true, ConfigDirectory);

        Assert.Contains("before=not completed, after=completed", wrapped.Message);
    }

    [Fact]
    public void The_original_exception_is_preserved_as_the_inner_exception_with_its_stack_trace()
    {
        SqliteException original;
        try
        {
            throw new SqliteException("database is locked", DatabaseLockedErrorCode);
        }
        catch (SqliteException caught)
        {
            original = caught;
        }

        var wrapped = SeedDiagnostics.Wrap(original, TimeSpan.Zero, before: null, after: null, ConfigDirectory);

        Assert.Same(original, wrapped.InnerException);
        Assert.NotNull(wrapped.InnerException!.StackTrace);
    }

    [Fact]
    public void Wrap_throws_for_a_null_exception()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SeedDiagnostics.Wrap(null!, TimeSpan.Zero, before: null, after: null, ConfigDirectory));
    }
}
