# frozen_string_literal: true

require 'test_helper'
require_relative 'support/memory_calls'

# The memory tool's refusals: each an error result, in Claude's memory tool's words, that changes nothing.
class MemoryToolMistakesTest < Minitest::Test
  include Sleepyshark::Officina
  include MemoryCalls

  cover 'Sleepyshark::Officina*'

  def test_mem01_mistakes_are_error_results_that_change_nothing
    invalid_range = 'Error: Invalid `view_range`: it should be [start, end] with 1 <= start <= end <= 2, or end -1 ' \
                    'for the end of the file.'
    each_store do |store|
      { 'a.md' => "x\nx\n", 'b.md' => 'b' }.each { store.write('sam', *it) }

      views = [[3, 1], [1], [0, 1], [1, 3]].map { { command: 'view', path: '/memories/a.md', view_range: it } }
      results = run_memory(store, 'sam', *MISTAKES.keys, *views, { command: 'view' },
                           { command: 'undo', path: '/memories/a.md' })

      assert_equal [*MISTAKES.values, *[invalid_range] * 4,
                    'Error: The path (none) is not a valid path under /memories.',
                    "The input does not match the tool's schema:\n" \
                    '/command: must be one of ["view","create","str_replace","insert","delete","rename"]'],
                   results.map(&:content)
      assert(results.all?(&:error?))
      assert_equal({ 'a.md' => "x\nx\n", 'b.md' => 'b' }, contents(store, 'sam'))
    end
  end

  MISTAKES = {
    { command: 'str_replace', path: '/memories/a.md', old_str: 'x', new_str: 'y' } =>
      'No replacement was performed. Multiple occurrences of old_str `x` in lines: 1, 2. Please ensure it is unique',
    { command: 'str_replace', path: '/memories/a.md', old_str: 'z', new_str: 'y' } =>
      'No replacement was performed, old_str `z` did not appear verbatim in /memories/a.md.',
    { command: 'str_replace', path: '/memories/a.md' } =>
      'Error: Parameter `old_str` is required for command: str_replace',
    { command: 'str_replace', path: '/memories/a.md', old_str: '' } =>
      'Error: Parameter `old_str` is required for command: str_replace',
    { command: 'str_replace', path: '/memories/c.md', old_str: 'x' } =>
      'Error: The path /memories/c.md does not exist. Please provide a valid path.',
    { command: 'insert', path: '/memories/a.md', insert_line: 9, insert_text: 'y' } =>
      'Error: Invalid `insert_line` parameter: 9. It should be within the range of lines of the file: [0, 3]',
    { command: 'insert', path: '/memories/a.md', insert_line: -1, insert_text: 'y' } =>
      'Error: Invalid `insert_line` parameter: -1. It should be within the range of lines of the file: [0, 3]',
    { command: 'insert', path: '/memories/a.md', insert_text: 'y' } =>
      'Error: Parameters `insert_line` and `insert_text` are required for command: insert',
    { command: 'insert', path: '/memories/a.md', insert_line: 0 } =>
      'Error: Parameters `insert_line` and `insert_text` are required for command: insert',
    { command: 'insert', path: '/memories/c.md', insert_line: 0, insert_text: 'y' } =>
      'Error: The path /memories/c.md does not exist',
    { command: 'create', path: '/memories/a.md/c.md', file_text: 'y' } =>
      'Error: Cannot create /memories/a.md/c.md: /memories/a.md is a file.',
    { command: 'create', path: '/memories/a.md' } => 'Error: Parameter `file_text` is required for command: create',
    { command: 'create', path: '/memories', file_text: 'y' } => 'Error: Cannot create /memories: it is a directory.',
    { command: 'rename', old_path: '/memories/a.md', new_path: '/memories/b.md' } =>
      'Error: The destination /memories/b.md already exists',
    { command: 'rename', old_path: '/memories/c.md', new_path: '/memories/d.md' } =>
      'Error: The path /memories/c.md does not exist',
    { command: 'rename', old_path: '/memories', new_path: '/memories/d' } =>
      'Error: The memory directory /memories itself cannot be renamed.',
    { command: 'rename', old_path: '/memories/b.md', new_path: '/memories/a.md/b.md' } =>
      'Error: Cannot move to /memories/a.md/b.md: /memories/a.md is a file.',
    { command: 'rename', old_path: '/memories/a.md', new_path: '/memories/a.md/c.md' } =>
      'Error: Cannot move /memories/a.md into itself.',
    { command: 'rename', old_path: '/memories/b.md' } => 'Error: The path (none) is not a valid path under /memories.',
    { command: 'delete', path: '/memories' } => 'Error: The memory directory /memories itself cannot be deleted.',
    { command: 'delete', path: '/memories/c.md' } => 'Error: The path /memories/c.md does not exist'
  }.freeze
end
