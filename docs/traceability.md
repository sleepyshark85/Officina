# Phase 1 traceability

Each phase 1 requirement of [`REQUIREMENTS.md`](../REQUIREMENTS.md) (§2, §4) and the tests that check it, or how it is
checked otherwise. AGT-07 and TOOL-07 moved to the north star (NS-18) and are not listed.

Test files are under `tests/`, shortened as:

| Short | Project |
|---|---|
| Core | `Sleepyshark.Officina.Tests` |
| Claude | `Sleepyshark.Officina.Claude.Tests` |
| Mcp | `Sleepyshark.Officina.Mcp.Tests` |
| Deps | `Sleepyshark.Officina.Dependencies.Tests` |
| App | `BookshopAssistant.Tests` (real console and database in Docker, TEST-09) |
| Samples | `Samples.Tests` (the GEN-06 samples) |

**Summary:** 88 requirements. 86 have tests, TEST-04's live and on demand only; 2 are checked by other means only:
APP-19 (the demo script) and TEST-03 (the CI workflow). Nine more have a part that
only a live run or inspection checks, noted in the last column.

## Reference application (APP)

| ID | Tests | Also checked by |
|---|---|---|
| APP-01 | App/EndToEndTests: `APP_01_the_reply_streams_with_the_text_between_tool_calls_and_each_tool_with_its_input_and_outcome` | |
| APP-02 | App/SessionTests: `APP_02_help_lists_every_command_and_calls_no_model`, `APP_02_new_starts_a_session_of_its_own_and_resume_of_an_unknown_id_says_so`, `APP_14_the_status_line_and_cost_show_…` (`/cost`); App/SummaryTests: `APP_15_leaving_a_session_summarizes_it_and_sessions_shows_…` (`/sessions`); App/MemoryTests: `APP_11_memory_shows_what_is_remembered_…` (`/memory`); App/AuditTests: `APP_16_audit_shows_…`, `APP_16_audit_with_an_id_shows_an_earlier_session` (`/audit`); `/quit` ends every App console test | |
| APP-03 | App/EndToEndTests: `APP_03_cancelling_stops_the_reply_and_the_session_goes_on`, `APP_03_cancelling_at_the_approval_prompt_stops_the_reply_at_once_and_the_change_is_not_made` | |
| APP-04 | App/ToolTests (all run against the seeded database in Docker); App/SearchBenchmarkTests (opt-in: a million books) | Inspection: `compose.yaml`, `database/` |
| APP-05 | App/EndToEndTests: `APP_05_the_read_tools_of_one_reply_all_answer_from_the_database`; App/ToolTests: `Search_…`, `Reads_find_customers_their_orders_books_and_orders`; Core/ToolLoopTests: `Reads_overlap_and_writes_run_alone_in_call_order` | |
| APP-06 | App/EndToEndTests: `APP_06_a_write_shows_its_exact_input_for_approval_and_runs_only_if_approved`; App/ToolTests: `Add_customer_…`, `Place_order_…`, `Cancel_order_returns_the_copies_once`, `Restock_adds_copies`, `Concurrent_orders_never_take_more_copies_than_are_in_stock` | |
| APP-07 | App/EndToEndTests: `APP_07_not_enough_stock_comes_back_as_an_error_result_and_the_model_recovers_in_the_same_reply`; App/ToolTests: `Business_rule_failures_are_error_results_and_change_nothing`, `Unknown_ids_are_error_results` | |
| APP-08 | App/NoSqlTextTests: `The_only_text_inputs_are_known_values`, `Every_command_runs_a_constant` | Inspection |
| APP-09 | App/EndToEndTests: `APP_09_a_multi_step_request_finds_searches_orders_after_approval_and_answers` | Live: App/LiveSmokeTests `APP_09_places_the_order_after_approval_and_reads_the_cache_from_the_second_call` (TEST-04) |
| APP-10 | App/SessionTests: `APP_10_quit_restart_and_resume_continues_the_session_with_its_prefix_byte_identical`, `APP_10_a_crash_mid_reply_loses_at_most_the_step_in_flight_and_the_session_resumes`, `APP_10_a_session_whose_agent_changed_is_refused_and_a_new_one_is_offered`; App/SessionPropertyTests: `TEST_07_the_prefix_stays_byte_identical_…`; App/UnreadableSessionTests: `A_session_that_cannot_be_read_is_reported_…`; App/SessionConflictTests (both); Core/SharedSessionTests: `The_prefix_fingerprint_and_a_crashed_session_are_written_as_the_shared_fixtures_hold_them`, `APP_10_A_session_another_implementation_saved_mid_reply_resumes_with_its_prefix_and_interrupted_calls_answered` (shared `testdata/session/`: Go's and Ruby's sessions resume here, and both resume .NET's) | Cache reads after `/resume`: demo script |
| APP-11 | App/MemoryTests: `APP_11_memory_shows_what_is_remembered_for_the_staff_member_at_the_counter`, `APP_11_a_preference_saved_in_one_session_is_applied_in_a_new_one` | |
| APP-12 | App/ExportTests: `APP_12_the_allow_list_offers_only_writing_a_file_with_approval_and_listing_the_folder`, `APP_12_an_order_history_is_exported_as_csv_after_approval`, `APP_12_a_declined_export_writes_no_file` | |
| APP-13 | App/EndToEndTests: `APP_13_the_run_context_names_the_date_and_staff_member_and_is_sent_again_only_on_a_new_day` | |
| APP-14 | App/SessionTests: `APP_14_the_status_line_and_cost_show_the_tokens_cache_share_and_cost_of_the_reply_and_the_session`, `APP_14_a_reply_that_reaches_its_budget_stops_and_says_why_…`, `APP_14_a_session_that_reaches_its_budget_stops_the_next_reply_before_any_model_call` | |
| APP-15 | App/SummaryTests: all five `APP_15_…` tests; App/SummaryCostTests: `Sessions_summarizes_at_most_three_stale_sessions_…` | |
| APP-16 | App/AuditTests: `APP_16_audit_shows_the_session_s_entries_grouped_by_run_…`, `APP_16_audit_with_an_id_shows_an_earlier_session`, `The_table_keeps_every_field_of_an_entry_and_reads_a_session_s_entries_in_order`, `A_write_the_database_refuses_throws` | |
| APP-17 | App/LongConversationTests: `APP_17_demo_mode_compacts_and_clears_early_and_the_console_and_audit_report_each`, `Outside_demo_mode_compaction_and_clearing_come_later`, `A_compacting_reply_without_text_says_so`, `A_clearing_the_provider_repeats_is_shown_once_and_a_new_one_again`; App/ToolTests: `A_broad_search_returns_10_to_15k_tokens_within_the_result_limit` | Live compaction: smoke test and demo script |
| APP-18 | App/EndToEndTests: `APP_18_with_the_database_down_tools_return_errors_and_once_it_is_back_the_session_works_again`; App/ToolTests: `With_the_database_down_tools_fail_and_once_it_is_back_they_work_again` | |
| APP-19 | — | The demo script, [`docs/demo.md`](demo.md) |
| APP-20 | Core/TelemetryTests: `A_run_is_one_trace_with_a_span_per_model_call_and_per_tool_call_…`, `Metrics_count_tokens_…`; App/AuditTests: `APP_16_audit_shows_…` (trace links) | The dashboard view: demo script |

## Generality (GEN)

| ID | Tests | Also checked by |
|---|---|---|
| GEN-01 | Deps/DependencyRulesTests: `The_core_referencing_any_package_fails`, `The_core_referencing_another_project_fails`; Deps/RepositoryDependencyTests: `The_solution_follows_the_dependency_rules` | Inspection: no domain, UI, storage or transport in `src/Sleepyshark.Officina` |
| GEN-02 | Core/RunTests: `An_agent_with_only_a_model_and_instructions_runs_statelessly`; Core/AgentTests: `An_agent_needs_a_model_and_instructions` | |
| GEN-03 | Core/OutputTests: `A_stateless_run_starts_a_new_conversation_each_time`; Core/RunTests: `A_scripted_multi_turn_conversation_completes_with_text`; Samples/ExtractionTests, Samples/ChatAssistantTests | |
| GEN-04 | Core/ToolLoopTests: `An_unattended_run_denies_calls_that_need_approval_and_tells_the_model_why`, `An_approved_call_runs`; Samples/BackgroundAgentTests: `An_unattended_job_reads_the_ticket_is_denied_the_refund_…` | |
| GEN-05 | Core/RunTests: `A_scripted_multi_turn_conversation_completes_with_text` (text); Core/OutputTests: `A_valid_reply_completes_with_the_typed_output_…` (typed); Core/ToolLoopTests: `GEN_05_a_run_whose_work_is_its_side_effect_ends_after_its_write_tool` (side effects); Samples/BackgroundAgentTests: `An_unattended_job_…` (typed output beside a side effect) | |
| GEN-06 | Samples/ExtractionTests, Samples/ChatAssistantTests, Samples/BackgroundAgentTests (all) | |

## Agent and turn loop (AGT)

| ID | Tests | Also checked by |
|---|---|---|
| AGT-01 | Core/AgentTests: `An_agent_needs_a_model_and_instructions`, `Tools_are_sorted_by_name_and_names_are_unique`, `Hundred_concurrent_runs_of_one_agent_each_keep_their_own_conversation` | |
| AGT-02 | Core/ToolLoopTests: `Tool_calls_run_their_results_go_back_and_the_run_continues_to_its_answer`; Core/RunTests: `A_scripted_multi_turn_conversation_completes_with_text` | |
| AGT-03 | Core/RunTests: `Every_stop_reason_maps_to_its_result_and_the_reply_is_kept`, `A_model_failure_ends_the_run_as_failed_and_appends_nothing`, `A_model_that_throws_before_streaming_fails_the_run_instead_of_throwing`; Core/ToolLoopTests: `A_run_that_keeps_calling_tools_stops_at_the_iteration_limit_…`; Core/OutputTests: `An_exception_from_the_output_type_s_constructor_ends_the_run_as_failed` | |
| AGT-04 | Core/AgentTests: `Hundred_concurrent_runs_of_one_agent_each_keep_their_own_conversation` | |
| AGT-05 | Core/CancellationTests: `Cancelling_mid_stream_appends_nothing_and_the_next_run_sends_a_valid_request`, `A_run_cancelled_before_it_starts_appends_nothing_and_calls_no_model`, `A_run_cancelled_while_it_connects_its_tool_sources_stops_as_cancelled_and_calls_no_model`, `AGT_08_AGT_05_a_host_that_stops_reading_at_the_first_append_holds_the_message_context_and_reply_together`; Core/ToolLoopTests: `Cancelling_during_tools_keeps_finished_results_marks_unstarted_calls_cancelled_and_calls_the_model_no_more`, `A_call_running_when_the_run_is_cancelled_gets_a_cancelled_result`, `A_call_approved_after_the_host_cancelled_does_not_start`, `A_write_waiting_for_reads_when_the_host_cancels_does_not_start` | |
| AGT-06 | Core/ConversationJsonTests: `A_conversation_round_trips_through_JSON_keeping_every_block_byte_for_byte`, `Blocks_and_the_prefix_survive_a_store_that_normalizes_JSON`, `The_JSON_form_is_plain_and_readable_and_the_same_as_Go_s` (shared `testdata/conversation/`) | |
| AGT-08 | Core/RunTests: `Events_stream_text_and_usage_then_each_append_with_the_reply_and_the_result_last`; Core/CancellationTests: `A_conversation_saved_while_its_tools_ran_gets_error_results_for_them_when_resumed`, `AGT_08_AGT_05_a_host_that_stops_reading_at_the_first_append_holds_the_message_context_and_reply_together`; App/SessionTests: `APP_10_a_crash_mid_reply_loses_at_most_the_step_in_flight_…` | |

## Models (MDL)

| ID | Tests | Also checked by |
|---|---|---|
| MDL-01 | Every Core test reaches the model through the model interface (the scripted model); Deps: `The_core_referencing_any_package_fails` | Inspection |
| MDL-02 | Claude/RequestTests: `Stored_blocks_reach_the_wire_byte_for_byte_and_the_request_streams`; Claude/StreamTests: `Text_streams_as_it_arrives_…`; Deps: `A_package_other_than_Claude_referencing_the_Anthropic_SDK_fails` | |
| MDL-03 | Claude/StreamTests: `The_settings_name_every_setting_that_shapes_a_request`; Claude/RequestTests: `The_request_body_matches_the_golden_layout` (effort sent explicitly; it is a required setting); Core/PrefixTests: `A_changed_prefix_fails_the_run_with_a_prefix_mismatch_before_any_model_call` (its "model settings" case: settings are fixed for a conversation) | |
| MDL-04 | Claude/RetryTests (all) | |
| MDL-05 | Core/RunTests: `Reply_blocks_are_appended_exactly_as_received`; Claude/RequestTests: `A_reply_is_stored_and_replayed_unchanged_on_the_next_request`; Claude/LongConversationTests: `The_compaction_block_is_kept_and_replayed_as_received_and_the_run_reports_it` | |
| MDL-06 | Claude/StreamTests: `A_refusal_carries_its_category`; Core/RunTests: `Every_stop_reason_maps_to_its_result_and_the_reply_is_kept` | |

## Context and caching (CTX)

| ID | Tests | Also checked by |
|---|---|---|
| CTX-01 | Claude/RequestTests: `The_request_body_matches_the_golden_layout`; Core/PrefixTests: `Tools_given_in_another_order_are_the_same_prefix` | |
| CTX-02 | Core/RunTests: `Run_context_is_appended_as_an_operator_message_after_the_user_message`; Claude/RequestTests: `The_request_body_matches_the_golden_layout`; App/EndToEndTests: `APP_13_…` | |
| CTX-03 | Claude/RequestTests: `The_request_body_matches_the_golden_layout`, `A_prefix_cached_for_less_time_than_the_conversation_is_refused`, `Stored_blocks_reach_the_wire_…`; Claude/StreamTests: `The_settings_name_every_setting_that_shapes_a_request`, `Cache_writes_kept_for_an_hour_are_counted_apart_as_they_cost_more` | |
| CTX-04 | Core/PrefixTests: `A_changed_prefix_fails_the_run_with_a_prefix_mismatch_before_any_model_call`; Mcp: `MCP_03_CTX_04_the_tool_list_is_read_once_and_pinned_for_the_conversation`; Core/LongConversationTests: `Context_management_is_part_of_the_prefix`; Core/OutputTests: `The_output_schema_is_part_of_the_prefix_fingerprint`; Core/SharedSessionTests: `The_prefix_fingerprint_with_context_management_is_the_shared_fixtures`, `The_prefix_fingerprint_with_an_output_schema_is_the_shared_fixtures` (the shared `testdata/session/prefix.json`, which Go checks too) | |
| CTX-05 | Claude/StreamTests: `Usage_adds_up_every_iteration_of_the_call_and_a_compaction_block_is_kept_raw`; Core/TelemetryTests: `Metrics_count_tokens_by_type_cost_the_cache_hit_ratio_…` | |
| CTX-06 | Core/ToolLoopTests: `Results_of_one_reply_return_in_one_message_in_call_order_whichever_finishes_first`; Claude/RequestTests: `Tool_results_go_back_as_one_user_message_in_call_order_with_is_error_on_failures` | |

## History compaction (HIST)

| ID | Tests | Also checked by |
|---|---|---|
| HIST-01 | Claude/LongConversationTests: `Context_management_asks_for_clearing_then_threshold_compaction_with_their_betas`, `The_compaction_block_is_kept_and_replayed_as_received_and_the_run_reports_it` | Live: smoke test |
| HIST-02 | Claude/LongConversationTests: `Context_management_asks_for_clearing_…`, `A_clearing_is_reported_from_the_applied_edits` | |
| HIST-03 | Core/LongConversationTests: `Without_compaction_a_full_window_stops_the_run_as_context_full_and_leaves_the_conversation_as_it_was`, `A_model_without_compaction_says_so_and_an_agent_that_needs_it_cannot_run_on_it` | |
| HIST-04 | Core/LongConversationTests: `Compaction_and_clearing_are_events_audit_entries_and_telemetry_and_the_summary_is_kept`; Claude/LongConversationTests: `A_compaction_is_reported_from_its_iteration_and_priced_with_the_reply` | |

## Memory (MEM)

| ID | Tests | Also checked by |
|---|---|---|
| MEM-01 | Core/MemoryTests: `MEM_01_the_model_views_creates_edits_renames_and_deletes_files_…`, `MEM_01_mistakes_are_error_results_that_change_nothing`, `MEM_01_a_long_file_s_view_is_cut_at_16_000_characters_…`; Claude/MemoryRequestTests: `The_memory_tool_is_sent_as_claude_s_native_tool_…` | |
| MEM-02 | Core/MemoryTests: `MEM_01_the_model_views_…` (both stores), `MEM_03_scopes_that_differ_only_in_case_stay_apart` | |
| MEM-03 | Core/MemoryTests: `MEM_03_a_run_sees_only_its_scope_s_files`, `MEM_03_a_path_outside_the_scope_is_refused_with_an_error_result`, `MEM_03_a_run_of_an_agent_with_memory_needs_a_valid_scope`, `MEM_03_windows_device_names_are_not_memory_paths`; Core/ToolLoopTests: `MEM_03_a_tool_handler_gets_the_runs_memory_scope`; Core/MemoryPropertyTests: `Memory_paths_never_leave_their_scope`; Samples/ChatAssistantTests: `Memory_is_kept_per_user_…` | |
| MEM-04 | Core/MemoryTests: `MEM_04_memory_writes_are_audited_before_they_run_and_ask_approval_while_views_do_not` | |
| MEM-05 | Core/MemoryTests: `MEM_05_memory_never_reaches_the_instructions_and_the_prefix_stays_stable_as_it_changes` | |

## Tools (TOOL)

| ID | Tests | Also checked by |
|---|---|---|
| TOOL-01 | Core/ToolDefinitionTests: `The_schema_is_derived_from_the_parameters`, `A_typed_function_runs_with_its_arguments_and_its_result_goes_back_as_JSON`, `Each_kind_of_return_value_becomes_a_result`, `An_input_schema_must_be_an_object_schema` | |
| TOOL-02 | Core/ToolLoopTests: `Invalid_input_a_thrown_handler_a_denial_and_an_unknown_tool_each_come_back_as_error_results_and_the_run_continues`; Core/SchemaValidatorTests (all) | |
| TOOL-03 | Core/ToolLoopTests: `Reads_overlap_and_writes_run_alone_in_call_order` | |
| TOOL-04 | Core/ToolLoopTests: `An_approved_call_runs`, `Invalid_input_a_thrown_handler_a_denial_…` | |
| TOOL-05 | Core/ToolLoopTests: `Invalid_input_a_thrown_handler_a_denial_…`, `A_pattern_that_takes_too_long_and_a_null_output_come_back_as_error_results` | |
| TOOL-06 | Core/ToolLoopTests: `A_result_over_the_size_limit_is_truncated_with_a_note` | |

## MCP (MCP)

| ID | Tests | Also checked by |
|---|---|---|
| MCP-01 | Mcp: `MCP_01_a_stdio_server_s_tools_run_and_their_results_reach_the_model`, `MCP_01_an_http_server_s_tools_run_with_the_credential_the_host_gives`; Samples/BackgroundAgentTests (HTTP); App/ExportTests (HTTP) | |
| MCP-02 | Mcp: `MCP_02_an_mcp_tool_goes_through_validation_approval_audit_truncation_and_events`, `MCP_02_MCP_03_only_allowed_tools_appear_…`, `MCP_02_an_empty_credential_is_not_redacted_and_does_not_break_the_calls` | |
| MCP-03 | Mcp: `MCP_02_MCP_03_only_allowed_tools_appear_named_by_server_and_tool_…`, `MCP_03_an_allowed_tool_the_server_lacks_fails_the_connection_clearly`, `MCP_03_CTX_04_the_tool_list_is_read_once_and_pinned_for_the_conversation`, `MCP_03_a_registered_source_is_connected_with_its_pinned_tools_under_the_server_s_name`, `MCP_03_an_allowed_tool_whose_schema_the_core_cannot_use_fails_the_connect_with_why`, `MCP_03_a_server_name_that_cannot_prefix_tool_names_is_refused` | |
| MCP-04 | Mcp: `MCP_04_a_server_down_at_the_start_of_a_run_fails_it_clearly_and_the_next_run_reconnects`, `MCP_04_an_http_server_that_fails_mid_run_gives_error_results_…`, `MCP_04_a_stdio_server_that_exits_mid_run_…`, `MCP_04_a_stdio_server_that_cannot_start_fails_to_connect_clearly`, `MCP_04_a_connect_cancelled_while_the_server_starts_leaves_no_server_running`, `MCP_04_a_source_that_cannot_connect_is_not_registered`, `MCP_04_an_http_session_the_server_ends_mid_run_gives_error_results_and_the_next_run_starts_a_new_one`, `MCP_04_a_server_that_speaks_another_protocol_version_is_refused`, `MCP_04_a_stdio_server_that_exits_while_connecting_is_reported_with_what_it_said_on_its_error_output` | |

## Output (OUT)

| ID | Tests | Also checked by |
|---|---|---|
| OUT-01 | Core/OutputTests: `A_valid_reply_completes_with_the_typed_output_and_the_request_carries_the_schema_as_exported`, `The_exported_schema_names_members_in_camel_case_…`; Claude/OutputTests: `An_exported_type_s_schema_is_sent_adjusted_as_the_output_format_…`, `A_structured_reply_comes_back_as_the_typed_output`; Samples/ExtractionTests | |
| OUT-02 | Core/OutputTests: `Output_that_fails_to_validate_or_deserialize_ends_the_run_as_failed_with_the_errors_and_no_correction_round`, `An_exception_from_the_output_type_s_constructor_ends_the_run_as_failed`; Samples/ExtractionTests: `Output_that_does_not_match_the_type_fails_the_run_and_classifies_nothing`; App/SummaryTests: `APP_15_output_that_does_not_match_the_schema_is_told_…` | |

## Budgets (BUD)

| ID | Tests | Also checked by |
|---|---|---|
| BUD-01 | Core/BudgetTests: every `BUD_01_…` test, `TEST_07_a_budget_is_overshot_by_at_most_one_call_s_input_…`; Claude/RequestTests: `The_output_limit_is_the_lower_of_the_model_s_and_the_budget_s`; Samples/BackgroundAgentTests: `A_job_that_keeps_calling_tools_stops_at_its_budget` | |
| BUD-02 | Core/BudgetTests: `Cost_prices_each_kind_of_token_and_cache_writes_by_how_long_they_are_kept`; Claude/StreamTests: `Opus_5_5_is_priced_by_default_and_a_host_may_give_another_price` | |
| BUD-03 | Core/BudgetTests: `BUD_03_a_result_reports_tokens_cost_model_calls_tool_calls_and_duration_…` | |

## Events and observability (EVT)

| ID | Tests | Also checked by |
|---|---|---|
| EVT-01 | Core/RunTests: `Events_stream_text_and_usage_then_each_append_with_the_reply_and_the_result_last`; Core/ToolLoopTests: `Tool_calls_and_approvals_stream_as_events_between_the_reply_and_the_tool_results`; Core/LongConversationTests: `Compaction_and_clearing_are_events_…` | |
| EVT-02 | Core/TelemetryTests: `A_run_is_one_trace_…`, `Metrics_count_tokens_…`, `A_retry_is_counted_…`, `Time_to_first_token_is_measured_on_the_attempt_that_succeeded`, `A_run_the_host_abandons_is_counted_…` | |
| EVT-03 | Core/SecretPropertyTests: `No_secret_reaches_the_events_the_telemetry_or_the_audit_trail`; Core/TelemetryTests: `A_failed_model_call_marks_its_span_and_the_run_s_without_the_secret`; Mcp: both `EVT_03_…` tests | |
| EVT-04 | Core/TelemetryTests: `Telemetry_carries_no_text_unless_the_agent_opts_in_and_never_a_secret` | |

## Audit (AUD)

| ID | Tests | Also checked by |
|---|---|---|
| AUD-01 | Core/AuditTests: `A_run_records_its_start_its_tool_calls_its_approvals_and_its_end_in_sequence`, `Refusals_failures_prefix_mismatches_and_budget_stops_are_recorded_as_the_run_ends`, `A_tool_source_that_cannot_report_its_changes_is_audited_as_failed_and_the_run_still_ends`; Core/MemoryTests: `MEM_04_…`; Core/LongConversationTests: `Compaction_and_clearing_are_events_audit_entries_…`; Mcp: `MCP_04_a_server_down_at_the_start_…` (connected, failed, disconnected) | |
| AUD-02 | Core/AuditTests: `A_write_runs_only_after_its_attempt_is_recorded`, `A_write_whose_attempt_cannot_be_audited_never_runs_…`; Core/ConversationPropertyTests: `Any_sequence_of_runs_keeps_the_conversation_valid_and_every_write_audited_first` | |
| AUD-03 | Core/TelemetryTests: `A_run_is_one_trace_…_and_its_audit_entries_point_into_it`; Core/MemoryTests: `AUD_03_every_audit_entry_of_a_run_names_its_memory_scope`; Core/AuditTests: `An_entry_the_sink_failed_to_write_leaves_a_gap_in_the_sequence`; App/AuditTests: `The_table_keeps_every_field_…` | |
| AUD-04 | Core/AuditTests: `The_JSON_lines_sink_appends_one_entry_per_line`, `The_JSON_lines_sink_reports_a_failed_write`; Samples/BackgroundAgentTests: `An_unattended_job_…` | |
| AUD-05 | Core/AuditTests: `Long_text_is_truncated_with_its_size_and_secrets_are_redacted`, `A_secret_is_redacted_as_escaped_in_JSON_too_…`, `Secrets_that_overlap_or_contain_each_other_…`, `A_secret_s_escaped_form_overlapping_another_…` | |
| AUD-06 | Core/TelemetryTests: `An_audit_sink_failure_and_a_write_it_blocks_show_in_telemetry` | |

## Testing (TEST)

| ID | Tests | Also checked by |
|---|---|---|
| TEST-01 | Core/RunTests: `The_scripted_model_rejects_role_sequences_the_API_rejects`, `A_rejected_request_leaves_its_scripted_reply_for_the_next_one`; the scripted approver, in-memory store and fake MCP server in Core/ToolLoopTests, Core/MemoryTests and Mcp | |
| TEST-02 | Core/PrefixTests: `The_prefix_is_stable_across_turns_and_across_save_restart_and_resume`, `The_stability_check_reports_each_kind_of_change`, `A_prefix_keeps_its_tools_sorted_however_it_is_built_and_is_equal_to_another_with_the_same_fingerprint`; App/SessionTests: `APP_10_quit_restart_and_resume_…`; App/SessionPropertyTests; Samples: `TEST_02_…` tests and the prefix checks of Samples/BackgroundAgentTests | |
| TEST-03 | — | The CI workflow (`.github/workflows/ci.yml`): Linux and Windows, offline; Docker tests skip off Linux |
| TEST-04 | App/LiveSmokeTests: `APP_09_places_the_order_after_approval_and_reads_the_cache_from_the_second_call`, `Demo_mode_compacts_after_the_demo_scripts_searches` | Live, on demand (`OFFICINA_LIVE_TESTS=1`, trait `Category=Live`); skipped by `dotnet test` and CI |
| TEST-05 | Deps/DependencyRulesTests (all), Deps/RepositoryDependencyTests | |
| TEST-06 | Core/TelemetryTests (all) | |
| TEST-07 | Core/ConversationPropertyTests, Core/SecretPropertyTests, Core/MemoryPropertyTests, Core/BudgetTests: `TEST_07_…`, App/SessionPropertyTests | |
| TEST-08 | Core/SchemaValidatorTests: `On_schemas_in_the_subset_it_accepts_and_rejects_what_an_established_validator_does`; Core/ToolDefinitionTests: `A_schema_outside_the_subset_is_refused_when_the_tool_is_defined`; Core/OutputTests: `A_type_whose_schema_is_outside_the_subset_is_refused_when_the_contract_is_defined` | |
| TEST-09 | App (all console tests) | |
