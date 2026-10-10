# frozen_string_literal: true

module Bookshop
  # The export server could not be reached at the start, or lacks a tool the assistant needs; the message says which
  # server, why, and how to start it.
  class ExportServerError < StandardError
  end
end
