# frozen_string_literal: true

require 'test_helper'
require_relative 'support/memory_calls'

# The memory tool's listings of a directory.
class MemoryToolListingTest < Minitest::Test
  include Sleepyshark::Officina
  include MemoryCalls

  cover 'Sleepyshark::Officina*'

  def test_mem01_a_listing_shows_two_levels_without_hidden_items_and_sizes_in_k_and_m
    store = HashMemoryStore.new
    store.write('sam', 'a/b/c/d.md', 'x' * 2048)
    store.write('sam', '.hidden/e.md', 'e')
    store.write('sam', 'a/.f.md', 'f')
    store.write('sam', 'big.md', 'y' * (3 * 1024 * 1024 / 2))

    results = run_memory(store, 'sam', { command: 'view', path: '/memories' }, { command: 'view', path: '/memories/a' },
                         { command: 'view', path: '/memories/a/b/c/' })

    assert_equal [
      "Here're the files and directories up to 2 levels deep in /memories, excluding hidden items:\n" \
      "1.5M\t/memories\n2.0K\t/memories/a\n2.0K\t/memories/a/b\n1.5M\t/memories/big.md",
      "Here're the files and directories up to 2 levels deep in /memories/a, excluding hidden items:\n" \
      "2.0K\t/memories/a\n2.0K\t/memories/a/b\n2.0K\t/memories/a/b/c",
      "Here're the files and directories up to 2 levels deep in /memories/a/b/c, excluding hidden items:\n" \
      "2.0K\t/memories/a/b/c\n2.0K\t/memories/a/b/c/d.md"
    ], results.map(&:content)
  end

  def test_mem01_a_listing_shows_sizes_below_a_kilobyte_in_bytes
    store = HashMemoryStore.new
    store.write('sam', 'a.md', 'x' * 1023)
    store.write('sam', 'b.md', 'x' * 1024)
    store.write('sam', 'c.md', 'x' * ((1024 * 1024) - 1))

    assert_equal "Here're the files and directories up to 2 levels deep in /memories, excluding hidden items:\n" \
                 "1.0M\t/memories\n1023B\t/memories/a.md\n1.0K\t/memories/b.md\n1024.0K\t/memories/c.md",
                 run_memory(store, 'sam', { command: 'view', path: '/memories' })[0].content
  end
end
