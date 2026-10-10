# frozen_string_literal: true

module Bookshop
  # A session as /sessions lists it: its title, summary and changes, nil, nil and none until the summarizer has
  # written them; its cost in US dollars; when its conversation last changed; and whether it is stale, changed since
  # its last summary or never summarized.
  SessionListing = Data.define(:id, :staff_member, :title, :summary, :changes, :cost, :updated, :stale)
  private_constant :SessionListing
end
