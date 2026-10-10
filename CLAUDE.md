# Officina — working notes for Claude sessions

Officina is a purpose-neutral library for building agentic applications. Phase 1 is accepted against one reference
application, **Bookshop Assistant**: an interactive console chatbot over PostgreSQL in Docker. It has three
implementations: **.NET 10** at the repository root (done), **Go** in [`go/`](go/) (done) and **Ruby** in
[`ruby/`](ruby/) (planned), each tracked in [`docs/plan/phase-1.md`](docs/plan/phase-1.md). Working in `go/` loads
[`go/CLAUDE.md`](go/CLAUDE.md) as well, and working in `ruby/` loads [`ruby/CLAUDE.md`](ruby/CLAUDE.md).

The conventions every implementation follows (workflow, tests, design rules, Claude API notes):

@docs/conventions.md

## Where things are

- `REQUIREMENTS.md`: what to build (IDs such as `TOOL-03`), phase 1 and north star, with decisions in §7.
  Language-agnostic.
- `ARCHITECTURE.md`: concepts, components, contracts and flows. **It holds no code, type names or API names;** keep it
  that way. Implementation detail goes in code and its comments.
- `docs/implementations/`: each implementation's platform decisions (`dotnet.md`, `go.md`, `ruby.md`).
- `docs/plan/phase-1.md`: the phase 1 slices, shared by every implementation, with notes and progress per
  implementation.

## The .NET implementation

Code in `src/`, `tests/`, `apps/` and `samples/`; packages named under `Sleepyshark.Officina`. Decisions:
[`docs/implementations/dotnet.md`](docs/implementations/dotnet.md). It replaces `~/sources/agentic-core` (the first
Officina, now archived), whose Claude provider, MCP client, test kit and spike findings (`docs/spikes/` there) were
reused (ARCHITECTURE §13).

- `docs/design/`: the .NET type-level view (class, package and sequence diagrams, principles and trade-offs). Unlike
  ARCHITECTURE.md it names types, so update it when a change moves what it shows. Each diagram's `.html` is the source;
  the `.svg` beside it is exported from it. `docs/traceability.md` maps the .NET tests to requirement IDs.
- Prerequisites: Docker, the .NET 10 SDK, an Anthropic API key or `ant auth login` for live tests.
- Required checks before a merge: `ubuntu-latest`, `windows-latest`, `quality`, `mutation`, and the Go ones
  (`go-changes`, `go-ubuntu`, `go-windows`, `go-quality`, `go-mutation`): every PR needs all nine, and the five Ruby
  ones too once Ruby S01 is merged (R15).
- Hooks: before a commit that stages .NET code, `dotnet format --verify-no-changes` and the Release build (warnings
  are errors), and the staged files shown; before a push that changes more than docs outside `go/` and `ruby/`, the
  tests. After an edit to a `.cs` file, one type per file named after it, and no requirement IDs in comments. The Go checks run
  only for files under `go/` ([`go/README.md`](go/README.md)), and from Ruby S01 the Ruby checks only for files under
  `ruby/`.
- **One class, record, struct, interface or enum per file**, named after it. Related types share a folder (in the
  core: `Conversations`, `Models`, `Runs`, `Tools`, `Memory`, `Audit`…); the namespace stays the package's.
- **Dependencies:** no Microsoft Agent Framework or `Microsoft.Extensions.AI`. The Anthropic C# SDK is used only in the
  Claude package. The core references only `Microsoft.Extensions.DependencyInjection.Abstractions`.
- **Each project registers its own services** (`Add…` methods on `IServiceCollection`); the application composes them in
  one container, and tests build that container and replace only the boundaries.
