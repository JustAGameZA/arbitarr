#!/usr/bin/env bash
# Sourced by the backend matrix job's test step in
# .github/workflows/build-test.yml. It is a SCRIPT rather than inline workflow
# bash so the positive controls in test-count-gate-controls.sh invoke the
# shipped code itself, not a re-typed copy of it -- see
# docs/standards/process.md. The workflow's `source` therefore runs exactly the
# text the controls run.
#
# Expects nothing in the environment. Reads ./TestResults relative to the CWD.
# Callers set `set -euo pipefail` themselves; this file declares functions only
# and runs nothing at source time.

# --- the aborted-run check (arb-whhe) -----------------------------
# WHY dotnet test's own exit code is not trusted here.
#
# `dotnet test` has been observed exiting 0 after the test host aborted, having
# run a fraction of the assembly, while still printing "Passed! - Failed: 0".
# Two sightings:
#
#   1. Under four-lane contention all four lanes printed "Test Run Aborted"
#      after 5-26 of 490 tests -- the host died on a log-sink write failure --
#      and every lane still reported Passed! and exited 0.
#   2. A plain sequential run exited 0 after running only 8 of 11 assemblies;
#      three never appeared in the output at all, with no error.
#
# This is the same masking shape as CLAUDE.md section 5's `dotnet build | tail`:
# the status that reaches the step belongs to something other than the thing
# being judged, so a real failure arrives as a green. Today the only thing
# standing between an aborted run and a false green is the aggregate test-count
# ratchet, which is a LOWER bound on the whole run and cannot name the assembly
# that died -- and cannot see an abort at all whenever the surviving assemblies
# happen to sum above the floor.
#
# So the run's own artefacts are judged instead of its exit code: the console
# log must not carry abort text, and the trx must exist and record tests that
# actually executed.
#
# Call AFTER the assembly's `dotnet test` returns, once per trx_name, from
# ./TestResults's parent. $1 is the trx basename (`<Assembly>` or
# `<Assembly>.shard<k>of<N>`) -- the same name the run's --logger LogFileName
# and the tee'd console log were given, so this cannot check a different file
# from the one that was written.
assert_test_run_completed() {
  local trx_name="$1"
  local log="./TestResults/${trx_name}.console.log"
  local trx="./TestResults/${trx_name}.trx"

  if [ -z "$trx_name" ]; then
    echo "BLOCKED: assert_test_run_completed was called with no trx name." >&2
    exit 1
  fi

  # The log is the primary evidence and its absence is itself a fault: the tee
  # that writes it runs unconditionally beside the test command, so a missing
  # log means the step did not run what it thinks it ran. Blocking here rather
  # than skipping the grep is what stops this check passing vacuously
  # (CLAUDE.md section 4).
  if [ ! -f "$log" ]; then
    echo "BLOCKED: ${log} does not exist, so this run's output cannot be checked" >&2
    echo "for abort text. The console log is tee'd beside the test command, so its" >&2
    echo "absence means the run did not happen as this step assumes." >&2
    exit 1
  fi

  # Fixed strings, one per line, matched case-insensitively: these are vendor
  # messages, not a pattern language, and a regex here would only add ways to
  # mis-escape them. "Test Run Aborted" and "The active test run was aborted"
  # are what sighting 1 printed; the testhost lines are how the same death
  # surfaces when the host process is the thing that goes rather than the run.
  #
  # The case folding is done by `tr`, and the patterns are written already
  # lowercased, rather than by handing grep `-i`. That is not a style choice:
  # GNU grep 3.0 as shipped in git-bash ABORTS (SIGABRT, status 134) on
  # `grep -i -F` over these patterns, and the `|| true` below -- which has to
  # be there, since "no match" is grep's status 1 and the healthy case -- would
  # swallow that crash into an empty result and pass the check. That is this
  # bead's own defect wearing a different hat: a status belonging to something
  # other than the question being asked, arriving as a green. Folding with `tr`
  # keeps the matching case-insensitive on every grep build.
  #
  # `-n` numbers the lines of the folded stream, which is line-for-line the
  # log, so the numbers printed still address the real file.
  #
  # `grep_status` is captured with `|| grep_status=$?` on the assignment rather
  # than by reading `$?` on the NEXT line. Under the `set -euo pipefail` the
  # callers set, a bare `abort_hits=$(... grep ...)` assignment whose grep finds
  # nothing exits status 1, and `set -e` kills the function THERE -- on the
  # healthy path, before any `$?` line could run, blocking every clean run with
  # no message at all. The `||` puts the assignment in a tested context, which
  # is what exempts it from `set -e`.
  local abort_hits grep_status
  grep_status=0
  abort_hits=$(tr '[:upper:]' '[:lower:]' < "$log" | grep -n -F \
    -e 'test run aborted' \
    -e 'the active test run was aborted' \
    -e 'testhost process exited with error' \
    -e 'testhost process crashed' \
    -e 'test host process crashed') || grep_status=$?

  # Status 1 is "no match" and is the healthy path. Anything above 1 means grep
  # itself failed, and is NOT allowed to read as a clean log.
  if [ "$grep_status" -gt 1 ]; then
    echo "BLOCKED: scanning ${log} for abort text failed (grep exited ${grep_status})." >&2
    echo "A scanner that could not run is not evidence that the run was clean, so this" >&2
    echo "blocks rather than treating the empty result as a pass." >&2
    exit 1
  fi

  if [ -n "$abort_hits" ]; then
    echo "BLOCKED: ${trx_name} aborted its test run, however green dotnet test's exit" >&2
    echo "code was. The offending lines from ${log}:" >&2
    # The hits come from the case-folded stream, so print the REAL line text
    # from the log by line number rather than the lowercased copy -- the
    # numbers address the log line-for-line, and a reader comparing this output
    # against the artefact should see the artefact's own words.
    while IFS= read -r hit; do
      [ -n "$hit" ] || continue
      printf '  %s:%s\n' "${hit%%:*}" "$(sed -n "${hit%%:*}p" "$log")" >&2
    done <<< "$abort_hits"
    echo "An aborted run reports 'Passed! - Failed: 0' and exits 0 after running a" >&2
    echo "fraction of the assembly, so the exit code cannot be what decides this." >&2
    exit 1
  fi

  # The trx is the second half of the evidence. A run that died before the
  # logger flushed leaves no file at all, and the gate's own trx-set check
  # (check_trx_set in test-count-gate.sh) would catch that later -- but only
  # after the whole matrix has run, and only by artifact name. Naming it here
  # puts the failure in the step that produced it.
  if [ ! -f "$trx" ]; then
    echo "BLOCKED: ${trx} was not written, so ${trx_name} produced no test results." >&2
    echo "The trx logger writes it at the end of the run; no file means the run did" >&2
    echo "not reach that point." >&2
    exit 1
  fi

  # An executed= of 0 is a completed run that ran nothing. Every assembly in
  # this repo carries tests that survive $TEST_FILTER, so zero is always a
  # fault here -- an assembly filtered down to nothing, or a trx written by a
  # host that died between opening the file and running a test. An unparseable
  # figure is NOT folded to zero: a count that could not be read is not a count
  # of zero, and treating it as one would report a malformed file as an empty
  # assembly. Same reasoning as the gate's executed= parsers (arb-4f1).
  local executed
  executed=$(grep -o 'executed="[0-9]*"' "$trx" | head -1 | grep -o '[0-9]*') || true
  if ! printf '%s' "$executed" | grep -qE '^[0-9]+$'; then
    echo "BLOCKED: ${trx} carries no parseable executed= figure." >&2
    echo "Its <ResultSummary><Counters/> element is missing or malformed. A file that" >&2
    echo "cannot be read is not a run of zero tests, so this blocks by name rather" >&2
    echo "than folding it into the empty-assembly branch below." >&2
    exit 1
  fi
  if [ "$executed" -eq 0 ]; then
    echo "BLOCKED: ${trx_name} executed 0 tests." >&2
    echo "Every assembly here carries tests that survive the run's filter, so a run" >&2
    echo "that completed over nothing means the filter, the shard slice or the host" >&2
    echo "went wrong -- not that the assembly is empty." >&2
    exit 1
  fi

  echo "${trx_name}: run completed, no abort text, ${executed} tests executed."
}
