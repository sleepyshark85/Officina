# frozen_string_literal: true

# Symbolic links in the memory tests: a test that needs one is skipped where the system refuses to make it, as Windows
# does without the privilege.
module SymbolicLinks
  # Makes a link at +at+ to +target+, or skips the test.
  def link(target, at)
    File.symlink(target, at)
  rescue NotImplementedError, Errno::EPERM, Errno::EACCES => e
    skip "This system does not let the tests make a symbolic link (#{e.class})."
  end
end
