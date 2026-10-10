# frozen_string_literal: true

require_relative 'memory_concurrency_contract'
require_relative 'memory_scope_contract'

# The tests every memory store passes, run against each built-in store by a test class that includes them and gives
# the store (#store) and the files it keeps on disk outside a scope's own (#files_outside).
module MemoryStoreContract
  include MemoryScopeContract
  include MemoryConcurrencyContract

  MemoryFile = Sleepyshark::Officina::MemoryFile

  def test_mem02_a_written_file_reads_back_and_is_listed_with_its_size_in_bytes
    store.write('alice', 'notes/café.md', 'Likes crème')

    assert_equal 'Likes crème', store.read('alice', 'notes/café.md')
    assert_equal [MemoryFile.new(path: 'notes/café.md', size: 12)], store.list('alice')
  end

  def test_mem02_writing_again_replaces_the_text
    store.write('alice', 'a.md', 'first')
    store.write('alice', 'a.md', 'second')

    assert_equal({ 'a.md' => 'second' }, contents('alice'))
  end

  def test_mem02_a_missing_file_reads_as_nil_and_an_unwritten_scope_lists_nothing
    assert_nil store.read('alice', 'missing.md')
    assert_empty store.list('alice')
  end

  def test_mem02_paths_with_dots_spaces_and_letters_beyond_ascii_are_accepted
    paths = ['.hidden', 'a..b', 'a b/c d.md', '~', 'été/日本.md', 'console.md', 'COM10', 'x' * 255]
    paths.each { store.write('alice', it, it) }

    assert_equal paths.to_h { [it, it] }, contents('alice')
  end

  def test_mem02_the_longest_scope_and_parts_the_rules_allow_are_kept
    scope = "#{'é' * 63}x"
    path = "#{'é' * 127}x/#{'日' * 85}"
    store.write(scope, path, 'x')

    assert_equal({ path => 'x' }, contents(scope))
  end

  def test_mem02_deleting_removes_the_file_and_deleting_a_missing_one_does_nothing
    store.write('alice', 'a/b/c.md', 'x')
    store.write('alice', 'a/d.md', 'y')
    2.times { store.delete('alice', 'a/b/c.md') }
    store.delete('alice', 'never.md')

    assert_equal({ 'a/d.md' => 'y' }, contents('alice'))
  end

  def test_mem02_a_deleted_files_directory_can_become_a_file
    store.write('alice', 'a/b.md', 'x')
    store.delete('alice', 'a/b.md')
    store.write('alice', 'a', 'now a file')

    assert_equal({ 'a' => 'now a file' }, contents('alice'))
  end

  def test_mem02_renaming_moves_the_file_and_leaves_no_directory_behind
    store.write('alice', 'old/a.md', 'text')
    store.rename('alice', 'old/a.md', 'new/deeper/b.md')
    store.write('alice', 'old', 'its directory went with it')

    assert_equal({ 'new/deeper/b.md' => 'text', 'old' => 'its directory went with it' }, contents('alice'))
  end

  def test_mem02_renaming_a_missing_file_or_onto_a_taken_path_is_refused_and_changes_nothing
    { 'a.md' => 'a', 'b.md' => 'b', 'dir/c.md' => 'c' }.each { |path, text| store.write('alice', path, text) }

    refusals = {
      %w[missing.md x.md] => 'There is no memory file missing.md.',
      %w[missing.md b.md] => 'There is no memory file missing.md.',
      %w[a.md b.md] => 'There is already a memory file b.md.',
      %w[a.md a.md] => 'There is already a memory file a.md.',
      %w[a.md dir] => 'dir is a memory directory.',
      %w[a.md b.md/x] => 'b.md/x runs through the memory file b.md.'
    }
    messages = refusals.keys.to_h { |paths| [paths, assert_raises(Error) { store.rename('alice', *paths) }.message] }

    assert_equal refusals, messages
    assert_equal({ 'a.md' => 'a', 'b.md' => 'b', 'dir/c.md' => 'c' }, contents('alice'))
  end

  def test_mem02_a_path_that_is_a_directory_or_runs_through_a_file_is_refused
    store.write('alice', 'dir/a.md', 'a')

    messages = %w[dir dir/a.md/b.md].map { |path| assert_raises(Error) { store.write('alice', path, 'x') }.message }

    assert_equal ['dir is a memory directory.', 'dir/a.md/b.md runs through the memory file dir/a.md.'], messages
    assert_nil store.read('alice', 'dir')
    assert_equal({ 'dir/a.md' => 'a' }, contents('alice'))
  end

  def test_mem02_text_that_is_not_valid_utf8_is_refused
    texts = ["\xFF".b, (+"\xFF").force_encoding(Encoding::UTF_8), 'a'.encode(Encoding::UTF_16LE)]
    messages = texts.map { |text| assert_raises(Error) { store.write('alice', 'a.md', text) }.message }.uniq

    assert_equal ['Memory text must be a valid UTF-8 string.'], messages
    assert_empty store.list('alice')
  end
end
