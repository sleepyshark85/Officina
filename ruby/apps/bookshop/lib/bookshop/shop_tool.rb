# frozen_string_literal: true

module Bookshop
  # How every bookshop tool is built: the core's tool, with a request the shop refuses answered in the shop's words.
  module ShopTool
    # An Officina::Tool, defined as Officina::Tool.new defines one, whose handler answers a RefusedError with a
    # ToolFailure carrying its message. Anything else the handler raises, such as a PG::Error, goes on to the core.
    # @yieldparam input [Data] the call's input; the handler does not get the run's Cancellation
    # @yieldreturn [Object] the result, which the core writes as JSON
    # @return [Sleepyshark::Officina::Tool]
    def self.define(name:, description:, input:, kind:, **approval)
      Sleepyshark::Officina::Tool.new(name:, description:, input:, kind:, **approval) do |given|
        yield(given)
      rescue RefusedError => e
        Sleepyshark::Officina::ToolFailure.new(message: e.message)
      end
    end
  end
  private_constant :ShopTool
end
