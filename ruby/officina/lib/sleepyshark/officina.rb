# frozen_string_literal: true

require_relative 'officina/version'
require_relative 'officina/error'
require_relative 'officina/usage'
require_relative 'officina/cancellation'
require_relative 'officina/tool'
require_relative 'officina/tool_call'
require_relative 'officina/tool_result'
require_relative 'officina/block'
require_relative 'officina/message'
require_relative 'officina/request'
require_relative 'officina/reply'
require_relative 'officina/text_delta'
require_relative 'officina/usage_reported'
require_relative 'officina/conversation_appended'
require_relative 'officina/completed'
require_relative 'officina/stopped'
require_relative 'officina/failed'
require_relative 'officina/conversation'
require_relative 'officina/run_engine'
require_relative 'officina/agent'

module Sleepyshark
  # Officina's core: a purpose-neutral library for building agentic applications.
  module Officina
  end
end
