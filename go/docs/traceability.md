# Go traceability

Each phase 1 requirement of [`REQUIREMENTS.md`](../../REQUIREMENTS.md) that the Go implementation covers so far, and
the tests that check it, or how it is checked otherwise. It grows slice by slice
([`docs/plan/phase-1.md`](../../docs/plan/phase-1.md)); by Go S13 it lists every requirement, as
[`docs/traceability.md`](../../docs/traceability.md) does for .NET.

Tests are under `go/`, shortened as:

| Short | Package |
|---|---|
| Deps | `github.com/sleepyshark85/officina/go` (`dependencies_test.go`, `size_test.go`, fixtures in `testdata/dependencies/`) |
| Core | `github.com/sleepyshark85/officina/go/officina` (`officina_test`) |
| Kit | `github.com/sleepyshark85/officina/go/officina/officinatest` (`officinatest_test`) |

## Generality (GEN)

| ID | Tests | Also checked by |
|---|---|---|
| GEN-02 | Core: `TestRun_GEN02_GEN03_AgentOfModelAndInstructionsRunsStatelessly` | |
| GEN-03 | Core: `TestRun_GEN02_GEN03_AgentOfModelAndInstructionsRunsStatelessly` (stateless); `TestRun_AGT02_MultiTurnRunCompletesWithText` (stateful) | |

## Agent and turn loop (AGT)

| ID | Tests | Also checked by |
|---|---|---|
| AGT-01 | Core: `TestNewAgent_AGT01_RejectsAnInvalidDefinition`, `TestNewAgent_AGT01_TheDefinitionDoesNotChangeWithTheCallersTools`; `TestRun_AGT04_HundredConcurrentRunsOfOneAgentEachKeepTheirConversation` (shared under `-race`) | |
| AGT-02 | Core: `TestRun_AGT02_MultiTurnRunCompletesWithText`; `ExampleAgent_Run` | The tool loop comes with tools (Go S05) |
| AGT-03 | Core: `TestRun_AGT03_EveryFinishReasonMapsToItsResultAndTheReplyIsKept`, `TestRun_AGT03_ARunWithoutAReplyFailsOrEndsAndAppendsNothing`, `TestResult_AGT03_StatusesAndReasonsPrintTheirNames` | |
| AGT-04 | Core: `TestRun_AGT04_HundredConcurrentRunsOfOneAgentEachKeepTheirConversation`, `TestRun_AGT04_ASecondRunOnAConversationInUseFails` | |
| AGT-05 | Core: `TestRun_AGT05_CancellingMidStreamAppendsNothingAndTheNextRequestIsValid`, `TestRun_AGT05_ARunCancelledBeforeItStartsCallsNoModel`, `TestRun_AGT05_CancellingStopsTheRunEvenIfTheModelIgnoresIt` | Tool calls on cancel come with tools (Go S05) |
| AGT-06 | Core: `TestConversation_AGT06_JSONRoundTripKeepsEveryBlockByteForByte` (the canonical-form pin: `<`, `&`, non-ASCII, `\u` escapes, and stores that rewrite the JSON), `TestConversation_AGT06_TheJSONFormIsPlainAndTheSameAsDotNets`, `TestConversation_AGT06_UnmarshalRejectsAnInvalidConversation`, `FuzzConversation_AGT06_UnmarshalThenMarshalIsAFixedPoint`, `TestRun_AGT06_ReplyBlocksAreAppendedExactlyAsReceived` | |
| AGT-08 | Core: `TestRun_EVT01_AGT08_EventsStreamThenEachAppendThenResult`, `TestRun_AGT08_AConsumerThatBreaksAtAnAppendKeepsAValidConversation` | |

## Context and caching (CTX)

| ID | Tests | Also checked by |
|---|---|---|
| CTX-01 | Core: `TestRun_CTX01_RequestHasSortedToolsInstructionsThenUserAndOperatorMessages`, `TestRun_CTX01_ToolsGivenInAnotherOrderAreTheSamePrefix` | The provider's request layout (Go S04) |
| CTX-04 | Core: `TestRun_CTX04_AChangedPrefixFailsWithPrefixMismatchBeforeAnyModelCall` (instructions, tool description, schema, added and removed tool, model settings) | |

## Events and observability (EVT)

| ID | Tests | Also checked by |
|---|---|---|
| EVT-01 | Core: `TestRun_EVT01_AGT08_EventsStreamThenEachAppendThenResult`, `TestRun_EVT01_AConsumerThatBreaksMidRunLeavesNoGoroutine` (with `goleak` in `TestMain`), `TestRun_EVT01_EventsRangeOnceAndTheResultWaitsForThem`; `ExampleAgent_Stream` | Tool, approval and compaction events come with their slices |

## Principles

| Principle | Tests |
|---|---|
| 11, small core | Deps: `TestCoreSize_StaysWithinItsLineBudget` (G13) |

## Tests (TEST)

| ID | Tests | Also checked by |
|---|---|---|
| TEST-01 | Kit: `TestModel_TEST01_RepliesInOrderAndRecordsRequests`, `TestModel_TEST01_RejectsRoleSequencesTheProviderRejectsAndKeepsTheReply`, `TestTextBlock_TEST01_StoresRawJSONInTheCanonicalForm` | The scripted approver and fake MCP server come in Go S05 and Go S11 |
| TEST-02 | Core: `TestRun_TEST02_ThePrefixIsStableAcrossTurnsAndAcrossSaveRestartAndResume`; Kit: `TestCheckPrefix_TEST02_ReportsEachKindOfChange`, `ExampleCheckPrefix` | |
| TEST-03 | — | The Go workflow (`.github/workflows/go.yml`): `go-ubuntu` and `go-windows`, offline |
| TEST-05 | Deps: `TestDependencies_TEST05_ModuleKeepsTheRules`, `TestDependencies_TEST05_FixturesBreakingTheRulesFail` (the core's own rule, D15 and G6, as well) | |
