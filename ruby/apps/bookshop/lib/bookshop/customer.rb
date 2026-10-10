# frozen_string_literal: true

module Bookshop
  # A customer of the shop. Emails are unique.
  Customer = Data.define(:id, :name, :email)
end
