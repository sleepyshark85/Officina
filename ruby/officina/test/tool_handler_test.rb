# frozen_string_literal: true

require 'test_helper'

# What a tool's handler is given: a block or a lambda, with or without the run's memory scope.
class ToolHandlerTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Search = Input.define { string :title, 'Part of the title.' }

  def test_tool01_a_handler_that_takes_a_third_parameter_gets_the_memory_scope
    { 'a block' => proc { |_, _, scope| scope },
      'a lambda' => ->(_, _, scope) { scope },
      'a lambda whose scope is optional' => ->(_, _, scope = 'none') { scope },
      'a lambda taking the rest' => ->(*given) { given.last },
      'a lambda taking a fourth' => ->(_, _, scope, _more = nil) { scope } }.each do |kind, handler|
      tool = Tool.new(name: 'search', description: 'Searches.', input: Search, kind: :read, &handler)

      assert_equal 'sam', tool.invoke('{"title":"Dune"}', Cancellation.new, 'sam'), kind
    end
  end

  def test_tool01_a_handler_of_the_input_and_the_cancellation_alone_still_works
    cancel = Cancellation.new
    with_keyword = ->(input, given, verbose: false) { [input.title, given.equal?(cancel) && !verbose] }
    { 'a block' => proc { |input, given| [input.title, given.equal?(cancel)] },
      'a lambda' => ->(input, given) { [input.title, given.equal?(cancel)] },
      'a lambda whose cancellation is optional' => ->(input, given = nil) { [input.title, given.equal?(cancel)] },
      'a lambda with a keyword' => with_keyword }.each do |kind, handler|
      tool = Tool.new(name: 'search', description: 'Searches.', input: Search, kind: :read, &handler)

      assert_equal '["Dune",true]', tool.invoke('{"title":"Dune"}', cancel, 'sam'), kind
    end
  end
end
