using Microsoft.Data.Sqlite;
using Xunit;

namespace Arbitarr.Integration.Tests.TestSupport;

/// <summary>
/// arb-tdc4: pins that <see cref="SeedDiagnostics.Wrap"/> actually carries the fields the next CI
/// sighting of the intermittent seeding "database is locked" needs, without inventing a production
/// fix ahead of a failing baseline (see the bead's comments for why not).
/// </summary>
public sealed class SeedDiagnosticsTests
{
    private const string ConfigDirectory = "arbitarr-tdc4-tests-config-directory";

    /// <summary>
    /// SQLite's own "database is locked" code, matching the bead's real-world sighting.
    /// </summary>
    private const int DatabaseLockedErrorCode = 5;

    [Fact]
    public void A_wrapped_SqliteException_carries_the_error_codes_elapsed_time_and_maintenance_state()
    {
        var original = new SqliteException("database is locked", DatabaseLockedErrorCode, extendedErrorCode: 261);
        var elapsed = TimeSpan.FromMilliseconds(1234);

        var wrapped = SeedDiagnostics.Wrap(original, elapsed, maintenanceFirstPassCompleted: true, ConfigDirectory);

        Assert.Contains("SqliteErrorCode=5", wrapped.Message);
        Assert.Contains("SqliteExtendedErrorCode=261", wrapped.Message);
        Assert.Contains("1234ms", wrapped.Message);
        Assert.Contains("completed", wrapped.Message);
    }

    [Fact]
    public void A_wrapped_SqliteException_names_the_database_file_name_but_not_the_config_directory_path()
    {
        var original = new SqliteException("database is locked", DatabaseLockedErrorCode);

        var wrapped = SeedDiagnostics.Wrap(original, TimeSpan.Zero, maintenanceFirstPassCompleted: false, ConfigDirectory);

        Assert.Contains("arbitarr.db", wrapped.Message);
        Assert.DoesNotContain(ConfigDirectory, wrapped.Message);
    }

    [Fact]
    public void A_wrapped_SqliteException_reports_unknown_maintenance_state_as_unknown()
    {
        var original = new SqliteException("database is locked", DatabaseLockedErrorCode);

        var wrapped = SeedDiagnostics.Wrap(original, TimeSpan.Zero, maintenanceFirstPassCompleted: null, ConfigDirectory);

        Assert.Contains("unknown", wrapped.Message);
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

        var wrapped = SeedDiagnostics.Wrap(original, TimeSpan.Zero, maintenanceFirstPassCompleted: null, ConfigDirectory);

        Assert.Same(original, wrapped.InnerException);
        Assert.NotNull(wrapped.InnerException!.StackTrace);
    }

    [Fact]
    public void Wrap_throws_for_a_null_exception()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SeedDiagnostics.Wrap(null!, TimeSpan.Zero, maintenanceFirstPassCompleted: null, ConfigDirectory));
    }
}
