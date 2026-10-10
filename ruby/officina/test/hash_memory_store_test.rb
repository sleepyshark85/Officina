# frozen_string_literal: true

require 'test_helper'
require_relative 'memory_store_contract'

# The in-memory store, against the tests every memory store passes.
class HashMemoryStoreTest < Minitest::Test
  include MemoryStoreContract

  cover 'Sleepyshark::Officina::HashMemoryStore*'
  cover 'Sleepyshark::Officina::MemoryPath*'

  def setup
    @store = Sleepyshark::Officina::HashMemoryStore.new
  end

  def test_mem02_changing_the_callers_strings_after_a_write_or_rename_changes_nothing_stored
    strings = [+'alice', +'a.md', +'tea', +'b.md']
    store.write(*strings.first(3))
    store.rename(strings[0], strings[1], strings[3])
    strings.each { it << 'x' }

    assert_equal({ 'b.md' => 'tea' }, contents('alice'))
    assert_predicate store.read('alice', 'b.md'), :frozen?
  end

  private

  attr_reader :store

  # The store keeps nothing outside its Hash, where scopes are apart by its keys.
  def files_outside(_scope) = []
end
