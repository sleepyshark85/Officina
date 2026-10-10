# frozen_string_literal: true

module Sleepyshark
  module Officina
    # JSON strings written as .NET's default encoder writes them, for the bytes every implementation must agree on:
    # the prefix the fingerprint hashes and the schemas in it, and the canonical form of a stored block.
    module DotnetJson
      # The characters .NET escapes: everything outside printable ASCII (DEL included), and " & ' + < > ` \.
      ESCAPED_CHARACTER = /[^\x20-\x7E]|["&'+<>`\\]/
      # The escapes .NET writes in short form; it writes every other one as a \u escape.
      SHORT_ESCAPES = { "\b" => '\b', "\t" => '\t', "\n" => '\n', "\f" => '\f', "\r" => '\r', '\\' => '\\\\' }.freeze
      private_constant :ESCAPED_CHARACTER, :SHORT_ESCAPES

      # Invalid UTF-8 becomes U+FFFD, as .NET's encoder writes text it cannot encode, so the result is always JSON.
      # @param text [String] UTF-8 text.
      # @return [String] text as a JSON string, quotes included.
      def self.string(text)
        escaped = text.scrub.gsub(ESCAPED_CHARACTER) { |char| SHORT_ESCAPES.fetch(char) { unicode_escape(char) } }
        %("#{escaped}")
      end

      # @param char [String] one character.
      # @return [String] its UTF-16 code units as \u escapes in upper-case hex: two for a character outside the BMP.
      def self.unicode_escape(char)
        char.encode(Encoding::UTF_16BE).unpack('n*').map { |unit| format('\u%04X', unit) }.join
      end
    end
    private_constant :DotnetJson
  end
end
