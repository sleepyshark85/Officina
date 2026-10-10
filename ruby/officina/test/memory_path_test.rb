# frozen_string_literal: true

require 'test_helper'

# The rules for memory scopes and paths, which every store applies.
class MemoryPathTest < Minitest::Test
  cover 'Sleepyshark::Officina::MemoryPath*'

  MemoryPath = Sleepyshark::Officina::MemoryPath

  def test_mem03_a_path_is_at_most_1024_characters
    assert_equal([true, false], ["#{'x/' * 511}xx", "#{'x/' * 512}x"].map { MemoryPath.valid?(it) })
  end

  def test_mem03_a_part_is_at_most_255_utf8_bytes
    parts = ["#{'é' * 127}x", 'é' * 128, 'x' * 255, 'x' * 256]

    assert_equal([true, false, true, false], parts.map { MemoryPath.valid?("a/#{it}") })
  end

  def test_mem03_a_scope_is_at_most_127_utf8_bytes
    scopes = ['x' * 127, 'x' * 128, "#{'é' * 63}x", 'é' * 64]

    assert_equal([true, false, true, false], scopes.map { MemoryPath.valid_scope?(it) })
  end

  def test_mem03_only_utf8_strings_are_scopes_or_paths
    values = [nil, :a, 1, 'a'.encode(Encoding::UTF_16LE), 'a'.b, (+"\xFF").force_encoding(Encoding::UTF_8)]

    assert_empty(values.select { MemoryPath.valid?(it) || MemoryPath.valid_scope?(it) })
  end

  def test_mem03_a_scope_is_one_part_of_a_path
    assert MemoryPath.valid_scope?('staff-17')
    refute MemoryPath.valid_scope?('a/b')
    assert MemoryPath.valid?('a/b')
  end

  def test_mem03_a_string_of_a_subclass_is_a_string
    text = Class.new(String)

    assert_equal [true, true], [MemoryPath.valid?(text.new('a/b')), MemoryPath.valid_scope?(text.new('a'))]
  end

  def test_mem03_device_names_are_refused_whatever_their_case_or_extension
    refused = %w[con PRN.md aux.tar.gz Nul COM0 lpt9 CONOUT$ conin$.x] + ['COM²', 'NUL .txt']
    accepted = %w[CONSOLE COM COM10 LPT nul_ aconin$ x.con]

    assert_equal([refused, []], [refused, accepted].map { |names| names.reject { MemoryPath.valid?(it) } })
  end

  def test_mem03_check_names_what_it_refuses
    path = assert_raises(Sleepyshark::Officina::Error) { MemoryPath.check('alice', 'ok.md', '../x') }
    scope = assert_raises(Sleepyshark::Officina::Error) { MemoryPath.check('..', 'ok.md') }

    assert_equal ['"../x" is not a valid memory path.', '".." is not a valid memory scope.'],
                 [path.message, scope.message]
  end

  def test_test07_a_valid_path_is_relative_and_never_climbs
    Pbt.assert do
      Pbt.property(Pbt.array(Pbt.one_of('..', '.', 'a', '', 'b c', 'C:', '\\', '/', '%'), min: 1, max: 6)) do |parts|
        path = parts.join('/')

        refute MemoryPath.valid?(path) && (path.start_with?('/') || path.split('/').include?('..') ||
                                           path.match?(/[\\:]/)), path
      end
    end
  end
end
