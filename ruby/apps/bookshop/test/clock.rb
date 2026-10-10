# frozen_string_literal: true

# A clock that stands still until the test, or a tool, moves it on, so every time it gives is known.
class Clock
  def initialize(now) = @now = now

  def call = @now

  def advance(seconds) = @now += seconds
end
