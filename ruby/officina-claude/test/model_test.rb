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
      { name: ' ' } => 'A Claude model needs a name',
      { effort: nil } => "Claude's effort is one of low, medium, high, xhigh, max, not nil",
      { effort: :huge } => "Claude's effort is one of low, medium, high, xhigh, max, not :huge",
      { max_output_tokens: 0 } => "Claude's max output tokens must be positive, not 0",
      { prefix_cache: '1d' } => %(Claude's cache lifetimes are 5m or 1h, not "1d" and "5m"),
      { conversation_cache: '1d' } => %(Claude's cache lifetimes are 5m or 1h, not "5m" and "1d"),
      { conversation_cache: '1h' } => "Claude's prefix cache may not be shorter than its conversation cache"
    }.each do |setting, message|
      error = assert_raises(Error) do
        Claude::Model.new(name: 'claude-opus-5-5', effort: :low, api_key: 'test-key', **setting)
      end

      assert_equal message, error.message
    end
  end

  def test_evt02_info_names_the_provider_and_model_and_prices_opus
    opus = Claude::Model.new(name: 'claude-opus-5-5', effort: :low, api_key: 'test-key').info
    other = Claude::Model.new(name: 'claude-other', effort: :low, api_key: 'test-key').info

    assert_equal ['anthropic', 'claude-opus-5-5', [4, 20, BigDecimal('0.2'), 5]],
                 [opus.provider, opus.name, opus.price.to_h.values]
    assert_equal ['anthropic', 'claude-other', nil], [other.provider, other.name, other.price]
  end

  def test_agt01_a_model_and_its_settings_are_frozen
    claude = Claude::Model.new(name: 'claude-opus-5-5', effort: :low, api_key: 'test-key')

    assert_predicate claude, :frozen?
    assert_predicate claude.settings, :frozen?
  end
end
