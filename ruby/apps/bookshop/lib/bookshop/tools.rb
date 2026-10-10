# frozen_string_literal: true

module Bookshop
  # The bookshop's nine tools on the core's tool API: five read, four write, and each write needs the staff member's
  # approval. The model gives values, never SQL. A request the shop refuses (too few copies, an unknown id) raises
  # RefusedError and a database that cannot be reached PG::Error; the core turns either into an error result with its
  # message, which the model reads and recovers from. A result is the JSON .NET's and Go's tools return.
  module Tools
    # @param shop [Shop]
    # @return [Array<Sleepyshark::Officina::Tool>]
    def self.all(shop)
      CatalogueTools.all(shop.catalogue) + CustomerTools.all(shop.customers) + OrderTools.all(shop.orders)
    end
  end
end
