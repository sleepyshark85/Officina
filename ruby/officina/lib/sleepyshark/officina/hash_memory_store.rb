# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A memory store that keeps its files in a Hash, for tests, demos and short-lived agents: they are gone when the
    # process ends. It refuses what the file store refuses: the same paths, and a path that clashes with a directory
    # or a file of the scope. Many runs may share it.
    class HashMemoryStore
      def initialize
        @files = {} # [scope, path] => frozen text
        @lock = Mutex.new
      end

      # Every file of the scope, in no particular order; none for a scope never written to.
      # @raise [Error] when the scope is invalid
      def list(scope)
        MemoryPath.check(scope)
        @lock.synchronize do
          @files.filter_map { |(at, path), text| MemoryFile.new(path:, size: text.bytesize) if at == scope }
        end
      end

      # The file's text, or nil when there is no such file.
      # @raise [Error] when the scope or the path is invalid
      def read(scope, path)
        MemoryPath.check(scope, path)
        @lock.synchronize { @files[[scope, path]] }
      end

      # Creates the file, or replaces its text.
      # @raise [Error] when the scope or the path is invalid, the text is not valid UTF-8, or the path is a directory
      #   or runs through a file
      def write(scope, path, text)
        MemoryPath.check(scope, path)
        unless text.encoding == Encoding::UTF_8 && text.valid_encoding?
          raise Error, 'Memory text must be a valid UTF-8 string.'
        end

        @lock.synchronize do
          refuse_clash(scope, path)
          @files[[scope.dup.freeze, path.dup.freeze]] = text.dup.freeze
        end
      end

      # Deletes the file; does nothing when there is none.
      # @raise [Error] when the scope or the path is invalid
      def delete(scope, path)
        MemoryPath.check(scope, path)
        @lock.synchronize { @files.delete([scope, path]) }
      end

      # Moves the file to the new path, within the scope.
      # @raise [Error] when the scope or a path is invalid, there is no file at the path, or the new path is taken,
      #   is a directory or runs through a file
      def rename(scope, path, new_path)
        MemoryPath.check(scope, path, new_path)
        @lock.synchronize do
          raise Error, "There is already a memory file #{new_path}." if @files.key?([scope, new_path])

          refuse_clash(scope, new_path)
          text = @files.delete([scope, path]) or raise Error, "There is no memory file #{path}."
          @files[[scope, new_path.dup.freeze]] = text
        end
      end

      private

      # Refuses a path that is a directory of the scope's files, or that runs through one of them.
      def refuse_clash(scope, path)
        paths = @files.each_key.filter_map { |at, other| other if at == scope }
        raise Error, "#{path} is a memory directory." if paths.any? { |other| other.start_with?("#{path}/") }

        file = paths.find { |other| path.start_with?("#{other}/") }
        raise Error, "#{path} runs through the memory file #{file}." if file
      end
    end
  end
end
