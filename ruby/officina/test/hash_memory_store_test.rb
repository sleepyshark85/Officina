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

  def test_mem02_a_read_text_cannot_change_the_stored_one
    text = +'tea'
    store.write('alice', 'prefs.md', text)
    text << ' and cake'

    assert_predicate store.read('alice', 'prefs.md'), :frozen?
    assert_equal 'tea', store.read('alice', 'prefs.md')
  end

  private

  attr_reader :store

  # The store keeps nothing outside its Hash, where scopes are apart by its keys.
  def files_outside(_scope) = []
end
