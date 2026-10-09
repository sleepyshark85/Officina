# .NET implementation

The phase 1 implementation, at the repository root (`src/`, `tests/`, `apps/`, `samples/`). Its platform decisions
fill in the language-agnostic ones of [`REQUIREMENTS.md`](../../REQUIREMENTS.md) §7. Working rules:
[`CLAUDE.md`](../../CLAUDE.md). Status: phase 1 complete ([`docs/plan/phase-1.md`](../plan/phase-1.md)).

| # | Fills in | Decision | Status |
|---|---|---|---|
| N1 | D1, TEST-03 | .NET 10; CI on Linux and Windows. | Decided |
| N2 | D8 | Namespace and package prefix `Sleepyshark.Officina`. | Decided (owner) |
| N3 | MDL-02, TEST-05 | The official Anthropic C# SDK (`Anthropic`), in `Sleepyshark.Officina.Claude` only. | Decided |
| N4 | D2 | Not `Microsoft.Extensions.AI`, and not the Microsoft Agent Framework. | Decided |
| N5 | D15 | The core may reference `Microsoft.Extensions.DependencyInjection.Abstractions` and no other package; tracing and metrics use the base library's `ActivitySource` and `Meter`. Each project registers its services with `Add…` methods on `IServiceCollection`, and the application composes them in one container. | Decided (owner) |
| N6 | Q1 | Npgsql, no object mapper. | Decided (owner) |
| N7 | Q2, TEST-08 | The core's own small validator; `JsonSchema.Net` as the reference validator, in tests only. | Decided (owner) |
| N8 | Principle 11 | Core line budget: about 3,500 lines. | Decided |
| N9 | TEST-07 | Property tests with CsCheck. | Decided |
| N10 | NS-13 | Hosting helpers, when they enter, target ASP.NET Core. | Proposed |
