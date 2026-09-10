#!/usr/bin/env bash
# Sourced by the gate job's "Enforce test-count floor" step in
# .github/workflows/build-test.yml. It is a SCRIPT rather than inline workflow
# bash so the positive controls in test-count-gate-controls.sh invoke the
# shipped code itself, not a re-typed copy of it -- see
# docs/standards/process.md. The workflow's `source` therefore runs exactly the
# text the controls run, and a control that passes is evidence about CI.
#
# Expects in the environment: TEST_ASSEMBLIES, SHARD_ASSEMBLIES, BACKEND_FLOOR,
# FLOOR_SOURCE, GITHUB_ENV. Reads ./TestResults/*/ relative to the CWD. Callers
# set `set -euo pipefail` themselves; this file declares functions only and runs
# nothing at source time.
#
# The three functions run in this order and are NOT independent: check_trx_set
# proves the trx basename set is exactly right, check_shard_records proves each
# sharded assembly's shards partitioned one list and executed its sum exactly,
# and enforce_backend_floor ratchets the whole run. Splitting them into
# functions is a packaging change only -- each body is the workflow text
# verbatim.

# Asserts the set of trx basenames under ./TestResults/*/ is exactly the set
# TEST_ASSEMBLIES and SHARD_ASSEMBLIES imply, each appearing exactly once.
check_trx_set() {

  # Assert the trx SET by name, not just the count (arb-6ke). A
  # misspelled matrix.assemblies entry plus a duplicate across groups
  # can still yield ten files -- the count check that used to run here
  # would pass over a different test set than the floor was measured
  # over. Compare sorted basenames against TEST_ASSEMBLIES (the same
  # hoisted list the prep-job assertion uses, so the two cannot drift
  # apart) and require every basename to appear EXACTLY ONCE across
  # ./TestResults/*/  -- a duplicate is as much a loss as a missing
  # file, since it means some other assembly's trx never arrived.
  #
  # Two levels: the artifacts are NOT flattened (see the download step),
  # so each group's trx live under ./TestResults/trx-<group>/. The glob
  # is quoted only where expanded into a command argument; an empty
  # match here still enters the loop body once with a literal
  # non-existent path, which `[ -f ]` filters out -- so a run with zero
  # trx falls through to an empty `found` list and fails the "missing"
  # branch below by naming all ten, rather than passing vacuously.
  found_list=""
  for trx in ./TestResults/*/*.trx; do
    [ -f "$trx" ] || continue
    base=$(basename "$trx" .trx)
    found_list="${found_list}${base}"$'\n'
  done

  # The expected BASENAMES are not TEST_ASSEMBLIES verbatim once an
  # assembly is sharded (arb-adm). A sharded assembly yields N trx named
  # `<Assembly>.shard<k>of<N>` for k=1..N and NO bare `<Assembly>.trx`;
  # every other assembly still yields exactly one `<Assembly>.trx`. Both
  # kinds must still appear exactly once, so the missing / unexpected /
  # duplicate branches below keep working unchanged and keep naming the
  # culprit -- a lone shard 1 is reported as a missing
  # `Arbitarr.Integration.Tests.shard2of2`, not as a silent short count.
  #
  # This list is derived from SHARD_ASSEMBLIES, the same allow-list the
  # guards job checks the matrix against, so the gate and the guard
  # cannot disagree about which assemblies are sharded.
  expected_list=""
  for a in $TEST_ASSEMBLIES; do
    shards=1
    for tok in $SHARD_ASSEMBLIES; do
      [ "${tok%%:*}" = "$a" ] && shards="${tok##*:}"
    done
    if [ "$shards" -eq 1 ]; then
      expected_list="${expected_list}${a}"$'\n'
    else
      for k in $(seq 1 "$shards"); do
        expected_list="${expected_list}${a}.shard${k}of${shards}"$'\n'
      done
    fi
  done
  expected_sorted=$(printf '%s' "$expected_list" | sed '/^$/d' | sort)
  # found_sorted is a bare `sort`, NOT `sort -u`, and that is load-bearing,
  # not an inconsistency to tidy toward the `sort -u` calls below. Keeping
  # duplicates in found_sorted is what lets `uniq -d` (below) detect a
  # basename appearing twice across ./TestResults/*/ -- a duplicate that
  # `sort -u` would silently collapse to one occurrence before it was ever
  # visible. missing_names/unexpected_names then apply `sort -u` on top of
  # this same variable because THEY are set comparisons (present vs absent),
  # where a duplicate would only cause `comm` to double-count a line it
  # should compare once. Unifying the two functions onto one sort silently
  # drops either duplicate detection or the set comparison, depending on
  # which way it's "fixed".
  found_sorted=$(printf '%s' "$found_list" | sed '/^$/d' | sort)

  missing_names=$(comm -23 <(printf '%s\n' "$expected_sorted") <(printf '%s\n' "$found_sorted" | sort -u))
  unexpected_names=$(comm -13 <(printf '%s\n' "$expected_sorted") <(printf '%s\n' "$found_sorted" | sort -u))
  duplicate_names=$(printf '%s\n' "$found_sorted" | uniq -d)

  if [ -n "$missing_names" ] || [ -n "$unexpected_names" ] || [ -n "$duplicate_names" ]; then
    echo "BLOCKED: trx set does not match the expected test result names." >&2
    echo "(Ten assemblies; a sharded one contributes <Assembly>.shard<k>of<N> per shard.)" >&2
    [ -n "$missing_names" ] && { echo "Missing (expected, no trx found):" >&2; echo "$missing_names" | sed 's/^/  /' >&2; }
    [ -n "$unexpected_names" ] && { echo "Unexpected (trx found, not in TEST_ASSEMBLIES):" >&2; echo "$unexpected_names" | sed 's/^/  /' >&2; }
    [ -n "$duplicate_names" ] && { echo "Duplicated (same basename across group directories):" >&2; echo "$duplicate_names" | sed 's/^/  /' >&2; }
    echo "Files present:" >&2
    ls -1 ./TestResults/*/*.trx >&2 2>/dev/null || echo "(none)" >&2
    exit 1
  fi

  echo "All expected trx basenames present exactly once: $(printf '%s\n' "$expected_sorted" | tr '\n' ' ')"
}

# Asserts, per sharded assembly, that every declared shard filed exactly one
# byte-identical .listed record and that the shards' summed executed= equals the
# discovered count that record carries.
check_shard_records() {

  # Completeness of a shard split, proven independently of the ratchet
  # (arb-adm, tightened by arb-u9i). Each shard job recorded the SORTED
  # CLASS LIST it partitioned, plus the discovered TEST count as a
  # `tests=N` header line, to <Assembly>.shard<k>of<N>.listed. Both
  # shards derive their slice from one `--list-tests` run under the same
  # filter, so every copy of that file must be byte-identical; comparing
  # the LISTS rather than their lengths is what makes this a positive
  # control on the derivation itself, since equal counts over different
  # membership would compare equal on a number. The header line rides
  # INSIDE the compared bytes on purpose, so the same `cmp` also proves
  # the shards agree on the count.
  #
  # A disagreement means the two jobs discovered different test sets --
  # the failure that would otherwise surface only as a ratchet drop with
  # no cause attached.
  #
  # The expected FILENAMES are derived from SHARD_ASSEMBLIES exactly as
  # the trx basenames were above, and each must appear exactly once
  # (arb-u9i). Counting files alone did not do this: before the shard
  # suffix existed, two copies of the SAME shard's record -- an artifact
  # restored twice, a re-run overlaying -- passed both the count check
  # and the byte-identity check while shard 2 was never heard from.
  for tok in $SHARD_ASSEMBLIES; do
    asm="${tok%%:*}"
    shards="${tok##*:}"

    # Collect the copies WITHOUT deduplicating: a basename arriving twice
    # is a real condition (two group directories), and it is named below
    # rather than collapsed by a `sort -u` before anyone can see it.
    listed_files=""
    for f in ./TestResults/*/"${asm}".shard*of*.listed; do
      [ -f "$f" ] || continue
      listed_files="${listed_files}${f}"$'\n'
    done
    # `printf '%s\n'`, not `printf '%s'`: wc -l counts NEWLINES, so a
    # trailing-newline-less list of N paths counts N-1 and a single path
    # counts 0. That undercount made this step report "1 of 2 records
    # arrived" while listing both files it had just found.
    listed_files=$(printf '%s\n' "$listed_files" | sed '/^$/d')
    file_count=$(printf '%s\n' "$listed_files" | sed '/^$/d' | wc -l | tr -d ' ')

    if [ "$file_count" -eq 0 ]; then
      echo "BLOCKED: no ${asm}.shard<k>of<N>.listed record arrived from any shard job." >&2
      echo "That file is the completeness evidence for the shard split; without it the" >&2
      echo "only thing standing between a lost shard and a green run is the ratchet." >&2
      exit 1
    fi

    # One record per DECLARED shard, BY NAME. Derived from
    # SHARD_ASSEMBLIES the same way the expected trx basenames were, so
    # the two cannot disagree about which shards are supposed to exist.
    listed_expected=""
    for k in $(seq 1 "$shards"); do
      listed_expected="${listed_expected}${asm}.shard${k}of${shards}.listed"$'\n'
    done
    listed_expected=$(printf '%s' "$listed_expected" | sed '/^$/d' | sort)
    listed_found=$(printf '%s\n' "$listed_files" | sed 's#.*/##' | sort)

    listed_missing=$(comm -23 <(printf '%s\n' "$listed_expected") <(printf '%s\n' "$listed_found" | sort -u))
    listed_unexpected=$(comm -13 <(printf '%s\n' "$listed_expected") <(printf '%s\n' "$listed_found" | sort -u))
    listed_dupes=$(printf '%s\n' "$listed_found" | uniq -d)

    if [ -n "$listed_missing" ] || [ -n "$listed_unexpected" ] || [ -n "$listed_dupes" ]; then
      echo "BLOCKED: ${asm} is declared as ${shards} shards but its class-list records do" >&2
      echo "not match one per declared shard. Each shard job writes exactly one." >&2
      [ -n "$listed_missing" ] && { echo "Missing (declared shard, no record):" >&2; echo "$listed_missing" | sed 's/^/  /' >&2; }
      [ -n "$listed_unexpected" ] && { echo "Unexpected (record found, not a declared shard):" >&2; echo "$listed_unexpected" | sed 's/^/  /' >&2; }
      [ -n "$listed_dupes" ] && { echo "Duplicated (same shard's record across group directories):" >&2; echo "$listed_dupes" | sed 's/^/  /' >&2; }
      printf '%s\n' "$listed_files" | sed 's/^/  /' >&2
      exit 1
    fi

    # Byte-identical comparison against the first copy. This covers the
    # `tests=N` header as well as the class list.
    reference=$(printf '%s\n' "$listed_files" | head -1)
    for f in $listed_files; do
      if ! cmp -s "$reference" "$f"; then
        echo "BLOCKED: the shards of ${asm} did NOT partition the same discovered list," >&2
        echo "or did not agree on the discovered test count." >&2
        echo "${reference} and ${f} differ. Both shards run --list-tests against the same" >&2
        echo "dll under the same filter, so these must be byte-identical; a difference" >&2
        echo "means tests may have fallen into neither shard." >&2
        echo "Recorded counts: $(sed -n 's/^tests=//p' "$reference" | head -1) vs $(sed -n 's/^tests=//p' "$f" | head -1)" >&2
        echo "Classes in ${reference} but not ${f}:" >&2
        comm -23 <(sed '1d' "$reference") <(sed '1d' "$f") | sed 's/^/  /' >&2
        echo "Classes in ${f} but not ${reference}:" >&2
        comm -13 <(sed '1d' "$reference") <(sed '1d' "$f") | sed 's/^/  /' >&2
        exit 1
      fi
    done

    # The header is parsed only AFTER the copies were proven identical,
    # so this reads a value every shard agreed on.
    #
    # LINE 1 ONLY (arb-4f1, #182 LOW-1). The old parse scanned the whole file
    # for the first line matching `tests=N` and took that. The rest of the file
    # is the CLASS LIST, and a class named `tests=123` -- or any future record
    # line of that shape -- would be read as the discovered count from wherever
    # it sat, while the readers below (`sed '1d'`) go on stripping line 1 as the
    # header. The count and the list would then describe different things, and
    # the exact-sum assertion would compare against a number no discovery
    # produced. The writer puts the header on line 1, so the reader requires it
    # there: this matches nothing when line 1 is a class name, and the
    # empty-$recorded branch below blocks instead of reading further down.
    recorded=$(sed -n '1{s/^tests=\([0-9][0-9]*\)$/\1/p;}' "$reference")
    if [ -z "$recorded" ]; then
      echo "BLOCKED: ${reference} carries no parseable 'tests=N' header line." >&2
      echo "The shard step writes it as the first line; without it there is no discovered" >&2
      echo "count to assert the executed sum against, and this fails rather than quietly" >&2
      echo "skipping the check." >&2
      exit 1
    fi

    echo "${asm}: all ${file_count} shards partitioned an identical list of $(sed '1d' "$reference" | wc -l | tr -d ' ') discovered test classes, and recorded ${recorded} discovered tests."

    # EXACT sum (arb-u9i). The ratchet below is a LOWER bound on the
    # whole run and cannot see an over-count: a test running in two
    # shards ADDS to the total, over-satisfying the floor instead of
    # tripping it. This asserts the design intent directly and in BOTH
    # directions, per assembly, against the number that assembly's own
    # discovery produced. The floor check stays as it is -- it ratchets
    # the whole run, including the unsharded assemblies this cannot see.
    #
    # This loop sums whatever trx it finds and does NOT check that it found one
    # per shard -- it relies on check_trx_set having already proven the trx
    # basename set is exactly right, and on the .listed checks above having
    # proven one record per declared shard (arb-4f1, #182 LOW-3). Both run
    # BEFORE this, and the gate step invokes them in that order for this reason.
    # Read on its own the loop looks like it would under-count a missing shard
    # and report UNDER; in place it cannot be reached with a shard missing,
    # because the set check names it first. Reordering the calls, or invoking
    # this function alone, re-opens that.
    shard_sum=0
    for k in $(seq 1 "$shards"); do
      for trx in ./TestResults/*/"${asm}.shard${k}of${shards}.trx"; do
        [ -f "$trx" ] || continue
        # `|| true` so a no-match does not end the step here. Under
        # `set -euo pipefail` the assignment ITSELF carries the pipeline's
        # status, so without it a trx with no executed= figure aborts this
        # function at this line -- before the check below can name it, and
        # with no message at all. That was true of the previous
        # `${c:-0}` form too: the default could never be reached, because
        # the assignment that would have needed it had already killed the
        # step. Tolerating the failure here is what lets the fault be
        # REPORTED rather than merely fatal.
        c=$(grep -o 'executed="[0-9]*"' "$trx" | head -1 | grep -o '[0-9]*') || true
        # BLOCK by name on an unparseable trx (arb-4f1, #182 LOW-2). `${c:-0}`
        # used to fold a trx with no readable executed= figure into the sum as a
        # zero, so a truncated or malformed result file was reported as the
        # shards executing FEWER tests than were discovered -- the UNDER message,
        # which says "a test fell into NEITHER shard" and sends the reader to the
        # partition. The partition would be fine and the file would be the fault.
        # A count that could not be read is not a count of zero, so name the file
        # here where the cause is visible.
        if ! printf '%s' "$c" | grep -qE '^[0-9]+$'; then
          echo "BLOCKED: ${trx} carries no parseable executed= figure." >&2
          echo "Its <ResultSummary><Counters/> element is missing or malformed, so this shard's" >&2
          echo "executed count cannot be read. Treating it as 0 would report the shards as" >&2
          echo "having executed fewer tests than were discovered -- an incomplete PARTITION --" >&2
          echo "when the fault is this file. Blocking by name instead." >&2
          exit 1
        fi
        shard_sum=$((shard_sum + c))
      done
    done

    if [ "$shard_sum" -lt "$recorded" ]; then
      echo "BLOCKED: the shards of ${asm} executed ${shard_sum} tests but ${recorded} were" >&2
      echo "discovered. A test fell into NEITHER shard -- the partition is incomplete." >&2
      exit 1
    fi
    if [ "$shard_sum" -gt "$recorded" ]; then
      echo "BLOCKED: the shards of ${asm} executed ${shard_sum} tests but only ${recorded}" >&2
      echo "were discovered. A test ran in MORE THAN ONE shard -- the shard filters" >&2
      echo "overlap, and the surplus inflates the run total PAST the ratchet floor" >&2
      echo "instead of tripping it." >&2
      exit 1
    fi

    echo "${asm}: shard executed sum ${shard_sum} == ${recorded} discovered tests."
  done
}

# Sums executed= across every trx and ratchets it against master's last measured
# count. Sets BACKEND_COUNT in $GITHUB_ENV.
enforce_backend_floor() {
  local floor="$BACKEND_FLOOR"
  local total=0

  for trx in ./TestResults/*/*.trx; do
    [ -f "$trx" ] || continue
    # Each .trx's <ResultSummary><Counters .../> element carries both "total" (includes
    # skipped tests) and "executed" (tests that actually ran). Use "executed" so a
    # skipped test can't be counted toward the floor without actually running.
    count=$(grep -o 'executed="[0-9]*"' "$trx" | head -1 | grep -o '[0-9]*')
    count=${count:-0}
    total=$((total + count))
  done

  echo "Executed test count: $total (floor: $floor)"
  echo "Backend floor source: $FLOOR_SOURCE"
  # Recorded here rather than at the end of the job so the count is written
  # even if a later step fails: the ratchet's input is what this run
  # measured, not whether every other gate happened to pass.
  echo "BACKEND_COUNT=$total" >> "$GITHUB_ENV"
  if [ "$total" -lt "$floor" ]; then
    echo "BLOCKED: executed test count ($total) is below master's last measured count ($floor, from $FLOOR_SOURCE)." >&2
    exit 1
  fi

}
