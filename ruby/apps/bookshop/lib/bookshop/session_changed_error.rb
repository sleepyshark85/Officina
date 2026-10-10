# frozen_string_literal: true

module Bookshop
  # A session was not saved, as the stored one changed since this console last saved or loaded it: another console
  # went on with it, or its id is another session's. Saving it would lose what that one added.
  class SessionChangedError < StandardError
  end
  private_constant :SessionChangedError
end
