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
| App | `github.com/sleepyshark85/officina/go/internal/bookshop` (`bookshop_test`): `TestTools_…` and `TestSessions_…` on the real database, `TestConsole_…` the real console against it with only the model and the staff member scripted (TEST-09); one PostgreSQL container per run, from the shared schema and seed in `apps/BookshopAssistant/database/`, Linux only |

## Reference application (APP)

| ID | Tests | Also checked by |
|---|---|---|
| APP-01 | App: `TestConsole_APP01_TheReplyStreamsWithTextBetweenToolCallsAndEachToolWithItsInputAndOutcome` | |
| APP-02 | App: `TestConsole_APP02_HelpAndUnknownCommandsAreAnsweredAndQuitLeaves` (`/help`, `/quit`), `TestConsole_APP02_NewStartsASessionOfItsOwnAndResumeOfAnUnknownOrUnreadableIdSaysSo` (`/new`, `/resume`), `TestConsole_APP10_QuitRestartAndResumeContinuesTheSessionWithItsPrefixByteIdentical` (`/sessions`), `TestConsole_APP14_TheStatusLineAndCostShowTheTokensCacheShareAndCostOfTheReplyAndTheSession` (`/cost`), `TestConsole_APP16_APP20_AuditShowsTheSessionsEntriesByRunEachLinkedToItsTraceWhichTheLogsJoin` (`/audit`, `/audit <id>`) | `/memory` comes with Go S09; titles and summaries in `/sessions` with Go S12 |
| APP-03 | App: `TestConsole_APP03_CancellingStopsTheReplyAndTheSessionGoesOn`, `TestConsole_APP03_CancellingAtTheApprovalPromptStopsTheReplyAtOnceAndTheChangeIsNotMade` | Live: Ctrl+C through `signal.NotifyContext`, per reply (`cmd/bookshop`) |
| APP-04 | App: every `TestTools_…` and `TestConsole_…` runs against the shared schema and seed | Inspection: `apps/BookshopAssistant/compose.yaml`, `database/`, shared with .NET |
| APP-05 | App: `TestConsole_APP05_TheReadToolsOfOneReplyAllAnswerFromTheDatabase`, `TestTools_APP05_SearchFiltersByGenreAndStockAndListsTheCheapestFirst`, `TestTools_APP05_SearchMatchesPartOfTheTitleOrAuthorAndAHighestPrice`, `TestTools_APP05_ABroadSearchReturns10To15kTokensWithinTheResultLimit`, `TestTools_APP05_ReadsFindCustomersTheirOrdersBooksAndOrders` | |
| APP-06 | App: `TestConsole_APP06_AWriteShowsItsExactInputForApprovalAndRunsOnlyIfApproved`, `TestTools_APP06_AddCustomerAddsOneAndRefusesASecondWithTheSameEmail`, `TestTools_APP06_PlaceOrderTakesTheCopiesFromStockAndChargesTheCurrentPrices`, `TestTools_APP06_ConcurrentOrdersNeverTakeMoreCopiesThanAreInStock`, `TestTools_APP06_CancelOrderReturnsTheCopiesOnce`, `TestTools_APP06_RestockAddsCopies` | |
| APP-07 | App: `TestConsole_APP07_NotEnoughStockComesBackAsAnErrorResultAndTheModelRecoversInTheSameReply`, `TestTools_APP07_BusinessRuleFailuresAreErrorsAndChangeNothing`, `TestTools_APP07_UnknownIDsAreErrors` | |
| APP-08 | App: `TestTools_APP08_TheOnlyTextInputsAreKnownValues`, `TestTools_APP08_EveryQueryRunsAConstant` (static, no database), `TestTools_APP08_SearchTextIsTakenLiterallySoAWildcardOrBackslashMatchesOnlyItself` | Inspection |
| APP-09 | App: `TestConsole_APP09_AMultiStepRequestFindsSearchesOrdersAfterApprovalAndAnswers` | Live, once, with `docker compose up` (Go S06's pull request) |
| APP-10 | App: `TestConsole_APP10_QuitRestartAndResumeContinuesTheSessionWithItsPrefixByteIdentical`, `TestConsole_APP10_ACrashMidReplyLosesAtMostTheStepInFlightAndTheSessionResumes` (the application, a child process, killed at the approval prompt; the next start resumes with the call answered as interrupted), `TestConsole_APP10_ASessionWhoseAgentChangedIsRefusedAndANewOneIsOffered`, `TestConsole_APP10_AConsoleWhoseSessionChangedElsewhereSaysSoAndDoesNotOverwriteIt`, `TestSessions_APP10_ASaveNeverOverwritesAnotherConsolesChangesOrAnotherSession`, `TestSessions_TEST07_ThePrefixStaysByteIdenticalAcrossSavesToTheSessionsTableAndResumes`; Core: `TestRun_APP10_ASessionDotNetSavedMidReplyResumesWithItsPrefixAndInterruptedCallsAnswered`, `TestRun_APP10_ASessionSavedMidReplyIsWrittenAsTheSharedFixtureHoldsIt` (shared `testdata/session/`; .NET's `SharedSessionTests` resumes Go's), `TestRun_APP10_ACompleteConversationGetsNoInterruptedResults` | Live, once: quit, restart, `/resume`, cache reads on the first call (Go S08's pull request) |
| APP-13 | App: `TestConsole_APP13_TheRunContextNamesTheDateAndStaffMemberAndIsSentAgainOnlyOnANewDay` (`testing/synctest`'s clock) | |
| APP-14 | App: `TestConsole_APP14_TheStatusLineAndCostShowTheTokensCacheShareAndCostOfTheReplyAndTheSession`, `TestConsole_APP14_AReplyThatReachesItsBudgetStopsAndSaysWhyAndItsOutputLimitIsLoweredToWhatIsLeft`, `TestConsole_APP14_ASessionThatReachesItsBudgetStopsTheNextReplyBeforeAnyModelCall` | Live, once: the status line after each reply (Go S08's pull request) |
| APP-16 | App: `TestConsole_APP16_APP20_AuditShowsTheSessionsEntriesByRunEachLinkedToItsTraceWhichTheLogsJoin` (the audit table on pgx, every row with its trace and span; `/audit` grouped by run with time, kind, tool, outcome, approvals, tokens and cost), `TestConsole_APP16_AnAuditTrailThatCannotBeReadIsSaidSoAndTheSessionGoesOn` | Live, once: an `/audit` entry's link opens its trace (Go S07's pull request) |
| APP-17 | App: `TestConsole_APP17_HIST04_DemoModeCompactsAndClearsEarlyAndReportsEachOnceInTheConsoleAndTheAudit` (`--demo`'s thresholds in every request, the `~` lines, a repeated clearing shown once, the `/audit` entries), `TestConsole_HIST01_HIST02_OutsideDemoModeCompactionAndClearingComeLate` | Live, once: `--demo`, the four catalogue searches compacted 50,550 tokens into 1,958 and the two lookup turns cleared 9 tool calls (33,451 tokens), for $0.36 (Go S10's pull request) |
| APP-18 | App: `TestConsole_APP18_WithTheDatabaseDownToolsReturnErrorsAndOnceItIsBackTheSessionWorksAgain`, `TestTools_APP18_WithTheDatabaseDownToolsFailAndOnceItIsBackTheyWorkAgain` | |
| APP-20 | App: `TestConsole_APP16_APP20_AuditShowsTheSessionsEntriesByRunEachLinkedToItsTraceWhichTheLogsJoin` (each reply a span, the run's trace under it, the logs in that trace) | Live, once: an APP-09 reply as one trace with its model and tool spans, metrics and logs, on the compose file's dashboard (Go S07's pull request) |

## Generality (GEN)

| ID | Tests | Also checked by |
|---|---|---|
| GEN-02 | Core: `TestRun_GEN02_GEN03_AgentOfModelAndInstructionsRunsStatelessly`; the tool tests run without approver or audit sink unless they test one | |
| GEN-03 | Core: `TestRun_GEN02_GEN03_AgentOfModelAndInstructionsRunsStatelessly` (stateless); `TestRun_AGT02_MultiTurnRunCompletesWithText` (stateful) | |
| GEN-04 | Core: `TestRun_GEN04_AnUnattendedRunDeniesApprovalRequiringCallsAndTellsTheModelWhy`, `TestRun_TOOL04_EVT01_AnApprovedCallRunsAndEachStepIsAnEvent` (interactive); Kit: `ExampleNewApprover` | |

## Agent and turn loop (AGT)

| ID | Tests | Also checked by |
|---|---|---|
| AGT-01 | Core: `TestNewAgent_AGT01_RejectsAnInvalidDefinition`, `TestNewAgent_AGT01_TheDefinitionDoesNotChangeWithTheCallersTools`; `TestRun_AGT04_HundredConcurrentRunsOfOneAgentEachKeepTheirConversation` (shared under `-race`) | |
| AGT-02 | Core: `TestRun_AGT02_MultiTurnRunCompletesWithText`, `TestRun_AGT02_TheRunCallsToolsThenTheModelAgainUntilItEnds`; `ExampleAgent_Run`, `ExampleNewTool` | |
| AGT-03 | Core: `TestRun_AGT03_EveryFinishReasonMapsToItsResultAndTheReplyIsKept`, `TestRun_AGT03_ARunWithoutAReplyFailsOrEndsAndAppendsNothing`, `TestResult_AGT03_StatusesAndReasonsPrintTheirNames`, `TestRun_AGT03_AReplyThatCallsToolsIsNeverLeftWithoutResults` (a reply whose calls will not run is not kept), `TestRun_AGT03_ARunThatKeepsCallingToolsStopsAtTheIterationLimit` | |
| AGT-04 | Core: `TestRun_AGT04_HundredConcurrentRunsOfOneAgentEachKeepTheirConversation`, `TestRun_AGT04_ASecondRunOnAConversationInUseFails` | |
| AGT-05 | Core: `TestRun_AGT05_CancellingMidStreamAppendsNothingAndTheNextRequestIsValid`, `TestRun_AGT05_ARunCancelledBeforeItStartsCallsNoModel`, `TestRun_AGT05_CancellingStopsTheRunEvenIfTheModelIgnoresIt`, `TestRun_AGT05_CancellingDuringToolsKeepsFinishedResultsAndCancelsTheRest`, `TestRun_AGT05_EVT01_AConsumerThatBreaksDuringToolsStopsThemAndKeepsAValidConversation`, `TestRun_TEST07_GeneratedRunsKeepTheConversationValidAndNoWriteRunsUnaudited` | |
| AGT-06 | Core: `TestConversation_AGT06_JSONRoundTripKeepsEveryBlockByteForByte` (the canonical-form pin: `<`, `&`, non-ASCII, `\u` escapes, and stores that rewrite the JSON), `TestConversation_AGT06_TheJSONFormIsPlainAndTheSameAsDotNets` (shared `testdata/conversation/`, compared parsed), `TestConversation_AGT06_AToolCallAndItsResultAreKeptInDotNetsForm`, `TestConversation_AGT06_UnmarshalRejectsAnInvalidConversation`, `FuzzConversation_AGT06_UnmarshalThenMarshalIsAFixedPoint`, `TestRun_AGT06_ReplyBlocksAreAppendedExactlyAsReceived`, `TestRun_APP10_ASessionDotNetSavedMidReplyResumesWithItsPrefixAndInterruptedCallsAnswered` (a .NET-written session from the shared `testdata/session/`); Claude: `TestModel_MDL05_AGT06_AReplyIsReplayedUnchangedAfterSaveAndResume` | |
| AGT-08 | Core: `TestRun_EVT01_AGT08_EventsStreamThenEachAppendThenResult`, `TestRun_AGT08_AConsumerThatBreaksAtAnAppendKeepsAValidConversation` | |

## Models (MDL)

| ID | Tests | Also checked by |
|---|---|---|
| MDL-01 | Claude: `TestModel_MDL01_MDL05_TextStreamsAndEachBlockComesCompleteInTheCanonicalForm`, `TestModel_MDL01_StopReasonsMapByTheirWordAndAnUnknownOneKeepsIt` | |
| MDL-02 | Claude: `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (streamed, beta surface), `TestNew_MDL02_TheAPIKeyGivenIsSent`; Deps: `TestDependencies_TEST05_ModuleKeepsTheRules` (the SDK in `claude` only) | `examples/hello`, run live |
| MDL-03 | Claude: `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (effort, output limit, adaptive thinking), `TestNew_MDL03_SettingsNameEverySettingThatShapesARequest`, `TestNew_MDL03_RejectsAnInvalidSetting` | |
| MDL-04 | Claude: `TestModel_MDL04_ATransientFailureWaitsThenSucceeds` (`Retry-After` in seconds, as a date, and capped, also past what a duration holds; exponential backoff; 408, 409, 429, 500, 529; an error event and a dropped connection mid-stream), `TestModel_MDL04_AFailureMidStreamRestartsTheReplyKeepingTheTokensItUsed`, `TestModel_MDL04_ARestartedReplyReachesTheRunOnceAndTheHostIsTold`, `TestModel_MDL04_AFailureThatPersistsIsTransientAfterEveryAttempt`, `TestModel_MDL04_CancellingDuringARetryWaitEndsTheCallAtOnce`, `TestModel_MDL04_ErrorsRetryingCannotFixAreClassifiedAndNotRetried`, `TestModel_MDL04_APromptLongerThanTheContextWindowFinishesAsContextFull`, `TestModel_MDL04_AFailedRunCarriesTheFailuresClass` (all timed with `testing/synctest`); Core: `TestRun_MDL04_ARetriedReplyRestartsAndOnlyTheLastAttemptIsAppended` | |
| MDL-05 | Claude: `TestModel_MDL01_MDL05_TextStreamsAndEachBlockComesCompleteInTheCanonicalForm`, `TestModel_MDL05_BlocksAreStoredWithHTMLCharactersEscapedAndOtherEscapesAsReceived`, `TestModel_MDL05_BlocksTheDotNetImplementationStoredReachTheWireByteForByte` (shared `testdata/`), `TestModel_MDL05_AGT06_AReplyIsReplayedUnchangedAfterSaveAndResume`, `TestModel_HIST01_MDL05_TheCompactionBlockIsKeptAndReplayedAsReceivedAndTheRunReportsIt`, `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (stored blocks on the wire byte for byte) | |
| MDL-06 | Claude: `TestModel_MDL06_ARefusalFinishesWithItsCategoryAndTheRunStops` | |

## Context and caching (CTX)

| ID | Tests | Also checked by |
|---|---|---|
| CTX-01 | Core: `TestRun_CTX01_RequestHasSortedToolsInstructionsThenUserAndOperatorMessages`, `TestRun_CTX01_ToolsGivenInAnotherOrderAreTheSamePrefix`; Claude: `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (the shared golden layout, compared parsed), `TestModel_CTX01_ARequestWithoutToolsIsLaidOutAsDotNetsIs` (`"tools":[]`, as .NET sends it) | |
| CTX-02 | Claude: `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (run context as a system message after each user message) | `examples/hello`, run live |
| CTX-03 | Claude: `TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout` (cache point on the instructions, automatic caching, two lifetimes), `TestNew_MDL03_RejectsAnInvalidSetting` (prefix shorter than the tail) | `examples/hello`, run live: cache reads from the second message |
| CTX-04 | Core: `TestRun_CTX04_AChangedPrefixFailsWithPrefixMismatchBeforeAnyModelCall` (instructions, tool description, schema, added and removed tool, model settings), `TestAgent_CTX04_TheFingerprintIsDotNetsByteForByte` (shared `testdata/session/prefix.json`, written by .NET: every character JSON escapes, a schema with odd spacing and an escape), `TestAgent_CTX04_ASchemaIsFingerprintedAsGivenNotReEncoded`, `TestAgent_CTX04_HIST01_TheFingerprintWithContextManagementIsDotNetsByteForByte` (the same file's context management settings, checked by .NET's `SharedSessionTests` too), `TestRun_CTX04_HIST01_ContextManagementIsPartOfThePrefix`, `ExampleAgent_CanContinue` | Every code point escaped as .NET's encoder does, checked once against a dump of .NET's output (Go S08's pull request) |
| CTX-05 | Claude: `TestModel_CTX05_UsageCountsCacheReadsAndWritesAndAddsUpEveryIteration` (reads and writes, the hour-long writes among them), `TestModel_HIST04_ACompactionIsReportedFromItsIterationAndPricedWithTheReply`; Core: `TestRun_EVT02_CTX05_MetricsCountTokensCostCacheHitRatioToolOutcomesApprovalsAndResults` (the hit ratio by agent and model) | |
| CTX-06 | Core: `TestRun_CTX06_ResultsOfOneReplyReturnInOneMessageInCallOrder` (the first call finishes last), `TestRun_TOOL03_ReadsOverlapAndWritesRunAloneInOrder`; Kit: `TestCheckConversation_TEST07_AnswersEveryToolCallOnceInOrder`; Claude: `TestModel_CTX06_ToolResultsGoOutAsOneUserMessageOfToolResultBlocks` | |

## History compaction (HIST)

| ID | Tests | Also checked by |
|---|---|---|
| HIST-01 | Claude: `TestModel_HIST01_HIST02_ContextManagementAsksForClearingThenThresholdCompactionWithTheirBetas` (the request's `context_management` as .NET sends it, and its betas), `TestModel_HIST01_WithoutContextManagementTheRequestHasNoneAndNoBetas`, `TestModel_HIST01_MDL05_TheCompactionBlockIsKeptAndReplayedAsReceivedAndTheRunReportsIt` (shared `testdata/claude/compaction-iterations.sse`); Core: `TestRun_HIST01_HIST04_ACompactionAndAClearingAreReportedAuditedAndTheBlockReplayedAsReceived`, `TestRun_CTX04_HIST01_ContextManagementIsPartOfThePrefix`, `TestAgent_CTX04_HIST01_TheFingerprintWithContextManagementIsDotNetsByteForByte` | Live, once, in demo mode (APP-17). On-demand compaction is not used (D12): live, it replied with only the summary block, and a request that appended that block after the messages it summarized was refused (`compaction_block_misplaced`) |
| HIST-02 | Claude: `TestModel_HIST01_HIST02_ContextManagementAsksForClearingThenThresholdCompactionWithTheirBetas`, `TestModel_HIST04_AClearingIsReportedFromTheAppliedEdits` (shared `testdata/claude/clearing.sse`); App: `TestConsole_HIST01_HIST02_OutsideDemoModeCompactionAndClearingComeLate` | Live, once, in demo mode (APP-17) |
| HIST-03 | Core: `TestRun_HIST03_AProviderWithoutCompactionEndsWithContextFullAndAppendsNothing`, `TestNewAgent_HIST03_ContextManagementNeedsValidSettingsAndTheProvidersSupport`; Claude: `TestModel_EVT02_HIST03_InfoNamesTheProviderAndModelWithItsListPriceAndContextManagement`, `TestModel_MDL04_APromptLongerThanTheContextWindowFinishesAsContextFull` | |
| HIST-04 | Core: `TestRun_HIST01_HIST04_ACompactionAndAClearingAreReportedAuditedAndTheBlockReplayedAsReceived` (events and audit entries), `TestRun_EVT02_HIST04_ACompactionAndAClearingShowOnTheModelCallsSpanAndAreCounted`; Claude: `TestModel_HIST04_ACompactionIsReportedFromItsIterationAndPricedWithTheReply`, `TestModel_HIST04_AClearingIsReportedFromTheAppliedEdits`; App: `TestConsole_APP17_HIST04_DemoModeCompactsAndClearsEarlyAndReportsEachOnceInTheConsoleAndTheAudit` | |

## Tools (TOOL)

| ID | Tests | Also checked by |
|---|---|---|
| TOOL-01 | Core: `TestNewTool_TOOL01_DerivesTheSchemaFromTheInputType`, `TestNewTool_TOOL01_RefusesATypeWithoutASchema`, `TestNewTool_TOOL01_RunsOnTheDecodedInputAndSendsItsResultAsJSON`; `ExampleNewTool` | |
| TOOL-02 | Core: `TestRun_TOOL02_TOOL04_TOOL05_FailuresComeBackAsErrorResultsAndTheRunContinues` (input not JSON, invalid input, unknown tool), `TestValidate_TOOL02_ChecksEachKeywordOfTheSubset`, `FuzzValidate_TOOL02_NeverPanicsAndIsDeterministic` | |
| TOOL-03 | Core: `TestRun_TOOL03_ReadsOverlapAndWritesRunAloneInOrder` (under `testing/synctest`: reads that wait for each other would deadlock if run one after the other) | |
| TOOL-04 | Core: `TestRun_TOOL02_TOOL04_TOOL05_FailuresComeBackAsErrorResultsAndTheRunContinues` (denied, with and without a reason; a failing and a panicking approver), `TestRun_TOOL04_EVT01_AnApprovedCallRunsAndEachStepIsAnEvent` | |
| TOOL-05 | Core: `TestRun_TOOL02_TOOL04_TOOL05_FailuresComeBackAsErrorResultsAndTheRunContinues` (a failing and a panicking handler) | |
| TOOL-06 | Core: `TestRun_TOOL06_ALongResultIsTruncatedAndTheModelIsTold` (never inside a character), `TestRun_TOOL06_EVT02_AResultOfExactlyTheLimitIsKeptWholeAndNotMarkedTruncated` | |

## Events and observability (EVT)

| ID | Tests | Also checked by |
|---|---|---|
| EVT-01 | Core: `TestRun_EVT01_AGT08_EventsStreamThenEachAppendThenResult`, `TestRun_EVT01_AConsumerThatBreaksMidRunLeavesNoGoroutine` (with `goleak` in `TestMain`), `TestRun_EVT01_EventsRangeOnceAndTheResultWaitsForThem`; `ExampleAgent_Stream`; Claude: `TestModel_EVT01_AConsumerThatStopsEarlyEndsTheCall` (with `goleak`); Core: `TestRun_TOOL04_EVT01_AnApprovedCallRunsAndEachStepIsAnEvent` (tool and approval events), `TestRun_AGT05_EVT01_AConsumerThatBreaksDuringToolsStopsThemAndKeepsAValidConversation`, `TestRun_HIST01_HIST04_ACompactionAndAClearingAreReportedAuditedAndTheBlockReplayedAsReceived` (compaction and clearing events) | |
| EVT-02 | Core: `TestRun_EVT02_AUD03_ARunIsOneTraceWithASpanPerModelAndToolCallAndItsAuditEntriesPointIntoIt` (span tree, names and attributes, as .NET's), `TestRun_EVT02_CTX05_MetricsCountTokensCostCacheHitRatioToolOutcomesApprovalsAndResults`, `TestTelemetry_EVT02_InstrumentsHaveTheNamesAndUnitsOfDotNets`, `TestRun_EVT02_ARetryIsCountedAndTheFailedAttemptsTokensAreKept`, `TestRun_EVT02_TimeToFirstTokenIsTheWaitForTheFirstTextOfTheAttemptThatSucceeded`, `TestRun_EVT02_ATimeToFirstTokenOfZeroIsKept`, `TestRun_EVT02_AnUnknownFinishIsNamedByTheProvidersWordAndACallWithoutTokensHasNoHitRatio`, `TestRun_EVT02_EVT03_AFailedModelCallMarksItsSpanAndTheRunsWithoutTheSecret`, `TestRun_EVT02_ARunsSpanIsUnderTheHostsSpanAndWithoutProvidersEntriesCarryTheHostsTrace`, `TestRun_EVT02_ACallCancelledWhileWaitingForApprovalRecordsTheWaitOnly`, `TestRun_EVT02_HIST04_ACompactionAndAClearingShowOnTheModelCallsSpanAndAreCounted` (all on the SDK's in-memory exporter and reader, tests only); Claude: `TestModel_EVT02_HIST03_InfoNamesTheProviderAndModelWithItsListPriceAndContextManagement` | Live, once, on the dashboard (Go S07's pull request) |
| EVT-03 | Core: `TestRun_AUD05_EVT03_SecretsNeverReachTheTrailEventsOrResultsAndLongTextIsCut` (tool calls in events, tool results, audit, the final text; as written and as escaped in JSON), `TestRun_EVT03_AUD05_EveryFormOfASecretIsRedactedWhole` (a secret that starts with another, escaped non-ASCII in either case, escaped `/`, surrogate pairs), `TestRun_TEST07_EVT02_GeneratedSecretsNeverReachResultsEventsTelemetryOrTheTrail`, `TestRun_TOOL04_EVT01_AnApprovedCallRunsAndEachStepIsAnEvent`, `TestRun_EVT03_EVT04_TelemetryCarriesNoTextUnlessTheHostOptsInAndNeverASecret`, `TestRun_EVT02_EVT03_AFailedModelCallMarksItsSpanAndTheRunsWithoutTheSecret` | |
| EVT-04 | Core: `TestRun_EVT03_EVT04_TelemetryCarriesNoTextUnlessTheHostOptsInAndNeverASecret` (no text by default; with the host's opt-in, text redacted) | |

## Budgets (BUD)

| ID | Tests | Also checked by |
|---|---|---|
| BUD-01 | Core: `TestRun_BUD01_ACostBudgetUsedUpStopsTheRunBeforeTheNextCallWithItsReason`, `TestRun_BUD01_EachCallsOutputLimitIsLoweredToWhatTheRemainingCostAndTokensAllow`, `TestRun_BUD01_AReplyCutShortByTheLoweredLimitStopsForTheBudgetAndOneCutByTheModelsOwnDoesNot`, `TestRun_BUD01_ModelCallTimeAndTokenLimitsStopTheRunBeforeTheNextCall`, `TestRun_BUD01_AUsedUpBudgetStopsBeforeTheFirstCallAndAppendsNothing`, `TestRun_BUD01_ACostBudgetIsUsedUpWhenWhatIsLeftBuysLessThanOneOutputToken`, `TestRun_BUD01_ACostBudgetNeedsAModelWithAPrice`, `TestRun_BUD01_ACallBudgetUsedUpOnTheLastAllowedCallStopsForTheBudgetNotTheIterationLimit`, `TestRun_BUD01_AReplyCutByTheModelsOwnLimitStopsForThatLimitEvenWhenACallBudgetIsUsedUp`, `TestRun_BUD01_ATimeBudgetIsUsedUpTheMomentItIsReached`, `TestRun_BUD01_ATokenBudgetAloneLowersTheOutputLimitToTheTokensLeft`, `TestRun_BUD01_TheCostBudgetAtItsBoundaries` (one output token left; nothing left; output that costs nothing), `TestRun_TEST07_ABudgetIsOvershotByAtMostOneCallsInputOrTwiceThatWhenItCompacts`, `ExampleLimits`; Claude: `TestModel_BUD01_AnOutputLimitTheBudgetLowersIsSentAndAHigherOneIsNot` | |
| BUD-02 | Core: `TestRun_BUD02_BUD03_AResultReportsTokensCostCallsAndDurationAndEachUsageEventItsCost`; Claude: `TestModel_EVT02_InfoNamesTheProviderAndModelWithItsListPrice` | A host prices a model through its `Info`, as the tests' scripted models do |
| BUD-03 | Core: `TestRun_BUD02_BUD03_AResultReportsTokensCostCallsAndDurationAndEachUsageEventItsCost` | |

## Audit (AUD)

| ID | Tests | Also checked by |
|---|---|---|
| AUD-01 | Core: `TestRun_AUD01_AUD03_TheTrailRecordsTheRunAndEachStepInOrder` (run, approvals, tool calls), `TestRun_AUD01_TheEndEntrySaysHowTheRunEnded`, `TestRun_AUD01_InterruptedCallsAreAuditedAndReportedBeforeTheRunGoesOn`, `TestRun_HIST01_HIST04_ACompactionAndAClearingAreReportedAuditedAndTheBlockReplayedAsReceived` (compaction and clearing) | Memory and MCP entries come with their slices |
| AUD-02 | Core: `TestRun_AUD02_AWriteWhoseAttemptCannotBeRecordedNeverRuns`, `TestRun_AUD02_AWriteRunsOnlyAfterItsAttemptIsRecorded`, `TestRun_TEST07_GeneratedRunsKeepTheConversationValidAndNoWriteRunsUnaudited` | |
| AUD-03 | Core: `TestRun_AUD01_AUD03_TheTrailRecordsTheRunAndEachStepInOrder` (time, sequence, run, conversation, agent), `TestRun_EVT02_AUD03_ARunIsOneTraceWithASpanPerModelAndToolCallAndItsAuditEntriesPointIntoIt` (the run's trace, and the span of each step) | Memory scope comes with Go S09 |
| AUD-04 | Core: `TestJSONLinesSink_AUD04_AppendsOneLinePerEntryAndReadsBack`, `TestJSONLinesSink_AUD04_WritesEveryFieldAndReportsAFailure`; `ExampleNewJSONLinesSink` | |
| AUD-05 | Core: `TestRun_AUD05_EVT03_SecretsNeverReachTheTrailEventsOrResultsAndLongTextIsCut`, `TestRun_AUD05_TextOfExactlyTheAuditLimitIsKeptWhole`, `TestRun_EVT03_AUD05_EveryFormOfASecretIsRedactedWhole`, `TestRun_TEST07_EVT02_GeneratedSecretsNeverReachResultsEventsTelemetryOrTheTrail` | |
| AUD-06 | Core: `TestRun_AUD06_AnAuditSinkFailureAndAWriteItBlocksShowInTelemetry` (a failure counted and marked on its step's span; a blocked write's outcome and error type) | |

## Principles

| Principle | Tests |
|---|---|
| 11, small core | Deps: `TestCoreSize_StaysWithinItsLineBudget` (G13) |

## Tests (TEST)

| ID | Tests | Also checked by |
|---|---|---|
| TEST-01 | Kit: `TestModel_TEST01_RepliesInOrderAndRecordsRequests`, `TestModel_TEST01_RejectsRoleSequencesTheProviderRejectsAndKeepsTheReply`, `TestTextBlock_TEST01_StoresRawJSONInTheCanonicalForm`, `TestApprover_TEST01_AnswersInOrderAndRecordsEachCall`, `TestToolUseBlock_TEST01_StoresTheCallAndItsRawJSON` | The fake MCP server comes in Go S11 |
| TEST-02 | Core: `TestRun_TEST02_ThePrefixIsStableAcrossTurnsAndAcrossSaveRestartAndResume`; Kit: `TestCheckPrefix_TEST02_ReportsEachKindOfChange`, `ExampleCheckPrefix` | |
| TEST-03 | — | The Go workflow (`.github/workflows/go.yml`): `go-ubuntu` and `go-windows`, offline |
| TEST-05 | Deps: `TestDependencies_TEST05_ModuleKeepsTheRules`, `TestDependencies_TEST05_FixturesBreakingTheRulesFail` (the core's own rule, D15 and G6, as well) | |
| TEST-06 | Core: every `TestRun_EVT02_…`, `TestRun_AUD06_…` and `TestRun_EVT03_EVT04_…` test above, on the OpenTelemetry SDK's in-memory exporter and manual reader (tests only) | |
| TEST-07 | Core: `TestRun_TEST07_EVT02_GeneratedSecretsNeverReachResultsEventsTelemetryOrTheTrail` (`rapid`: generated secrets, prefixes of each other included, in generated JSON forms; once each is replaced whole only the text around them is left, in results, events, telemetry with and without text, the trail and the final text, also from a tool error, a denial's reason and a model failure), `TestRun_TEST07_GeneratedRunsKeepTheConversationValidAndNoWriteRunsUnaudited` (`rapid`: sequences of runs, model failures, cut-off replies, cancels and breaks at any event, tool calls that succeed, fail, panic, are unknown, invalid, denied or unaudited; after each run the conversation passes the kit's check, every call answered once, and the prefix is stable), `TestRun_TEST07_ABudgetIsOvershotByAtMostOneCallsInputOrTwiceThatWhenItCompacts` (`rapid`: cost, token and call limits over generated calls); App: `TestSessions_TEST07_ThePrefixStaysByteIdenticalAcrossSavesToTheSessionsTableAndResumes` (`rapid`: generated replies saved to the sessions table after every step and resumed with an agent built afresh, some crashing while their tool runs); Kit: `TestCheckConversation_TEST07_AnswersEveryToolCallOnceInOrder`, `TestModel_TEST01_RejectsRoleSequencesTheProviderRejectsAndKeepsTheReply` | CI runs the property tests with a fixed seed (`go.yml`); a failure prints its seed. Memory paths come with Go S09 |
| TEST-08 | Core: `TestValidate_TEST08_AgreesWithAReferenceValidator` (`rapid`, against `santhosh-tekuri/jsonschema/v6` under draft 2020-12), `TestValidate_TEST08_RefusesASchemaOutsideTheSubset`, `TestNewAgent_TEST08_RefusesAToolOutsideTheSubset` | |
| TEST-09 | App: every `TestConsole_…` test (APP-01, APP-02, APP-03, APP-05, APP-06, APP-07, APP-09, APP-10, APP-13, APP-14, APP-16, APP-17, APP-18, APP-20) | `go-ubuntu` runs them; elsewhere they skip, saying why |
