# frozen_string_literal: true

# The tests that every memory store stays whole when many runs share it, run against each built-in store through
# MemoryStoreContract. The test class gives the store (#store).
module MemoryConcurrencyContract
  def test_mem02_concurrent_writers_lose_no_file
    failures = on_threads(8) { |writer| write_and_rename(writer, 20) }

    assert_equal [[], 161], [failures, store.list('alice').size]
    assert_match(/\A\d-\d+\z/, store.read('alice', 'shared.md'))
  end

  def test_mem02_concurrent_writes_lists_and_deletes_in_one_directory_never_fail
    failures = on_threads(8) { |writer| write_list_and_delete("d/#{writer}.md", 50) }

    assert_equal [[], []], [failures, store.list('alice')]
  end

  private

  # Runs the work on that many threads at once, joins every one, and returns what they raised.
  def on_threads(count)
    threads = Array.new(count) do |index|
      Thread.new do
        yield(index)
        nil
      rescue StandardError => e
        e
      end
    end
    threads.filter_map(&:value)
  end

  # Writes files, moves each, and overwrites one file every writer shares.
  def write_and_rename(writer, count)
    count.times do |n|
      store.write('alice', "w#{writer}/#{n}.md", "#{writer}-#{n}")
      store.rename('alice', "w#{writer}/#{n}.md", "done/#{writer}-#{n}.md")
      store.write('alice', 'shared.md', "#{writer}-#{n}")
    end
  end

  # Writes, lists and deletes in a directory other writers create and remove all along.
  def write_list_and_delete(path, count)
    count.times do
      store.write('alice', path, 'x')
      store.list('alice')
      store.delete('alice', path)
    end
  end
end
