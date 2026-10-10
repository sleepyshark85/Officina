# frozen_string_literal: true

require 'json'
require 'securerandom'

module Sleepyshark
  module Officina
    # The append-only messages between an agent and its model. The host owns it and stores it between runs as JSON
    # (#to_json, Conversation.from_json), which keeps every block's raw JSON byte for byte; only a run appends to it.
    # One run at a time may use it. The host may read and save it while a run uses it, as from the run's block when a
    # message is appended: each read is a snapshot.
    class Conversation
      # The fingerprint and the messages, frozen together, so a reader on another thread never sees one of an append
      # without the other: an append replaces the whole state in one assignment.
      State = Data.define(:fingerprint, :messages)
      private_constant :State

      # @return [String] identifies the conversation for the host, such as a session id
      attr_reader :id

      # @param id [String] random unless given
      # @param fingerprint [String, nil] and +messages+ as a stored conversation holds them; a new one has neither
      # @param messages [Array<Message>]
      def initialize(id: SecureRandom.hex(16), fingerprint: nil, messages: [])
        @id = -id
        @state = State.new(fingerprint: fingerprint && -fingerprint, messages: messages.dup.freeze)
        @lock = Mutex.new
      end

      # @return [String, nil] the prefix fingerprint of the agent whose run first appended to it; nil before that. A run
      #   of an agent with another fingerprint fails without calling the model.
      def fingerprint = @state.fingerprint

      # @return [Array<Message>] oldest first, frozen; later appends do not change it
      def messages = @state.messages

      # Reads a conversation from the JSON #to_json writes, which the other implementations of Officina write too.
      # @raise [Error] when the JSON is not a conversation's: invalid, of another shape, a message of an unknown role or
      #   without blocks, a block without text, raw JSON or a tool result, or raw JSON that is not valid
      def self.from_json(json)
        # Steep does not type what a pattern binds.
        # @type var id: String
        # @type var messages: Array[untyped]
        # @type var fingerprint: String?
        JSON.parse(json, allow_duplicate_key: false, symbolize_names: true) => { id: String => id, **rest }
        rest => { messages: Array => messages }
        rest[:fingerprint] => String | nil => fingerprint
        new(id:, fingerprint:, messages: messages.map { |message| read_message(message) })
      rescue JSON::ParserError => e
        raise Error, "Not a conversation's JSON: #{e.message}"
      rescue NoMatchingPatternError
        # Its message holds the whole value that did not match, which may be long and hold anything.
        raise Error, "Not a conversation's JSON: a member is missing or of the wrong type"
      end

      # The conversation as JSON: each block's raw JSON as a JSON string, so it reads back byte for byte however a
      # store rewrites the JSON around it.
      # @return [String]
      def to_json(*)
        state = @state
        messages = state.messages.map { |message| message_json(message) }
        JSON.generate({ id:, fingerprint: state.fingerprint, messages: }.compact)
      end

      # Gives the block the conversation for one run, and a lambda that appends messages and binds the fingerprint of
      # the run's agent. It is the run engine's way in, public as Ruby has no visibility between classes; a host has no
      # need of it.
      # @raise [Error] when another run is using the conversation
      def hold
        raise Error, 'Another run is using the conversation; one run at a time may use it' unless @lock.try_lock

        begin
          yield ->(appended, fingerprint) { append(appended, fingerprint) }
        ensure
          @lock.unlock
        end
      end

      def self.read_message(json)
        # @type var role: String
        # @type var blocks: Array[untyped]
        json => { role: 'user' | 'assistant' | 'operator' => role, blocks: Array => blocks }
        Message.new(role: role.to_sym, blocks: blocks.map { |block| read_block(block) })
      end

      def self.read_block(json)
        # @type var text: String?
        # @type var raw: String?
        json => Hash
        json[:text] => String | nil => text
        json[:raw] => String | nil => raw
        JSON.parse(raw, allow_duplicate_key: false) if raw
        Block.new(text:, raw:, tool_call: read_call(json[:toolCall]), tool_result: read_result(json[:toolResult]))
      end

      def self.read_call(json)
        case json
        in nil then nil
        in { id: String => id, name: String => name, input: String => input }
          ToolCall.new(id:, name:, input:)
        end
      end

      def self.read_result(json)
        case json
        in nil then nil
        in { callId: String => call_id, content: String => content, isError: true | false => error }
          ToolResult.new(call_id:, content:, error:)
        end
      end
      private_class_method :read_message, :read_block, :read_call, :read_result

      private

      def append(appended, fingerprint)
        @state = State.new(fingerprint:, messages: [*@state.messages, *appended].freeze)
      end

      def message_json(message)
        { role: message.role, blocks: message.blocks.map { |block| block_json(block) } }
      end

      # The text is left out when empty in a block that has raw JSON or a tool result, as the other implementations
      # write it.
      def block_json(block)
        text = block.text unless block.text.to_s.empty? && (block.raw || block.tool_result)
        { text:, raw: block.raw, toolCall: block.tool_call&.then { |call| call_json(call) },
          toolResult: block.tool_result&.then { |result| result_json(result) } }.compact
      end

      def call_json(call) = { id: call.id, name: call.name, input: call.input }
      def result_json(result) = { callId: result.call_id, content: result.content, isError: result.error? }
    end
  end
end
