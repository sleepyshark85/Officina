# frozen_string_literal: true

module Bookshop
  # The bookshop's nine tools on the core's tool API: five read, four write, and each write needs the staff member's
  # approval. The model gives values, never SQL. A request the shop refuses (too few copies, an unknown id) is an error
  # result in the shop's own words; a database that cannot be reached raises PG::Error, which the core turns into an
  # error result saying the tool failed. The model reads either and recovers. A result is the JSON .NET's and Go's
  # tools return.
  module Tools
    # @param shop [Shop]
    # @return [Array<Sleepyshark::Officina::Tool>]
    def self.all(shop)
      CatalogueTools.all(shop.catalogue) + CustomerTools.all(shop.customers) + OrderTools.all(shop.orders)
    end

    # A tool of the shop: an Officina::Tool, defined as Officina::Tool.new defines one, whose handler answers a
    # RefusedError with a failure carrying its message.
    # @yieldparam input [Data] the call's input
    # @yieldreturn [Object] the result, which the core writes as JSON
    # @return [Sleepyshark::Officina::Tool]
    def self.tool(name:, description:, input:, kind:, needs_approval: false)
      Sleepyshark::Officina::Tool.new(name:, description:, input:, kind:, needs_approval:) do |given|
        yield(given)
      rescue RefusedError => e
        Sleepyshark::Officina::ToolFailure.new(message: e.message)
      end
    end
  end
end
