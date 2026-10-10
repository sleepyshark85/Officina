# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A scope's memory files as they were when a command of the memory tool started, and the store's operations on
    # them; a run's writes run one at a time. A path here is within the scope: parts joined by "/", empty for the
    # memory directory, whose directories are implied by its files.
    class MemoryFiles
      # The most characters a memory file may hold; a command that would make it longer is refused.
      MAX_FILE = 50_000

      def initialize(store, scope)
        @store = store
        @scope = scope
        @files = store.list(scope)
      end

      def file?(at) = @files.any? { it.path == at }
      def directory?(at) = at.empty? || under(at).any?
      def exists?(at) = file?(at) || directory?(at)

      # The files in the directory.
      # @return [Array<MemoryFile>]
      def under(at) = at.empty? ? @files : @files.select { it.path.start_with?("#{at}/") }

      # The file that one of the path's directories is, if any.
      def file_above(at) = @files.map(&:path).find { |file| at.start_with?("#{file}/") }

      # The file's text, or nil when there is no such file.
      def read(at) = (@store.read(@scope, at) if file?(at))

      # Writes the file, unless its text is longer than a memory file may be.
      # @return [String, nil] why it was refused, for the model; nil once written
      def write(at, text)
        if text.length > MAX_FILE
          return "would hold #{text.length} characters; a memory file holds at most #{MAX_FILE}. Keep it shorter, " \
                 'or split it.'
        end

        @store.write(@scope, at, text)
        nil
      end

      # Deletes the file, or every file in the directory.
      def delete(at) = affected(at).each { @store.delete(@scope, it) }

      # Moves the file, or every file in the directory, to the new path.
      def rename(at, to) = affected(at).each { @store.rename(@scope, it, to + it.delete_prefix(at)) }

      private

      def affected(at) = file?(at) ? [at] : under(at).map(&:path)
    end
    private_constant :MemoryFiles
  end
end
