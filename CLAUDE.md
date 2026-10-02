# Officina — working notes for Claude sessions

Officina is a .NET 10 engine for building AI agents from configuration. Its first application is
the coding team CLI, `sof`. Sleepyshark is the organization: projects are `Sleepyshark.Officina.*`.

## Where things are

- `REQUIREMENTS.md`: what to build (requirement IDs such as `TOOL-05`), with the decisions in §13.
- `DESIGN.md`: how it is built. `CONFIGURATION.md`: the configuration guide. `docs/user-guide.md`: setting up and using `sof`.
- `docs/plan/README.md`: the master plan. **Its Status section says what is done and what is next.**
  Each slice has a file in `docs/plan/` and a GitHub issue.
- `docs/spikes/`: findings from the sandbox and Claude SDK spikes. `benchmark/`: the TEST-31 coding team benchmark.

## How we work

- **Every change goes through a branch and a pull request** to `main`, including docs and plan
  updates. Never push to `main`, and never merge without the owner's go-ahead.
  Branches: `slice/<id>-<slug>`, `spike/<id>-<slug>`, `docs/<topic>`. A PR closes its slice's issue.
- **The simplest thing that works** (REQUIREMENTS principle 13): build only what the slice's
  acceptance criteria need. No settings without a known case, no speculative abstractions, no
  optimization without a measured target. Prefer a framework feature over custom code.
  Simplicity never at the cost of separation of concerns or clear design.
- **Tests replace only system boundaries** (DESIGN §11): model provider, network and tool servers,
  OS processes and sandbox, clock, environment and secrets, the human. Everything else is real.
- **No Microsoft Agent Framework or Microsoft.Extensions.AI.** The Anthropic C# SDK is used only
  in `Sleepyshark.Officina.Providers.Claude`, and the dependency check enforces this.
- When a slice is done, mark it `done` in its slice file and in the plan table, and update the
  plan's Status section.
- If a requirement moves between slices, update both slice files and run
  `python3 docs/plan/check_coverage.py`.
- Review comments may arrive as a pending (unsubmitted) review. The REST comment endpoints don't
  return those; read them with GraphQL `pullRequest(number: N) { reviewThreads { … } }`.

## Commands

- Build and test: `dotnet build` and `dotnet test` (CI runs both on Linux and Windows).
- Regenerate the configuration schema and settings reference:
  `OFFICINA_UPDATE_GENERATED=1 dotnet test --filter GeneratedDocumentationTests`.
- Requirement coverage: `python3 docs/plan/check_coverage.py`. MUST verification:
  `python3 docs/plan/check_verification.py` (it exits 1 while anything is pending).
- Rebuild the design diagrams: `cd docs/diagrams && python3 generate.py . && python3 export_svg.py .`.
