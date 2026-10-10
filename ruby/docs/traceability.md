# Ruby traceability

Each phase 1 requirement of [`REQUIREMENTS.md`](../../REQUIREMENTS.md), and the tests that check it in the Ruby
implementation, or how it is checked otherwise, as [`docs/traceability.md`](../../docs/traceability.md) does for .NET.
It grows with each slice; Ruby S13 completes it.

Tests are under `ruby/`, shortened as:

| Short | Where |
|---|---|
| Deps | `test/dependencies_test.rb` (`DependenciesTest`), fixture gems in `test/fixtures/dependencies/` |
| Core | `officina/test/` |

## Tools (TOOL)

| ID | Tests | Also checked by |
|---|---|---|
| TOOL-01 | Core/InputTest: `test_tool01_options_are_written_in_dotnets_key_order_and_names_in_camel_case`, `test_tool01_arrays_of_scalars_and_of_objects_may_be_nullable_and_optional`, `test_tool01_the_value_class_is_a_frozen_data_class_with_the_declared_members_in_order`, `test_tool01_valid_json_becomes_a_value_with_nested_values_and_absent_members_nil`, `test_tool01_an_array_becomes_a_frozen_array_and_an_absent_one_nil`, `test_tool01_an_integer_member_given_as_an_integral_float_becomes_an_integer`, `test_tool01_number_members_keep_their_floats`; Core/InputDotnetTest: `test_tool01_bookshops_search_books_input_has_dotnets_schema`, `test_tool01_bookshops_place_order_input_with_an_array_of_objects_has_dotnets_schema`, `test_tool01_descriptions_are_escaped_as_dotnets_encoder_escapes_them` (.NET's bytes in `officina/test/fixtures/dotnet-schemas.tsv` and the shared `testdata/session/prefix.json`); Core/SchemaTest: `test_tool01_the_schema_keeps_its_text_as_given_for_the_request` | The tool itself and its handler: Ruby S05 part B |
| TOOL-02 | Core/SchemaTest: `test_tool02_each_keyword_accepts_valid_input_and_names_the_problem_with_invalid_input`, `test_tool02_the_false_schema_allows_nothing`, `test_tool02_keywords_for_other_kinds_of_value_do_not_apply`, `test_tool02_problems_of_nested_values_are_named_by_their_json_pointer`, `test_tool02_a_pattern_that_takes_too_long_is_a_problem_of_the_value_not_an_error`, `test_tool02_a_nested_pattern_has_the_same_timeout`; Core/InputTest: `test_tool02_the_declared_schema_validates_input` | Invalid input sent back as an error result: Ruby S05 part B |

## Output (OUT)

| ID | Tests | Also checked by |
|---|---|---|
| OUT-01 | Core/InputDotnetTest: `test_out01_bookshops_session_summary_output_with_an_array_of_strings_has_dotnets_schema`, `test_out01_a_samples_output_with_enums_and_a_nullable_string_has_dotnets_schema` (the schema DSL declares typed output too) | The output contract: Ruby S12 |

## Testing (TEST)

| ID | Tests | Also checked by |
|---|---|---|
| TEST-03 | — | The Ruby workflow (`.github/workflows/ruby.yml`): `ruby-ubuntu` and `ruby-windows`, offline |
| TEST-05 | Deps: `test_test05_the_workspace_keeps_the_rule`, `test_test05_a_fixture_keeping_the_rule_passes`, `test_test05_the_anthropic_sdk_used_anywhere_but_the_claude_gem_fails`, `test_test05_the_core_using_more_than_its_rule_allows_fails` (the core's own rule, D15 and R6, and the other gems', as well) | |
| TEST-08 | Core/SchemaPropertyTest: `test_test08_on_generated_schemas_and_values_it_accepts_and_rejects_as_json_schemer_does`, `test_test08_a_generated_schema_with_any_keyword_outside_the_subset_is_refused_where_it_is`; Core/SchemaTest: `test_test08_a_schema_outside_the_subset_is_refused_when_it_is_defined_saying_where_and_why`, `test_test08_a_schema_that_is_not_json_is_refused`; Core/InputTest: `test_test08_a_malformed_declaration_is_refused_when_it_is_defined` | |
