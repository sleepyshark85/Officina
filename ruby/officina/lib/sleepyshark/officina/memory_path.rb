# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The scopes, paths and text a memory store accepts. A path is parts joined by "/", at most 1,024 characters; a
    # scope is one part. A part is never empty, "." or "..", never ends with a dot or a space, holds no control
    # character and none of \ / : * ? " < > | %, and is no reserved Windows device name (CON, COM1, CONIN$…), whatever
    # its case or extension. A part is at most 255 UTF-8 bytes, and a scope at most 127, as the file store names its
    # directory by the scope's bytes in hex. So a path is relative, cannot climb out of its scope, and every store can
    # hold it on every system. Scopes, paths and text are valid UTF-8 strings.
    module MemoryPath
      MAX_LENGTH = 1024
      MAX_PART_BYTES = 255
      MAX_SCOPE_BYTES = 127
      FORBIDDEN = %r{[\\/:*?"<>|%\p{Cc}]}
      DEVICES = (%w[CON PRN AUX NUL CONIN$ CONOUT$] +
                 %w[COM LPT].product([*'0'..'9', '¹', '²', '³']).map(&:join)).to_set.freeze
      private_constant :MAX_LENGTH, :MAX_PART_BYTES, :MAX_SCOPE_BYTES, :FORBIDDEN, :DEVICES

      class << self
        # Whether a store accepts the path.
        def valid?(path)
          utf8?(path) && path.length.between?(1, MAX_LENGTH) && path.split('/', -1).all? { |part| part?(part) }
        end

        # Whether a store accepts the scope.
        def valid_scope?(scope)
          utf8?(scope) && scope.bytesize <= MAX_SCOPE_BYTES && part?(scope)
        end

        # Checks a scope and the paths within it, as every store operation does first.
        # @raise [Error] when the scope or a path is not one a store accepts
        def check(scope, *paths)
          raise Error, "#{scope.inspect} is not a valid memory scope." unless valid_scope?(scope)

          invalid = paths.find { |path| !valid?(path) }
          raise Error, "#{invalid.inspect} is not a valid memory path." if invalid
        end

        # Checks the text a store is to keep.
        # @raise [Error] when it is not a valid UTF-8 string
        def check_text(text)
          raise Error, 'Memory text must be a valid UTF-8 string.' unless utf8?(text)
        end

        private

        def utf8?(text)
          text.is_a?(String) && text.encoding == Encoding::UTF_8 && text.valid_encoding?
        end

        def part?(part)
          part.bytesize.between?(1, MAX_PART_BYTES) && !part.end_with?('.', ' ') && !part.match?(FORBIDDEN) &&
            !DEVICES.include?(part.partition('.').first.rstrip.upcase)
        end
      end
    end
  end
end
