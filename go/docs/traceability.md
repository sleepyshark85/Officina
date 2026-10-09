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
| Claude | `github.com/sleepyshark85/officina/go/officina/claude` (`claude_test`), on recorded HTTP from an `httptest.Server`; shared fixtures in the repository's `testdata/` |

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
| AGT-03 | Core: `TestRun_AGT03_EveryFinishReasonMapsToItsResultAndTheReplyIsKept`, `TestRun_AGT03_ARunWithoutAReplyFailsOrEndsAndAppendsNothing`, `TestResult_AGT03_StatusesAndReasonsPrintTheirNames`, `TestRun_AGT03_AReplyThatCallsToolsIsNeverLeftWithoutResults` | |
| AGT-04 | Core: `TestRun_AGT04_HundredConcurrentRunsOfOneAgentEachKeepTheirConversation`, `TestRun_AGT04_ASecondRunOnAConversationInUseFails` | |
| AGT-05 | Core: `TestRun_AGT05_CancellingMidStreamAppendsNothingAndTheNextRequestIsValid`, `TestRun_AGT05_ARunCancelledBeforeItStartsCallsNoModel`, `TestRun_AGT05_CancellingStopsTheRunEvenIfTheModelIgnoresIt` | Tool calls on cancel come with tools (Go S05) |
| AGT-06 | Core: `TestConversation_AGT06_JSONRoundTripKeepsEveryBlockByteForByte` (the canonical-form pin: `<`, `&`, non-ASCII, `\u` escapes, and stores that rewrite the JSON), `TestConversation_AGT06_TheJSONFormIsPlainAndTheSameAsDotNets` (shared `testdata/conversation/`, compared parsed), `TestConversation_AGT06_AToolCallIsKeptInDotNetsForm`, `TestConversation_AGT06_UnmarshalRejectsAnInvalidConversation`, `FuzzConversation_AGT06_UnmarshalThenMarshalIsAFixedPoint`, `TestRun_AGT06_ReplyBlocksAreAppendedExactlyAsReceived`; Claude: `TestModel_MDL05_AGT06_AReplyIsReplayedUnchangedAfterSaveAndResume` | |
| AGT-08 | Core: `TestRun_EVT01_AGT08_EventsStreamThenEachAppendThenResult`, `TestRun_AGT08_AConsumerThatBreaksAtAnAppendKeepsAValidConversation` | |

## Models (MDL)

| ID | Tests | Also checked by |
|---|---|---|
| MDL-01 | Claude: `TestModel_MDL01_MDL05_TextStreamsAndEachBlockComesCompleteInTheCanonicalForm`, `TestModel_MDL01_StopReasonsMapByTheirWordAndAnUnknownOneKeepsIt` | |
| MDL-02 | Claude: `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (streamed, beta surface); Deps: `TestDependencies_TEST05_ModuleKeepsTheRules` (the SDK in `claude` only) | `examples/hello`, run live |
| MDL-03 | Claude: `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (effort, output limit, adaptive thinking), `TestNew_MDL03_SettingsNameEverySettingThatShapesARequest`, `TestNew_MDL03_RejectsAnInvalidSetting` | |
| MDL-04 | Claude: `TestModel_MDL04_ATransientFailureWaitsThenSucceeds` (`Retry-After` in seconds, as a date, and capped, also past what a duration holds; exponential backoff; 408, 409, 429, 500, 529; an error event and a dropped connection mid-stream), `TestModel_MDL04_AFailureMidStreamRestartsTheReplyKeepingTheTokensItUsed`, `TestModel_MDL04_ARestartedReplyReachesTheRunOnceAndTheHostIsTold`, `TestModel_MDL04_AFailureThatPersistsIsTransientAfterEveryAttempt`, `TestModel_MDL04_CancellingDuringARetryWaitEndsTheCallAtOnce`, `TestModel_MDL04_ErrorsRetryingCannotFixAreClassifiedAndNotRetried`, `TestModel_MDL04_APromptLongerThanTheContextWindowFinishesAsContextFull`, `TestModel_MDL04_AFailedRunCarriesTheFailuresClass` (all timed with `testing/synctest`); Core: `TestRun_MDL04_ARetriedReplyRestartsAndOnlyTheLastAttemptIsAppended` | |
| MDL-05 | Claude: `TestModel_MDL01_MDL05_TextStreamsAndEachBlockComesCompleteInTheCanonicalForm`, `TestModel_MDL05_BlocksAreStoredWithHTMLCharactersEscapedAndOtherEscapesAsReceived`, `TestModel_MDL05_BlocksTheDotNetImplementationStoredReachTheWireByteForByte` (shared `testdata/`), `TestModel_MDL05_AGT06_AReplyIsReplayedUnchangedAfterSaveAndResume`, `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (stored blocks on the wire byte for byte) | |
| MDL-06 | Claude: `TestModel_MDL06_ARefusalFinishesWithItsCategoryAndTheRunStops` | |

## Context and caching (CTX)

| ID | Tests | Also checked by |
|---|---|---|
| CTX-01 | Core: `TestRun_CTX01_RequestHasSortedToolsInstructionsThenUserAndOperatorMessages`, `TestRun_CTX01_ToolsGivenInAnotherOrderAreTheSamePrefix`; Claude: `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (the shared golden layout, compared parsed), `TestModel_CTX01_ARequestWithoutToolsIsLaidOutAsDotNetsIs` (`"tools":[]`, as .NET sends it) | |
| CTX-02 | Claude: `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (run context as a system message after each user message) | `examples/hello`, run live |
| CTX-03 | Claude: `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (cache point on the instructions, automatic caching, two lifetimes), `TestNew_MDL03_RejectsAnInvalidSetting` (prefix shorter than the tail) | `examples/hello`, run live: cache reads from the second message |
| CTX-04 | Core: `TestRun_CTX04_AChangedPrefixFailsWithPrefixMismatchBeforeAnyModelCall` (instructions, tool description, schema, added and removed tool, model settings) | |
| CTX-05 | Claude: `TestModel_CTX05_UsageCountsCacheReadsAndWritesAndAddsUpEveryIteration` (the usage part) | Telemetry's hit ratio comes in Go S07 |

## Events and observability (EVT)

| ID | Tests | Also checked by |
|---|---|---|
| EVT-01 | Core: `TestRun_EVT01_AGT08_EventsStreamThenEachAppendThenResult`, `TestRun_EVT01_AConsumerThatBreaksMidRunLeavesNoGoroutine` (with `goleak` in `TestMain`), `TestRun_EVT01_EventsRangeOnceAndTheResultWaitsForThem`; `ExampleAgent_Stream`; Claude: `TestModel_EVT01_AConsumerThatStopsEarlyEndsTheCall` (with `goleak`) | Tool, approval and compaction events come with their slices |

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
