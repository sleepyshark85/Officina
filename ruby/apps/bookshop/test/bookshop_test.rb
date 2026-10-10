# frozen_string_literal: true

require 'open3'
require 'rbconfig'
require 'test_helper'

# The Bookshop Assistant command.
class BookshopTest < Minitest::Test
  def test_the_command_says_the_application_is_not_built_yet_and_fails
    _, error, status = Open3.capture3(RbConfig.ruby, File.expand_path('../exe/bookshop', __dir__))

    refute_predicate status, :success?
    assert_match 'comes with Ruby S06', error
  end
end
