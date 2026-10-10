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
| Stores | `officina/test/memory_store_contract.rb` and `memory_scope_contract.rb`, the tests every memory store passes, run by both `HashMemoryStoreTest` and `FileMemoryStoreTest` |
| HashStore | `officina/test/hash_memory_store_test.rb` (`HashMemoryStoreTest`) |
| FileStore | `officina/test/file_memory_store_test.rb` (`FileMemoryStoreTest`) |
| Path | `officina/test/memory_path_test.rb` (`MemoryPathTest`) |

## Generality (GEN)

| ID | Tests | Also checked by |
|---|---|---|
| GEN-02 | Agent: `test_gen02_an_agent_needs_only_a_model_and_instructions` | |
| GEN-03 | Run: `test_gen03_a_run_is_stateless_on_a_new_conversation_and_stateful_on_a_kept_one` | |

## Agent and turn loop (AGT)

| ID | Tests | Also checked by |
|---|---|---|
| AGT-01 | Agent: `test_agt01_an_agent_is_frozen_with_its_tools_sorted_by_name`, `test_agt01_a_definition_that_cannot_work_is_refused` | |
| AGT-02 | Run: `test_agt02_a_scripted_multi_turn_run_completes_with_text`, `test_agt02_each_tool_call_gets_a_result_and_the_model_is_called_again` | |
| AGT-03 | Run: `test_agt03_every_stop_reason_maps_to_its_result`, `test_agt03_a_result_carries_the_usage_of_every_model_call`, `test_agt03_a_run_stops_at_the_iteration_limit_after_25_model_calls` | |
| AGT-04 | Run: `test_agt04_one_hundred_concurrent_runs_of_one_agent_each_complete_on_their_own_conversation`, `test_agt04_a_second_run_on_a_conversation_in_use_is_refused` | |
| AGT-05 | Run: `test_agt05_cancelling_mid_stream_appends_nothing`, `test_agt05_a_run_cancelled_before_it_starts_calls_no_model`, `test_agt05_cancelling_after_a_reply_with_calls_answers_them_and_calls_the_model_no_more`, `test_agt05_a_block_that_breaks_mid_run_ends_it_and_leaves_no_thread`, `test_agt05_breaking_at_a_reply_with_calls_still_answers_them`, `test_agt05_raising_at_a_reply_with_calls_still_answers_them` | The thread-leak check, after every test |
| AGT-06 | Conv: `test_agt06_the_shared_conversation_reads_and_writes_back_byte_for_byte`, `test_agt06_a_round_trip_keeps_blocks_with_escapes_and_non_ascii_byte_for_byte`, `test_agt06_any_text_round_trips` (property), `test_agt06_json_that_is_not_a_conversation_is_refused` | |
| AGT-08 | Run: `test_agt08_every_append_is_reported_once_all_of_a_step_is_appended` | |

## Context and caching (CTX)

| ID | Tests | Also checked by |
|---|---|---|
| CTX-01 | Agent: `test_ctx01_the_fingerprint_is_the_one_every_implementation_computes` (against `testdata/session/prefix.json`); Run: `test_ctx01_the_run_context_follows_the_users_message_as_an_operator_message` | |
| CTX-04 | Agent: `test_ctx04_a_changed_tool_instruction_or_model_setting_fails_the_run_with_a_prefix_mismatch` | |

## Memory (MEM)

| ID | Tests | Also checked by |
|---|---|---|
| MEM-02 | Stores: `test_mem02_a_written_file_reads_back_and_is_listed_with_its_size_in_bytes`, `test_mem02_writing_again_replaces_the_text`, `test_mem02_a_missing_file_reads_as_nil_and_an_unwritten_scope_lists_nothing`, `test_mem02_paths_with_dots_spaces_and_letters_beyond_ascii_are_accepted`, `test_mem02_the_longest_scope_and_parts_the_rules_allow_are_kept`, `test_mem02_deleting_removes_the_file_and_deleting_a_missing_one_does_nothing`, `test_mem02_a_deleted_files_directory_can_become_a_file`, `test_mem02_renaming_moves_the_file_and_leaves_no_directory_behind`, `test_mem02_renaming_a_missing_file_or_onto_a_taken_path_is_refused_and_changes_nothing`, `test_mem02_a_path_that_is_a_directory_or_runs_through_a_file_is_refused`, `test_mem02_text_that_is_not_valid_utf8_is_refused`, `test_mem02_concurrent_writers_lose_no_file`; FileStore: `test_mem02_each_scope_is_a_directory_named_by_the_hex_of_its_utf8_bytes`, `test_mem02_directories_left_empty_are_removed_up_to_the_scopes`, `test_mem02_a_file_that_is_not_utf8_text_is_refused_on_read`, `test_mem02_files_put_there_under_names_no_path_may_have_are_not_listed`; HashStore: `test_mem02_changing_the_callers_strings_after_a_write_or_rename_changes_nothing_stored` | |
| MEM-03 | Stores: `test_mem03_scopes_never_see_each_others_files`, `test_mem03_a_scopes_files_never_clash_with_anothers_paths`, `test_mem03_every_operation_refuses_a_path_outside_the_rules`, `test_mem03_every_operation_refuses_a_scope_outside_the_rules`; FileStore: `test_mem03_a_linked_directory_in_the_scope_is_never_followed`, `test_mem03_a_linked_file_in_the_scope_is_never_followed`, `test_mem03_a_scope_whose_directory_is_a_link_is_refused`; Path: `test_mem03_a_path_is_at_most_1024_characters`, `test_mem03_a_part_is_at_most_255_utf8_bytes`, `test_mem03_a_scope_is_at_most_127_utf8_bytes`, `test_mem03_only_utf8_strings_are_scopes_or_paths`, `test_mem03_a_scope_is_one_part_of_a_path`, `test_mem03_device_names_are_refused_whatever_their_case_or_extension`, `test_mem03_check_names_what_it_refuses` | The link tests skip where the system lets no test make a symbolic link (Windows without developer mode) |

## Events and observability (EVT)

| ID | Tests | Also checked by |
|---|---|---|
| EVT-01 | Run: `test_evt01_a_run_streams_text_and_usage_then_returns_its_result`, `test_evt01_an_exception_from_the_hosts_block_is_the_hosts_own` | |

## Testing (TEST)

| ID | Tests | Also checked by |
|---|---|---|
| TEST-01 | Kit: `test_test01_it_streams_its_replies_in_order_and_records_each_request`, `test_test01_it_rejects_messages_the_provider_would_reject`, `test_test01_it_accepts_a_conversation_with_answered_calls_and_a_run_context`, `test_test01_a_run_on_a_model_with_no_reply_left_fails` (the scripted model; the scripted approver and fake MCP server come in S05 and S11) | |
| TEST-02 | Prefix: `test_test02_the_prefix_is_stable_across_the_calls_of_a_run_and_across_runs`, `test_test02_the_prefix_is_stable_across_a_save_a_new_process_and_a_resume`, `test_test02_the_check_finds_a_changed_prefix` | |
| TEST-03 | — | The Ruby workflow (`.github/workflows/ruby.yml`): `ruby-ubuntu` and `ruby-windows`, offline |
| TEST-05 | Deps: `test_test05_the_workspace_keeps_the_rule`, `test_test05_a_fixture_keeping_the_rule_passes`, `test_test05_the_anthropic_sdk_used_anywhere_but_the_claude_gem_fails`, `test_test05_the_core_using_more_than_its_rule_allows_fails` (the core's own rule, D15 and R6, and the other gems', as well) | |
| TEST-07 | Stores: `test_test07_generated_paths_never_leave_their_scope` (memory paths never leave their scope); Path: `test_test07_a_valid_path_is_relative_and_never_climbs` | CI runs the properties with a fixed `PROPERTY_SEED` |
