# S10 — MCP and knowledge sources

**Milestone:** M2 · **Size:** M · **Depends on:** S03 · **Issue:** [#12](https://github.com/sleepyshark85/Officina/issues/12) · **Status:** done

## Goal

Tools from MCP servers, and knowledge retrieval.

**Closes:** TOOL-12, CTX-04, CTX-05, TEST-17

## Acceptance criteria

- [x] Our own MCP client works with a reference server over stdio and over Streamable HTTP.
- [x] MCP tools are sorted by name and governed exactly like other tools (TEST-17).
- [x] Large tool sets can load tool descriptions on demand.
- [x] Knowledge can be retrieved before the turn, as a tool, or both, and "not covered" can end the turn in a handoff.

## Notes

No MCP SDK, because the official one depends on Microsoft.Extensions.AI.

- The client implements `initialize`, `tools/list` and `tools/call` only. Each tool is configured by its exact
  `mcp:<server>/<tool>` source; there are no `*` patterns.
- "Not covered" ends the turn only for retrieval before the turn, where the core holds the coverage. A `knowledge:`
  tool gives the coverage to the model with the passages.
- Passages retrieved before the turn are one labelled fact per source in the volatile context, `knowledge:<name>`.
- S07: the `knowledge` capability switch (CAP-01). S16: `sof run` connects the tool servers.
