# frozen_string_literal: true

require 'json'
require 'strscan'

module Sleepyshark
  module Officina
    module Mcp
      # The members of a JSON object, or the elements of a JSON array, each as the text it was written as, which
      # JSON.parse does not keep. It reads text that JSON.parse has accepted without checking it again, but raises
      # JSON::ParserError where it cannot go on, so text it cannot follow never holds it in a loop.
      module RawJson
        # A string, a number, true, false or null.
        SCALAR = /"(?:[^"\\]++|\\.)*+"|[-+.\w]++/
        # What lies between the brackets of an object or array: strings, whole, and anything but brackets and quotes.
        BETWEEN = /(?:"(?:[^"\\]++|\\.)*+"|[^"\[\]{}]++)*+/
        OPENING = /[\[{]/
        CLOSING = /[\]}]/
        private_constant :SCALAR, :BETWEEN, :OPENING, :CLOSING

        # @param text [String] an object's JSON text
        # @return [Hash{String => String}] its members' names to their values' text
        def self.members(text)
          scanner = opened(text)
          # @type var members: Hash[String, String]
          members = {}
          until closing(scanner)
            name = JSON.parse(value(scanner))
            scanner.skip(/\s*:\s*/)
            members[name] = value(scanner)
          end
          members
        end

        # @param text [String] an array's JSON text
        # @return [Array<String>] its elements' text
        def self.elements(text)
          scanner = opened(text)
          # @type var elements: Array[String]
          elements = []
          elements << value(scanner) until closing(scanner)
          elements
        end

        def self.opened(text)
          scanner = StringScanner.new(text)
          scanner.skip(/\s+/)
          scanner.skip(OPENING)
          scanner
        end

        # Skips what comes before the next item, and the closing bracket if it comes instead: its length, else nil.
        def self.closing(scanner)
          scanner.skip(/\s*,?\s*/)
          scanner.skip(CLOSING)
        end

        # The text of the value the scanner is at, which it then skips. The slice is within the text, never nil, which
        # String#byteslice's signature allows.
        def self.value(scanner) # steep:ignore MethodBodyTypeMismatch
          start = scanner.pos
          skip_nested(scanner) unless scanner.skip(SCALAR)
          # Byte positions, as StringScanner counts them.
          scanner.string.byteslice(start, scanner.pos - start)
        end

        # Skips an object or an array, from its opening bracket, counting the brackets after it outside strings. Each
        # step moves on, or the text ends before the brackets close, or holds a string that does not end.
        def self.skip_nested(scanner)
          scanner.skip(OPENING)
          depth = 1
          until depth.zero?
            start = scanner.pos
            scanner.skip(BETWEEN)
            depth += 1 if scanner.skip(OPENING)
            depth -= 1 if scanner.skip(CLOSING)
            raise JSON::ParserError, "no JSON value can be read at byte #{start}" if scanner.pos == start
          end
        end
        private_class_method :opened, :closing, :value, :skip_nested
      end
      private_constant :RawJson
    end
  end
end
