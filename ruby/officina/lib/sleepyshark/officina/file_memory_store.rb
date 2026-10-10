# frozen_string_literal: true

require 'fileutils'

module Sleepyshark
  module Officina
    # A memory store that keeps its files on disk: each scope is a directory under the root, named by the hex of the
    # scope's UTF-8 bytes so that scopes differing in case stay apart, holding UTF-8 text files. Besides the path rules,
    # neither the scope's directory nor any part of a path may be a link (symbolic link or junction), so no file outside
    # the scope is reached; a file reached only through a link is not listed. Directories left empty are removed. Many
    # runs may share it.
    #
    # It guards against the model's paths, not against other processes changing the directory. On a case-insensitive
    # file system, paths of one scope that differ only in case name the same file.
    class FileMemoryStore
      # @param root [String] the directory that holds the scopes' directories, created when a file is first written
      def initialize(root)
        @root = root
        @lock = Mutex.new
      end

      # Every file of the scope, in no particular order; none for a scope never written to.
      # @raise [Error] when the scope is invalid, or its directory is a link
      def list(scope)
        MemoryRules.check(scope)
        directory = directory(scope)
        @lock.synchronize do
          # Nothing for a directory that is not there; ** never descends into a linked directory; a linked file is
          # not a file to lstat.
          Dir.glob('**/*', File::FNM_DOTMATCH, base: directory).filter_map do |path|
            file = File.join(directory, path)
            MemoryFile.new(path:, size: File.size(file)) if MemoryRules.valid_path?(path) && File.lstat(file).file?
          end
        end
      end

      # The file's text, or nil when there is no such file.
      # @raise [Error] when the scope or the path is invalid, the path leads through a link, or the file is not UTF-8
      def read(scope, path)
        within(scope, path) { |file| text_of(file, path) if File.file?(file) }
      end

      # Creates the file, or replaces its text.
      # @raise [Error] when the scope or the path is invalid, the path leads through a link, the text is not valid
      #   UTF-8, or the path is a directory or runs through a file
      def write(scope, path, text)
        within(scope, path) do |file, directory|
          MemoryRules.check_text(text)
          refuse_clash(directory, path)
          FileUtils.mkdir_p(File.dirname(file))
          File.binwrite(file, text)
        end
      end

      # Deletes the file; does nothing when there is none.
      # @raise [Error] when the scope or the path is invalid, or the path leads through a link
      def delete(scope, path)
        within(scope, path) do |file, directory|
          if File.file?(file)
            File.delete(file)
            prune(directory, path)
          end
        end
      end

      # Moves the file to the new path, within the scope.
      # @raise [Error] when the scope or a path is invalid, a path leads through a link, there is no file at the path,
      #   or the new path is taken, is a directory or runs through a file
      def rename(scope, path, new_path)
        within(scope, path, new_path) do |file, directory|
          raise Error, "There is no memory file #{path}." unless File.file?(file)

          target = vacant(directory, new_path)
          FileUtils.mkdir_p(File.dirname(target))
          File.rename(file, target)
          prune(directory, path)
        end
      end

      private

      # The file's text, refused when it is not UTF-8, as anything may have put its bytes there.
      def text_of(file, path)
        text = File.binread(file).force_encoding(Encoding::UTF_8)
        raise Error, "The memory file #{path} is not UTF-8 text." unless text.valid_encoding?

        text
      end

      # The directory of a valid scope, which must not be a link.
      def directory(scope)
        directory = File.join(@root, scope.unpack('H*').join)
        return directory unless File.symlink?(directory)

        raise Error, "The memory scope #{scope.inspect} is a link, which memory does not follow."
      end

      # Checks the scope and paths, then yields the first path's file and the scope's directory, holding the lock.
      def within(scope, path, *other_paths)
        MemoryRules.check(scope, path, *other_paths)
        directory = directory(scope)
        @lock.synchronize do
          refuse_links(directory, path)
          yield File.join(directory, path), directory
        end
      end

      def refuse_links(directory, path)
        linked = ancestors(path).push(path).find { |above| File.symlink?(File.join(directory, above)) }
        raise Error, "The memory path #{path} leads through the link #{linked}, which memory does not follow." if linked
      end

      # The new path's file, once it is free: no file or directory there, and no link or file on the way.
      def vacant(directory, path)
        refuse_links(directory, path)
        raise Error, "There is already a memory file #{path}." if File.file?(File.join(directory, path))

        refuse_clash(directory, path)
        File.join(directory, path)
      end

      def refuse_clash(directory, path)
        raise Error, "#{path} is a memory directory." if File.directory?(File.join(directory, path))

        file = ancestors(path).find { |above| File.file?(File.join(directory, above)) }
        raise Error, "#{path} runs through the memory file #{file}." if file
      end

      # The directories above the path, outermost first.
      def ancestors(path)
        parts = path.split('/')
        (1...parts.size).map { |count| parts.take(count).join('/') }
      end

      # Removes the directories above the path that it left empty, up to the scope's.
      def prune(directory, path)
        ancestors(path).reverse_each do |above|
          at = File.join(directory, above)
          break unless Dir.empty?(at)

          Dir.rmdir(at)
        end
      end
    end
  end
end
