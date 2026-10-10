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
| Loop | `officina/test/run_loop_test.rb` (`RunLoopTest`) |
| Leave | `officina/test/run_leaving_test.rb` (`RunLeavingTest`) |
| Events | `officina/test/run_events_test.rb` (`RunEventsTest`) |
| Conc | `officina/test/run_concurrency_test.rb` (`RunConcurrencyTest`) |
| Kit | `officina-testing/test/scripted_model_test.rb` (`ScriptedModelTest`) |
| Core | `officina/test/` |
| Tool | `officina/test/tool_test.rb` (`ToolTest`) |
| Pipe | `officina/test/tool_pipeline_test.rb` (`ToolPipelineTest`) |
| Audit | `officina/test/audit_test.rb` (`AuditTest`) |
| Prop | `officina/test/run_property_test.rb` (`RunPropertyTest`) |
| Approver | `officina-testing/test/scripted_approver_test.rb` (`ScriptedApproverTest`) |

## Generality (GEN)

| ID | Tests | Also checked by |
|---|---|---|
| GEN-02 | Agent: `test_gen02_an_agent_needs_only_a_model_and_instructions`; Audit: `test_gen02_without_a_sink_there_is_no_trail_and_a_write_runs` | |
| GEN-03 | Loop: `test_gen03_a_run_is_stateless_on_a_new_conversation_and_stateful_on_a_kept_one` | |
| GEN-04 | Pipe: `test_gen04_an_unattended_run_denies_calls_that_need_approval_and_runs_the_rest` | |

## Agent and turn loop (AGT)

| ID | Tests | Also checked by |
|---|---|---|
| AGT-01 | Agent: `test_agt01_an_agent_is_frozen_with_its_tools_sorted_by_name`, `test_agt01_a_definition_that_cannot_work_is_refused` | |
| AGT-02 | Loop: `test_agt02_a_scripted_multi_turn_run_completes_with_text`, `test_agt02_each_tool_call_gets_a_result_and_the_model_is_called_again` | |
| AGT-03 | Loop: `test_agt03_each_way_a_reply_ends_the_run_gives_its_result_and_keeps_what_the_provider_accepts`, `test_agt03_a_result_carries_the_usage_of_every_model_call`, `test_agt03_a_failed_run_carries_the_usage_reported_before_it_failed`, `test_agt03_a_run_stops_at_the_iteration_limit_after_25_model_calls` | |
| AGT-04 | Conc: `test_agt04_one_hundred_concurrent_runs_of_one_agent_each_complete_on_their_own_conversation`, `test_agt04_a_second_run_on_a_conversation_in_use_is_refused` | |
| AGT-05 | Leave: `test_agt05_cancelling_mid_stream_appends_nothing`, `test_agt05_a_run_cancelled_before_it_starts_calls_no_model`, `test_agt05_cancelling_after_a_reply_with_calls_answers_them_and_calls_the_model_no_more`, `test_agt05_a_block_that_breaks_mid_run_ends_it_and_leaves_no_thread`, `test_agt05_breaking_at_a_reply_with_calls_still_answers_them_and_reports_nothing_more`, `test_agt05_raising_at_a_reply_with_calls_still_answers_them`; Pipe: `test_agt05_cancelling_while_tools_run_stops_them_and_answers_every_call`, `test_agt05_breaking_while_tools_run_cancels_them_waits_for_them_and_still_answers_every_call`, `test_agt05_cancelling_while_waiting_for_approval_denies_the_call` | The thread-leak check, after every test; Prop (TEST-07) |
| AGT-06 | Conv: `test_agt06_the_shared_conversation_reads_and_writes_back_byte_for_byte`, `test_agt06_a_round_trip_keeps_blocks_with_escapes_and_non_ascii_byte_for_byte`, `test_agt06_any_text_round_trips` (property), `test_agt06_json_that_is_not_a_conversation_is_refused` | |
| AGT-08 | Events: `test_agt08_every_append_is_reported_once_all_of_a_step_is_appended` | |

## Context and caching (CTX)

| ID | Tests | Also checked by |
|---|---|---|
| CTX-01 | Agent: `test_ctx01_the_fingerprint_is_the_one_every_implementation_computes` (against `testdata/session/prefix.json`); Loop: `test_ctx01_the_run_context_follows_the_users_message_as_an_operator_message` | |
| CTX-04 | Agent: `test_ctx04_a_changed_tool_instruction_or_model_setting_fails_the_run_with_a_prefix_mismatch` | |
| CTX-06 | Pipe: `test_ctx06_results_of_one_reply_return_in_one_message_in_call_order` | Prop (TEST-07) |

## Events and observability (EVT)

| ID | Tests | Also checked by |
|---|---|---|
| EVT-01 | Events: `test_evt01_a_run_streams_text_and_usage_then_returns_its_result`; Leave: `test_evt01_an_exception_from_the_hosts_block_is_the_hosts_own`; Pipe: `test_evt01_a_calls_events_come_started_asked_answered_finished_then_its_results_are_appended` | |
| EVT-03 | Pipe: `test_evt03_secrets_are_redacted_from_results_tool_events_the_runs_result_and_the_audit_trail`, `test_evt03_every_form_of_every_secret_is_redacted_whole_however_they_overlap` | Telemetry and exceptions: Ruby S07 |

## Tools (TOOL)

| ID | Tests | Also checked by |
|---|---|---|
| TOOL-01 | Core/InputTest: `test_tool01_options_are_written_in_dotnets_key_order_and_names_in_camel_case`, `test_tool01_arrays_of_scalars_and_of_objects_may_be_nullable_and_optional`, `test_tool01_the_value_class_is_a_frozen_data_class_with_the_declared_members_in_order`, `test_tool01_valid_json_becomes_a_value_with_nested_values_and_absent_members_nil`, `test_tool01_an_array_becomes_a_frozen_array_and_an_absent_one_nil`, `test_tool01_an_integer_member_given_as_an_integral_float_becomes_an_integer`, `test_tool01_number_members_keep_their_floats`; Core/InputDotnetTest: `test_tool01_bookshops_search_books_input_has_dotnets_schema`, `test_tool01_bookshops_place_order_input_with_an_array_of_objects_has_dotnets_schema`, `test_tool01_descriptions_are_escaped_as_dotnets_encoder_escapes_them` (.NET's bytes in `officina/test/fixtures/dotnet-schemas.tsv` and the shared `testdata/session/prefix.json`); Core/SchemaTest: `test_tool01_the_schema_keeps_its_text_as_given_for_the_request`; Tool: `test_tool01_a_tool_from_a_declared_input_gets_its_value_and_describes_itself_with_its_schema`, `test_tool01_a_tool_from_a_schema_gets_the_json_value_and_a_result_that_is_not_text_is_sent_as_json`, `test_tool01_a_definition_that_cannot_work_is_refused`; Pipe: `test_tool01_each_call_runs_its_tool_and_the_model_reads_the_results` |  |
| TOOL-02 | Core/SchemaTest: `test_tool02_each_keyword_accepts_valid_input_and_names_the_problem_with_invalid_input`, `test_tool02_the_false_schema_allows_nothing`, `test_tool02_keywords_for_other_kinds_of_value_do_not_apply`, `test_tool02_problems_of_nested_values_are_named_by_their_json_pointer`, `test_tool02_a_pattern_that_takes_too_long_is_a_problem_of_the_value_not_an_error`, `test_tool02_a_nested_pattern_has_the_same_timeout`; Core/InputTest: `test_tool02_the_declared_schema_validates_input`; Tool: `test_tool02_input_that_cannot_reach_the_handler_is_said_for_the_model`; Pipe: `test_tool02_invalid_input_comes_back_as_an_error_result_and_the_handler_never_runs` |  |
| TOOL-03 | Pipe: `test_tool03_reads_overlap_and_a_write_waits_for_every_call_before_it_and_runs_alone` | |
| TOOL-04 | Pipe: `test_tool04_a_denial_goes_back_to_the_model_as_the_calls_result`, `test_tool04_an_approver_that_raises_denies_the_call`, `test_tool04_a_host_may_answer_approval_from_its_events_in_either_order` | |
| TOOL-05 | Pipe: `test_tool05_a_handler_that_raises_gives_an_error_result_and_the_run_goes_on` | Prop (TEST-07) |
| TOOL-06 | Pipe: `test_tool06_a_result_over_64000_characters_is_cut_with_a_note_and_one_at_the_limit_is_not` | |

## Audit (AUD)

| ID | Tests | Also checked by |
|---|---|---|
| AUD-01 | Audit: `test_aud01_a_run_records_its_start_and_end_and_each_call_and_approval`, `test_aud01_the_run_end_records_how_it_ended_with_its_usage` | Memory writes, budget stops, compactions and MCP sources: the slices that build them |
| AUD-02 | Audit: `test_aud02_a_write_runs_only_once_its_attempt_is_in_the_trail`, `test_aud02_a_write_whose_attempt_cannot_be_recorded_never_runs_and_a_read_still_does` | Prop (TEST-07) |
| AUD-03 | Audit: `test_aud03_each_entry_carries_its_time_sequence_run_conversation_and_agent` | Memory scope: Ruby S09; trace and span: Ruby S07 |
| AUD-04 | Audit: `test_aud04_the_json_lines_sink_appends_each_entry_as_one_object_per_line`, `test_aud04_a_sink_that_cannot_write_raises` | |
| AUD-05 | Audit: `test_aud05_audit_text_is_redacted_and_cut_at_4000_characters_with_its_length_noted`; Pipe: `test_evt03_secrets_are_redacted_from_results_tool_events_the_runs_result_and_the_audit_trail` | |

## Output (OUT)

| ID | Tests | Also checked by |
|---|---|---|
| OUT-01 | Core/InputDotnetTest: `test_out01_bookshops_session_summary_output_with_an_array_of_strings_has_dotnets_schema`, `test_out01_a_samples_output_with_enums_and_a_nullable_string_has_dotnets_schema` (the schema DSL declares typed output too) | The output contract: Ruby S12 |

## Testing (TEST)

| ID | Tests | Also checked by |
|---|---|---|
| TEST-01 | Kit: `test_test01_it_streams_its_replies_in_order_and_records_each_request`, `test_test01_it_rejects_messages_the_provider_would_reject`, `test_test01_it_accepts_a_conversation_with_answered_calls_and_a_run_context`, `test_test01_a_run_on_a_model_with_no_reply_left_fails` (the scripted model); Approver: `test_test01_the_approver_answers_as_scripted_in_order_and_records_each_call`, `test_test01_the_recording_sink_keeps_its_entries_and_fails_when_told` (the fake MCP server comes in Ruby S11) | |
| TEST-02 | Prefix: `test_test02_the_prefix_is_stable_across_the_calls_of_a_run_and_across_runs`, `test_test02_the_prefix_is_stable_across_a_save_a_new_process_and_a_resume`, `test_test02_the_check_finds_a_changed_prefix` | |
| TEST-03 | — | The Ruby workflow (`.github/workflows/ruby.yml`): `ruby-ubuntu` and `ruby-windows`, offline |
| TEST-05 | Deps: `test_test05_the_workspace_keeps_the_rule`, `test_test05_a_fixture_keeping_the_rule_passes`, `test_test05_the_anthropic_sdk_used_anywhere_but_the_claude_gem_fails`, `test_test05_the_core_using_more_than_its_rule_allows_fails` (the core's own rule, D15 and R6, and the other gems', as well) | |
| TEST-07 | Prop: `test_test07_any_session_keeps_the_conversation_valid_answers_every_call_once_and_audits_every_write_first`; Conv: `test_agt06_any_text_round_trips` | Save and resume, secrets in telemetry, budgets and memory paths: Ruby S07…S10 |
| TEST-08 | Core/SchemaPropertyTest: `test_test08_on_generated_schemas_and_values_it_accepts_and_rejects_as_json_schemer_does`, `test_test08_a_generated_schema_with_any_keyword_outside_the_subset_is_refused_where_it_is`; Core/SchemaTest: `test_test08_a_schema_outside_the_subset_is_refused_when_it_is_defined_saying_where_and_why`, `test_test08_a_schema_that_is_not_json_is_refused`; Core/InputTest: `test_test08_a_malformed_declaration_is_refused_when_it_is_defined` | |
