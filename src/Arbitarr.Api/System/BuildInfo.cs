using System.Reflection;

namespace Arbitarr.Api.SystemInfo;

/// <summary>
/// The build identity of the running process: what commit, what image, and when it was built.
///
/// The four build-time fields (<see cref="CommitSha"/>, <see cref="ImageTag"/>,
/// <see cref="BuildTimestampUtc"/>, <see cref="InformationalVersion"/>) come from MSBuild
/// properties baked into the assembly by the Dockerfile's build args, and cannot change during
/// the process lifetime -- a redeploy is required to change them. <see cref="ProcessStartedUtc"/>
/// is the one runtime field: it resets on every restart, independent of whether the image itself
/// changed.
/// </summary>
public sealed record BuildInfo(
    string CommitSha,
    string ImageTag,
    string BuildTimestampUtc,
    string InformationalVersion,
    DateTimeOffset ProcessStartedUtc)
{
    /// <summary>
    /// The marker rendered for any build-time field the Dockerfile did not stamp -- a local
    /// `dotnet build`/`dotnet run` with no build args supplied. Explicit and visible rather than
    /// blank: a blank stamp reads as a bug, a marked one reads as what it is.
    /// </summary>
    public const string UnknownLocalBuild = "unknown (local build)";

    /// <summary>
    /// Reads the build-time fields from the entry assembly's metadata attributes exactly once.
    /// Callers register the result as a singleton so per-request handlers never re-read
    /// reflection metadata that cannot change for the life of the process.
    /// </summary>
    public static BuildInfo ReadOnce(TimeProvider timeProvider)
    {
        var assembly = Assembly.GetEntryAssembly();
        var metadata = assembly?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var informationalVersion = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return new BuildInfo(
            CommitSha: NonEmptyOrUnknown(metadata.GetValueOrDefault("CommitSha")),
            ImageTag: NonEmptyOrUnknown(metadata.GetValueOrDefault("ImageTag")),
            BuildTimestampUtc: NonEmptyOrUnknown(metadata.GetValueOrDefault("BuildTimestampUtc")),
            InformationalVersion: NonEmptyOrUnknown(informationalVersion),
            ProcessStartedUtc: timeProvider.GetUtcNow());
    }

    private static string NonEmptyOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? UnknownLocalBuild : value;
}
