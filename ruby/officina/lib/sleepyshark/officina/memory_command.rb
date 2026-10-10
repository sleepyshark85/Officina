# frozen_string_literal: true

module Sleepyshark
  module Officina
    # One command of the memory tool, in the words of Claude's memory tool, on paths under the memory directory. Each
    # path comes as the model wrote it, for the messages, and as the path within the scope it names, empty for the
    # memory directory. A command it refuses is a ToolFailure whose message the model reads; what the store raises
    # fails the call as the pipeline says.
    class MemoryCommand
      # The memory directory as the model sees it.
      ROOT = '/memories'

      # @param files [MemoryFiles] the scope's
      # @param input [Hash] the call's input, valid against the memory tool's schema
      # @param path [String] the path the command names; a rename's old one
      # @param within [Hash{String => String}] each path the command names, and the path within the scope it names
      def initialize(files, input, path, within)
        @files = files
        @input = input
        @path = path
        @within = within
        @at = within.fetch(path)
      end

      # @return [String, ToolFailure]
      def run
        case @input.fetch('command')
        in 'view' then view
        in 'create' then create
        in 'str_replace' then str_replace
        in 'insert' then insert
        in 'delete' then delete
        in 'rename' then rename
        end
      end

      private

      def view
        text = @files.read(@at)
        return MemoryView.file(@path, text, @input['view_range']&.map(&:to_int)) if text
        return failure(missing) unless @files.directory?(@at)

        MemoryView.listing(@at.empty? ? ROOT : "#{ROOT}/#{@at}", @files.under(@at), @at)
      end

      def create
        text = @input['file_text']
        return failure('Error: Parameter `file_text` is required for command: create') unless text
        return failure("Error: Cannot create #{@path}: it is a directory.") if @files.directory?(@at)

        above = @files.file_above(@at)
        return failure("Error: Cannot create #{@path}: #{ROOT}/#{above} is a file.") if above

        write(text) || "File created successfully at: #{@path}"
      end

      def str_replace
        old = @input['old_str']
        return failure('Error: Parameter `old_str` is required for command: str_replace') if old.nil? || old.empty?

        text = @files.read(@at)
        return failure("Error: #{missing}") unless text

        replace_once(text, old)
      end

      def replace_once(text, old)
        case starts = MemoryView.occurrences(text, old)
        in [] then failure("No replacement was performed, old_str `#{old}` did not appear verbatim in #{@path}.")
        in [start] then replace(text, start, old)
        else
          failure("No replacement was performed. Multiple occurrences of old_str `#{old}` in lines: " \
                  "#{MemoryView.lines_at(text, starts).uniq.join(', ')}. Please ensure it is unique")
        end
      end

      def replace(text, start, old)
        new_text = @input['new_str'] || ''
        edited = "#{text[0, start]}#{new_text}#{text[(start + old.length)..]}"
        write(edited) || MemoryView.snippet(@path, edited, start, new_text.count("\n"))
      end

      def insert
        line = @input['insert_line']&.to_int
        text = @input['insert_text']
        unless line && text
          return failure('Error: Parameters `insert_line` and `insert_text` are required for command: insert')
        end

        content = @files.read(@at)
        return failure("Error: The path #{@path} does not exist") unless content

        insert_line(MemoryView.lines(content), line, text.delete_suffix("\n"))
      end

      def insert_line(lines, line, text)
        unless line.between?(0, lines.size)
          return failure("Error: Invalid `insert_line` parameter: #{line}. It should be within the range of lines of " \
                         "the file: [0, #{lines.size}]")
        end

        write(lines.insert(line, text).join("\n")) || "The file #{@path} has been edited."
      end

      def delete
        return failure("Error: The memory directory #{ROOT} itself cannot be deleted.") if @at.empty?
        return failure("Error: The path #{@path} does not exist") unless @files.exists?(@at)

        @files.delete(@at)
        "Successfully deleted #{@path}"
      end

      def rename
        new_path = @input.fetch('new_path')
        to = @within.fetch(new_path)
        problem = rename_problem(new_path, to)
        return failure(problem) if problem

        @files.rename(@at, to)
        "Successfully renamed #{@path} to #{new_path}"
      end

      def rename_problem(new_path, to)
        if @at.empty? then "Error: The memory directory #{ROOT} itself cannot be renamed."
        elsif !@files.exists?(@at) then "Error: The path #{@path} does not exist"
        elsif @files.exists?(to) then "Error: The destination #{new_path} already exists"
        elsif to.start_with?("#{@at}/") then "Error: Cannot move #{@path} into itself."
        elsif (file = @files.file_above(to)) then "Error: Cannot move to #{new_path}: #{ROOT}/#{file} is a file."
        end
      end

      # Writes the file at the command's path; the refusal, or nil once written.
      def write(text)
        refused = @files.write(@at, text)
        failure("Error: #{@path} #{refused}") if refused
      end

      def missing = "The path #{@path} does not exist. Please provide a valid path."
      def failure(message) = ToolFailure.new(message:)
    end
    private_constant :MemoryCommand
  end
end
