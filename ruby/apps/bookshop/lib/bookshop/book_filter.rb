# frozen_string_literal: true

module Bookshop
  BookFilter = Data.define(:title, :author, :genre, :max_price, :in_stock)

  # What a catalogue search keeps; every filter is optional. Title and author match part of the text, genre the whole
  # name, both ignoring case; max_price is in dollars; in_stock keeps only books with copies in stock.
  class BookFilter
    def initialize(title: nil, author: nil, genre: nil, max_price: nil, in_stock: false) = super
  end
end
