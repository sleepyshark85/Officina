# Officina

A .NET 10 engine for building AI agents from configuration, with a coding team CLI (`sof`) as its
first application. Status: specification and design, ready for the M0 design review.

- [User guide](docs/user-guide.md): set up `sof` and use it
- [Requirements](REQUIREMENTS.md)
- [Configuration](CONFIGURATION.md) and the [full reference](docs/configuration-reference.md)
- [Design](DESIGN.md), with diagrams in [`docs/diagrams/`](docs/diagrams/)
- [Master plan](docs/plan/README.md) and its slices

## Build

Needs the .NET 10 SDK. `dotnet build` and `dotnet test` at the repository root build and test
everything offline; no API key is needed.
