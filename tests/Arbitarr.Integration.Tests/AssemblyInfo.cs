using Xunit;

// The config-directory race that originally forced this is GONE as of arb-rga.2: hosts are now
// handed their /config directory per-builder via builder.UseSetting("Arbitarr:ConfigDir", …), not
// through the process-wide ARBITARR_CONFIG_DIR environment variable, so two concurrently-starting
// hosts can no longer overwrite each other's directory.
//
// The attribute stays for now only because a SECOND piece of process-global state is still in play:
// SqliteConnection.ClearAllPools() is called from many test files and clears the pool for every
// database in the process, not just the caller's. Removing this line before that is scoped per-file
// would trade one race for another. arb-rga.3 replaces those calls; arb-rga.4 then removes this.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
