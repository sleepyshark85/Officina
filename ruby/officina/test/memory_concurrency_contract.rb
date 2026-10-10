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

  # Threads are switched at every block call, so one walks the files while another adds or moves one.
  def test_mem02_writes_renames_and_lists_interleaved_inside_each_call_lose_no_file
    failures = switching_at_every_block { on_threads(6) { |worker| write_rename_or_list(worker, 20) } }
    written = Array.new(20) { "#{it}.md" }.sort

    assert_empty failures
    assert_equal [written, written, ['20.md'], ['20.md'], [], []],
                 Array.new(6) { store.list("t#{it}").map(&:path).sort }
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

  # Runs the block with a switch to another thread at every block call, so that threads interleave inside a store's
  # calls (a walk over its files, a check and the change it allows), not only between them.
  def switching_at_every_block(&)
    TracePoint.new(:b_call) { Thread.pass }.enable(target_thread: nil, &)
  end

  # In a scope of its own, by its number: writes new files (0 and 1), moves one file along 0.md, 1.md and so on (2
  # and 3), or lists (the others).
  def write_rename_or_list(worker, count)
    scope = "t#{worker}"
    case worker / 2
    when 0 then count.times { store.write(scope, "#{it}.md", 'x') }
    when 1
      store.write(scope, '0.md', 'x')
      count.times { store.rename(scope, "#{it}.md", "#{it + 1}.md") }
    else count.times { store.list(scope) }
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
