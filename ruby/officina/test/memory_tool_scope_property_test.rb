# frozen_string_literal: true

require 'test_helper'
require 'find'
require 'tmpdir'
require_relative 'support/memory_calls'

# Generated paths, most of them a route out of the memory directory (climbing, absolute, encoded, through a link,
# either separator), with or without /memories in front, through each of the memory tool's commands in each store:
# nothing outside the run's scope is read or changed. Each case starts with a file in scope sam, one in scope other,
# and a secret beside the store; in the file store, sam's directory holds a link to the secret's directory and one to
# other's directory.
class MemoryToolScopePropertyTest < Minitest::Test
  include Sleepyshark::Officina
  include MemoryCalls

  cover 'Sleepyshark::Officina*'

  SECRET = 'TOP-SECRET'
  OTHERS = 'in the other scope'
  # What a result would hold, were a file outside the scope reached.
  OUTSIDE = Regexp.union(SECRET, OTHERS)
  # The file store's directories of scopes sam and other: the hex of their UTF-8 bytes.
  SAM_DIRECTORY = '73616d'
  OTHER_DIRECTORY = '6f74686572'

  # Rename back renames the generated path to sam's file, so a file reached outside would be moved into the scope.
  COMMANDS = ['view', 'create', 'str_replace', 'insert', 'delete', 'rename', 'rename back'].freeze
  PIECES = [
    '..', '.', '', 'a', 'b.md', 'link', 'peer', 'memories', '/memories', 'other', 'outside', 'secret.txt', '%2e%2e',
    '%2f', '..%2f', '%252e%252e', 'C:', 'c:\\', '\\', '/', '..\\..', '\\\\server\\share', '~', '...', '.. ', 'a.',
    'NUL', 'con.txt', "x\u0000", "\u2215", "\uff0e\uff0e", '/etc/passwd'
  ].freeze
  ROUTES = [
    'link', './link', 'a/../link', 'peer', '..', '../..', 'a/../..', '../../outside', '%2e%2e', '..%2f..',
    "\uff0e\uff0e", '', 'C:', '\\\\server\\share', "x\u0000/..", '... ', '..\\..'
  ].freeze
  TARGETS = ['secret.txt', 'outside/secret.txt', 'other/o.md', 'o.md', 'memory/other/o.md', "#{OTHER_DIRECTORY}/o.md",
             "memory/#{OTHER_DIRECTORY}/o.md", 'etc/passwd'].freeze
  SEPARATORS = ['/', '/', '/', '\\'].freeze

  # A path: whether /memories/ starts it; a route to a target four times in five; otherwise one to four pieces, each
  # after a separator or none.
  PATH = Pbt.tuple(Pbt.boolean, Pbt.integer(min: 0, max: 4),
                   Pbt.tuple(Pbt.one_of(*ROUTES), Pbt.one_of(*SEPARATORS), Pbt.one_of(*TARGETS)),
                   Pbt.array(Pbt.tuple(Pbt.one_of(*SEPARATORS, ''), Pbt.one_of(*PIECES)), min: 1, max: 4))

  def test_test07_mem03_generated_paths_through_the_memory_tool_never_leave_the_scope
    Pbt.assert do
      Pbt.property(Pbt.tuple(Pbt.one_of(*COMMANDS), PATH, PATH)) do |command, path, other|
        input = input(command, path(*path), path(*other))
        Dir.mktmpdir do |directory|
          [arranged(HashMemoryStore.new), file_store(directory)].each { stays_in_scope(it, directory, input) }
        end
      end
    end
  end

  private

  def path(prefixed, kind, route, pieces)
    path = kind.positive? ? route.join : pieces.flatten.drop(1).join
    prefixed ? "/memories/#{path}" : path
  end

  # The command's input. Replacing "TOP" with itself would show the secret, were the file reached.
  def input(command, path, other)
    return input('rename', other, '/memories/a/b.md') if command == 'rename back'

    { command:, path:, old_path: path, new_path: other, file_text: 'x', old_str: 'TOP', new_str: 'TOP', insert_line: 0,
      insert_text: 'x' }
  end

  # A file store in the directory, beside the secret, with its links in sam's directory.
  def file_store(directory)
    secret = File.join(directory, 'outside', 'secret.txt')
    FileUtils.mkdir_p(File.dirname(secret))
    File.write(secret, SECRET)
    root = File.join(directory, 'memory')
    arranged(FileMemoryStore.new(root)).tap do
      File.symlink(File.dirname(secret), File.join(root, SAM_DIRECTORY, 'link'))
      File.symlink(File.join('..', OTHER_DIRECTORY), File.join(root, SAM_DIRECTORY, 'peer'))
    end
  end

  # The store, with a file in sam's scope and one in other's.
  def arranged(store)
    store.write('sam', 'a/b.md', 'inside')
    store.write('other', 'o.md', OTHERS)
    store
  end

  def stays_in_scope(store, directory, input)
    before = outside_sam(directory)

    results = run_memory(store, 'sam', input)

    message = "#{input} in #{store.class}"
    results.each { refute_match(OUTSIDE, it.content, message) }

    assert_equal({ 'o.md' => OTHERS }, contents(store, 'other'), message)
    assert_equal before, outside_sam(directory), message
  end

  # Every file under the directory but in sam's, with its text, without following links.
  def outside_sam(directory)
    sam = File.join(directory, 'memory', SAM_DIRECTORY, '')
    Find.find(directory).reject { it.start_with?(sam) }.select { File.file?(it) && !File.symlink?(it) }
        .to_h { [it, File.binread(it)] }
  end
end
