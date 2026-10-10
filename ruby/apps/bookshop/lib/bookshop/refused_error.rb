# frozen_string_literal: true

module Bookshop
  # The shop refused a request, changing nothing: an unknown id, too few copies in stock, a duplicate email. Its
  # message is written for the model, which can recover from it.
  class RefusedError < StandardError
  end
end
