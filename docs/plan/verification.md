# Verification of the MUSTs (v1 acceptance)

v1 is accepted when every MUST is verified by at least one test or a recorded review (REQUIREMENTS.md §13).
[`check_verification.py`](check_verification.py) finds each MUST's id in a test (a comment that names it, in `tests/` or
`benchmark/tests/`), or in the reviews below, and lists those that are neither, those whose reading the owner has still to confirm,
and those still pending.

## Recorded reviews

For a MUST that is a property of the design rather than a behaviour to run, a review records how it holds.

| Requirement | How it holds |
|---|---|
| CFG-10 | Configuration is JSON bound to the Options classes. The only expressions are the conditions of patterns and stop rules, a fixed grammar of comparisons that `Condition` parses and evaluates without running code (`ConditionTests`). Behaviour that needs logic is an `extension:` id the application registers (tools, gates, checks, shorteners, patterns). |
| EGR-01 | `AgentResult` holds the outcome (completed, handed off or rejected), the output, the record with its citations, the artifacts, the statistics (iterations, tool calls, usage, cost, elapsed time), the transcript and the handoff. Tests read each: `TurnTests` (outcome, output, transcript, tool calls), `ModelGatewayTests` (usage, cost), `RunRecordTests` (record, iterations), `AdmissionTests` (rejected). |
| TASK-01 | `BoardTask` holds the id, title, description, acceptance criteria, checks, role, assignee (its owner), state, dependencies, priority, budget and spending, artifacts and failed attempts; the board's change log holds each task's history (`TaskBoardTests`: every change is recorded with who, when, what and why). |

## Readings for the owner to confirm

Where what is built reads a MUST differently from its words. They are not verified until the owner confirms the reading, or the
requirement changes at revision 3.

| Requirement | The words | What is built |
|---|---|---|
| CFG-01 | The agent definition holds every part of an agent, policies and enabled capabilities included | `AgentDefinition` holds `model`, `instructions`, `tools`, `pattern`, `context`, `output`, `budget`, `permissions`, `triggers` and `stopWhen`. Policies (`policies`) and capabilities (`capabilities`) are the configuration's, the same for every agent of it |
| CAP-01 | Each capability can be switched on or off per application or per agent definition | Per application only (`capabilities.*.enabled`): an agent uses every capability that is on, and a tool of one that is off is an error (CAP-02). No agent has a capabilities setting |
| TASK-02 | Only configured status changes are allowed | The status changes are fixed in code (`TaskBoard.Transitions`), not configured; the test that names TASK-02 checks that a change outside them is refused |

## Pending

MUSTs that S21's later parts verify; S21 is not done while any is listed here.

| Requirement | Waits for |
|---|---|
| TEST-31 | The live benchmark run, on Linux and Windows, which the owner starts (S21 part 1). Its goals, suites, references and runner are tested offline |
| SBX-01 | S21 part 5: the CPU limit, tested on both systems (the memory, process, time and output limits, the network and the proxy are tested) |
