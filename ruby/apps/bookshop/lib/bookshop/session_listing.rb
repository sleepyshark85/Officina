# frozen_string_literal: true

module Bookshop
  # A session as /sessions lists it: its title, nil until the summarizer has written one, its cost in US dollars, and
  # when its conversation last changed.
  SessionListing = Data.define(:id, :staff_member, :title, :cost, :updated)
  private_constant :SessionListing
end
