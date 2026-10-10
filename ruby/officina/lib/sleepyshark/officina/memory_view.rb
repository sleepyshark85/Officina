# frozen_string_literal: true

module Sleepyshark
  module Officina
    # What the memory tool's view shows: a file's lines numbered, or a directory's listing, in the words of Claude's
    # memory tool.
    module MemoryView
      # The most characters a view shows, as the model's tool description says; ranges show the rest.
      MAX_VIEW = 16_000

      # The file's lines, or those of the range, numbered; a long view ends at a line break in its second half, or
      # else at the limit. A file's last line break ends its last line, and starts none.
      # @param range [Array<Integer>, nil] [start, end], from 1, end -1 for the last line
      # @return [String, ToolFailure]
      def self.file(path, text, range)
        lines = lines(text.delete_suffix("\n"))
        unless range.nil? || valid_range?(range, lines.size)
          return ToolFailure.new(message: 'Error: Invalid `view_range`: it should be [start, end] with 1 <= start <= ' \
                                          "end <= #{lines.size}, or end -1 for the end of the file.")
        end

        from, to = range ? [range.fetch(0), range.fetch(1)] : [1, -1]
        "Here's the content of #{path} with line numbers:\n#{cut(numbered(lines, from, to == -1 ? lines.size : to))}"
      end

      # Up to two levels below the directory, without hidden items, each with the size of everything in it.
      # @param shown [String] the directory as the model sees it
      # @param files [Array<MemoryFile>] the files in it
      # @param at [String] the directory within the scope, empty for the memory directory
      def self.listing(shown, files, at)
        entries = sizes(files, at).sort.map { |entry, bytes| "#{size(bytes)}\t#{shown}/#{entry}" }
        "Here're the files and directories up to 2 levels deep in #{shown}, excluding hidden items:\n" \
          "#{["#{size(files.sum(&:size))}\t#{shown}", *entries].join("\n")}"
      end

      # The edited lines of a file, from +line+ and +added+ lines more, with four lines of context on either side.
      def self.snippet(path, text, line, added)
        lines = lines(text)
        "The memory file has been edited. A snippet of #{path} with line numbers:\n" \
          "#{numbered(lines, [1, line - 4].max, [lines.size, line + added + 4].min)}"
      end

      # The text's lines; an empty text is one empty line.
      def self.lines(text) = text.empty? ? [''] : text.split("\n", -1)

      # The line, from 1, of the character at the index.
      def self.line_of(text, index) = text.each_char.take(index).count("\n") + 1

      # Where each occurrence of +old+ in the text starts, each after the one before it ends.
      def self.occurrences(text, old)
        # @type var starts: Array[Integer]
        starts = []
        from = 0
        while (start = text.index(old, from))
          starts << start
          from = start + old.length
        end
        starts
      end

      # Lines +from+ to +to+, numbered from 1, six wide.
      def self.numbered(lines, from, to)
        (from..to).map { format("%<line>6d\t%<text>s", line: it, text: lines[it - 1]) }.join("\n")
      end

      # The size of each entry of the listing: its path within the directory, one or two levels deep.
      def self.sizes(files, at)
        files.each_with_object(Hash.new(0)) do |file, sizes|
          parts = file.path.delete_prefix(at.empty? ? '' : "#{at}/").split('/').take(2)
          parts.take_while { !it.start_with?('.') }.each_index { sizes[parts.take(it + 1).join('/')] += file.size }
        end
      end

      def self.valid_range?(range, size)
        return false unless range.size == 2

        from = range.fetch(0)
        to = range.fetch(1)
        from.between?(1, size) && (to == -1 || to.between?(from, size))
      end

      def self.cut(view)
        return view if view.length <= MAX_VIEW

        last_break = view.rindex("\n", MAX_VIEW)
        ends = last_break && last_break >= MAX_VIEW / 2 ? last_break : MAX_VIEW
        "#{view[0, ends]}\n[Truncated at #{MAX_VIEW} characters: view the rest with view_range.]"
      end

      # A size in bytes as a listing shows it.
      def self.size(bytes)
        if bytes < 1024 then "#{bytes}B"
        elsif bytes < 1024 * 1024 then format('%.1fK', bytes / 1024.0)
        else format('%.1fM', bytes / (1024.0 * 1024))
        end
      end
      private_class_method :sizes, :valid_range?, :cut, :size
    end
    private_constant :MemoryView
  end
end
