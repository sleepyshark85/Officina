# frozen_string_literal: true

require 'test_helper'
require_relative 'support/memory_calls'

# The memory tool through real runs, in either store: only the model is scripted.
class MemoryToolTest < Minitest::Test
  include Sleepyshark::Officina
  include MemoryCalls

  cover 'Sleepyshark::Officina*'

  # A session's commands, each with its result: every command, nested directories, a rename and a delete of one.
  ROUND_TRIP = {
    { command: 'view', path: '/memories' } =>
      "Here're the files and directories up to 2 levels deep in /memories, excluding hidden items:\n0B\t/memories",
    { command: 'create', path: '/memories/prefs.md', file_text: "Prices: without tax.\nTone: brief.\n" } =>
      'File created successfully at: /memories/prefs.md',
    { command: 'str_replace', path: '/memories/prefs.md', old_str: 'without', new_str: 'with' } =>
      "The memory file has been edited. A snippet of /memories/prefs.md with line numbers:\n     " \
      "1\tPrices: with tax.\n     2\tTone: brief.\n     3\t",
    { command: 'insert', path: '/memories/prefs.md', insert_line: 0, insert_text: "# Sam\n" } =>
      'The file /memories/prefs.md has been edited.',
    { command: 'view', path: '/memories/prefs.md' } =>
      "Here's the content of /memories/prefs.md with line numbers:\n     1\t# Sam\n     2\tPrices: with tax.\n     " \
      "3\tTone: brief.",
    { command: 'view', path: '/memories/prefs.md', view_range: [2, -1] } =>
      "Here's the content of /memories/prefs.md with line numbers:\n     2\tPrices: with tax.\n     3\tTone: brief.",
    { command: 'create', path: '/memories/customers/ana/notes.md', file_text: 'Likes crime.' } =>
      'File created successfully at: /memories/customers/ana/notes.md',
    { command: 'rename', old_path: '/memories/customers', new_path: '/memories/people' } =>
      'Successfully renamed /memories/customers to /memories/people',
    { command: 'view', path: '/memories/' } =>
      "Here're the files and directories up to 2 levels deep in /memories, excluding hidden items:\n49B\t" \
      "/memories\n12B\t/memories/people\n12B\t/memories/people/ana\n37B\t/memories/prefs.md",
    { command: 'delete', path: '/memories/people' } => 'Successfully deleted /memories/people',
    { command: 'view', path: '/memories/people/ana/notes.md' } =>
      'The path /memories/people/ana/notes.md does not exist. Please provide a valid path.'
  }.freeze

  def test_mem01_mem02_the_model_views_creates_edits_renames_and_deletes_files_in_either_store
    each_store do |store|
      results = run_memory(store, 'sam', *ROUND_TRIP.keys)

      assert_equal ROUND_TRIP.values, results.map(&:content)
      assert_equal [*[false] * 10, true], results.map(&:error?)
      assert_equal [MemoryFile.new(path: 'prefs.md', size: 37)], store.list('sam')
    end
  end

  def test_mem03_a_path_outside_the_scope_is_refused_with_an_error_result
    ['/etc/passwd', '/memories/../secrets.env', '/memories/notes/../../x', '/memories/%2e%2e/x', '/memories/..\\x',
     '/memories//x', '/memoriesx/a', 'memories/a', '/memories/C:/a', '/memories/./a', '/memories/ ', '/memories/NUL',
     "/memories/a\u0000b", '/', ''].each do |path|
      store = HashMemoryStore.new

      results = run_memory(store, 'sam', { command: 'create', path:, file_text: 'x' },
                           { command: 'rename', old_path: '/memories/a', new_path: path })

      assert_equal [["Error: The path #{path} is not a valid path under /memories.", true]] * 2,
                   results.map { [it.content, it.error?] }, path
      assert_empty store.list('sam')
    end
  end

  def test_mem03_a_run_sees_only_its_scopes_files
    store = HashMemoryStore.new
    run_memory(store, 'ana', { command: 'create', path: '/memories/a.md', file_text: "Ana's" })

    results = run_memory(store, 'ben', { command: 'view', path: '/memories' },
                         { command: 'view', path: '/memories/a.md' })

    assert results[0].content.end_with?("\n0B\t/memories")
    assert_predicate results[1], :error?
    assert_equal "Ana's", store.read('ana', 'a.md')
  end

  def test_mem01_what_the_store_raises_fails_the_call_with_its_message
    store = Object.new
    def store.list(_) = []
    def store.write(*) = raise IOError, 'the disk is gone'

    results = run_memory(store, 'sam', { command: 'create', path: '/memories/b.md', file_text: 'b' })

    assert_equal ['The tool failed: the disk is gone', true], [results[0].content, results[0].error?]
  end

  def test_mem01_the_memory_tool_is_a_write_named_memory_that_says_what_it_is
    memory = MemoryTool.new(HashMemoryStore.new)

    assert_equal ['memory', :write, true], [memory.name, memory.kind, memory.memory?]
    refute_predicate Tool.new(name: 'search', description: 'Searches.', input: Schema.new('{}'), kind: :read) { '' },
                     :memory?
    assert_match %r{directory of text files under /memories}, memory.description
    refused = assert_raises(Error) { memory.invoke('{"command":"view","path":"/memories"}', Cancellation.new) }

    assert_equal 'The memory tool needs the memory scope of a run', refused.message
  end

  def test_aud03_the_json_lines_sink_writes_the_memory_scope
    Dir.mktmpdir do |dir|
      path = File.join(dir, 'audit.jsonl')
      model = Model.new(Model.text('Hi.'))

      Agent.new(model:, instructions: 'Hi.', tools: [MemoryTool.new(HashMemoryStore.new)],
                audit_sink: JsonLinesAuditSink.new(path))
           .run(Conversation.new, 'Hi', memory_scope: 'sam')

      assert_equal(%w[sam sam], File.readlines(path).map { JSON.parse(it).fetch('memoryScope') })
    end
  end
end
