# S17 — Project memory

**Milestone:** M5 · **Size:** S · **Depends on:** S05, S16 · **Issue:** [#19](https://github.com/sleepyshark85/Officina/issues/19) · **Status:** done

## Goal

Durable project knowledge shared across runs.

**Closes:** MEM-01, MEM-02, MEM-03, MEM-04, MEM-05

## Acceptance criteria

- [x] Memory is in the stable prefix of the definitions that use it, followed by cache boundary ② (CTX-11; left
  over from S05).
- [x] A memory change does not edit the prefix of a running conversation (the last TEST-09 case, left over from S05).
- [x] Approved changes reach running conversations as appended messages, and new conversations get them in the prefix.
- [x] Project-wide decisions are recorded with reason, author and date.
- [x] Exceeding the size limit triggers condensing, which the owner reviews; nothing is dropped silently.

## Notes

- Memory is a log of proposals and decisions per scope (`project:<name>`, `owner:<caller>` or `tenant`), in Core
  (`Memory`), kept through `IStorage.Memory` with optimistic revisions like the task board. An entry is a note or a
  decision (with its reason); author is the proposer, date the approval. Entry numbers are the proposals' numbers, and a
  proposal can `replaces` entries, which leave memory but stay in the log. The text is rendered in entry order inside
  `<project-memory>`, and one switch, `capabilities.projectMemory`, puts it in every agent's prefix (there is no
  per-agent setting; agents use the capabilities that are on, as for the others).
- Agents use `builtin:memory.propose_change` and `memory.review`. With `approveBy: owner` the tool pipeline asks the
  owner before the proposal and applies it at once; with `lead`, the proposal waits for a `memory.review` by an agent
  that holds the tool (the lead's tool sets decide who), never its proposer. The owner uses `AgentRunner.Memory`:
  propose, approve and reject.
- The prefix is a separate `ModelRequest.Memory` that Claude sends as a second system block, with boundary ② after it
  (CachePoint.Memory, one hour); with no entries there is no block and no boundary. The conversation stores the memory
  revision of its prefix and the revision it was told of (`ConversationTurn.PrefixMemory`, `SeenMemory`), so a
  continued conversation keeps its prefix and a window of the last turns, which is not append-only, starts afresh. Before
  each model call, changes approved since appear as one operator message. Shortening may drop earlier messages, so
  changes since the prefix are told again after it, which can repeat one already in the current turn.
- The limit is counted as one token per four characters. A change that would take memory past it, unless it makes memory
  smaller, is not applied and stays proposed. A proposal replacing several entries is a condensation; only the owner
  approves it, whoever approves changes. Replaced entries stay in the log, so nothing is dropped silently.
- The SQLite format version is now 4: a `memory_changes` table, and the conversation rows carry the memory revisions.
  Memory has no retention: it outlives runs.

Left to later slices:
- The CLI has no command to list proposals or review condensing (S16's interactive session asks about owner-approved
  proposals through the usual approval, like any tool).
- S19: the checkpoint records memory's revision (DESIGN.md §8).
- An owner's memory (`scope: owner`) is not yet in the owner's export and deletion (PRIV-02), and text the model proposes
  is not unmasked when masking is on; masking tokens are per run, so a token would be stored.

M5 demo: one agent changes, builds and tests a real project from the CLI.
