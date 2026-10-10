# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A file a memory store lists: its path within the scope (parts joined by "/") and its size in bytes.
    MemoryFile = Data.define(:path, :size)
  end
end
