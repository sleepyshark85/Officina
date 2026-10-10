# frozen_string_literal: true

require 'anthropic'
require 'sleepyshark/officina'

module Sleepyshark
  module Officina
    # Officina's model adapter for Claude: the only gem that uses the Anthropic SDK.
    module Claude
    end
  end
end

require_relative 'claude/transient_error'
require_relative 'claude/authentication_error'
require_relative 'claude/invalid_request_error'
require_relative 'claude/failure'
require_relative 'claude/context_editing'
require_relative 'claude/call'
require_relative 'claude/model'
