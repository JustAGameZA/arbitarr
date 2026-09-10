#!/usr/bin/env bash
# Sourced by the backend matrix job's test step in
# .github/workflows/build-test.yml. It is a SCRIPT rather than inline workflow
# bash so the positive controls in test-count-gate-controls.sh invoke the
# shipped code itself, not a re-typed copy of it -- see
# docs/standards/process.md. The workflow's `source` therefore runs exactly the
# text the controls run.
#
# Expects in the environment: TEST_FILTER. Writes its records under
# ./TestResults. Callers set `set -euo pipefail` themselves; this file declares
# functions only and runs nothing at source time.

# --- sharding (arb-adm) -------------------------------------------
# An entry carrying `shard: "<k>of<N>"` runs only its k-th slice of the
# assembly. The slice is derived HERE, in the job, from
# `dotnet test --list-tests`: take the discovered test names under the
# SAME $TEST_FILTER the run will use, reduce them to fully-qualified
# CLASS names (everything before the last dot), sort -u, and give class
# i to shard (i % N) + 1.
#
# The assignment is round-robin over the SORTED class list, and it is
# deliberately not weighted. Balance is adequate, so index-mod-N stays.
#
# HISTORY, not current evidence. The measurement that originally
# justified leaving this unweighted (arb-adm: 49 classes, 363 tests,
# 313.8 s summed, shards of 202/161 tests and 1 m 38 s / 25 s wall) is
# superseded and is kept only to explain why weighting was rejected.
# xunit runs maxParallelThreads=4 collections in parallel but never
# splits a single class, so a shard's wall clock is
# max(its summed time / 4, its heaviest class). Back then ONE class,
# AuthEndpointsTests, was 80.9 s of the 313.8 s, so whichever shard
# held it could not finish sooner and all three candidate strategies --
# this index round-robin, greedy longest-first by test count, and
# greedy longest-first by MEASURED time (the best any weighting could
# do) -- modelled to the SAME ~81 s job. That floor was structural: no
# partition could go below it.
#
# arb-n3q (merged as PR #183) removed it by splitting that class into
# seven. On #183's run the shards came out even -- A1 executed 182
# tests in 39 s and A2 181 in 36 s -- so the imbalance the paragraph
# above describes no longer exists, and none of those figures should be
# read as describing the suite today. The heaviest class is now
# AdminApiKeyRouteEnumerationTests at about 33 s, and the remaining
# per-class floor is AuthPasswordChangeRateLimitTests at about 26-30 s,
# which is one KDF-heavy test; its only lever is arb-hsm, not a
# cleverer partition. Reweighting still buys nothing, and would still
# cost a weights table that goes stale silently -- as this comment
# itself did.
#
# Deriving both shards from one sorted list is what makes the partition
# complete BY CONSTRUCTION: every discovered class lands in exactly one
# shard, because the assignment is a total function of its index. The
# alternative -- splitting on namespace prefixes -- needs a
# hand-maintained prefix list that a new namespace silently falls
# outside of, and its completeness check ("every class matched some
# prefix") is satisfied vacuously by a catch-all. Rejected for that.
#
# Construction alone is NOT accepted as the only evidence, because a
# --list-tests that under-reports would shard a short list completely
# and still lose tests. So the discovered class list AND the discovered
# TEST count are recorded to
# ./TestResults/<Assembly>.shard<k>of<N>.listed and uploaded beside the
# trx, and the gate asserts the shards' summed executed= for the
# assembly EQUALS that recorded test count -- exactly, in both
# directions. A test that falls into neither shard, or one that runs in
# two shards, shows up there rather than only as a ratchet drop that a
# double-count can over-satisfy instead of trip.
#
# --list-tests runs against the SAME dll and the SAME filter as the
# real run, so discovery cannot diverge from execution.
#
# Two invariants hold this together, and BOTH exist because their
# failure mode is a silent green run, not a red one (arb-adm review):
#
#   1. The parsed names are STRUCTURAL. The character class below
#      includes `+` (nested classes, `Outer+Nested`) and a backtick
#      (generic classes, GenericTests`1) because without them such a
#      name TRUNCATES at that character, the following
#      `sed 's/\.[^.]*$//'` then strips the last surviving segment,
#      and what is left is the BARE NAMESPACE. A term
#      `FullyQualifiedName~Arbitarr.Integration.Tests` matches every
#      test in the assembly: one shard runs the whole suite, tests are
#      double-counted, the summed executed= EXCEEDS the floor so the
#      ratchet is over-satisfied rather than tripped, and the
#      .shard<k>of<N>.listed byte-identity check still passes because
#      both shards derived the same bad list. (The exact-sum check
#      below WOULD fire on that, since the recorded test count comes
#      from the same listing; this assertion stays because it names the
#      cause where the sum names only the symptom.) Widening the class
#      alone is not accepted as the guard, so every parsed name is also
#      asserted to start with the assembly's root namespace AND carry at
#      least one segment beyond it -- a namespace-only token is BLOCKED
#      by name, never silently treated as a wildcard.
#
#   2. The terms are TERMINATED. `FullyQualifiedName~<Class>` is a
#      SUBSTRING match, so a class whose name is a prefix of another
#      (`AuthEndpointsTests` / `AuthEndpointsTestsV2`) would land in
#      BOTH shards and double-count past the ratchet the same way. A
#      test's FQN is `<Class>.<Method>`, so emitting `<Class>.` with a
#      trailing dot can only match the class itself: `Class.` cannot
#      match `ClassV2.`. Verified against the built dll on this SDK --
#      `--filter` accepts a trailing `.` in a `~` term and the
#      executed counts are unchanged at 202/161.
#
# There are no nested or generic test classes, and no prefix
# collisions, among the 49 classes today. Both invariants are
# therefore asserted rather than merely relied upon: each fires the
# day such a class is added, which is exactly when nobody is looking.
shard_filter() {
  local dll="$1" k="$2" n="$3" a="$4"
  local listing names classes count tests terms bad
  # Block by name on an unset input (arb-2nx), before the --list-tests call
  # below that would otherwise use an empty filter silently.
  if [ -z "${TEST_FILTER:-}" ]; then
    echo "BLOCKED: shard_filter requires TEST_FILTER to be set." >&2
    exit 1
  fi
  # ONE --list-tests run feeds BOTH records. The test count and the
  # class list must describe the SAME discovery, or the gate's
  # exact-sum assertion compares a count against a partition it does
  # not belong to -- so both are derived from one captured listing
  # here, never from two invocations of --list-tests.
  listing=$(dotnet test "$dll" --filter "$TEST_FILTER" --list-tests)
  # NOT sort -u, and that is the whole point of this line. A [Theory]
  # is listed ONCE PER DATA ROW, and the capture below keeps only the
  # leading identifier -- it stops at the `(` -- so `Foo.Bar(x: 1)`
  # and `Foo.Bar(x: 2)` both reduce to `Foo.Bar`. Deduplicating here
  # folds those rows into one and undercounts by exactly the theory
  # surplus: the first run of this check recorded 304 against 363
  # genuinely executed tests, and the gate correctly called it an
  # overlap that did not exist. Each listed row is one executed test
  # case, so the rows are counted as they come.
  names=$(printf '%s\n' "$listing" \
          | sed -n 's/^[[:space:]][[:space:]]*\([A-Za-z_][A-Za-z0-9_.+`]*\).*$/\1/p' \
          | sed '/^$/d')
  # The CLASS list keeps its `sort -u`: there the unit is a class, a
  # class is meant to appear once however many tests it holds, and
  # this parse is unchanged from what shipped.
  classes=$(printf '%s\n' "$names" | sed 's/\.[^.]*$//' | sed '/^$/d' | sort -u)
  if [ -z "$classes" ]; then
    echo "BLOCKED: --list-tests discovered no test classes in ${dll}." >&2
    echo "A shard derived from an empty list would run nothing and report" >&2
    echo "executed=0, so this fails here rather than passing vacuously." >&2
    exit 1
  fi
  # Structural assertion (invariant 1). The root namespace is derived
  # from the assembly name -- the one already in $a -- deliberately,
  # rather than written out again here: a second hard-coded copy is a
  # thing to keep in sync, and the failure of keeping it in sync is
  # this check quietly matching nothing.
  #
  # It is load-bearing for the recorded COUNT too, not only for the
  # partition (arb-4f1, #182 LOW-4). $classes and $tests come from the
  # SAME listing, so a truncated parse that collapses names to a bare
  # namespace also mis-parses the rows $tests is counted from -- the
  # header this writes would then record a count describing a listing
  # nobody would want, and the gate's exact-sum assertion would compare
  # the executed sum against it and be satisfied. Blocking here stops
  # the bad count from ever being written, which is why this check
  # cannot be relaxed on the grounds that the sum check would catch it:
  # the sum check's own reference value comes through this parse.
  bad=$(printf '%s\n' "$classes" | grep -v -e "^${a}\..\+" || true)
  if [ -n "$bad" ]; then
    echo "BLOCKED: --list-tests of ${a} parsed name(s) that are not a class under" >&2
    echo "the ${a} namespace:" >&2
    printf '%s\n' "$bad" | sed 's/^/  /' >&2
    echo "A bare namespace here is not a class: as a ~ term it matches EVERY test in" >&2
    echo "the assembly, so one shard runs the whole suite and the summed executed=" >&2
    echo "exceeds the floor instead of dropping below it -- the ratchet would not" >&2
    echo "fire and the .shard<k>of<N>.listed comparison would still pass. Blocking" >&2
    echo "by name here, where the cause is visible." >&2
    exit 1
  fi
  # The WHOLE sorted class list is recorded, not just its length.
  # Both shards write this file and the gate requires the copies to be
  # byte-identical, which is a cheap positive control that the two
  # shards really did partition ONE list: equal counts over different
  # membership -- a class renamed between the two jobs, a flaky
  # discovery dropping one class and inventing another -- compares
  # equal on a number and unequal on the list. Recording the count
  # alone would have let exactly that through.
  #
  # $TEST_FILTER is passed to --list-tests above deliberately.
  # Verified on this SDK against the built dll: discovery HONOURS the
  # filter (363 tests listed with it, 365 without, and executed=363 in
  # the unsharded baseline trx). Listing unfiltered would name the two
  # Timing/Load/Quarantine tests that are never executed, and the
  # gate's completeness comparison could then never be satisfied.
  count=$(printf '%s\n' "$classes" | wc -l | tr -d ' ')
  # The discovered TEST count (arb-u9i). The parse is: count the
  # indented test lines of the SAME listing the class list came from,
  # undeduplicated -- see the note on $names above for why `sort -u`
  # here is wrong. One listed row is one test case the run will
  # execute, which is precisely what the gate compares against the
  # summed executed=.
  #
  # That last sentence is CONTINGENT, not structural, and the day it
  # stops being true this check goes red on an innocent partition.
  # It holds because every theory in this assembly has its data
  # available at DISCOVERY time: today 23 [Theory], 70 [InlineData],
  # and one [MemberData] over two literal arrays, which --list-tests
  # enumerates as its full 12 rows. A [MemberData] backed by
  # something computed at RUN time -- a database read, a file glob --
  # would be listed ONCE and executed N times, so the sum would
  # exceed the recorded count and the gate would report the UNDER
  # message ("a test fell into NEITHER shard") against a partition
  # that is perfectly correct.
  #
  # The response to that is to revisit THIS check -- count such
  # theories from the trx side, or record them separately -- and NOT
  # to relax the assertion to `>=`. A `>=` restores exactly the
  # over-count blindness the whole bead exists to close: it is the
  # lower bound the ratchet already gives, and a test running in two
  # shards would once again pass by adding to the total.
  tests=$(printf '%s\n' "$names" | sed '/^$/d' | wc -l | tr -d ' ')
  # Fail closed on zero, separately from the $classes emptiness check
  # above. An empty or unparsed listing would otherwise record tests=0,
  # and `sum == 0` is a condition a run that executed NOTHING satisfies
  # -- the exact-sum assertion would then pass vacuously, which is the
  # one way it could be worse than the ratchet alone. A non-empty
  # $classes does not imply a non-zero count, since a future change to
  # either parse could keep one while losing the other.
  if [ "$tests" -eq 0 ]; then
    echo "BLOCKED: --list-tests of ${a} discovered 0 tests under the filter." >&2
    echo "The gate asserts sum(executed=) == this number; recording 0 would make that" >&2
    echo "assertion satisfiable by a run that executed nothing at all." >&2
    exit 1
  fi
  # The record is named by SHARD (arb-u9i), mirroring the trx naming
  # below, so the gate can require exactly one per DECLARED shard. The
  # unsuffixed name could not: two copies from the SAME shard -- an
  # artifact restored twice, a re-run overlaying -- satisfied both the
  # file count and the byte-identity check, because two identical
  # copies of one shard's file are, of course, identical.
  #
  # The test count is a HEADER LINE in this same file rather than a
  # sibling file, deliberately: one `cmp` then proves the shards agree
  # on the count AND on the membership together, so the count cannot
  # drift from the list it describes. A sibling file would be a second
  # artifact able to go missing on its own, and a missing count file is
  # exactly the case where the gate must choose between blocking and
  # skipping the check. Readers of the class list therefore strip this
  # first line -- see the gate.
  {
    printf 'tests=%s\n' "$tests"
    printf '%s\n' "$classes"
  } > "./TestResults/${a}.shard${k}of${n}.listed"
  # The trailing `.` is invariant 2 and is NOT a typo to tidy away.
  #
  # Placement is a function of sorted class NAME, not of cost: renaming a class
  # or adding one re-parities every class after it in the sorted list. After
  # such a change, check that the two heaviest classes did not land on the same
  # shard. Nothing here enforces that -- the exact-sum gate proves the partition
  # is COMPLETE, never that it is balanced -- so a re-parity that co-locates
  # them shows up only as a slower job.
  terms=$(printf '%s\n' "$classes" \
          | awk -v k="$k" -v n="$n" 'NR % n == (k % n) { print "FullyQualifiedName~" $0 "." }' \
          | paste -sd'|' -)
  if [ -z "$terms" ]; then
    echo "BLOCKED: shard ${k}of${n} of ${a} selected no classes out of ${count}." >&2
    exit 1
  fi
  printf '(%s)&(%s)\n' "$terms" "$TEST_FILTER"
}
