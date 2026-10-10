# frozen_string_literal: true

require_relative 'claude_test_case'

# A model's settings: named in .NET's and Go's words, and checked when it is made.
class ModelTest < ClaudeTestCase
  def test_mdl03_settings_name_every_setting_that_shapes_a_request
    {
      { effort: :medium } => 'claude model=claude-opus-5-5 effort=medium max_tokens=64000 cache=5m thinking=adaptive',
      { effort: :high, max_output_tokens: 2000, prefix_cache: '1h', conversation_cache: '1h' } =>
        'claude model=claude-opus-5-5 effort=high max_tokens=2000 cache=1h thinking=adaptive',
      { effort: :max, prefix_cache: '1h' } =>
        'claude model=claude-opus-5-5 effort=max max_tokens=64000 cache=1h/5m thinking=adaptive'
    }.each do |settings, expected|
      assert_equal expected, Claude::Model.new(name: 'claude-opus-5-5', api_key: 'test-key', **settings).settings
    end
  end

  def test_mdl03_an_invalid_setting_is_refused
    {
      { name: ' ' } => 'needs a name',
      { effort: nil } => 'effort is one of',
      { effort: :huge } => 'not :huge',
      { max_output_tokens: 0 } => 'must be positive',
      { prefix_cache: '1d' } => 'cache lifetimes are 5m or 1h',
      { conversation_cache: '1h' } => 'may not be shorter'
    }.each do |setting, message|
      error = assert_raises(Error) do
        Claude::Model.new(name: 'claude-opus-5-5', effort: :low, api_key: 'test-key', **setting)
      end

      assert_includes error.message, message
    end
  end

  def test_agt01_a_model_is_frozen
    assert_predicate Claude::Model.new(name: 'claude-opus-5-5', effort: :low, api_key: 'test-key'), :frozen?
  end
end
