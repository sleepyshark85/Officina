# frozen_string_literal: true

require 'test_helper'

# Tokens as the provider bills them.
class UsageTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  def test_agt03_usages_add_up_kind_by_kind
    first = Usage.new(input: 1, output: 2, cache_read: 3, cache_write: 4, cache_write_hour: 1)
    second = Usage.new(input: 10, output: 20, cache_read: 30, cache_write: 40, cache_write_hour: 5)

    assert_equal Usage.new(input: 11, output: 22, cache_read: 33, cache_write: 44, cache_write_hour: 6), first + second
  end

  def test_agt03_all_input_counts_the_input_tokens_cached_or_not
    assert_equal 7, Usage.new(input: 1, output: 8, cache_read: 2, cache_write: 4, cache_write_hour: 3).all_input
  end

  def test_agt03_a_new_usage_counts_no_tokens
    assert_equal Usage.new(input: 0, output: 0, cache_read: 0, cache_write: 0, cache_write_hour: 0), Usage.new
  end
end
