# frozen_string_literal: true

require 'test_helper'
require 'tmpdir'
require_relative 'memory_store_contract'

# The file store, against the tests every memory store passes, and the links it never follows.
class FileMemoryStoreTest < Minitest::Test
  include MemoryStoreContract

  cover 'Sleepyshark::Officina::FileMemoryStore*'
  cover 'Sleepyshark::Officina::MemoryPath*'

  # A directory of its own for each test: the store's root, and beside it a file the store must never reach.
  def setup
    @temp = Dir.mktmpdir('officina-memory')
    @root = File.join(@temp, 'memory')
    @outside = File.join(@temp, 'outside')
    Dir.mkdir(@outside)
    File.write(File.join(@outside, 'secret.md'), 'secret')
    @store = Sleepyshark::Officina::FileMemoryStore.new(@root)
  end

  def teardown
    FileUtils.remove_entry(@temp)
  end

  def test_mem02_each_scope_is_a_directory_named_by_the_hex_of_its_utf8_bytes
    store.write('é', 'a/b.md', "line\n")

    assert_equal "line\n".b, File.binread(File.join(@root, 'c3a9', 'a', 'b.md'))
  end

  def test_mem02_directories_left_empty_are_removed_up_to_the_scopes
    store.write('alice', 'a/b/c.md', 'x')
    store.rename('alice', 'a/b/c.md', 'd.md')
    store.write('alice', 'e/f.md', 'x')
    store.delete('alice', 'e/f.md')

    assert_equal ['d.md'], Dir.children(scope_directory('alice'))
  end

  def test_mem02_a_file_that_is_not_utf8_text_is_refused_on_read
    store.write('alice', 'a.md', 'x')
    File.binwrite(File.join(scope_directory('alice'), 'a.md'), "\xFF".b)

    assert_raises(Error) { store.read('alice', 'a.md') }
  end

  def test_mem02_files_put_there_under_names_no_path_may_have_are_not_listed
    store.write('alice', 'a.md', 'x')
    File.write(File.join(scope_directory('alice'), 'b%'), 'x')
    File.write(File.join(scope_directory('alice'), 'c.'), 'x') unless Gem.win_platform?

    assert_equal({ 'a.md' => 'x' }, contents('alice'))
  end

  def test_mem03_a_linked_directory_in_the_scope_is_never_followed
    store.write('alice', 'a.md', 'x')
    link(@outside, File.join(scope_directory('alice'), 'out'))
    %w[out/secret.md out/new.md].each { refuse_every_operation('alice', it, /link/) }

    assert_equal [{ 'a.md' => 'x' }, { 'secret.md' => 'secret' }], [contents('alice'), outside]
  end

  def test_mem03_a_linked_file_in_the_scope_is_never_followed
    store.write('alice', 'a.md', 'x')
    link(File.join(@outside, 'secret.md'), File.join(scope_directory('alice'), 'secret.md'))
    refuse_every_operation('alice', 'secret.md', /link/)

    assert_equal [{ 'a.md' => 'x' }, { 'secret.md' => 'secret' }], [contents('alice'), outside]
  end

  def test_mem03_a_scope_whose_directory_is_a_link_is_refused
    Dir.mkdir(@root)
    link(@outside, scope_directory('alice'))
    refuse_every_operation('alice', 'secret.md', /link/)

    assert_raises(Error) { store.list('alice') }
    assert_equal({ 'secret.md' => 'secret' }, outside)
  end

  private

  attr_reader :store

  def scope_directory(scope) = File.join(@root, scope.unpack1('H*'))

  # The files beside the root, and their text.
  def outside
    Dir.children(@outside).to_h { [it, File.read(File.join(@outside, it))] }
  end

  # Every file under the test's directory that is neither the scope's nor the one put beside the root.
  def files_outside(scope)
    inside = "#{scope_directory(scope)}/"
    Dir.glob('**/*', File::FNM_DOTMATCH, base: @temp).map { File.join(@temp, it) }
       .select { File.file?(it) && !it.start_with?(inside) } - [File.join(@outside, 'secret.md')]
  end

  def link(target, at)
    File.symlink(target, at)
  rescue NotImplementedError, Errno::EPERM, Errno::EACCES => e
    skip "This system does not let the tests make a symbolic link (#{e.class})."
  end
end
