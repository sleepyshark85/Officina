# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The scopes and paths a memory store accepts. A scope is one part; a path is parts joined by "/". A part is never
    # empty, "." or "..", never ends with a dot or a space, holds no control character and none of \ / : * ? " < > | %,
    # and is no reserved Windows device name (CON, COM1, CONIN$…), whatever its case or extension. So a path is
    # relative, cannot climb out of its scope, and names the same file in every store on every system. Scopes and paths
    # are UTF-8 strings.
    module MemoryPath
      # The longest path, in characters.
      MAX_LENGTH = 1024

      # The longest part, in characters, which is what file systems allow in one name.
      MAX_PART = 255
      private_constant :MAX_PART

      FORBIDDEN = %r{[\\/:*?"<>|%\p{Cc}]}
      private_constant :FORBIDDEN

      DEVICES = (%w[CON PRN AUX NUL CONIN$ CONOUT$] +
                 %w[COM LPT].product([*'0'..'9', "\u00b9", "\u00b2", "\u00b3"]).map(&:join)).to_set.freeze
      private_constant :DEVICES

      class << self
        # Whether a store accepts the path.
        def valid?(path)
          utf8?(path) && path.length.between?(1, MAX_LENGTH) && path.split('/', -1).all? { |part| part?(part) }
        end

        # Whether a store accepts the scope.
        def valid_scope?(scope)
          utf8?(scope) && part?(scope)
        end

        # Checks a scope and the paths within it, as every store operation does first.
        # @raise [Error] when the scope or a path is not one a store accepts
        def check(scope, *paths)
          raise Error, "#{scope.inspect} is not a valid memory scope." unless valid_scope?(scope)

          invalid = paths.find { |path| !valid?(path) }
          raise Error, "#{invalid.inspect} is not a valid memory path." if invalid
        end

        private

        def utf8?(text)
          text.is_a?(String) && text.encoding == Encoding::UTF_8 && text.valid_encoding?
        end

        def part?(part)
          part.length.between?(1, MAX_PART) && !part.end_with?('.', ' ') && !part.match?(FORBIDDEN) &&
            !DEVICES.include?(part.partition('.').first.rstrip.upcase)
        end
      end
    end
  end
end
