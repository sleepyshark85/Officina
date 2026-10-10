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
| Refusal | `officina/test/conversation_refusal_test.rb` (`ConversationRefusalTest`) |
| Usage | `officina/test/usage_test.rb` (`UsageTest`) |
| Prefix | `officina/test/prefix_stability_test.rb` (`PrefixStabilityTest`) |
| Kit | `officina-testing/test/scripted_model_test.rb` (`ScriptedModelTest`) |
| CModel | `officina-claude/test/model_test.rb` (`ModelTest`) |
| CReq | `officina-claude/test/request_test.rb` (`RequestTest`), with the shared `testdata/claude/` layouts, blocks and SSE fixtures, on the fake API |
| CStream | `officina-claude/test/stream_test.rb` (`StreamTest`) |
| CStop | `officina-claude/test/stop_test.rb` (`StopTest`) |
| CEnd | `officina-claude/test/early_end_test.rb` (`EarlyEndTest`) |
| CRetry | `officina-claude/test/retry_test.rb` (`RetryTest`) |
| CClass | `officina-claude/test/classification_test.rb` (`ClassificationTest`) |
| Hello | `examples/hello/hello_test.rb` (`HelloTest`) |
| Cancel | `officina/test/cancellation_callback_test.rb` (`CancellationCallbackTest`) |
| Stores | `officina/test/memory_store_contract.rb`, `memory_scope_contract.rb` and `memory_concurrency_contract.rb`, the tests every memory store passes, run by both `HashMemoryStoreTest` and `FileMemoryStoreTest` |
| HashStore | `officina/test/hash_memory_store_test.rb` (`HashMemoryStoreTest`) |
| FileStore | `officina/test/file_memory_store_test.rb` (`FileMemoryStoreTest`) |
| Rules | `officina/test/memory_rules_test.rb` (`MemoryRulesTest`) |
| Core | `officina/test/` |
| Loop | `officina/test/run_loop_test.rb` (`RunLoopTest`) |
| Leave | `officina/test/run_leaving_test.rb` (`RunLeavingTest`) |
| Events | `officina/test/run_events_test.rb` (`RunEventsTest`) |
| Conc | `officina/test/run_concurrency_test.rb` (`RunConcurrencyTest`) |
| Tool | `officina/test/tool_test.rb` (`ToolTest`) |
| Results | `officina/test/tool_call_results_test.rb` (`ToolCallResultsTest`) |
| Reads | `officina/test/tool_concurrency_test.rb` (`ToolConcurrencyTest`) |
| Approval | `officina/test/approval_test.rb` (`ApprovalTest`) |
| Stop | `officina/test/tool_cancelling_test.rb` (`ToolCancellingTest`) |
| Redact | `officina/test/redaction_test.rb` (`RedactionTest`) |
| Trail | `officina/test/audit_trail_test.rb` (`AuditTrailTest`) |
| Order | `officina/test/audit_order_test.rb` (`AuditOrderTest`) |
| End | `officina/test/audit_run_end_test.rb` (`AuditRunEndTest`) |
| Sink | `officina/test/json_lines_audit_sink_test.rb` (`JsonLinesAuditSinkTest`) |
| Prop | `officina/test/run_property_test.rb` (`RunPropertyTest`) |
| SProp | `officina/test/secrets_property_test.rb` (`SecretsPropertyTest`) |
| TTrace | `officina/test/telemetry_trace_test.rb` (`TelemetryTraceTest`). Every telemetry test collects through the SDK's in-memory span exporter and metric reader (`officina/test/support/collector.rb`), on the fake clock of `support/fake_clock.rb` where times count |
| TMetrics | `officina/test/telemetry_metrics_test.rb` (`TelemetryMetricsTest`) |
| TModel | `officina/test/telemetry_model_call_test.rb` (`TelemetryModelCallTest`) |
| TTool | `officina/test/telemetry_tool_call_test.rb` (`TelemetryToolCallTest`) |
| TNames | `officina/test/telemetry_names_test.rb` (`TelemetryNamesTest`), which parses .NET's `Telemetry.cs` and `RunEngine.cs` and Go's `telemetry.go` and `run.go` |
| TContent | `officina/test/telemetry_content_test.rb` (`TelemetryContentTest`) |
| Approver | `officina-testing/test/scripted_approver_test.rb` (`ScriptedApproverTest`) |
| App | `apps/bookshop/test/`: `CatalogueTest`, `CustomersTest`, `OrdersTest`, `DatabaseTest` (against the seeded database in Docker, Linux only, skipping elsewhere with the reason) and `SqlTest` (static) |

## Generality (GEN)

| ID | Tests | Also checked by |
|---|---|---|
| GEN-02 | Agent: `test_gen02_an_agent_needs_only_a_model_and_instructions`; Trail: `test_gen02_without_a_sink_there_is_no_trail_and_a_write_runs` | |
| GEN-03 | Loop: `test_gen03_a_run_is_stateless_on_a_new_conversation_and_stateful_on_a_kept_one` | |
| GEN-04 | Approval: `test_gen04_an_unattended_run_denies_calls_that_need_approval_and_runs_the_rest` | |

## Agent and turn loop (AGT)

| ID | Tests | Also checked by |
|---|---|---|
| AGT-01 | Agent: `test_agt01_an_agent_is_frozen_with_a_sorted_copy_of_its_tools`, `test_agt01_blank_instructions_are_refused`, `test_agt01_two_tools_with_one_name_are_refused_whatever_their_descriptions`; CModel: `test_agt01_a_model_and_its_settings_are_frozen` | |
| AGT-02 | Agent: `test_agt02_a_blank_message_is_refused`; Loop: `test_agt02_a_scripted_multi_turn_run_completes_with_text`, `test_agt02_each_tool_call_gets_a_result_and_the_model_is_called_again` | |
| AGT-03 | Usage: `test_agt03_usages_add_up_kind_by_kind`, `test_agt03_a_new_usage_counts_no_tokens`; Loop: `test_agt03_each_way_a_reply_ends_the_run_gives_its_result_and_keeps_what_the_provider_accepts`, `test_agt03_a_result_carries_the_usage_of_every_model_call`, `test_agt03_a_failed_run_carries_the_usage_reported_before_it_failed`, `test_agt03_a_run_stops_at_the_iteration_limit_after_25_model_calls` | |
| AGT-04 | Conc: `test_agt04_one_hundred_concurrent_runs_of_one_agent_each_complete_on_their_own_conversation`, `test_agt04_a_second_run_on_a_conversation_in_use_is_refused` | |
| AGT-05 | CEnd: `test_agt05_cancelling_mid_stream_ends_the_call_with_no_reply_and_closes_the_connection`, `test_agt05_a_call_cancelled_before_it_starts_sends_nothing`, `test_mdl04_agt05_cancelling_during_a_retry_wait_ends_the_call_at_once`; Cancel: `test_agt05_cancelling_calls_each_callback_once`, `test_agt05_a_callback_given_after_cancelling_runs_at_once`, `test_agt05_a_callback_given_after_cancelling_is_not_called_again`, `test_agt05_no_callback_runs_until_cancelling`; Leave: `test_agt05_cancelling_mid_stream_appends_nothing`, `test_agt05_a_run_cancelled_before_it_starts_calls_no_model`, `test_agt05_cancelling_after_a_reply_with_calls_answers_them_and_calls_the_model_no_more`, `test_agt05_a_block_that_breaks_mid_run_ends_it_and_leaves_no_thread`, `test_agt05_breaking_at_a_reply_with_calls_still_answers_them_and_reports_nothing_more`, `test_agt05_raising_at_a_reply_with_calls_still_answers_them`; Approval: `test_agt05_a_call_approved_after_the_run_was_cancelled_never_starts_nor_does_any_other`, `test_agt05_a_denial_while_the_run_is_cancelled_says_so`; Stop: `test_agt05_cancelling_while_tools_run_stops_them_and_answers_every_call`, `test_agt05_breaking_while_tools_run_cancels_them_waits_for_them_and_still_answers_every_call`, `test_agt05_breaking_before_the_tools_start_answers_every_call_without_running_one`, `test_agt05_a_run_cancelled_before_its_tools_start_starts_none_and_answers_every_call`, `test_agt05_a_read_that_fails_the_run_stops_the_other_reads_first` | The thread-leak check, after every test |
| AGT-06 | Conv: `test_agt06_the_shared_conversation_reads_and_writes_back_byte_for_byte`, `test_agt06_a_round_trip_keeps_blocks_with_escapes_and_non_ascii_byte_for_byte`, `test_agt06_a_tool_result_reads_back_with_its_error_flag`, `test_agt06_any_text_round_trips` (property), `test_agt06_a_conversation_no_run_has_bound_is_written_without_a_fingerprint`, `test_agt06_a_new_conversation_has_a_random_id_of_32_hex_digits`, `test_agt06_what_a_run_appends_is_frozen`; Refusal: `test_agt06_json_of_another_shape_is_refused`, `test_agt06_a_message_of_another_shape_is_refused`, `test_agt06_a_message_without_blocks_is_refused`, `test_agt06_a_block_of_another_shape_is_refused`, `test_agt06_a_block_with_neither_text_raw_json_nor_a_tool_result_is_refused`, `test_agt06_json_that_does_not_parse_is_refused_with_the_parsers_reason` | |
| AGT-08 | Events: `test_agt08_every_append_is_reported_once_all_of_a_step_is_appended` | |

## Models (MDL)

| ID | Tests | Also checked by |
|---|---|---|
| MDL-01 | CStream: `test_mdl01_mdl05_ctx05_text_streams_and_the_reply_keeps_each_block_with_the_calls_usage`, `test_mdl01_a_reply_returns_once_it_stops_and_closes_the_connection`; CStop: `test_mdl01_stop_reasons_map_by_their_word_and_an_unknown_one_keeps_it` | |
| MDL-02 | CReq: `test_ctx01_ctx02_ctx03_mdl02_mdl03_a_runs_request_matches_the_shared_golden_layout` (streamed), `test_mdl02_the_api_key_given_is_sent`; Hello: `test_mdl02_hello_chats_until_an_empty_line_and_shows_each_calls_tokens` | Deps: `test_test05_the_anthropic_sdk_used_anywhere_but_the_claude_gem_fails`; the hello sample, run live by hand |
| MDL-03 | CModel: `test_mdl03_settings_name_every_setting_that_shapes_a_request` (.NET's and Go's settings string), `test_mdl03_an_invalid_setting_is_refused`; CReq: `test_ctx01_ctx02_ctx03_mdl02_mdl03_a_runs_request_matches_the_shared_golden_layout` | |
| MDL-04 | CRetry: `test_mdl04_a_retry_waits_as_long_as_retry_after_asks_in_seconds_within_a_cap`, `test_mdl04_backoff_doubles_from_a_second`, `test_mdl04_a_status_another_attempt_may_pass_is_retried_whatever_its_type`, `test_mdl04_an_error_type_another_attempt_may_pass_is_retried_mid_stream`, `test_mdl04_a_broken_stream_is_retried`, `test_mdl04_backoff_jitter_spreads_the_waits_over_the_upper_half_of_each_step`, `test_mdl04_a_failure_mid_stream_restarts_the_reply_keeping_the_tokens_it_used`, `test_mdl04_a_failure_that_persists_is_transient_after_every_attempt`; CClass: `test_mdl04_a_request_the_api_rejects_is_invalid_and_not_retried`, `test_mdl04_an_invalid_request_mid_stream_keeps_what_streamed_before_it`, `test_mdl04_refused_credentials_are_an_authentication_error_by_status_or_type_alone`, `test_mdl04_an_event_the_sdk_cannot_read_is_an_invalid_request_and_not_retried`, `test_mdl04_credentials_the_sdk_cannot_find_are_an_authentication_error_and_not_retried`, `test_mdl04_a_prompt_longer_than_the_context_window_stops_for_context_full`, `test_mdl04_an_invalid_request_that_does_not_say_so_in_the_apis_shape_is_not_context_full`; CEnd: `test_mdl04_agt05_cancelling_during_a_retry_wait_ends_the_call_at_once` | |
| MDL-05 | CStream: `test_mdl05_each_block_is_kept_as_the_gem_writes_it_in_the_canonical_form_with_its_text_or_call`; CReq: `test_mdl05_blocks_the_dotnet_implementation_stored_reach_the_wire_byte_for_byte` (`testdata/claude/blocks.json`), `test_mdl05_a_reply_is_stored_canonical_and_replayed_byte_for_byte` | |
| MDL-06 | CStop: `test_mdl06_a_refusal_stops_with_its_category`, `test_mdl06_a_refusal_stops_the_run_with_its_category`, `test_mdl06_a_refusal_with_no_category_stops_with_no_detail` | |

## Context and caching (CTX)

| ID | Tests | Also checked by |
|---|---|---|
| CTX-01 | Agent: `test_ctx01_the_fingerprint_is_the_one_every_implementation_computes` (against `testdata/session/prefix.json`); CReq: `test_ctx01_ctx02_ctx03_mdl02_mdl03_a_runs_request_matches_the_shared_golden_layout` (`testdata/claude/request-layout.json`), `test_ctx01_a_request_without_tools_is_laid_out_as_dotnets_is`; Loop: `test_ctx01_the_run_context_follows_the_users_message_as_an_operator_message` | |
| CTX-02 | Agent: `test_ctx02_a_blank_run_context_is_refused`; CReq: `test_ctx01_ctx02_ctx03_mdl02_mdl03_a_runs_request_matches_the_shared_golden_layout` (the run context as a system message after each user message); Hello: `test_mdl02_hello_chats_until_an_empty_line_and_shows_each_calls_tokens` | |
| CTX-03 | CReq: `test_ctx01_ctx02_ctx03_mdl02_mdl03_a_runs_request_matches_the_shared_golden_layout` (the cache point on the instructions, 1 h, and automatic caching of the tail, 5 min) | The live hello sample: cache reads from the second message |
| CTX-04 | Agent: `test_ctx04_a_changed_tool_fails_the_run_with_a_prefix_mismatch`, `test_ctx04_changed_instructions_fail_the_run_with_a_prefix_mismatch`, `test_ctx04_a_changed_model_setting_fails_the_run_with_a_prefix_mismatch` | |
| CTX-05 | CStream: `test_mdl01_mdl05_ctx05_text_streams_and_the_reply_keeps_each_block_with_the_calls_usage`, `test_ctx05_a_message_delta_without_input_counts_keeps_those_the_call_started_with`, `test_ctx05_a_message_deltas_counts_replace_those_the_call_started_with` (usage per call, cache reads and writes); TMetrics: `test_evt02_ctx05_metrics_count_tokens_cost_cache_hit_ratio_tool_outcomes_approvals_and_results` (the cache hit ratio per agent); TTrace: `test_evt02_ctx05_a_runs_span_carries_its_identity_result_usage_cost_and_calls` | |
| CTX-06 | CReq: `test_ctx06_tool_results_go_out_as_one_user_message_of_tool_result_blocks`; Results: `test_ctx06_results_of_one_reply_return_in_one_message_in_call_order` | |

## Memory (MEM)

| ID | Tests | Also checked by |
|---|---|---|
| MEM-02 | Stores: `test_mem02_a_written_file_reads_back_and_is_listed_with_its_size_in_bytes`, `test_mem02_writing_again_replaces_the_text`, `test_mem02_a_missing_file_reads_as_nil_and_an_unwritten_scope_lists_nothing`, `test_mem02_paths_with_dots_spaces_and_letters_beyond_ascii_are_accepted`, `test_mem02_the_longest_scope_and_parts_the_rules_allow_are_kept`, `test_mem02_deleting_removes_the_file_and_deleting_a_missing_one_does_nothing`, `test_mem02_a_deleted_files_directory_can_become_a_file`, `test_mem02_renaming_moves_the_file_and_leaves_no_directory_behind`, `test_mem02_renaming_a_missing_file_or_onto_a_taken_path_is_refused_and_changes_nothing`, `test_mem02_a_path_that_is_a_directory_or_runs_through_a_file_is_refused`, `test_mem02_text_that_is_not_valid_utf8_is_refused`, `test_mem02_concurrent_writers_lose_no_file`, `test_mem02_concurrent_writes_lists_and_deletes_in_one_directory_never_fail`, `test_mem02_writes_renames_and_lists_interleaved_inside_each_call_lose_no_file`; FileStore: `test_mem02_each_scope_is_a_directory_named_by_the_hex_of_its_utf8_bytes`, `test_mem02_directories_left_empty_are_removed_up_to_the_scopes`, `test_mem02_a_file_that_is_not_utf8_text_is_refused_on_read`, `test_mem02_files_put_there_under_names_no_path_may_have_are_not_listed`; HashStore: `test_mem02_changing_the_callers_strings_after_a_write_or_rename_changes_nothing_stored` | |
| MEM-03 | Stores: `test_mem03_scopes_never_see_each_others_files`, `test_mem03_a_scopes_files_never_clash_with_anothers_paths`, `test_mem03_every_operation_refuses_a_path_outside_the_rules`, `test_mem03_every_operation_refuses_a_scope_outside_the_rules`; FileStore: `test_mem03_a_linked_directory_in_the_scope_is_never_followed`, `test_mem03_a_linked_file_in_the_scope_is_never_followed`, `test_mem03_a_scope_whose_directory_is_a_link_is_refused`; Rules: `test_mem03_a_path_is_at_most_1024_utf8_bytes`, `test_mem03_a_part_is_at_most_255_utf8_bytes`, `test_mem03_a_scope_is_at_most_127_utf8_bytes`, `test_mem03_only_utf8_strings_are_scopes_or_paths`, `test_mem03_a_scope_is_one_part_of_a_path`, `test_mem03_a_string_of_a_subclass_is_a_string`, `test_mem03_device_names_are_refused_whatever_their_case_or_extension`, `test_mem03_check_names_what_it_refuses` | The link tests skip where the system lets no test make a symbolic link (Windows without developer mode) |

## Events and observability (EVT)

| ID | Tests | Also checked by |
|---|---|---|
| EVT-01 | CEnd: `test_evt01_what_the_consumers_block_raises_passes_through_is_not_retried_and_closes_the_connection`, `test_evt01_a_consumer_that_leaves_the_block_ends_the_call_and_closes_the_connection`; Leave: `test_evt01_an_exception_from_the_hosts_block_is_the_hosts_own`; Events: `test_evt01_a_run_streams_text_and_usage_then_returns_its_result`, `test_evt01_a_retry_the_model_reports_reaches_the_host_and_the_run_goes_on`; Approval: `test_evt01_a_calls_events_come_started_asked_answered_finished_then_its_results_are_appended`, `test_evt01_a_denied_call_is_reported_as_not_approved` | |
| EVT-02 | TTrace: `test_evt02_aud03_a_run_is_one_trace_with_a_span_per_model_and_tool_call_and_its_entries_point_into_it`, `test_evt02_ctx05_a_runs_span_carries_its_identity_result_usage_cost_and_calls`, `test_evt02_a_model_calls_span_is_a_client_span_with_its_usage_cost_finish_reason_and_retries`, `test_evt02_a_tool_calls_span_has_its_kind_approval_wait_outcome_and_time`, `test_evt02_aud03_a_runs_span_is_under_the_hosts_span_and_without_telemetry_its_entries_carry_the_hosts_span`; TMetrics: `test_evt02_ctx05_metrics_count_tokens_cost_cache_hit_ratio_tool_outcomes_approvals_and_results`, `test_evt02_a_denied_calls_span_is_marked_failed_as_a_tool_error`, `test_evt02_a_hosts_views_get_every_measurement_and_what_one_adds_reaches_no_other`, `test_evt02_telemetry_is_frozen_with_its_instruments_as_every_run_shares_it`, `test_evt02_a_models_info_keeps_frozen_copies_of_its_names_and_has_no_price_unless_given`, `test_evt02_a_model_without_a_price_costs_nothing`; TModel: `test_evt02_a_retry_is_counted_and_the_failed_attempts_tokens_are_kept`, `test_evt02_the_time_to_first_token_is_the_wait_for_the_first_text_of_the_attempt_that_counts`, `test_evt02_evt03_a_failed_model_call_marks_its_span_and_the_runs_without_the_secret`, `test_evt02_a_stopped_runs_reason_is_named_as_dotnet_names_it_and_its_span_is_not_failed`, `test_evt02_an_unknown_finish_is_named_by_the_providers_word_and_a_call_without_tokens_has_no_hit_ratio`, `test_evt02_a_run_the_host_left_ends_its_spans_and_is_counted_as_abandoned`; TTool: `test_evt02_a_call_cancelled_while_waiting_for_approval_records_the_wait_only`, `test_evt02_an_approval_given_as_the_run_was_cancelled_is_counted_and_the_call_ends_unrun`, `test_evt02_a_tool_that_raises_ends_its_span_as_a_tool_error_after_running`, `test_tool06_evt02_a_result_of_exactly_the_limit_is_kept_whole_and_one_longer_is_marked_truncated`, `test_evt02_a_call_of_a_tool_the_agent_lacks_has_no_kind_and_a_reads_kind_is_read`; TNames: `test_evt02_the_instruments_have_the_kinds_names_units_and_descriptions_of_dotnets_and_gos`, `test_evt02_the_spans_events_and_metrics_carry_the_attributes_dotnet_and_go_name`, `test_evt02_the_spans_and_metrics_come_from_the_scope_of_officinas_name`; CModel: `test_evt02_info_names_the_provider_and_model_and_prices_opus` | Compaction and clearing (Ruby S10) and the memory scope (Ruby S09) bring their attributes and counters; TNames lists them as not yet in Ruby |
| EVT-03 | Redact: `test_evt03_secrets_are_redacted_from_results_and_the_runs_text_but_not_from_the_conversation`, `test_evt03_every_tool_event_shows_the_call_without_the_secrets`, `test_evt03_a_failed_runs_detail_has_no_secret`, `test_evt03_secrets_that_overlap_touch_or_contain_one_another_are_redacted_whole`, `test_evt03_every_form_a_json_writer_may_give_a_secret_is_redacted`; TModel: `test_evt02_evt03_a_failed_model_call_marks_its_span_and_the_runs_without_the_secret`; TContent: `test_evt03_evt04_by_default_telemetry_carries_no_text_and_no_secret`, `test_evt03_evt04_a_host_that_opts_in_gets_the_text_without_the_secrets`; SProp: `test_test07_evt03_generated_secrets_never_reach_results_events_telemetry_or_the_trail` | |
| EVT-04 | TContent: `test_evt03_evt04_by_default_telemetry_carries_no_text_and_no_secret`, `test_evt03_evt04_a_host_that_opts_in_gets_the_text_without_the_secrets` | |

## Tools (TOOL)

| ID | Tests | Also checked by |
|---|---|---|
| TOOL-01 | Core/InputTest: `test_tool01_options_are_written_in_dotnets_key_order_and_names_in_camel_case`, `test_tool01_arrays_of_scalars_and_of_objects_may_be_nullable_and_optional`, `test_tool01_the_value_class_is_a_frozen_data_class_with_the_declared_members_in_order`, `test_tool01_valid_json_becomes_a_value_with_nested_values_and_absent_members_nil`, `test_tool01_an_array_becomes_a_frozen_array_and_an_absent_one_nil`, `test_tool01_an_integer_member_given_as_an_integral_float_becomes_an_integer`, `test_tool01_number_members_keep_their_floats`; Core/InputDotnetTest: `test_tool01_bookshops_search_books_input_has_dotnets_schema`, `test_tool01_bookshops_place_order_input_with_an_array_of_objects_has_dotnets_schema`, `test_tool01_descriptions_are_escaped_as_dotnets_encoder_escapes_them`, `test_tool01_invalid_utf8_is_written_as_the_replacement_character` (.NET's bytes in `officina/test/fixtures/dotnet-schemas.tsv` and the shared `testdata/session/prefix.json`); Core/SchemaTest: `test_tool01_the_schema_keeps_its_text_as_given_for_the_request`, `test_tool01_a_schema_is_the_input_type_of_its_own_json_values`; Tool: `test_tool01_a_tool_from_a_declared_input_gets_its_value_and_describes_itself_with_its_schema`, `test_tool01_a_tool_from_a_schema_gets_the_json_value_and_a_result_that_is_not_text_is_sent_as_json`, `test_tool01_the_handler_gets_the_runs_cancellation`, `test_tool01_a_definition_that_cannot_work_is_refused_saying_why`, `test_tool01_a_tool_keeps_frozen_copies_of_its_strings`; Results: `test_tool01_each_call_runs_its_tool_and_the_model_reads_the_results` | |
| TOOL-02 | Core/SchemaTest: `test_tool02_each_keyword_accepts_valid_input_and_names_the_problem_with_invalid_input`, `test_tool02_the_false_schema_allows_nothing`, `test_tool02_keywords_for_other_kinds_of_value_do_not_apply`, `test_tool02_problems_of_nested_values_are_named_by_their_json_pointer`, `test_tool02_a_pattern_that_takes_too_long_is_a_problem_of_the_value_not_an_error`, `test_tool02_a_nested_pattern_has_the_same_timeout`; Core/InputTest: `test_tool02_the_declared_schema_validates_input`; Tool: `test_tool02_input_that_cannot_reach_the_handler_is_said_for_the_model`; Results: `test_tool02_invalid_input_comes_back_as_an_error_result_and_the_handler_never_runs` | |
| TOOL-03 | Reads: `test_tool03_the_reads_of_a_reply_overlap`, `test_tool03_a_write_waits_for_every_call_before_it_and_a_later_call_waits_for_the_write` | |
| TOOL-04 | Approval: `test_tool04_a_denial_goes_back_to_the_model_as_the_calls_result`, `test_tool04_the_approver_gets_the_tool_and_the_call_without_the_agents_secrets`, `test_tool04_an_approver_that_raises_denies_the_call`, `test_tool04_a_host_may_answer_approval_from_its_events_whether_or_not_the_approver_is_waiting`, `test_tool04_an_approval_keeps_a_frozen_copy_of_its_reason` | |
| TOOL-05 | Results: `test_tool05_a_handler_that_raises_gives_an_error_result_and_the_run_goes_on` | Prop (TEST-07) |
| TOOL-06 | Results: `test_tool06_a_result_over_64000_characters_is_cut_with_a_note_and_one_at_the_limit_is_not`; TTool: `test_tool06_evt02_a_result_of_exactly_the_limit_is_kept_whole_and_one_longer_is_marked_truncated` | |

## Audit (AUD)

| ID | Tests | Also checked by |
|---|---|---|
| AUD-01 | Trail: `test_aud01_a_run_records_its_start_and_end_and_each_call_and_approval`, `test_aud01_a_read_records_its_attempt_too`, `test_aud01_a_denial_and_a_failed_call_are_recorded_with_why`; End: `test_aud01_the_run_end_records_how_it_ended_with_its_usage`, `test_aud01_a_run_the_host_left_is_recorded_as_abandoned` | Memory writes, budget stops, compactions and MCP sources: the slices that build them |
| AUD-02 | Trail: `test_aud02_a_write_runs_only_once_its_attempt_is_in_the_trail`, `test_aud02_a_write_whose_attempt_cannot_be_recorded_never_runs_and_a_read_still_does` | Prop (TEST-07) |
| AUD-03 | Trail: `test_aud03_each_entry_carries_its_time_sequence_run_conversation_and_agent`, `test_aud03_without_a_clock_entries_carry_the_real_time`, `test_aud03_the_agent_keeps_a_frozen_copy_of_the_name_its_entries_carry`; TTrace: `test_aud03_each_entry_carries_the_runs_trace_and_the_span_of_the_step_it_records`, `test_evt02_aud03_a_run_is_one_trace_with_a_span_per_model_and_tool_call_and_its_entries_point_into_it`, `test_evt02_aud03_a_runs_span_is_under_the_hosts_span_and_without_telemetry_its_entries_carry_the_hosts_span`, `test_aud03_without_telemetry_or_a_hosts_span_entries_carry_no_trace`; Sink: `test_aud03_aud04_an_entrys_trace_and_span_are_written_in_camel_case_after_its_kind` | Memory scope: Ruby S09 |
| AUD-04 | Order: `test_aud04_entries_reach_the_sink_one_at_a_time_in_sequence_order`; Sink: `test_aud04_each_entry_is_appended_as_one_json_object_with_its_members_in_camel_case`, `test_aud04_members_an_entry_does_not_have_are_left_out`, `test_aud04_entries_written_at_once_are_each_one_whole_line`, `test_aud04_a_sink_that_cannot_write_raises`, `test_aud03_aud04_an_entrys_trace_and_span_are_written_in_camel_case_after_its_kind` | |
| AUD-05 | Redact: `test_aud05_no_secret_reaches_the_audit_trail`; Trail: `test_aud05_audit_text_is_redacted_and_cut_at_4000_characters_with_its_length_noted` | |
| AUD-06 | TTool: `test_aud06_an_audit_sink_failure_and_a_write_it_blocks_show_on_their_spans`, `test_aud06_audit_failures_are_counted_by_kind_and_a_blocked_write_by_its_outcome` | |

## Output (OUT)

| ID | Tests | Also checked by |
|---|---|---|
| OUT-01 | Core/InputDotnetTest: `test_out01_bookshops_session_summary_output_with_an_array_of_strings_has_dotnets_schema`, `test_out01_a_samples_output_with_enums_and_a_nullable_string_has_dotnets_schema` (the schema DSL declares typed output too) | The output contract: Ruby S12 |

## Bookshop Assistant (APP)

The data layer so far (Ruby S06 part A); the tools, console and end-to-end tests follow in part B.

| ID | Tests | Also checked by |
|---|---|---|
| APP-04 | App: every `CatalogueTest`, `CustomersTest`, `OrdersTest` and `DatabaseTest` test runs against the shared schema and seed, through a shop `Bookshop.build` makes from `BOOKSHOP_DATABASE` | Inspection: `apps/BookshopAssistant/compose.yaml`, `database/`, shared with .NET and Go |
| APP-05 | App: `CatalogueTest#test_app05_search_filters_by_genre_and_stock_and_lists_the_cheapest_first`, `#test_app05_search_matches_part_of_the_title_or_author_and_a_highest_price`, `#test_app05_a_blank_text_filter_filters_nothing`, `#test_app05_find_reads_one_book`; `CustomersTest#test_app05_search_finds_customers_by_part_of_their_name_or_email`; `OrdersTest#test_app05_reads_a_customers_orders_and_an_order_with_its_lines` | |
| APP-06 | App: `CustomersTest#test_app06_add_adds_one_and_refuses_a_second_with_the_same_email`; `OrdersTest#test_app06_place_takes_the_copies_from_stock_and_charges_the_current_prices`, `#test_app06_concurrent_orders_never_take_more_copies_than_are_in_stock`, `#test_app06_cancel_returns_the_copies_once`; `CatalogueTest#test_app06_restock_adds_copies` | |
| APP-07 | App: `OrdersTest#test_app07_business_rule_failures_are_refused_and_change_nothing`, `#test_app07_unknown_ids_are_refused`; `CatalogueTest#test_app07_unknown_books_are_refused`, `#test_app07_restocking_no_copies_is_refused_and_changes_nothing`; `CustomersTest#test_app07_a_customer_without_a_name_is_refused` (a refusal raises `RefusedError`, whose message part B returns as an error result) | |
| APP-08 | App: `SqlTest#test_app08_every_query_runs_a_constant_of_literal_sql`, `#test_app08_the_operations_take_only_values` (static, no database); `CatalogueTest#test_app08_search_text_is_taken_literally_so_a_wildcard_matches_only_itself`, `CustomersTest#test_app08_search_text_is_taken_literally_and_blank_text_names_no_one` | Inspection |
| APP-17 | App: `CatalogueTest#test_app17_a_broad_search_returns_10_to_15k_tokens_within_the_result_limit` (the books as JSON; part B's tool result is checked again there) | |
| APP-18 | App: `DatabaseTest#test_app18_with_the_database_down_calls_fail_and_once_it_is_back_they_work_again` | |

## Testing (TEST)

| ID | Tests | Also checked by |
|---|---|---|
| TEST-01 | Kit: `test_test01_it_streams_its_replies_in_order_and_records_each_request`, `test_test01_it_rejects_messages_the_provider_would_reject`, `test_test01_it_accepts_a_conversation_with_answered_calls_and_a_run_context`, `test_test01_a_run_on_a_model_with_no_reply_left_fails` (the scripted model); Approver: `test_test01_the_approver_answers_as_scripted_in_order_and_records_each_call`, `test_test01_the_recording_sink_keeps_its_entries_and_fails_when_told` | |
| TEST-02 | Prefix: `test_test02_the_prefix_is_stable_across_the_calls_of_a_run_and_across_runs`, `test_test02_the_prefix_is_stable_across_a_save_a_new_process_and_a_resume`, `test_test02_the_check_finds_a_changed_prefix` | |
| TEST-03 | — | The Ruby workflow (`.github/workflows/ruby.yml`): `ruby-ubuntu` and `ruby-windows`, offline; the App database tests run on `ruby-ubuntu` (in CI, a missing Docker fails them) and skip on Windows, saying why |
| TEST-05 | Deps: `test_test05_the_workspace_keeps_the_rule`, `test_test05_a_fixture_keeping_the_rule_passes`, `test_test05_the_anthropic_sdk_used_anywhere_but_the_claude_gem_fails`, `test_test05_the_core_using_more_than_its_rule_allows_fails` (the core's own rule, D15 and R6, and the other gems', as well) | |
| TEST-06 | TTrace, TMetrics, TModel, TTool, TNames and TContent: scripted runs collected in memory, checked for their span tree, attributes and metrics, and for no text or secret by default | |
| TEST-07 | Conv: `test_agt06_any_text_round_trips`; Prop: `test_test07_any_session_keeps_the_conversation_valid_answers_every_call_once_and_audits_every_write_first`; Stores: `test_test07_generated_paths_never_leave_their_scope` (memory paths never leave their scope); Rules: `test_test07_a_valid_path_is_relative_and_never_climbs`; SProp: `test_test07_evt03_generated_secrets_never_reach_results_events_telemetry_or_the_trail` (no secret reaches events, telemetry or audit) | Save and resume, secrets in telemetry and budgets: Ruby S07, S08; CI runs the properties with a fixed `PROPERTY_SEED` |
| TEST-08 | Core/SchemaPropertyTest: `test_test08_on_generated_schemas_and_values_it_accepts_and_rejects_as_json_schemer_does`, `test_test08_a_generated_schema_with_any_keyword_outside_the_subset_is_refused_where_it_is`; Core/SchemaRefusalTest: `test_test08_a_schema_outside_the_subset_is_refused_when_it_is_defined_saying_where_and_why`, `test_test08_a_schema_that_is_not_json_is_refused_with_the_parsers_detail`; Core/InputDeclarationTest: `test_test08_a_malformed_declaration_is_refused_when_it_is_defined`, `test_test08_a_declaration_reaches_only_the_member_methods` | |
