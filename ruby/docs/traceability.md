# Ruby traceability

Each phase 1 requirement of [`REQUIREMENTS.md`](../../REQUIREMENTS.md), and the tests that check it in the Ruby
implementation, or how it is checked otherwise, as [`docs/traceability.md`](../../docs/traceability.md) does for .NET.
It grows with each slice; Ruby S13 completes it.

Tests are under `ruby/`, shortened as:

| Short | Where |
|---|---|
| Deps | `test/dependencies_test.rb` (`DependenciesTest`), fixture gems in `test/fixtures/dependencies/` |
| Stdio | `officina-mcp/test/stdio_test.rb` (`StdioTest`), against the fake server in `officina-mcp/test/fixtures/stdio_server.rb` |
| Http | `officina-mcp/test/streamable_http_test.rb` (`StreamableHttpTest`) |
| Wire | `officina-mcp/test/wire_test.rb` (`WireTest`), generated messages and event streams |
| Server | `officina-mcp/test/server_test.rb` (`ServerTest`) |

## MCP (MCP)

The client's protocol (Ruby S11, part A). MCP-02 and MCP-03, and MCP-04's run failing and error results, come with the
tool source (part B).

| ID | Tests | Also checked by |
|---|---|---|
| MCP-01 | Stdio: `test_mcp01_a_stdio_server_agrees_on_the_protocol_lists_every_page_of_its_tools_and_runs_them`, `test_mcp01_requests_from_several_threads_each_get_their_own_response`, `test_mcp01_closing_stops_and_reaps_the_server`; Http: `test_mcp01_an_http_server_agrees_on_the_protocol_in_a_session_and_runs_its_tools`, `test_mcp01_every_page_of_any_tool_list_is_read_in_order`, `test_mcp01_a_url_that_is_not_http_is_refused`; Server: `test_mcp01_a_server_is_reached_by_either_a_command_or_a_url`, `test_mcp01_a_server_holds_frozen_copies_of_what_it_was_given`; Wire: `test_mcp01_the_response_is_found_in_any_event_stream_and_nothing_else_is_taken_for_it`, `test_mcp01_a_stream_that_ends_before_the_response_is_complete_raises`, `test_mcp01_a_json_body_in_any_chunks_is_its_response`, `test_mcp01_any_text_is_no_response`, `test_mcp01_a_message_is_a_response_only_with_a_result_or_error_a_positive_integer_id_and_no_method` | |
| MCP-04 | Stdio: `test_mcp04_a_program_that_cannot_start_raises_clearly`, `test_mcp04_a_server_that_exits_while_connecting_raises_with_what_it_said`, `test_mcp04_a_server_silent_for_30_seconds_at_connect_raises_and_is_killed_and_reaped`, `test_mcp04_a_connect_cancelled_while_the_server_starts_leaves_no_server_running`, `test_mcp04_a_server_that_exits_mid_call_raises_with_what_it_said_and_stays_lost`, `test_mcp04_a_tool_that_fails_gives_an_error_result`, `test_mcp04_a_request_the_server_refuses_raises_its_error`, `test_mcp04_a_call_cancelled_while_it_waits_raises`; Http: `test_mcp04_an_http_server_down_at_connect_raises_clearly`, `test_mcp04_an_http_server_that_goes_down_mid_call_raises_and_stays_lost`, `test_mcp04_a_session_the_server_ends_loses_the_connection`, `test_mcp04_a_server_that_speaks_another_protocol_version_is_refused`, `test_mcp04_a_call_cancelled_while_it_waits_raises_and_the_next_call_runs`; Wire: `test_mcp04_a_body_longer_than_16_mb_raises` | |

## Testing (TEST)

| ID | Tests | Also checked by |
|---|---|---|
| TEST-01 | The fake MCP server (`Testing::FakeMcpServer`, over stdio and Streamable HTTP) is the server of every Stdio and Http test; the scripted model and approver come with Ruby S03 and S05 | |
| TEST-03 | — | The Ruby workflow (`.github/workflows/ruby.yml`): `ruby-ubuntu` and `ruby-windows`, offline |
| TEST-05 | Deps: `test_test05_the_workspace_keeps_the_rule`, `test_test05_a_fixture_keeping_the_rule_passes`, `test_test05_the_anthropic_sdk_used_anywhere_but_the_claude_gem_fails`, `test_test05_the_core_using_more_than_its_rule_allows_fails` (the core's own rule, D15 and R6, and the other gems', as well) | |
