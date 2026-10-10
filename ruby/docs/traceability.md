# Ruby traceability

Each phase 1 requirement of [`REQUIREMENTS.md`](../../REQUIREMENTS.md), and the tests that check it in the Ruby
implementation, or how it is checked otherwise, as [`docs/traceability.md`](../../docs/traceability.md) does for .NET.
It grows with each slice; Ruby S13 completes it.

Tests are under `ruby/`, shortened as:

| Short | Where |
|---|---|
| Deps | `test/dependencies_test.rb` (`DependenciesTest`), fixture gems in `test/fixtures/dependencies/` |
| Agent | `officina/test/agent_test.rb` (`AgentTest`) |
| Conv | `officina/test/conversation_test.rb` (`ConversationTest`), with the shared `testdata/conversation/conversation.json` |
| Prefix | `officina/test/prefix_stability_test.rb` (`PrefixStabilityTest`) |
| Run | `officina/test/run_test.rb` (`RunTest`) |
| Kit | `officina-testing/test/scripted_model_test.rb` (`ScriptedModelTest`) |
| Cancel | `officina/test/cancellation_callback_test.rb` (`CancellationCallbackTest`) |
| CModel | `officina-claude/test/model_test.rb` (`ModelTest`) |
| CReq | `officina-claude/test/request_test.rb` (`RequestTest`), with the shared `testdata/claude/` layouts, blocks and SSE fixtures, on the fake API |
| CStream | `officina-claude/test/stream_test.rb` (`StreamTest`) |
| CRetry | `officina-claude/test/retry_test.rb` (`RetryTest`) |
| Hello | `examples/hello/hello_test.rb` (`HelloTest`) |

## Generality (GEN)

| ID | Tests | Also checked by |
|---|---|---|
| GEN-02 | Agent: `test_gen02_an_agent_needs_only_a_model_and_instructions` | |
| GEN-03 | Run: `test_gen03_a_run_is_stateless_on_a_new_conversation_and_stateful_on_a_kept_one` | |

## Agent and turn loop (AGT)

| ID | Tests | Also checked by |
|---|---|---|
| AGT-01 | Agent: `test_agt01_an_agent_is_frozen_with_its_tools_sorted_by_name`, `test_agt01_a_definition_that_cannot_work_is_refused`; CModel: `test_agt01_a_model_and_its_settings_are_frozen` | |
| AGT-02 | Run: `test_agt02_a_scripted_multi_turn_run_completes_with_text`, `test_agt02_each_tool_call_gets_a_result_and_the_model_is_called_again` | |
| AGT-03 | Run: `test_agt03_every_stop_reason_maps_to_its_result`, `test_agt03_a_result_carries_the_usage_of_every_model_call`, `test_agt03_a_run_stops_at_the_iteration_limit_after_25_model_calls` | |
| AGT-04 | Run: `test_agt04_one_hundred_concurrent_runs_of_one_agent_each_complete_on_their_own_conversation`, `test_agt04_a_second_run_on_a_conversation_in_use_is_refused` | |
| AGT-05 | Run: `test_agt05_cancelling_mid_stream_appends_nothing`, `test_agt05_a_run_cancelled_before_it_starts_calls_no_model`, `test_agt05_cancelling_after_a_reply_with_calls_answers_them_and_calls_the_model_no_more`, `test_agt05_a_block_that_breaks_mid_run_ends_it_and_leaves_no_thread`, `test_agt05_breaking_at_a_reply_with_calls_still_answers_them`, `test_agt05_raising_at_a_reply_with_calls_still_answers_them`; Cancel: `test_agt05_cancelling_calls_each_callback_once`, `test_agt05_a_callback_given_after_cancelling_runs_at_once`, `test_agt05_a_callback_given_after_cancelling_is_not_called_again`, `test_agt05_no_callback_runs_until_cancelling`; CStream: `test_agt05_cancelling_mid_stream_ends_the_call_with_no_reply_and_closes_the_connection`, `test_agt05_a_call_cancelled_before_it_starts_sends_nothing`; CRetry: `test_mdl04_agt05_cancelling_during_a_retry_wait_ends_the_call_at_once` | The thread-leak check, after every test |
| AGT-06 | Conv: `test_agt06_the_shared_conversation_reads_and_writes_back_byte_for_byte`, `test_agt06_a_round_trip_keeps_blocks_with_escapes_and_non_ascii_byte_for_byte`, `test_agt06_any_text_round_trips` (property), `test_agt06_json_that_is_not_a_conversation_is_refused` | |
| AGT-08 | Run: `test_agt08_every_append_is_reported_once_all_of_a_step_is_appended` | |

## Models (MDL)

| ID | Tests | Also checked by |
|---|---|---|
| MDL-01 | CStream: `test_mdl01_mdl05_ctx05_text_streams_and_the_reply_keeps_each_block_with_the_calls_usage`, `test_mdl01_stop_reasons_map_by_their_word_and_an_unknown_one_keeps_it`, `test_mdl01_a_reply_returns_once_it_stops_and_closes_the_connection` | |
| MDL-02 | CReq: `test_ctx01_ctx02_ctx03_mdl02_mdl03_a_runs_request_matches_the_shared_golden_layout` (streamed), `test_mdl02_the_api_key_given_is_sent`; Hello: `test_mdl02_hello_chats_until_an_empty_line_and_shows_each_calls_tokens` | Deps: `test_test05_the_anthropic_sdk_used_anywhere_but_the_claude_gem_fails`; the hello sample, run live by hand |
| MDL-03 | CModel: `test_mdl03_settings_name_every_setting_that_shapes_a_request` (.NET's and Go's settings string), `test_mdl03_an_invalid_setting_is_refused`; CReq: `test_ctx01_ctx02_ctx03_mdl02_mdl03_a_runs_request_matches_the_shared_golden_layout` | |
| MDL-04 | CRetry: `test_mdl04_a_transient_failure_waits_then_succeeds`, `test_mdl04_backoff_jitter_spreads_the_waits_over_the_upper_half_of_each_step`, `test_mdl04_a_failure_mid_stream_restarts_the_reply_keeping_the_tokens_it_used`, `test_mdl04_a_failure_that_persists_is_transient_after_every_attempt`, `test_mdl04_errors_retrying_cannot_fix_are_classified_and_not_retried`, `test_mdl04_credentials_the_sdk_cannot_find_are_an_authentication_error_and_not_retried`, `test_mdl04_a_prompt_longer_than_the_context_window_stops_for_context_full`, `test_mdl04_an_invalid_request_that_does_not_say_so_in_the_apis_shape_is_not_context_full`, `test_mdl04_agt05_cancelling_during_a_retry_wait_ends_the_call_at_once` | |
| MDL-05 | CStream: `test_mdl05_each_block_is_kept_as_the_gem_writes_it_in_the_canonical_form_with_its_text_or_call`; CReq: `test_mdl05_blocks_the_dotnet_implementation_stored_reach_the_wire_byte_for_byte` (`testdata/claude/blocks.json`), `test_mdl05_a_reply_is_stored_canonical_and_replayed_byte_for_byte` | |
| MDL-06 | CStream: `test_mdl06_a_refusal_stops_with_its_category`, `test_mdl06_a_refusal_stops_the_run_with_its_category`, `test_mdl06_a_refusal_with_no_category_stops_with_no_detail` | |

## Context and caching (CTX)

| ID | Tests | Also checked by |
|---|---|---|
| CTX-01 | Agent: `test_ctx01_the_fingerprint_is_the_one_every_implementation_computes` (against `testdata/session/prefix.json`); Run: `test_ctx01_the_run_context_follows_the_users_message_as_an_operator_message`; CReq: `test_ctx01_ctx02_ctx03_mdl02_mdl03_a_runs_request_matches_the_shared_golden_layout` (`testdata/claude/request-layout.json`), `test_ctx01_a_request_without_tools_is_laid_out_as_dotnets_is` | |
| CTX-02 | CReq: `test_ctx01_ctx02_ctx03_mdl02_mdl03_a_runs_request_matches_the_shared_golden_layout` (the run context as a system message after each user message); Hello: `test_mdl02_hello_chats_until_an_empty_line_and_shows_each_calls_tokens` | |
| CTX-03 | CReq: `test_ctx01_ctx02_ctx03_mdl02_mdl03_a_runs_request_matches_the_shared_golden_layout` (the cache point on the instructions, 1 h, and automatic caching of the tail, 5 min) | The live hello sample: cache reads from the second message |
| CTX-04 | Agent: `test_ctx04_a_changed_tool_instruction_or_model_setting_fails_the_run_with_a_prefix_mismatch` | |
| CTX-05 | CStream: `test_mdl01_mdl05_ctx05_text_streams_and_the_reply_keeps_each_block_with_the_calls_usage`, `test_ctx05_a_message_delta_without_input_counts_keeps_those_the_call_started_with`, `test_ctx05_a_message_deltas_counts_replace_those_the_call_started_with` (usage per call, cache reads and writes; the telemetry part comes in S07) | |
| CTX-06 | CReq: `test_ctx06_tool_results_go_out_as_one_user_message_of_tool_result_blocks` | |

## Events and observability (EVT)

| ID | Tests | Also checked by |
|---|---|---|
| EVT-01 | Run: `test_evt01_a_run_streams_text_and_usage_then_returns_its_result`, `test_evt01_an_exception_from_the_hosts_block_is_the_hosts_own`; CStream: `test_evt01_what_the_consumers_block_raises_passes_through_is_not_retried_and_closes_the_connection`, `test_evt01_a_consumer_that_leaves_the_block_ends_the_call_and_closes_the_connection` | |

## Testing (TEST)

| ID | Tests | Also checked by |
|---|---|---|
| TEST-01 | Kit: `test_test01_it_streams_its_replies_in_order_and_records_each_request`, `test_test01_it_rejects_messages_the_provider_would_reject`, `test_test01_it_accepts_a_conversation_with_answered_calls_and_a_run_context`, `test_test01_a_run_on_a_model_with_no_reply_left_fails` (the scripted model; the scripted approver and fake MCP server come in S05 and S11) | |
| TEST-02 | Prefix: `test_test02_the_prefix_is_stable_across_the_calls_of_a_run_and_across_runs`, `test_test02_the_prefix_is_stable_across_a_save_a_new_process_and_a_resume`, `test_test02_the_check_finds_a_changed_prefix` | |
| TEST-03 | — | The Ruby workflow (`.github/workflows/ruby.yml`): `ruby-ubuntu` and `ruby-windows`, offline |
| TEST-05 | Deps: `test_test05_the_workspace_keeps_the_rule`, `test_test05_a_fixture_keeping_the_rule_passes`, `test_test05_the_anthropic_sdk_used_anywhere_but_the_claude_gem_fails`, `test_test05_the_core_using_more_than_its_rule_allows_fails` (the core's own rule, D15 and R6, and the other gems', as well) | |
