# frozen_string_literal: true

module Bookshop
  # A book of the catalogue: its price a BigDecimal in dollars, year its year of publication, stock the copies in
  # stock.
  Book = Data.define(:id, :title, :author, :genre, :price, :year, :stock)
end
