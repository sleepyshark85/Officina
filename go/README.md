# Officina for Go

The Go implementation of Officina: the same requirements ([`REQUIREMENTS.md`](../REQUIREMENTS.md)) and architecture
([`ARCHITECTURE.md`](../ARCHITECTURE.md)) as the .NET one at the repository root, written as idiomatic Go.

Status: planned. No code yet; slice G01 creates the module here. See
[`docs/plan/go-port.md`](../docs/plan/go-port.md) for the slices and
[`docs/implementations/go.md`](../docs/implementations/go.md) for the decisions.

Planned layout (G2, G3):

| Path | Holds |
|---|---|
| `go.mod` | Module `github.com/sleepyshark85/officina/go` |
| `officina/` | The core: agent, run loop, conversation, tool pipeline, memory, audit, built-in stores |
| `officina/claude/` | The Claude adapter, the only importer of the Anthropic SDK |
| `officina/mcp/` | The MCP client (stdio and Streamable HTTP) |
| `officina/officinatest/` | The test kit: scripted model and approver, fake MCP server, prefix stability check |
| `cmd/bookshop/`, `internal/bookshop/` | Bookshop Assistant |
| `examples/` | The GEN-06 samples |
| `docs/` | Go design notes and traceability |
