# Officina

A .NET 10 engine for building AI agents from configuration, with a coding team CLI (`sof`) as its
first application.

Status: v1 is built and passes its offline tests on Linux and Windows. The live coding team benchmark (TEST-31) is still to
run; see the [plan's status](docs/plan/README.md#status).

- [User guide](docs/user-guide.md): set up `sof` and use it
- [Team guide](docs/team-guide.md): set up a small coding team, with a worked example
- [Requirements](REQUIREMENTS.md)
- [Configuration](CONFIGURATION.md), the [configuration reference](docs/configuration-reference.md) and the generated
  [settings reference](docs/configuration-settings.md)
- [Design](DESIGN.md), with diagrams in [`docs/diagrams/`](docs/diagrams/)
- [Master plan](docs/plan/README.md) and its slices
- [Benchmark](benchmark/README.md)

## Build

Needs the .NET 10 SDK. `dotnet build` and `dotnet test` at the repository root build and test
everything offline; no API key is needed.
