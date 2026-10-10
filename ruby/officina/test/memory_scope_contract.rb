# frozen_string_literal: true

# The tests that every memory store keeps each scope's files in that scope, run against each built-in store through
# MemoryStoreContract. The test class gives the store (#store), and the files it keeps on disk outside the scope's own
# (#files_outside).
module MemoryScopeContract
  Error = Sleepyshark::Officina::Error
  MemoryRules = Sleepyshark::Officina::MemoryRules

  REFUSED_PATHS = [
    '', '.', '..', '../x', 'a/../../x', './a', 'a/./b', '/etc/passwd', '/a', 'a/', 'a//b', 'a\\b', '..\\x',
    'C:/x', 'c:x', 'a:b', 'CON', 'con.txt', 'nul', 'Aux.md', 'COM1.log', "LPT\u00b9", 'conin$', 'CON .txt', 'a.',
    'a ', 'a/b.', '%2e%2e/x', "a\u0000b", "a\nb", 'a*', 'a?', 'a"b', '<a>', 'a|b', "#{'x' * 256}/a", "#{'x/' * 512}x"
  ].freeze

  REFUSED_SCOPES = ['', '.', '..', 'a/b', 'a\\b', 'CON', 'c:', 'a.', "a\tb"].freeze

  # Segments of generated paths: traversal, separators, absolute forms and reserved names among ordinary parts, which
  # come ten times as often so that about a third of the paths are valid and written.
  HOSTILE = [
    '..', '.', '', '...', '..\\..', 'x/../..', 'CON', 'aux.txt', 'com9', 'a:b', 'C:', '%2e%2e', 'a ', 'b.',
    "\u0000", 'a*b', '\\\\?\\C:'
  ].freeze
  ORDINARY = ['a', 'B', 'notes.md', '.hidden', '~', 'a..b', "\u00e9t\u00e9", "\u202e", 'x y'].freeze
  SEGMENTS = ((ORDINARY * 10) + HOSTILE).freeze
  SEPARATORS = ((['/'] * 7) + ['\\']).freeze
  PREFIXES = (([''] * 10) + ['/', '\\', 'C:/', 'C:', '\\\\server\\share\\', 'file:///']).freeze

  def test_mem03_scopes_never_see_each_others_files
    store.write('alice', 'prefs.md', 'tea')
    store.write('Alice', 'prefs.md', 'coffee')

    assert_equal({ 'alice' => { 'prefs.md' => 'tea' }, 'Alice' => { 'prefs.md' => 'coffee' }, 'bob' => {} },
                 %w[alice Alice bob].to_h { [it, contents(it)] })
    assert_nil store.read('bob', 'prefs.md')
  end

  def test_mem03_a_scopes_files_never_clash_with_anothers_paths
    store.write('bob', 'a', 'a file')
    store.write('carol', 'b/c', 'a file in a directory')
    store.write('alice', 'a/b', 'x')
    store.write('alice', 'b', 'y')

    assert_equal({ 'a/b' => 'x', 'b' => 'y' }, contents('alice'))
  end

  def test_mem03_every_operation_refuses_a_path_outside_the_rules
    store.write('alice', 'a.md', 'a')
    REFUSED_PATHS.each { refuse_every_operation('alice', it) }

    assert_equal({ 'a.md' => 'a' }, contents('alice'))
    assert_empty files_outside('alice')
  end

  def test_mem03_every_operation_refuses_a_scope_outside_the_rules
    REFUSED_SCOPES.each do |scope|
      assert_raises(Error, scope.inspect) { store.list(scope) }
      refuse_every_operation(scope, 'a.md')
    end

    assert_empty files_outside('alice')
  end

  def test_test07_generated_paths_never_leave_their_scope
    Pbt.assert do
      Pbt.property(generated_path) do |parts, extra, at, separator, prefix|
        stays_in_scope(prefix + parts.dup.insert(at % (parts.size + 1), extra).join(separator))
      end
    end
  end

  private

  # A generated path: segments, one arbitrary printable part among them, a separator and a prefix.
  def generated_path
    Pbt.tuple(Pbt.array(Pbt.one_of(*SEGMENTS), min: 1, max: 4), Pbt.printable_ascii_string(min: 1, max: 3),
              Pbt.nat(max: 5), Pbt.one_of(*SEPARATORS), Pbt.one_of(*PREFIXES))
  end

  # Every operation on the path is refused, as the rules refuse it, or stays in the scope.
  def stays_in_scope(path)
    return refuse_every_operation('alice', path) unless MemoryRules.valid_path?(path)

    store.write('alice', path, 'x')
    store.rename('alice', path, 'moved.md')
    store.rename('alice', 'moved.md', path)

    assert_equal [{ path => 'x' }, {}, []], [contents('alice'), contents('bob'), files_outside('alice')]
    store.delete('alice', path)
  end

  # Each operation on the path raises an error whose message matches the reason.
  def refuse_every_operation(scope, path, reason = /is not a valid memory (path|scope)\.\z/)
    [[:read, path], [:write, path, 'x'], [:delete, path], [:rename, path, 'b.md'], [:rename, 'a.md', path]]
      .each do |operation, *arguments|
        error = assert_raises(Error) { store.public_send(operation, scope, *arguments) }

        assert_match reason, error.message, "#{operation} #{path.inspect}"
      end
  end

  # The scope's files and their text, by path.
  def contents(scope)
    store.list(scope).to_h { [it.path, store.read(scope, it.path)] }
  end
end
