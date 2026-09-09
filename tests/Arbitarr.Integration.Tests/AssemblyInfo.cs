using Xunit;

// The config-directory race that originally forced this is GONE as of arb-rga.2: hosts are now
// handed their /config directory per-builder via builder.UseSetting("Arbitarr:ConfigDir", …), not
// through the process-wide ARBITARR_CONFIG_DIR environment variable, so two concurrently-starting
// hosts can no longer overwrite each other's directory.
//
// The second piece of process-global state — SqliteConnection.ClearAllPools(), which cleared the
// pool for every database in the process rather than the caller's — is GONE as of arb-rga.3: every
// call site now clears a named pool (SqliteTestDatabase / SqlitePools), and an architecture test
// bans the process-global form from returning.
//
// This attribute is therefore the last thing serialising this assembly, and it is kept only because
// enabling parallelism here is its own change with its own verification: arb-rga.4 removes it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
