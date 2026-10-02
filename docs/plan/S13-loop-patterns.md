# S13 — Loop patterns

**Milestone:** M4 · **Size:** M · **Depends on:** S04, S06 · **Issue:** [#15](https://github.com/sleepyshark85/Officina/issues/15) · **Status:** done

## Goal

All the built-in patterns, built from the turn primitive.

**Closes:** PAT-01, PAT-02, PAT-03, PAT-04, PAT-05, PAT-06, PAT-07, PAT-08, TEST-06, TEST-07

## Acceptance criteria

- [x] Each pattern has a runnable example configuration in `samples/`, tested offline, plus one nested example.
- [x] A router value with no mapping, and structured output that stays invalid, both end in a handoff.
- [x] Budgets are drawn from the parent, and an extension pattern runs by name.

## Notes

An agent's `pattern` is a turn of its own (`toolLoop`, `singleCall`) or steps. A step is a turn of a named agent, that
agent's own pattern, a nested pattern, or, with neither, a turn of the pattern's agent; a step that needs another model
or tools names an agent defined with them. Built-in patterns and the application's implement `ILoopPattern`, and run
steps only through `PatternContext.RunStepAsync`. Each level draws on its parent's budget: turn, pattern (the agent's
turn budget), run. Branches, routes, votes and plans read only a turn's structured output, checked against its schema at
validation. Events, spans and logs name each step by its path, such as `draft/generate` (EVT-02, OBS-01, OBS-03, left
over from S08), and every step reports its outcome in a `stepEnded` event (PAT-08).

The output-check failure outcome `revise` (OUT-03, left over from S06) is `output.onCheckFailure`: the findings go back
to the model, masked, within `output.attempts`. `evaluateAndRevise` does the same across steps, with any step as
`generate`. Checks see artifacts as tools produced them, so findings are masked before a model sees them.

Left to later slices:
- S20: the team pattern's runtime; until then a team step fails with "the team pattern cannot run yet", and only its
  configuration shape is validated. Its sample is validated, not run.
- The condition roots `checks.<name>`, `outcome` and `stopReason` of the configuration reference §6 are not built:
  branches read structured output, and outcomes have `onOutcome`. Add them when a case needs them.
