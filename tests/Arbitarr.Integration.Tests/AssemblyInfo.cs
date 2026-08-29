using Xunit;

// ArbitarrWebApplicationFactory configures its per-instance /config directory by setting the
// process-wide ARBITARR_CONFIG_DIR environment variable (Program.cs reads it via
// Environment.GetEnvironmentVariable during top-level statement execution, before any DI seam
// exists to override it per-host). Running test classes in parallel would race that variable
// across concurrently-starting hosts, so this assembly runs its tests serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
