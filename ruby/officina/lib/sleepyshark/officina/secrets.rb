# frozen_string_literal: true

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

      # @param secrets [Array<String>] the empty one is ignored
      def initialize(secrets)
        @forms = secrets.reject(&:empty?).flat_map { |secret| forms(secret) }.uniq.freeze
        freeze
      end

      # The text with every stretch that holds a form of a secret replaced by "[redacted]". Stretches that overlap or
      # touch are replaced as one, so secrets that share characters are redacted whole, whatever their order.
      # @param text [String]
      # @return [String]
      def redact(text)
        # @type var spans: Array[[Integer, Integer]]
        spans = @forms.flat_map { |form| starts(text, form).map { |at| [at, at + form.length] } }
        spans.empty? ? text : redacted(text, spans.sort)
      end

      private

      def forms(secret)
        escaped = ESCAPED.product(%w[%04x %04X]).map { |(pattern, hex)| json(secret, pattern, hex) }
        [secret, *escaped, *escaped.map { |form| form.gsub('/', '\/') }]
      end

      # The secret inside a JSON string, the characters the pattern matches escaped with the hex format.
      def json(secret, pattern, hex)
        secret.each_char.map do |char|
          if pattern&.match?(char)
            char.encode(Encoding::UTF_16BE).unpack('n*').map { |unit| "\\u#{format(hex, unit)}" }.join
          else
            JSON.generate(char)[1...-1].to_s
          end
        end.join
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

      # The text with each span, sorted, replaced; spans that overlap or touch are replaced as one.
      def redacted(text, spans)
        out = +''
        kept = 0
        spans.each_with_index do |(from, to), index|
          out << text[kept...from].to_s << REDACTED if index.zero? || from > kept
          kept = [kept, to].max
        end
        out << text[kept..].to_s
      end
    end
    private_constant :Secrets
  end
end
