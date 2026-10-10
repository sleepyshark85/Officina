# frozen_string_literal: true

module Bookshop
  # The export server could not be reached at the start, or lacks a tool the assistant needs or gives one a schema it
  # cannot use; the message says which server, why, and what to do.
  class ExportServerError < StandardError
  end
end
