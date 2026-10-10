# frozen_string_literal: true

require 'minitest'

# Turns a Ruby warning about a file of the workspace into a test failure. A warning from a gem it uses, which the
# workspace cannot fix (the pinned Anthropic SDK warns as it loads), is printed as usual.
module WorkspaceWarnings
  WORKSPACE = File.expand_path('..', __dir__) + File::SEPARATOR
  # Where CI's Bundler installs the gems, inside the workspace.
  VENDOR = "#{WORKSPACE}vendor#{File::SEPARATOR}".freeze

  # Ruby calls it for every warning; the message starts with the path of the file it is about.
  def warn(message, category: nil)
    raise Minitest::UnexpectedWarning, message if message.start_with?(WORKSPACE) && !message.start_with?(VENDOR)

    super
  end
end
