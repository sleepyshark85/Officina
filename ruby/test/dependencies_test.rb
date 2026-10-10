# frozen_string_literal: true

require 'prism'
require 'rbconfig'
require 'test_helper'

# The dependency rule, read without running the code: each gem's runtime dependencies from its gemspec, and with
# Prism each require in its lib/.
class DependenciesTest < Minitest::Test
  FIXTURES = File.join(__dir__, 'fixtures', 'dependencies')
  CLAUDE = 'sleepyshark-officina-claude'
  # What each library gem may use beside the standard library. The application may use anything but the SDK.
  ALLOWED = {
    'sleepyshark-officina' => %w[bigdecimal json opentelemetry-api opentelemetry-metrics-api].freeze,
    CLAUDE => %w[anthropic sleepyshark-officina].freeze,
    'sleepyshark-officina-mcp' => %w[sleepyshark-officina].freeze,
    'sleepyshark-officina-testing' => %w[sleepyshark-officina].freeze
  }.freeze
  STANDARD_LIBRARY = RbConfig::CONFIG.values_at('rubylibdir', 'rubyarchdir').freeze

  def test_test05_the_workspace_keeps_the_rule
    assert_empty violations(File.expand_path('..', __dir__))
  end

  def test_test05_a_fixture_keeping_the_rule_passes
    assert_empty violations(File.join(FIXTURES, 'allowed'))
  end

  def test_test05_the_anthropic_sdk_used_anywhere_but_the_claude_gem_fails
    uses = [
      'apps/bookshop/lib/bookshop.rb:3 requires anthropic',
      'officina-mcp/lib/sleepyshark/officina/mcp.rb:4 requires anthropic',
      'officina-testing/sleepyshark-officina-testing.gemspec depends on anthropic',
      'officina/lib/sleepyshark/officina.rb:3 requires anthropic',
      'officina/sleepyshark-officina.gemspec depends on anthropic'
    ]

    assert_equal(uses.map { "#{it}: only #{CLAUDE} may use anthropic" },
                 violations(File.join(FIXTURES, 'sdk_outside_claude')))
  end

  def test_test05_the_core_using_more_than_its_rule_allows_fails
    uses = [
      'lib/sleepyshark/officina.rb:3 requires opentelemetry/sdk',
      'lib/sleepyshark/officina.rb:4 requires minitest',
      'lib/sleepyshark/officina.rb:5 requires sleepyshark/officina/mcp',
      'lib/sleepyshark/officina.rb:6 requires ../../../officina-mcp/lib/sleepyshark/officina/mcp outside its lib/',
      'lib/sleepyshark/officina.rb:7 has a require it cannot read',
      'sleepyshark-officina.gemspec depends on opentelemetry-sdk'
    ]
    beyond = "sleepyshark-officina may use only the standard library and #{ALLOWED['sleepyshark-officina'].join(', ')}"

    assert_equal(uses.map { "officina/#{it}: #{beyond}" }, violations(File.join(FIXTURES, 'core_outside_rule')))
  end

  private

  # Each use of something by a gem in the workspace or fixture at root that the rule does not allow, sorted.
  def violations(root)
    Dir.glob('{*,apps/*}/*.gemspec', base: root).flat_map do |gemspec|
      spec = Gem::Specification.load(File.join(root, gemspec))
      uses(root, gemspec, spec).filter_map { |use, used| broken(spec.name, use, used) }
    end.sort
  end

  # Each [what the gem does, what it uses]: its runtime dependencies, then its requires. What it uses is a gem's name,
  # nil for the standard library and the gem's own files, or else the use itself, which no rule allows.
  def uses(root, gemspec, spec)
    lib = File.join(File.dirname(gemspec), 'lib')
    spec.runtime_dependencies.map { |dependency| ["#{gemspec} depends on #{dependency.name}", dependency.name] } +
      Dir.glob('**/*.rb', base: File.join(root, lib)).flat_map { |file| requires(root, lib, File.join(lib, file)) }
  end

  def requires(root, lib, file)
    calls(Prism.parse_file(File.join(root, file)).value).map { |call| use_of(call, lib, file) }
  end

  # [what the call does, what it uses] for a require or require_relative call in a file of the lib.
  def use_of(call, lib, file)
    use = "#{file}:#{call.location.start_line}"
    argument = call.arguments&.arguments&.first
    return [use += ' has a require it cannot read', use] unless argument.is_a?(Prism::StringNode)

    feature = argument.unescaped
    return ["#{use} requires #{feature}", gem_of(feature)] if call.name == :require
    return ["#{use} requires #{feature}", nil] if inside?(lib, File.join(File.dirname(file), feature))

    [use += " requires #{feature} outside its lib/", use]
  end

  # Each require or require_relative call under the node.
  def calls(node)
    found = node.compact_child_nodes.flat_map { |child| calls(child) }
    required = node.is_a?(Prism::CallNode) && node.receiver.nil? && %i[require require_relative].include?(node.name)
    required ? [node, *found] : found
  end

  def inside?(directory, path)
    File.expand_path(path).start_with?(File.expand_path(directory) + File::SEPARATOR)
  end

  # The name of the gem the feature is in, nil for the standard library, or the feature when no gem in the bundle has
  # it.
  def gem_of(feature)
    return if standard?(feature)

    path = $LOAD_PATH.resolve_feature_path(feature)&.last
    spec = path && Gem.loaded_specs.each_value.find { inside?(it.full_gem_path, path) }
    spec ? spec.name : feature
  end

  def standard?(feature)
    STANDARD_LIBRARY.product(['rb', RbConfig::CONFIG['DLEXT']]).any? do |directory, extension|
      File.exist?(File.join(directory, "#{feature}.#{extension}"))
    end
  end

  def broken(name, use, used)
    return if used.nil?
    return "#{use}: only #{CLAUDE} may use anthropic" if used == 'anthropic' && name != CLAUDE

    allowed = ALLOWED[name]
    "#{use}: #{name} may use only the standard library and #{allowed.join(', ')}" if allowed && !allowed.include?(used)
  end
end
