# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    # An agent's secrets, and the redaction of every form of them a text may hold: as written, and as a JSON string
    # may escape it.
    class Secrets
      REDACTED = '[redacted]'
      # Which characters each form escapes as \u and its UTF-16 code units: none, those Go's encoder escapes, those
      # beyond ASCII, and those .NET's default encoder escapes.
      ESCAPED = [nil, /[<>&]/, /[^\x00-\x7F]/, /[^\x20-\x7E]|["&'+<>`]/].freeze
      private_constant :REDACTED, :ESCAPED

      # @param secrets [Array<String>] an empty one redacts nothing
      def initialize(secrets)
        @forms = secrets.flat_map { |secret| forms(secret) }
      end

      # The text with every stretch that holds a form of a secret replaced by "[redacted]". Stretches that overlap or
      # touch are replaced as one, so secrets that share characters are redacted whole, whatever their order.
      # @param text [String]
      # @return [String]
      def redact(text)
        covered = covered(text)
        text.each_char.with_index.chunk { |_, at| covered.fetch(at) }.sum('') do |hidden, stretch|
          hidden ? REDACTED : stretch.map(&:first).join
        end
      end

      private

      def forms(secret)
        escaped = ESCAPED.product(%w[%04x %04X]).map { |pattern, hex| json(secret, pattern, hex) }
        [secret, *escaped, *escaped.map { |form| form.gsub('/', '\/') }]
      end

      # The secret inside a JSON string, the characters the pattern matches escaped with the hex format.
      def json(secret, pattern, hex)
        secret.each_char.sum('') do |char|
          if pattern&.match?(char)
            char.encode(Encoding::UTF_16BE).unpack('n*').sum('') { |unit| "\\u#{format(hex, unit)}" }
          else
            JSON.generate(char).delete_prefix('"').delete_suffix('"')
          end
        end
      end

      # For each character of the text, whether a form of a secret covers it.
      def covered(text)
        covered = Array.new(text.length, false)
        @forms.each { |form| starts(text, form).each { |at| covered.fill(true, at, form.length) } }
        covered
      end

      def starts(text, form)
        # @type var found: Array[Integer]
        found = []
        # @type var at: Integer?
        at = text.index(form)
        while at
          found << at
          at = text.index(form, at + 1)
        end
        found
      end
    end
    private_constant :Secrets
  end
end
