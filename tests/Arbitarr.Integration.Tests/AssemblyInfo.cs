// This assembly runs its test classes IN PARALLEL (arb-rga.4). The attribute that used to
// serialise it -- [assembly: CollectionBehavior(DisableTestParallelization = true)] -- is gone,
// and the two pieces of process-global state that forced it are gone from the TESTS:
//
//   * The config-directory race (arb-rga.2): hosts are handed their /config directory per-builder
//     via builder.UseSetting("Arbitarr:ConfigDir", ...), not through the process-wide
//     ARBITARR_CONFIG_DIR environment variable, so two concurrently-starting hosts can no longer
//     overwrite each other's directory.
//   * SqliteConnection.ClearAllPools() (arb-rga.3): every TEST-SIDE call site now clears a NAMED
//     pool (SqliteTestDatabase / SqlitePools) instead of every pool in the process, and
//     TestProcessGlobalStateTests bans the process-global form from returning to test IL.
//
// THE PROCESS-GLOBAL CLEAR IS NOT GONE FROM THE PROCESS, and that is the live hazard for the
// parallelism enabled here. Production Arbitarr.Data.Backup.RestoreService still calls
// ClearAllPools deliberately before swapping the database file -- it has to, since pooled handles
// hold a share lock on Windows and keep reading the replaced inode on Linux -- and the IL ban
// covers test assemblies only.
//
// Restore-driving tests DO live in this assembly: AdminBackupEndpointsTests posts to RestoreRoute
// in seven tests and BackupSecretExposureTests in two. None of them reaches the clear. Every one
// stops earlier in the pipeline -- at the admin-key gate (401), the confirmation check, the
// validator (400), the size limit (413) or the breaker (503) -- and RestoreService returns before
// ApplyValidatedFiles, which is where the ClearAllPools call sits. So no test here currently
// reaches the process-global clear, which is why parallelism is safe now.
//
// A HAPPY-PATH RESTORE TEST WOULD BE THE FIRST TO REACH IT, and would reintroduce arb-cbc/arb-5ba
// from the production side: it would force-close the pooled connections of whatever class happened
// to be running beside it. That is the thing to look at before adding one here. Tracked as
// arb-n21.
//
// WHY THE WIDTH IS CAPPED AT 4, in xunit.runner.json beside this file. That file is JSON and
// cannot carry a comment, so the reasoning lives here. A class here can hold a LIVE HOST, so the
// unit of concurrency is a whole ASP.NET application plus its SQLite file, not a cheap test body:
// the ceiling is memory, not CPU. One such host was measured at ~1.06 GB RSS, and CI pins
// DOTNET_GCHeapHardLimit to 0x80000000 (2GB) in build-test.yml, so raising the cap without
// raising that limit trades a fast suite for an OOM that reads as a flaky test (arb-rga.6).
//
// NOTE: xunit.runner.json only takes effect if it is COPIED TO THE OUTPUT DIRECTORY beside the
// test assembly -- the csproj does that. If that None entry is ever dropped, this assembly
// silently reverts to unbounded parallelism and still passes, which is the failure this comment
// exists to make visible.
