# Later

Improvements noted, not adopted yet.

- **Code orchestrator, model for judgement:** a script or state machine runs the PR pipeline (setting auto-merge,
  updating branches, watching merges, relaying rounds, starting a slice once the PR it depends on merges). It wakes a
  model only for events that need judgement (must-fixes, an agent's "needs the lead", round 3, a failing check), each
  with a fresh, small context holding only that PR's state; possibly built on the Claude Agent SDK. Planned after Ruby,
  before the .NET/Go audit or phase 2.
- **Update branches without the lead:** a GitHub workflow on push to main updates BEHIND auto-merge PRs, replacing the
  lead's 4-hour background keeper. Or GitHub's merge queue, once the review gate supports `merge_group`.
- **Fewer conflicting merges:** plan parallel slices so that no two edit the same shared files at once, or stack them.
  #117 needed three merges of main and two merge checks.
- **Environment notes for agents:** one file with this machine's facts that live briefs point to (the compose database
  on 5433, another project's Postgres on 5432). Two live runs were wasted on it.
- **Mechanical mutation in the app:** a mutant config for ruby/apps/bookshop and the test kit, so reviewers stop
  mutating app lines by hand.
- **Cheaper agents where the work allows:** the fixer (Sonnet) for any well-bounded follow-up, the developer only for
  slices.
- **A session-end reminder hook** that prompts the agent-usage regeneration.

## Owner-triggered follow-ups

The lead never starts these on its own; the owner asks for them by name.

- **MCP ping:** a refused `ping` is an answer. Keep the client, record nothing, and reconnect only on a loss or a
  timeout (MCP-04). .NET and Go already do (`McpConnection.PingAsync`, `conn.ping`); only Ruby records `disconnected`
  and reconnects, which the lead fixes in normal Ruby work. Owner-triggered: a test in .NET and one in Go that pin
  this behaviour. Found in #119's review.
- **Streamable HTTP close:** closing sends no DELETE in Ruby, Go or .NET, so each Bookshop session leaves the compose
  file's filesystem MCP server with one more child process (the count went from 4 to 5 after one `/quit`). Fix all
  three together. Found in #123's review.
- **The strict .NET and Go audit against the code quality bar.** Findings so far: a dead prefix-mismatch branch in Go's
  console.go; .NET's MCP parsing has no generated-input tests; and a flake to watch, .NET windows-latest
  BookshopAssistant.Tests "Catastrophic failure: Test process did not return valid JSON" (seen once, on #114).
