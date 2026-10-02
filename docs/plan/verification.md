# Verification of the MUSTs (v1 acceptance)

v1 is accepted when every MUST is verified by at least one test or a recorded review (REQUIREMENTS.md §13).
[`check_verification.py`](check_verification.py) finds each MUST's id in a test (a comment that names it, in `tests/` or
`benchmark/tests/`), or in the reviews below, and lists those that are neither, or still pending.

## Recorded reviews

For a MUST that is a property of the design rather than a behaviour to run, a review records how it holds.

| Requirement | How it holds |
|---|---|
| CFG-01 | `AgentDefinition` holds each part an agent is described by: `model` (the profile), `instructions`, `tools` (tool sets), `pattern`, `context`, `output`, `budget`, `permissions`, `triggers` and `stopWhen`; policies and capabilities are the configuration's, which every agent's run uses. The settings reference, generated from the Options classes, lists them all (`GeneratedDocumentationTests`). |
| CFG-10 | Configuration is JSON bound to the Options classes. The only expressions are the conditions of patterns and stop rules, a fixed grammar of comparisons that `Condition` parses and evaluates without running code (`ConditionTests`). Behaviour that needs logic is an `extension:` id the application registers (tools, gates, checks, shorteners, patterns). |
| EGR-01 | `AgentResult` holds the outcome (completed, handed off or rejected), the output, the record with its citations, the artifacts, the statistics (iterations, tool calls, usage, cost, elapsed time), the transcript and the handoff. Tests read each: `TurnTests` (outcome, output, transcript, tool calls), `ModelGatewayTests` (usage, cost), `RunRecordTests` (record, iterations), `AdmissionTests` (rejected). |
| TASK-01 | `BoardTask` holds the id, title, description, acceptance criteria, checks, role, assignee (its owner), state, dependencies, priority, budget and spending, artifacts and failed attempts; the board's change log holds each task's history (`TaskBoardTests`: every change is recorded with who, when, what and why). |

## Pending

MUSTs that S21's later parts verify; S21 is not done while any is listed here.

| Requirement | Waits for |
|---|---|
| TEST-31 | The live benchmark run, on Linux and Windows, which the owner starts (S21 part 1). Its goals, suites, references and runner are tested offline |
| CLD-06 | S21 part 3: the Claude provider's feature switches |
| SBX-01 | S21 part 5: the CPU limit, tested on both systems (the memory, process, time and output limits, the network and the proxy are tested) |
