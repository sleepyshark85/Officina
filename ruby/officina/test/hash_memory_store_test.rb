# frozen_string_literal: true

require 'test_helper'
require_relative 'memory_store_contract'

# The in-memory store, against the tests every memory store passes.
class HashMemoryStoreTest < Minitest::Test
  include MemoryStoreContract

  cover 'Sleepyshark::Officina::HashMemoryStore*'
  cover 'Sleepyshark::Officina::MemoryRules*'

  def setup
    @store = Sleepyshark::Officina::HashMemoryStore.new
  end

  def test_mem02_changing_the_callers_strings_after_a_write_or_rename_changes_nothing_stored
    scope = +'alice'
    path = +'a.md'
    text = +'tea'
    new_path = +'b.md'
    store.write(scope, path, text)
    store.rename(scope, path, new_path)
    store.write(scope, path, text)
    [scope, path, text, new_path].each { it << 'x' }

    assert_equal({ 'a.md' => 'tea', 'b.md' => 'tea' }, contents('alice'))
    assert_predicate store.read('alice', 'b.md'), :frozen?
  end

  private

  attr_reader :store

  # The store keeps nothing outside its Hash, where scopes are apart by its keys.
  def files_outside(_scope) = []
end
