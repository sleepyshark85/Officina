# frozen_string_literal: true

module Sleepyshark
  module Officina
    Block = Data.define(:text, :raw, :tool_call, :tool_result)

    # One piece of a message. A block the model produced keeps the provider's JSON in +raw+, stored and replayed byte
    # for byte; the core reads only its views, +text+ and +tool_call+. A block the core made, such as the user's
    # message or a tool's result, has no raw form: the provider adapter renders it.
    class Block
      # A string, kept whole, or the whitespace between tokens.
      TOKEN = /("(?:[^"\\]|\\.)*")|[ \t\n\r]+/
      # Each as a \u escape of its code in upper-case hex.
      HTML = %w[< > &].to_h { |char| [char, format('\\u%04X', char.ord)] }.freeze
      private_constant :TOKEN, :HTML

      # @param text [String, nil] the text, for a text block
      # @param raw [String, nil] the provider's JSON for the block, exactly as received; nil for a block the core made
      # @param tool_call [ToolCall, nil] the call, for a block that asks to run a tool
      # @param tool_result [ToolResult, nil] a call's result, for a block the core made to answer it
      # @raise [Error] when the block has no text, raw JSON or tool result
      def initialize(text: nil, raw: nil, tool_call: nil, tool_result: nil)
        raise Error, 'A block needs text, raw JSON or a tool result' unless text || raw || tool_result

        # Empty text is no text in a block that holds anything else, as the JSON form leaves it out.
        text = nil if text == '' && (raw || tool_result)
        super(text: frozen(text), raw: frozen(raw), tool_call:, tool_result:)
      end

      # The canonical form in which a provider adapter stores a block's JSON: compact, with <, > and & escaped, and
      # every other escape as received. It changes no byte of a block already in that form.
      # @param json [String] valid JSON
      # @return [String] frozen
      def self.canonical(json)
        -json.gsub(TOKEN) { Regexp.last_match(1)&.gsub(/[<>&]/, HTML) || '' }
      end

      private

      def frozen(string) = string && -string
    end
  end
end
