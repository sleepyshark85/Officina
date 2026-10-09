# Go traceability

Each phase 1 requirement of [`REQUIREMENTS.md`](../../REQUIREMENTS.md) that the Go implementation covers so far, and
the tests that check it, or how it is checked otherwise. It grows slice by slice
([`docs/plan/phase-1.md`](../../docs/plan/phase-1.md)); by Go S13 it lists every requirement, as
[`docs/traceability.md`](../../docs/traceability.md) does for .NET.

Tests are under `go/`, shortened as:

| Short | Package |
|---|---|
| Deps | `github.com/sleepyshark85/officina/go` (`dependencies_test.go`, fixtures in `testdata/dependencies/`) |

## Tests (TEST)

| ID | Tests | Also checked by |
|---|---|---|
| TEST-03 | — | The Go workflow (`.github/workflows/go.yml`): `go-ubuntu` and `go-windows`, offline |
| TEST-05 | Deps: `TestDependencies_TEST05_ModuleKeepsTheRules`, `TestDependencies_TEST05_FixturesBreakingTheRulesFail` (the core's own rule, D15 and G6, as well) | |
