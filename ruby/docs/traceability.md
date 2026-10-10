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
| Tool | `officina/test/tool_test.rb` (`ToolTest`) |
| Usage | `officina/test/usage_test.rb` (`UsageTest`) |
| Prefix | `officina/test/prefix_stability_test.rb` (`PrefixStabilityTest`) |
| Run | `officina/test/run_test.rb` (`RunTest`) |
| Kit | `officina-testing/test/scripted_model_test.rb` (`ScriptedModelTest`) |
| Core | `officina/test/` |
| App | `apps/bookshop/test/`: `CatalogueTest`, `CustomersTest`, `OrdersTest`, `DatabaseTest` (against the seeded database in Docker, Linux only, skipping elsewhere with the reason) and `SqlTest` (static) |

## Generality (GEN)

| ID | Tests | Also checked by |
|---|---|---|
| GEN-02 | Agent: `test_gen02_an_agent_needs_only_a_model_and_instructions` | |
| GEN-03 | Run: `test_gen03_a_run_is_stateless_on_a_new_conversation_and_stateful_on_a_kept_one` | |

## Agent and turn loop (AGT)

| ID | Tests | Also checked by |
|---|---|---|
| AGT-01 | Agent: `test_agt01_an_agent_is_frozen_with_a_sorted_copy_of_its_tools`, `test_agt01_blank_instructions_are_refused`, `test_agt01_two_tools_with_one_name_are_refused_whatever_their_descriptions` | |
| AGT-02 | Run: `test_agt02_a_scripted_multi_turn_run_completes_with_text`, `test_agt02_each_tool_call_gets_a_result_and_the_model_is_called_again`; Agent: `test_agt02_a_blank_message_is_refused` | |
| AGT-03 | Run: `test_agt03_every_stop_reason_maps_to_its_result`, `test_agt03_a_result_carries_the_usage_of_every_model_call`, `test_agt03_a_run_stops_at_the_iteration_limit_after_25_model_calls`; Usage: `test_agt03_usages_add_up_kind_by_kind`, `test_agt03_a_new_usage_counts_no_tokens` | |
| AGT-04 | Run: `test_agt04_one_hundred_concurrent_runs_of_one_agent_each_complete_on_their_own_conversation`, `test_agt04_a_second_run_on_a_conversation_in_use_is_refused` | |
| AGT-05 | Run: `test_agt05_cancelling_mid_stream_appends_nothing`, `test_agt05_a_run_cancelled_before_it_starts_calls_no_model`, `test_agt05_cancelling_after_a_reply_with_calls_answers_them_and_calls_the_model_no_more`, `test_agt05_a_block_that_breaks_mid_run_ends_it_and_leaves_no_thread`, `test_agt05_breaking_at_a_reply_with_calls_still_answers_them`, `test_agt05_raising_at_a_reply_with_calls_still_answers_them` | The thread-leak check, after every test |
| AGT-06 | Conv: `test_agt06_the_shared_conversation_reads_and_writes_back_byte_for_byte`, `test_agt06_a_round_trip_keeps_blocks_with_escapes_and_non_ascii_byte_for_byte`, `test_agt06_a_tool_result_reads_back_with_its_error_flag`, `test_agt06_any_text_round_trips` (property), `test_agt06_a_conversation_no_run_has_bound_is_written_without_a_fingerprint`, `test_agt06_a_new_conversation_has_a_random_id_of_32_hex_digits`, `test_agt06_what_a_run_appends_is_frozen`; Refusal: `test_agt06_json_of_another_shape_is_refused`, `test_agt06_a_message_of_another_shape_is_refused`, `test_agt06_a_message_without_blocks_is_refused`, `test_agt06_a_block_of_another_shape_is_refused`, `test_agt06_a_block_with_neither_text_raw_json_nor_a_tool_result_is_refused`, `test_agt06_json_that_does_not_parse_is_refused_with_the_parsers_reason` | |
| AGT-08 | Run: `test_agt08_every_append_is_reported_once_all_of_a_step_is_appended` | |

## Context and caching (CTX)

| ID | Tests | Also checked by |
|---|---|---|
| CTX-01 | Agent: `test_ctx01_the_fingerprint_is_the_one_every_implementation_computes` (against `testdata/session/prefix.json`); Run: `test_ctx01_the_run_context_follows_the_users_message_as_an_operator_message` | |
| CTX-02 | Agent: `test_ctx02_a_blank_run_context_is_refused` | |
| CTX-04 | Agent: `test_ctx04_a_changed_tool_fails_the_run_with_a_prefix_mismatch`, `test_ctx04_changed_instructions_fail_the_run_with_a_prefix_mismatch`, `test_ctx04_a_changed_model_setting_fails_the_run_with_a_prefix_mismatch` | |

## Events and observability (EVT)

| ID | Tests | Also checked by |
|---|---|---|
| EVT-01 | Run: `test_evt01_a_run_streams_text_and_usage_then_returns_its_result`, `test_evt01_an_exception_from_the_hosts_block_is_the_hosts_own` | |

## Tools (TOOL)

| ID | Tests | Also checked by |
|---|---|---|
| TOOL-01 | Core/InputTest: `test_tool01_options_are_written_in_dotnets_key_order_and_names_in_camel_case`, `test_tool01_arrays_of_scalars_and_of_objects_may_be_nullable_and_optional`, `test_tool01_the_value_class_is_a_frozen_data_class_with_the_declared_members_in_order`, `test_tool01_valid_json_becomes_a_value_with_nested_values_and_absent_members_nil`, `test_tool01_an_array_becomes_a_frozen_array_and_an_absent_one_nil`, `test_tool01_an_integer_member_given_as_an_integral_float_becomes_an_integer`, `test_tool01_number_members_keep_their_floats`; Core/InputDotnetTest: `test_tool01_bookshops_search_books_input_has_dotnets_schema`, `test_tool01_bookshops_place_order_input_with_an_array_of_objects_has_dotnets_schema`, `test_tool01_descriptions_are_escaped_as_dotnets_encoder_escapes_them`, `test_tool01_invalid_utf8_is_written_as_the_replacement_character` (.NET's bytes in `officina/test/fixtures/dotnet-schemas.tsv` and the shared `testdata/session/prefix.json`); Core/SchemaTest: `test_tool01_the_schema_keeps_its_text_as_given_for_the_request`; Tool: `test_tool01_a_tool_with_a_blank_name_is_refused`, `test_tool01_a_tool_whose_schema_is_not_a_json_object_is_refused`, `test_tool01_a_tool_keeps_frozen_copies_of_its_strings` | The tool itself and its handler: Ruby S05 part B |
| TOOL-02 | Core/SchemaTest: `test_tool02_each_keyword_accepts_valid_input_and_names_the_problem_with_invalid_input`, `test_tool02_the_false_schema_allows_nothing`, `test_tool02_keywords_for_other_kinds_of_value_do_not_apply`, `test_tool02_problems_of_nested_values_are_named_by_their_json_pointer`, `test_tool02_a_pattern_that_takes_too_long_is_a_problem_of_the_value_not_an_error`, `test_tool02_a_nested_pattern_has_the_same_timeout`; Core/InputTest: `test_tool02_the_declared_schema_validates_input` | Invalid input sent back as an error result: Ruby S05 part B |

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
| TEST-01 | Kit: `test_test01_it_streams_its_replies_in_order_and_records_each_request`, `test_test01_it_rejects_messages_the_provider_would_reject`, `test_test01_it_accepts_a_conversation_with_answered_calls_and_a_run_context`, `test_test01_a_run_on_a_model_with_no_reply_left_fails` (the scripted model; the scripted approver and fake MCP server come in S05 and S11) | |
| TEST-02 | Prefix: `test_test02_the_prefix_is_stable_across_the_calls_of_a_run_and_across_runs`, `test_test02_the_prefix_is_stable_across_a_save_a_new_process_and_a_resume`, `test_test02_the_check_finds_a_changed_prefix` | |
| TEST-03 | — | The Ruby workflow (`.github/workflows/ruby.yml`): `ruby-ubuntu` and `ruby-windows`, offline; the App database tests run on `ruby-ubuntu` (in CI, a missing Docker fails them) and skip on Windows, saying why |
| TEST-05 | Deps: `test_test05_the_workspace_keeps_the_rule`, `test_test05_a_fixture_keeping_the_rule_passes`, `test_test05_the_anthropic_sdk_used_anywhere_but_the_claude_gem_fails`, `test_test05_the_core_using_more_than_its_rule_allows_fails` (the core's own rule, D15 and R6, and the other gems', as well) | |
| TEST-08 | Core/SchemaPropertyTest: `test_test08_on_generated_schemas_and_values_it_accepts_and_rejects_as_json_schemer_does`, `test_test08_a_generated_schema_with_any_keyword_outside_the_subset_is_refused_where_it_is`; Core/SchemaRefusalTest: `test_test08_a_schema_outside_the_subset_is_refused_when_it_is_defined_saying_where_and_why`, `test_test08_a_schema_that_is_not_json_is_refused_with_the_parsers_detail`; Core/InputDeclarationTest: `test_test08_a_malformed_declaration_is_refused_when_it_is_defined`, `test_test08_a_declaration_reaches_only_the_member_methods` | |
