# frozen_string_literal: true

require 'json'
require 'strscan'

module Sleepyshark
  module Officina
    module Mcp
      # The members of a JSON object, or the elements of a JSON array, each as the text it was written as, which
      # JSON.parse does not keep. It reads only text that JSON.parse has accepted, so it does not check it again.
      module RawJson
        # A string, a number, true, false or null.
        SCALAR = /"(?:[^"\\]++|\\.)*+"|[-+.\w]++/
        # What lies between the brackets of an object or array: strings, whole, and anything but brackets and quotes.
        BETWEEN = /(?:"(?:[^"\\]++|\\.)*+"|[^"\[\]{}]++)*+/
        private_constant :SCALAR, :BETWEEN

        # @param text [String] an object's JSON text
        # @return [Hash{String => String}] its members' names to their values' text
        def self.members(text)
          scanner = opened(text)
          # @type var members: Hash[String, String]
          members = {}
          until closed?(scanner)
            name = JSON.parse(scanner.scan(SCALAR).to_s)
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
          elements << value(scanner) until closed?(scanner)
          elements
        end

        def self.opened(text)
          scanner = StringScanner.new(text)
          scanner.skip(/\s*[\[{]/)
          scanner
        end

        # Skips what comes before the next item, and says whether the closing bracket came instead.
        def self.closed?(scanner)
          scanner.skip(/\s*,?\s*/)
          scanner.skip(/[\]}]/) ? true : false
        end

        # The text of the value the scanner is at, which it then skips.
        def self.value(scanner)
          start = scanner.pos
          skip_nested(scanner) unless scanner.skip(SCALAR)
          # Byte positions, as StringScanner counts them.
          scanner.string.byteslice(start, scanner.pos - start).to_s
        end

        # Skips an object or an array, counting brackets outside strings.
        def self.skip_nested(scanner)
          depth = 0
          loop do
            scanner.skip(BETWEEN)
            depth += scanner.getch.to_s.match?(/[\[{]/) ? 1 : -1
            break if depth.zero?
          end
        end
        private_class_method :opened, :closed?, :value, :skip_nested
      end
      private_constant :RawJson
    end
  end
end
