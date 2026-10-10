# frozen_string_literal: true

require 'test_helper'
require_relative 'support/memory_calls'

# The edges of the memory tool's views, ranges, inserts and snippets.
class MemoryToolViewTest < Minitest::Test
  include Sleepyshark::Officina
  include MemoryCalls

  cover 'Sleepyshark::Officina*'

  TRUNCATED = "\n[Truncated at 16000 characters: view the rest with view_range.]"

  def test_mem01_view_ranges_insert_lines_and_snippets_hold_at_their_edges
    store = HashMemoryStore.new
    store.write('sam', 'a.md', "a\nb")
    store.write('sam', 'long.md', (1..12).map { "line #{it}" }.join("\n"))
    store.write('sam', 'empty.md', '')

    results = run_memory(store, 'sam', *EDGES.keys)

    assert_equal EDGES.values, results.map(&:content)
    assert_equal "a\nb\nc", store.read('sam', 'a.md')
    assert_equal "first\n", store.read('sam', 'empty.md')
  end

  EDGES = {
    { command: 'view', path: '/memories/a.md', view_range: [1, 2] } =>
      "Here's the content of /memories/a.md with line numbers:\n     1\ta\n     2\tb",
    { command: 'view', path: '/memories/a.md', view_range: [2, 2] } =>
      "Here's the content of /memories/a.md with line numbers:\n     2\tb",
    { command: 'view', path: '/memories/a.md', view_range: [1.0, 1] } =>
      "Here's the content of /memories/a.md with line numbers:\n     1\ta",
    { command: 'view', path: '/memories/a.md', view_range: [3, -1] } =>
      'Error: Invalid `view_range`: it should be [start, end] with 1 <= start <= end <= 2, or end -1 for the end of ' \
      'the file.',
    { command: 'view', path: '/memories/a.md', view_range: [2, -2] } =>
      'Error: Invalid `view_range`: it should be [start, end] with 1 <= start <= end <= 2, or end -1 for the end of ' \
      'the file.',
    { command: 'view',
      path: '/memories/empty.md' } => "Here's the content of /memories/empty.md with line numbers:\n     " \
                                      "1\t",
    { command: 'insert', path: '/memories/a.md', insert_line: 2.0, insert_text: 'c' } =>
      'The file /memories/a.md has been edited.',
    { command: 'insert', path: '/memories/empty.md', insert_line: 2, insert_text: "first\n" } =>
      'Error: Invalid `insert_line` parameter: 2. It should be within the range of lines of the file: [0, 1]',
    { command: 'insert', path: '/memories/empty.md', insert_line: 0, insert_text: "first\n" } =>
      'The file /memories/empty.md has been edited.',
    { command: 'str_replace', path: '/memories/long.md', old_str: 'line 7', new_str: "seven\nand a half" } =>
      "The memory file has been edited. A snippet of /memories/long.md with line numbers:\n     " \
      "3\tline 3\n     4\tline 4\n     5\tline 5\n     6\tline 6\n     7\tseven\n     8\tand a half\n     " \
      "9\tline 8\n    10\tline 9\n    11\tline 10\n    12\tline 11",
    { command: 'str_replace', path: '/memories/long.md', old_str: 'line 2' } =>
      "The memory file has been edited. A snippet of /memories/long.md with line numbers:\n     " \
      "1\tline 1\n     2\t\n     3\tline 3\n     4\tline 4\n     5\tline 5\n     6\tline 6",
    { command: 'str_replace', path: '/memories/long.md', old_str: 'line 12', new_str: 'twelve' } =>
      "The memory file has been edited. A snippet of /memories/long.md with line numbers:\n     " \
      "9\tline 8\n    10\tline 9\n    11\tline 10\n    12\tline 11\n    13\ttwelve"
  }.freeze

  def test_mem01_a_long_files_view_is_cut_at_16000_characters_and_a_file_holds_at_most_its_limit
    store = HashMemoryStore.new
    { 'long.md' => (['x' * 99] * 400).join("\n"), 'one-line.md' => 'é' * 20_000 }.each { store.write('sam', *it) }

    results = run_memory(store, 'sam', { command: 'view', path: '/memories/long.md' },
                         { command: 'create', path: '/memories/big.md', file_text: 'é' * 50_001 },
                         { command: 'create', path: '/memories/fits.md', file_text: 'é' * 50_000 },
                         { command: 'view', path: '/memories/one-line.md' })
    numbered = (1..149).map { format("%<line>6d\t%<text>s", line: it, text: 'x' * 99) }.join("\n")

    assert_equal "Here's the content of /memories/long.md with line numbers:\n#{numbered}#{TRUNCATED}",
                 results[0].content
    assert_equal ['Error: /memories/big.md would hold 50001 characters; a memory file holds at most 50000. Keep it ' \
                  'shorter, or split it.', true], [results[1].content, results[1].error?]
    assert_equal ['File created successfully at: /memories/fits.md', false], [results[2].content, results[2].error?]
    # A view with no line break to end at is cut at the limit.
    assert_equal "Here's the content of /memories/one-line.md with line numbers:\n     1\t#{'é' * (16_000 - 7)}" \
                 "#{TRUNCATED}", results[3].content
    assert_nil store.read('sam', 'big.md')
  end

  def test_mem01_a_view_ends_at_the_last_line_break_in_its_second_half_else_at_the_limit
    {
      'x' * (16_000 - 7) => "     1\t#{'x' * (16_000 - 7)}",
      "#{'x' * (8_000 - 7)}\n#{'y' * 20_000}" => "     1\t#{'x' * (8_000 - 7)}#{TRUNCATED}",
      "#{'x' * (7_999 - 7)}\n#{'y' * 20_000}" =>
        "     1\t#{'x' * (7_999 - 7)}\n     2\t#{'y' * (16_000 - 7_999 - 8)}#{TRUNCATED}"
    }.each do |text, view|
      store = HashMemoryStore.new
      store.write('sam', 'a.md', text)

      assert_equal "Here's the content of /memories/a.md with line numbers:\n#{view}",
                   run_memory(store, 'sam', { command: 'view', path: '/memories/a.md' })[0].content
    end
  end
end
