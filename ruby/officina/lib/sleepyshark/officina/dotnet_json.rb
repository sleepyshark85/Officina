# frozen_string_literal: true

module Sleepyshark
  module Officina
    # JSON written as .NET's default encoder writes it, for the bytes that must match the .NET implementation's: the
    # schemas a session's prefix holds, so a session one implementation saved resumes in another.
    module DotnetJson
      # The escapes .NET writes in short form; every other character it escapes is a \uXXXX, in upper-case hex.
      SHORT = { "\b" => '\b', "\t" => '\t', "\n" => '\n', "\f" => '\f', "\r" => '\r', '\\' => '\\\\' }.freeze
      # What .NET escapes: everything outside printable ASCII (DEL included), and the HTML-sensitive characters.
      ESCAPED = /[^\x20-\x7E]|["&'+<>`\\]/
      private_constant :SHORT, :ESCAPED

      # @param text [String] UTF-8 text.
      # @return [String] text as a JSON string, quotes included.
      def self.string(text)
        escaped = text.gsub(ESCAPED) do |char|
          SHORT.fetch(char) { char.encode(Encoding::UTF_16BE).unpack('n*').map { |unit| format('\u%04X', unit) }.join }
        end
        %("#{escaped}")
      end
    end
    private_constant :DotnetJson
  end
end
