# S10 — MCP and knowledge sources

**Milestone:** M2 · **Size:** M · **Depends on:** S03 · **Status:** todo

## Goal

Tools from MCP servers, and knowledge retrieval.

**Closes:** TOOL-12, CTX-04, CTX-05, TEST-17

## Acceptance criteria

- [ ] Our own MCP client works with a reference server over stdio and over Streamable HTTP.
- [ ] MCP tools are sorted by name and governed exactly like other tools (TEST-17).
- [ ] Large tool sets can load tool descriptions on demand.
- [ ] Knowledge can be retrieved before the turn, as a tool, or both, and "not covered" can end the turn in a handoff.

## Notes

No MCP SDK, because the official one depends on Microsoft.Extensions.AI.
