# frozen_string_literal: true

require 'test_helper'

# What a tool's handler is given: the input, the run's cancellation and its memory scope.
class ToolHandlerTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Search = Input.define { string :title, 'Part of the title.' }

  def test_tool01_a_handler_that_takes_a_third_parameter_gets_the_memory_scope
    { 'a block' => proc { |_, _, scope| scope }, 'a lambda' => ->(_, _, scope) { scope } }.each do |kind, handler|
      tool = Tool.new(name: 'search', description: 'Searches.', input: Search, kind: :read, &handler)

      assert_equal 'sam', tool.invoke('{"title":"Dune"}', Cancellation.new, 'sam'), kind
    end
  end
end
