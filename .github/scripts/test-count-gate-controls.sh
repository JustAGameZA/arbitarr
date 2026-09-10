#!/usr/bin/env bash
# Positive controls for the test-count gate. Run by the Guards job on every PR.
#
# These exist because an absence assertion passes just as happily when the thing
# it looks for was never in play (CLAUDE.md section 4). "The gate went green" is
# not evidence the gate can go red. Each control below plants a specific fault
# and requires the gate to BLOCK on it, by exit code AND by a substring of the
# message that names the cause -- so a gate that blocked for the wrong reason
# fails the control too.
#
# The controls SOURCE .github/scripts/test-count-gate.sh and
# .github/scripts/shard-filter.sh, the same files the workflow sources. That is
# the whole point of those scripts existing (see docs/standards/process.md):
# these controls previously lived in PR bodies and ran a re-typed copy of the
# workflow text, which is evidence about the copy, not about CI.
#
# NOT covered here, deliberately: the dll-backed parse control (a real
# `dotnet test --list-tests` producing tests=363 over 49 classes). It needs the
# built Integration assembly, which the Guards job does not have and should not
# wait for. The shard step's OWN checks cover that case at run time and block
# rather than pass vacuously -- the root-namespace assertion rejects a parse
# that collapsed to a bare namespace, and the zero-tests and empty-classes
# branches reject a listing that produced nothing.
#
# Run standalone: bash .github/scripts/test-count-gate-controls.sh

set -uo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)

pass_count=0
fail_count=0

pass() { echo "PASS  $1"; pass_count=$((pass_count + 1)); }
fail() { echo "FAIL  $1: $2"; fail_count=$((fail_count + 1)); }

# Runs one gate function in a SUBSHELL against a fixture tree and reports its
# exit code and combined output. A subshell is required: the gate functions
# `exit 1` on a fault, which would otherwise take this script down with them.
#
# $1 fixture dir, $2 gate function name, rest: env assignments.
run_gate() {
  local dir="$1" fn="$2"
  shift 2
  (
    cd "$dir" || exit 99
    set -euo pipefail
    # shellcheck source=/dev/null
    . "$repo_root/.github/scripts/test-count-gate.sh"
    export "$@"
    "$fn"
  ) 2>&1
}

# Asserts the gate BLOCKED and that its message names the expected cause.
expect_block() {
  local name="$1" dir="$2" fn="$3" needle="$4"
  shift 4
  local out status
  out=$(run_gate "$dir" "$fn" "$@")
  status=$?
  if [ "$status" -eq 0 ]; then
    fail "$name" "expected a BLOCK, but the gate exited 0"
    return
  fi
  if ! printf '%s' "$out" | grep -qF "$needle"; then
    fail "$name" "blocked, but no message matched: ${needle}"
    printf '%s\n' "$out" | sed 's/^/        /'
    return
  fi
  pass "$name"
}

# Asserts the gate PASSED. Without these the suite could be satisfied by a gate
# that blocks on everything, which is not a working gate either.
expect_pass() {
  local name="$1" dir="$2" fn="$3"
  shift 3
  local out status
  out=$(run_gate "$dir" "$fn" "$@")
  status=$?
  if [ "$status" -ne 0 ]; then
    fail "$name" "expected a pass, but the gate exited ${status}"
    printf '%s\n' "$out" | sed 's/^/        /'
    return
  fi
  pass "$name"
}

# ---------------------------------------------------------------------------
# Fixture construction.
#
# A fixture is a ./TestResults tree shaped exactly like the one the download
# step produces: one subdirectory per artifact, trx and .listed records inside.
# The assemblies are reduced to two -- one sharded, one not -- because the
# controls are about the gate's LOGIC, not about the real ten. Every fixture
# passes the same reduced TEST_ASSEMBLIES/SHARD_ASSEMBLIES to the gate, so a
# control that blocks does so for the fault it planted.
# ---------------------------------------------------------------------------

FIX_ASSEMBLIES="Demo.Other.Tests Demo.Sharded.Tests"
FIX_SHARDS="Demo.Sharded.Tests:2"

# $1 path, $2 executed count.
write_trx() {
  mkdir -p "$(dirname "$1")"
  printf '<TestRun><ResultSummary><Counters total="%s" executed="%s" passed="%s" /></ResultSummary></TestRun>\n' \
    "$2" "$2" "$2" > "$1"
}

# $1 path, $2 tests=N header, rest: class names.
write_listed() {
  local path="$1" count="$2"
  shift 2
  mkdir -p "$(dirname "$path")"
  {
    printf 'tests=%s\n' "$count"
    printf '%s\n' "$@"
  } > "$path"
}

# A complete, correct fixture: the sharded assembly executes 10 + 6 = 16 against
# 16 discovered, and the unsharded one 20, for 36 in the run.
# $1 target dir.
make_good_fixture() {
  local d="$1"
  rm -rf "$d"
  mkdir -p "$d"
  write_trx "$d/TestResults/trx-O/Demo.Other.Tests.trx" 20
  write_trx "$d/TestResults/trx-A1/Demo.Sharded.Tests.shard1of2.trx" 10
  write_trx "$d/TestResults/trx-A2/Demo.Sharded.Tests.shard2of2.trx" 6
  write_listed "$d/TestResults/trx-A1/Demo.Sharded.Tests.shard1of2.listed" 16 \
    "Demo.Sharded.Tests.AlphaTests" "Demo.Sharded.Tests.BetaTests"
  write_listed "$d/TestResults/trx-A2/Demo.Sharded.Tests.shard2of2.listed" 16 \
    "Demo.Sharded.Tests.AlphaTests" "Demo.Sharded.Tests.BetaTests"
}

tmp_root=$(mktemp -d)
trap 'rm -rf "$tmp_root"' EXIT

gate_env=(
  "TEST_ASSEMBLIES=$FIX_ASSEMBLIES"
  "SHARD_ASSEMBLIES=$FIX_SHARDS"
  "BACKEND_FLOOR=36"
  "FLOOR_SOURCE=controls"
  "GITHUB_ENV=/dev/null"
)

echo "test-count gate positive controls"
echo "sourcing .github/scripts/test-count-gate.sh and .github/scripts/shard-filter.sh"
echo

# --- the happy path, so the suite is not satisfied by a gate that always blocks
d="$tmp_root/good"
make_good_fixture "$d"
expect_pass "complete trx set passes" "$d" check_trx_set "${gate_env[@]}"
expect_pass "sum == recorded passes" "$d" check_shard_records "${gate_env[@]}"
expect_pass "count at the floor passes" "$d" enforce_backend_floor "${gate_env[@]}"

# --- the exact-sum assertion, both directions (#182 controls)
d="$tmp_root/under"
make_good_fixture "$d"
write_trx "$d/TestResults/trx-A2/Demo.Sharded.Tests.shard2of2.trx" 5
expect_block "sum one UNDER blocks" "$d" check_shard_records \
  "fell into NEITHER shard" "${gate_env[@]}"

d="$tmp_root/over"
make_good_fixture "$d"
write_trx "$d/TestResults/trx-A2/Demo.Sharded.Tests.shard2of2.trx" 7
expect_block "sum one OVER blocks" "$d" check_shard_records \
  "ran in MORE THAN ONE shard" "${gate_env[@]}"

# --- the .listed record checks (#182 controls)
# Shard 1's record arrives twice, from two group directories, and shard 2's
# never arrives. Counting files alone passed this; naming the shards catches it.
d="$tmp_root/dupe"
make_good_fixture "$d"
rm "$d/TestResults/trx-A2/Demo.Sharded.Tests.shard2of2.listed"
write_listed "$d/TestResults/trx-A2/Demo.Sharded.Tests.shard1of2.listed" 16 \
  "Demo.Sharded.Tests.AlphaTests" "Demo.Sharded.Tests.BetaTests"
expect_block "duplicate same-shard record blocks naming the missing shard" \
  "$d" check_shard_records "Demo.Sharded.Tests.shard2of2.listed" "${gate_env[@]}"

d="$tmp_root/disagree"
make_good_fixture "$d"
write_listed "$d/TestResults/trx-A2/Demo.Sharded.Tests.shard2of2.listed" 15 \
  "Demo.Sharded.Tests.AlphaTests" "Demo.Sharded.Tests.BetaTests"
expect_block "recorded counts disagreeing block" "$d" check_shard_records \
  "did NOT partition the same discovered list" "${gate_env[@]}"

d="$tmp_root/nohdr"
make_good_fixture "$d"
for s in "trx-A1/Demo.Sharded.Tests.shard1of2" "trx-A2/Demo.Sharded.Tests.shard2of2"; do
  printf '%s\n' "Demo.Sharded.Tests.AlphaTests" "Demo.Sharded.Tests.BetaTests" \
    > "$d/TestResults/${s}.listed"
done
expect_block "missing tests= header blocks" "$d" check_shard_records \
  "carries no parseable 'tests=N' header line" "${gate_env[@]}"

# LOW-1. The header sits on line 2 with a class name above it. The old parse
# scanned the whole file and accepted it; the line-1 parse must block.
d="$tmp_root/hdr2"
make_good_fixture "$d"
for s in "trx-A1/Demo.Sharded.Tests.shard1of2" "trx-A2/Demo.Sharded.Tests.shard2of2"; do
  printf '%s\ntests=16\n%s\n' \
    "Demo.Sharded.Tests.AlphaTests" "Demo.Sharded.Tests.BetaTests" \
    > "$d/TestResults/${s}.listed"
done
expect_block "tests= header on line 2 blocks (LOW-1)" "$d" check_shard_records \
  "carries no parseable 'tests=N' header line" "${gate_env[@]}"

# LOW-2. A shard trx with no readable executed= must be named as a FILE fault,
# not folded into the sum as a zero and reported as an incomplete partition.
# Both halves are asserted: the file is named, AND the partition message is
# absent -- without the second half this control would pass on the old
# behaviour, which also blocked, just with the wrong cause.
d="$tmp_root/badtrx"
make_good_fixture "$d"
printf '<TestRun><ResultSummary/></TestRun>\n' \
  > "$d/TestResults/trx-A2/Demo.Sharded.Tests.shard2of2.trx"
out=$(run_gate "$d" check_shard_records "${gate_env[@]}")
if printf '%s' "$out" | grep -qF "carries no parseable executed= figure" \
  && printf '%s' "$out" | grep -qF "Demo.Sharded.Tests.shard2of2.trx"; then
  if printf '%s' "$out" | grep -qF "fell into NEITHER shard"; then
    fail "unparseable trx blocks by name (LOW-2)" "reported the partition, not the file"
  else
    pass "unparseable trx blocks by name (LOW-2)"
  fi
else
  fail "unparseable trx blocks by name (LOW-2)" "did not name the file as the fault"
  printf '%s\n' "$out" | sed 's/^/        /'
fi

# --- the trx set check
d="$tmp_root/missingtrx"
make_good_fixture "$d"
rm "$d/TestResults/trx-A2/Demo.Sharded.Tests.shard2of2.trx"
expect_block "a missing shard trx blocks by name" "$d" check_trx_set \
  "Demo.Sharded.Tests.shard2of2" "${gate_env[@]}"

d="$tmp_root/duptrx"
make_good_fixture "$d"
write_trx "$d/TestResults/trx-O/Demo.Sharded.Tests.shard1of2.trx" 10
expect_block "the same trx basename twice blocks" "$d" check_trx_set \
  "Duplicated (same basename across group directories)" "${gate_env[@]}"

# --- the ratchet floor
# An unparseable trx in the WHOLE-RUN sum must be named too, not folded in as a
# zero (arb-4f1). The unsharded assembly is the target here: check_shard_records
# never looks at it, so only enforce_backend_floor can catch this one. Asserted
# both ways -- the file is named, AND the floor message is absent, since a trx
# counted as zero would drop the total below the floor and report a shrink that
# never happened.
d="$tmp_root/badtrx_run"
make_good_fixture "$d"
printf '<TestRun><ResultSummary/></TestRun>\n' \
  > "$d/TestResults/trx-O/Demo.Other.Tests.trx"
out=$(run_gate "$d" enforce_backend_floor "${gate_env[@]}")
if printf '%s' "$out" | grep -qF "carries no parseable executed= figure" \
  && printf '%s' "$out" | grep -qF "Demo.Other.Tests.trx"; then
  if printf '%s' "$out" | grep -qF "is below master's last measured count"; then
    fail "unparseable trx in the whole-run sum blocks by name" "reported a floor breach, not the file"
  else
    pass "unparseable trx in the whole-run sum blocks by name"
  fi
else
  fail "unparseable trx in the whole-run sum blocks by name" "did not name the file as the fault"
  printf '%s\n' "$out" | sed 's/^/        /'
fi

d="$tmp_root/floor"
make_good_fixture "$d"
expect_block "a count below the floor blocks" "$d" enforce_backend_floor \
  "is below master's last measured count" \
  "TEST_ASSEMBLIES=$FIX_ASSEMBLIES" "SHARD_ASSEMBLIES=$FIX_SHARDS" \
  "BACKEND_FLOOR=37" "FLOOR_SOURCE=controls" "GITHUB_ENV=/dev/null"

# ---------------------------------------------------------------------------
# The shard step's own discovery checks. These drive shard_filter's guards by
# stubbing `dotnet` on PATH -- the logic under test is the PARSE and the
# assertions on it, not dotnet itself, and a stub is what lets a control plant a
# listing no real assembly would produce.
# ---------------------------------------------------------------------------

# $1 fixture dir, rest: the lines the stub `dotnet` prints as its listing.
run_shard_filter() {
  local d="$1"
  shift
  rm -rf "$d"
  mkdir -p "$d/bin" "$d/TestResults"
  printf '#!/usr/bin/env bash\ncat "$0.listing"\n' > "$d/bin/dotnet"
  printf '%s\n' "$@" > "$d/bin/dotnet.listing"
  chmod +x "$d/bin/dotnet"
  (
    cd "$d" || exit 99
    PATH="$d/bin:$PATH"
    export PATH
    set -euo pipefail
    export TEST_FILTER="Category!=Timing"
    # shellcheck source=/dev/null
    . "$repo_root/.github/scripts/shard-filter.sh"
    shard_filter "Demo.Sharded.Tests.dll" 1 2 "Demo.Sharded.Tests"
  ) 2>&1
}

shard_expect_block() {
  local name="$1" needle="$2"
  shift 2
  local out status
  out=$(run_shard_filter "$tmp_root/sf" "$@")
  status=$?
  if [ "$status" -eq 0 ]; then
    fail "$name" "expected a BLOCK, but shard_filter exited 0"
    return
  fi
  if ! printf '%s' "$out" | grep -qF "$needle"; then
    fail "$name" "blocked, but no message matched: ${needle}"
    printf '%s\n' "$out" | sed 's/^/        /'
    return
  fi
  pass "$name"
}

# Nested (`+`) and generic (backtick) class names must survive the parse as
# CLASSES. If the character class dropped either, the name truncates there, the
# following `sed 's/\.[^.]*$//'` strips the last surviving segment, and what is
# left is the bare namespace -- which as a ~ term matches the whole assembly.
out=$(run_shard_filter "$tmp_root/sf" \
  "    Demo.Sharded.Tests.Outer+Nested.RunsIt" \
  "    Demo.Sharded.Tests.GenericTests\`1.RunsIt" \
  "    Demo.Sharded.Tests.PlainTests.RunsIt")
status=$?
if [ "$status" -ne 0 ]; then
  fail "nested and generic class names parse" "shard_filter blocked on a valid listing"
  printf '%s\n' "$out" | sed 's/^/        /'
elif printf '%s' "$out" | grep -qF 'FullyQualifiedName~Demo.Sharded.Tests.Outer+Nested.' \
  || printf '%s' "$out" | grep -qF 'FullyQualifiedName~Demo.Sharded.Tests.GenericTests`1.'; then
  pass "nested and generic class names parse (not a namespace wildcard)"
else
  fail "nested and generic class names parse" "the emitted terms are not the full class names"
  printf '%s\n' "$out" | sed 's/^/        /'
fi

# A name that truncates to the bare namespace must be BLOCKED by name, never
# treated as a wildcard term matching every test in the assembly.
shard_expect_block "a bare-namespace token blocks by name" \
  "parsed name(s) that are not a class under" \
  "    Demo.Sharded.Tests.RunsIt"

# Termination. `FullyQualifiedName~<Class>` is a substring match, so a class
# whose name is a prefix of another would land in both shards. The emitted term
# carries a trailing dot, which `ClassV2.` cannot match.
out=$(run_shard_filter "$tmp_root/sf" \
  "    Demo.Sharded.Tests.AuthTests.RunsIt" \
  "    Demo.Sharded.Tests.AuthTestsV2.RunsIt")
if printf '%s' "$out" | grep -qF 'FullyQualifiedName~Demo.Sharded.Tests.AuthTests.'; then
  pass "a prefix class name does not collide (terminated term)"
else
  fail "a prefix class name does not collide" "the emitted term is not dot-terminated"
  printf '%s\n' "$out" | sed 's/^/        /'
fi

# Zero discovered tests must fail closed. `sum == 0` is a condition a run that
# executed nothing satisfies, so recording it would make the exact-sum
# assertion vacuous -- the one way it could be worse than the ratchet alone.
shard_expect_block "zero discovered tests blocks" \
  "discovered no test classes in" \
  ""

# ---------------------------------------------------------------------------
# Unset-input BLOCKs (arb-2nx). Each of these unsets exactly one of the
# variables the gate functions read and requires a named BLOCK naming that
# variable -- and, since these fire before any other output, that no LATER
# message (a later assertion, the floor line) also appears, which is what
# distinguishes "blocked here, before anything else ran" from "blocked, but
# only after limping partway through."
# ---------------------------------------------------------------------------

# $1 name, $2 fixture dir, $3 function, $4 unset var, $5 later-message needle
# that must be ABSENT, rest: env assignments (the ones that remain set).
expect_block_unset() {
  local name="$1" dir="$2" fn="$3" unset_var="$4" absent_needle="$5"
  shift 5
  local out status
  out=$( (
    cd "$dir" || exit 99
    set -euo pipefail
    # shellcheck source=/dev/null
    . "$repo_root/.github/scripts/test-count-gate.sh"
    unset "$unset_var" || true
    export "$@"
    "$fn"
  ) 2>&1 )
  status=$?
  if [ "$status" -eq 0 ]; then
    fail "$name" "expected a BLOCK, but the gate exited 0"
    return
  fi
  if ! printf '%s' "$out" | grep -qF "BLOCKED: ${fn} requires ${unset_var} to be set"; then
    fail "$name" "blocked, but did not name ${unset_var}"
    printf '%s\n' "$out" | sed 's/^/        /'
    return
  fi
  if [ -n "$absent_needle" ] && printf '%s' "$out" | grep -qF "$absent_needle"; then
    fail "$name" "blocked, but a later message also fired: ${absent_needle}"
    printf '%s\n' "$out" | sed 's/^/        /'
    return
  fi
  pass "$name"
}

d="$tmp_root/unset"
make_good_fixture "$d"

expect_block_unset "check_trx_set blocks on unset TEST_ASSEMBLIES" "$d" check_trx_set \
  "TEST_ASSEMBLIES" "" \
  "SHARD_ASSEMBLIES=$FIX_SHARDS" "BACKEND_FLOOR=36" "FLOOR_SOURCE=controls" "GITHUB_ENV=/dev/null"

expect_block_unset "check_trx_set blocks on unset SHARD_ASSEMBLIES" "$d" check_trx_set \
  "SHARD_ASSEMBLIES" "" \
  "TEST_ASSEMBLIES=$FIX_ASSEMBLIES" "BACKEND_FLOOR=36" "FLOOR_SOURCE=controls" "GITHUB_ENV=/dev/null"

expect_block_unset "check_shard_records blocks on unset SHARD_ASSEMBLIES" "$d" check_shard_records \
  "SHARD_ASSEMBLIES" "" \
  "TEST_ASSEMBLIES=$FIX_ASSEMBLIES" "BACKEND_FLOOR=36" "FLOOR_SOURCE=controls" "GITHUB_ENV=/dev/null"

expect_block_unset "enforce_backend_floor blocks on unset BACKEND_FLOOR" "$d" enforce_backend_floor \
  "BACKEND_FLOOR" "is below master's last measured count" \
  "TEST_ASSEMBLIES=$FIX_ASSEMBLIES" "SHARD_ASSEMBLIES=$FIX_SHARDS" "FLOOR_SOURCE=controls" "GITHUB_ENV=/dev/null"

expect_block_unset "enforce_backend_floor blocks on unset FLOOR_SOURCE" "$d" enforce_backend_floor \
  "FLOOR_SOURCE" "is below master's last measured count" \
  "TEST_ASSEMBLIES=$FIX_ASSEMBLIES" "SHARD_ASSEMBLIES=$FIX_SHARDS" "BACKEND_FLOOR=36" "GITHUB_ENV=/dev/null"

expect_block_unset "enforce_backend_floor blocks on unset GITHUB_ENV" "$d" enforce_backend_floor \
  "GITHUB_ENV" "Executed test count:" \
  "TEST_ASSEMBLIES=$FIX_ASSEMBLIES" "SHARD_ASSEMBLIES=$FIX_SHARDS" "BACKEND_FLOOR=36" "FLOOR_SOURCE=controls"

# shard_filter's TEST_FILTER, via the same dotnet-stub harness as the other
# shard_filter controls above.
out=$( (
  d2="$tmp_root/sf-unset"
  rm -rf "$d2"
  mkdir -p "$d2/bin" "$d2/TestResults"
  printf '#!/usr/bin/env bash\ncat "$0.listing"\n' > "$d2/bin/dotnet"
  printf '    Demo.Sharded.Tests.PlainTests.RunsIt\n' > "$d2/bin/dotnet.listing"
  chmod +x "$d2/bin/dotnet"
  cd "$d2" || exit 99
  PATH="$d2/bin:$PATH"
  export PATH
  set -euo pipefail
  unset TEST_FILTER || true
  # shellcheck source=/dev/null
  . "$repo_root/.github/scripts/shard-filter.sh"
  shard_filter "Demo.Sharded.Tests.dll" 1 2 "Demo.Sharded.Tests"
) 2>&1 )
status=$?
if [ "$status" -eq 0 ]; then
  fail "shard_filter blocks on unset TEST_FILTER" "expected a BLOCK, but shard_filter exited 0"
elif ! printf '%s' "$out" | grep -qF "BLOCKED: shard_filter requires TEST_FILTER to be set"; then
  fail "shard_filter blocks on unset TEST_FILTER" "did not name TEST_FILTER"
  printf '%s\n' "$out" | sed 's/^/        /'
else
  pass "shard_filter blocks on unset TEST_FILTER"
fi

echo
echo "controls: ${pass_count} passed, ${fail_count} failed"
[ "$fail_count" -eq 0 ] || exit 1
