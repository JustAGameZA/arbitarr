#!/usr/bin/env bash
# scripts/test-affected.sh -- run only the test assemblies a change can reach.
#
# THIS SCRIPT IS NEVER REFERENCED BY ANY WORKFLOW. CI runs the full suite so that
# a mapping bug in this script cannot narrow what CI checks. It is the LOCAL lane
# described in docs/standards/process.md ("Lanes"); the PR/master lane is
# .github/workflows/build-test.yml and the nightly lane is nightly.yml.
#
# What it does
#   1. Collects the changed paths (a git range, the index, or paths you name).
#   2. Maps each path to its owning project (src/<X>/ or tests/<X>/).
#   3. Builds the project-reference graph from the .csproj files ON EVERY RUN --
#      nothing about the solution's shape is hard-coded here -- and selects every
#      tests/*.Tests project that references a changed project transitively.
#      A change under tests/<X>.Tests/ selects that project itself; a change to
#      tests/Arbitarr.TestSupport selects every test project that references it.
#   4. A change OUTSIDE src/ and tests/ (workflow, docs, the .sln, a root props
#      file) selects EVERY backend test project and says why. A change under
#      src/Arbitarr.Web selects the frontend only.
#   5. Runs the selection, or with --dry-run just prints it. Exits non-zero if
#      any selected run fails.
#
# Usage
#   scripts/test-affected.sh [--dry-run] [--no-build]
#                            [<git-range> | --staged | -- <path>...]
#
#   default      origin/master...HEAD, plus uncommitted and untracked changes
#                (everything not yet on master, whether committed or not)
#   <git-range>  any range `git diff --name-only` accepts, e.g. HEAD~3..HEAD
#   --staged     the index only (`git diff --cached`)
#   -- <path>... explicit paths; nothing is read from git. This is how the
#                mapping is exercised against synthetic inputs.
#   --dry-run    print the selection and the commands, run nothing
#   --no-build   skip the solution build before `dotnet test --no-build`
#
# Why the backend runs `dotnet build Arbitarr.sln` first, then each selected
# project with --no-build: Arbitarr.Architecture.Tests reads SIBLING build
# output -- the Mono.Cecil IL scans (TestProcessGlobalStateTests,
# ProductionProcessGlobalStateTests, NoInlineDatabaseConnectionStringsTests)
# walk to tests/*/bin and src/*/bin and fail LOUDLY when an assembly is missing
# rather than passing over an empty set. `dotnet test <one csproj>` builds only
# that project's closure, so Architecture.Tests run on its own would always
# fail. Building the solution once (incrementally -- cheap when nothing
# changed) is what build-test.yml's `prep` job does for the same reason.
#
# The filter below is the PR lane's. It is COPIED VERBATIM from
# .github/workflows/build-test.yml (workflow-level `TEST_FILTER`); if the two
# ever differ, THIS SCRIPT is wrong, not the workflow. Timing, Load and
# Quarantine run unfiltered in nightly.yml.
#
# -m:1 is kept on `dotnet test` because CLAUDE.md section 4 still requires it;
# retiring it is bead arb-8qw and is not this script's call.
#
# Portability: bash 4+, git, awk, sed, grep, sort, find. Runs under git-bash on
# Windows and under Linux. No python3, no bc, no GNU-only flags.

set -euo pipefail

TEST_FILTER='Category!=Timing&Category!=Load&Category!=Quarantine'
BASE_REF='origin/master'
FRONTEND_DIR='src/Arbitarr.Web'
SOLUTION='Arbitarr.sln'

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

# ----------------------------------------------------------------------------
# Arguments
# ----------------------------------------------------------------------------
dry_run=0
no_build=0
mode='range'
range=''
explicit_paths=()

while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) dry_run=1 ;;
    --no-build) no_build=1 ;;
    --staged) mode='staged' ;;
    -h|--help)
      sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    --)
      shift
      mode='paths'
      explicit_paths=("$@")
      break
      ;;
    -*)
      echo "test-affected: unknown option '$1'" >&2
      exit 2
      ;;
    *)
      if [ -n "$range" ]; then
        echo "test-affected: only one range may be given (got '$range' and '$1')" >&2
        exit 2
      fi
      range="$1"
      ;;
  esac
  shift
done

if [ "$mode" = 'paths' ] && [ "${#explicit_paths[@]}" -eq 0 ]; then
  echo "test-affected: '--' must be followed by at least one path" >&2
  exit 2
fi

# ----------------------------------------------------------------------------
# 1. Changed paths
# ----------------------------------------------------------------------------
changed_file=$(mktemp)
graph_file=$(mktemp)
projects_file=$(mktemp)
trap 'rm -f "$changed_file" "$changed_file.n" "$graph_file" "$projects_file"' EXIT

vitest_since=''

case "$mode" in
  paths)
    printf '%s\n' "${explicit_paths[@]}" > "$changed_file"
    source_desc="${#explicit_paths[@]} explicit path(s)"
    ;;
  staged)
    git diff --cached --name-only --diff-filter=ACMRD > "$changed_file"
    source_desc='the index (git diff --cached)'
    # vitest --changed with no argument means "since the last commit", which is
    # the closest match to the index.
    vitest_since=''
    ;;
  range)
    if [ -n "$range" ]; then
      git diff --name-only --diff-filter=ACMRD "$range" > "$changed_file"
      source_desc="range $range"
      # The left side of the range is what vitest should diff against.
      vitest_since=$(git rev-parse "${range%%..*}" 2>/dev/null || true)
    else
      if ! git rev-parse --verify -q "$BASE_REF" >/dev/null; then
        echo "test-affected: '$BASE_REF' is not a known ref; run 'git fetch origin' or pass a range" >&2
        exit 2
      fi
      merge_base=$(git merge-base "$BASE_REF" HEAD)
      {
        # Committed and uncommitted (staged or not), relative to the merge base.
        git diff --name-only --diff-filter=ACMRD "$merge_base"
        # Untracked, because a new file is a change too.
        git ls-files --others --exclude-standard
      } > "$changed_file"
      source_desc="$BASE_REF...HEAD plus the working tree (merge base ${merge_base:0:9})"
      vitest_since="$merge_base"
    fi
    ;;
esac

# Normalise: forward slashes, no leading ./, sorted, unique.
sed -e 's#\\#/#g' -e 's#^\./##' "$changed_file" | grep -v '^$' | sort -u > "$changed_file.n" || true
mv "$changed_file.n" "$changed_file"

echo "Changed paths from $source_desc:"
if [ ! -s "$changed_file" ]; then
  echo "  (none)"
  echo "Nothing to test."
  exit 0
fi
sed 's/^/  /' "$changed_file"
echo

# ----------------------------------------------------------------------------
# 2. Project graph from the .csproj files
#
# Each project is identified by its directory name under src/ or tests/, which
# in this repository is also the assembly name (src/Arbitarr.Data -> Arbitarr.Data).
# edges: "<project> <referenced-project>" one per line.
# ----------------------------------------------------------------------------
find src tests -mindepth 2 -maxdepth 2 -name '*.csproj' | sed 's#\\#/#g' | sort > "$projects_file"

: > "$graph_file"
while IFS= read -r csproj; do
  proj=$(basename "$(dirname "$csproj")")
  # Every <ProjectReference Include="..."> -- the referenced project is the
  # basename of the referenced .csproj without its extension, which matches the
  # directory-name identity above for every project in this tree.
  grep -o '<ProjectReference[^>]*Include="[^"]*"' "$csproj" 2>/dev/null \
    | sed -e 's#.*Include="##' -e 's#"$##' -e 's#\\#/#g' \
    | while IFS= read -r ref; do
        refname=$(basename "$ref" .csproj)
        printf '%s %s\n' "$proj" "$refname"
      done >> "$graph_file" || true
done < "$projects_file"

# Test projects: tests/*.Tests with a csproj. TestSupport is deliberately NOT
# one -- it is a library every test project references, so the generic rule
# "select every test project that references the changed project" covers it.
all_test_projects=$(sed -n 's#^tests/\([^/]*\.Tests\)/.*#\1#p' "$projects_file" | sort -u)
all_test_count=$(printf '%s\n' "$all_test_projects" | grep -c . || true)

# Transitive dependents of one project: every node from which the project is
# reachable along reference edges. Plain BFS over the edge list in awk.
dependents_of() {
  awk -v start="$1" '
    { rev[$2] = rev[$2] " " $1 }
    END {
      seen[start] = 1; queue[1] = start; head = 1; tail = 1
      while (head <= tail) {
        n = queue[head++]
        cnt = split(rev[n], ds, " ")
        for (i = 1; i <= cnt; i++) {
          if (ds[i] != "" && !(ds[i] in seen)) { seen[ds[i]] = 1; queue[++tail] = ds[i] }
        }
      }
      for (k in seen) print k
    }' "$graph_file"
}

# ----------------------------------------------------------------------------
# 3. Map changed paths to a selection
# ----------------------------------------------------------------------------
select_all_backend=0
select_frontend=0
declare -A selected=()
declare -A reasons=()
declare -A changed_projects=()

while IFS= read -r path; do
  case "$path" in
    "$FRONTEND_DIR"/*)
      select_frontend=1
      ;;
    src/*/*|tests/*/*)
      proj=${path#*/}; proj=${proj%%/*}
      if [ -f "src/$proj/$proj.csproj" ] || [ -f "tests/$proj/$proj.csproj" ]; then
        changed_projects["$proj"]=1
      else
        # A directory under src/ or tests/ with no csproj is not a project this
        # script knows how to map, so treat it as out-of-tree.
        select_all_backend=1
        reasons["$path"]="no .csproj at src/$proj or tests/$proj; cannot map, so everything runs"
      fi
      ;;
    *)
      select_all_backend=1
      reasons["$path"]="outside src/ and tests/ (workflow, docs, solution or root config); everything runs"
      ;;
  esac
done < "$changed_file"

if [ "$select_all_backend" -eq 1 ]; then
  for t in $all_test_projects; do selected["$t"]=1; done
elif [ "${#changed_projects[@]}" -gt 0 ]; then
  for proj in "${!changed_projects[@]}"; do
    for dep in $(dependents_of "$proj"); do
      case " $(printf '%s ' $all_test_projects)" in
        *" $dep "*) selected["$dep"]=1 ;;
      esac
    done
  done
fi

# ----------------------------------------------------------------------------
# 4. Report
# ----------------------------------------------------------------------------
if [ "${#reasons[@]}" -gt 0 ]; then
  for p in $(printf '%s\n' "${!reasons[@]}" | sort); do
    echo "Reason: $p -> ${reasons[$p]}"
  done
  echo
fi

backend_list=''
backend_count=0
if [ "${#selected[@]}" -gt 0 ]; then
  backend_list=$(printf '%s\n' "${!selected[@]}" | sort)
  backend_count=${#selected[@]}
fi

echo "Backend test projects selected ($backend_count of $all_test_count):"
if [ "$backend_count" -eq 0 ]; then echo "  (none)"; else printf '%s\n' "$backend_list" | sed 's/^/  /'; fi
if [ "$select_frontend" -eq 1 ]; then
  echo "Frontend: yes ($FRONTEND_DIR changed)"
else
  echo "Frontend: no"
fi
echo "Filter:   $TEST_FILTER"
echo

# ----------------------------------------------------------------------------
# 5. Run
# ----------------------------------------------------------------------------
status=0

run() {
  echo "+ $*"
  if [ "$dry_run" -eq 1 ]; then return 0; fi
  "$@" || { rc=$?; echo "test-affected: FAILED: $*" >&2; status=1; return "$rc"; }
}

if [ "$backend_count" -gt 0 ]; then
  if [ "$no_build" -eq 0 ]; then
    # See the header: the solution is built once so Architecture.Tests' IL scans
    # find every sibling assembly. -m:1 on the build bounds memory use, as in CI.
    run dotnet build "$SOLUTION" -m:1 || true
  fi
  if [ "$status" -eq 0 ]; then
    while IFS= read -r t; do
      [ -n "$t" ] || continue
      run dotnet test "tests/$t/$t.csproj" --no-build -m:1 --filter "$TEST_FILTER" || true
    done <<< "$backend_list"
  else
    echo "test-affected: build failed; not running tests" >&2
  fi
fi

if [ "$select_frontend" -eq 1 ]; then
  # vitest 3.x: `--changed [since]` runs only the tests whose import graph
  # reaches a changed file; `--passWithNoTests` because a change that touches no
  # test's graph (a README, say) is a legitimate empty selection, not a failure.
  # In explicit-path mode there is no git base to hand vitest, so the whole
  # frontend suite runs instead.
  if [ "$mode" = 'paths' ]; then
    echo "Frontend: explicit paths given, no git base for 'vitest --changed'; running the full suite"
    ( cd "$FRONTEND_DIR" && run npx vitest run ) || status=1
  elif [ -n "$vitest_since" ]; then
    ( cd "$FRONTEND_DIR" && run npx vitest run --changed "$vitest_since" --passWithNoTests ) || status=1
  else
    ( cd "$FRONTEND_DIR" && run npx vitest run --changed --passWithNoTests ) || status=1
  fi
fi

if [ "$dry_run" -eq 1 ]; then
  echo "(dry run: nothing was executed)"
fi

exit "$status"
