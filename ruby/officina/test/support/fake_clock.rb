# frozen_string_literal: true

# An agent's clock that stands still until a test moves it, from any of the run's threads.
class FakeClock
  START = Time.utc(2026, 10, 10, 9)

  def initialize
    @now = START
    @lock = Mutex.new
  end

  # @return [Time] the time now, as the agent reads it
  def call = @lock.synchronize { @now }

  # Moves the time on by the seconds.
  def advance(seconds)
    @lock.synchronize { @now += seconds }
    nil
  end

  # @return [Float] the seconds from the start to the SDK's time, in nanoseconds since the epoch
  def since_start(nanoseconds) = (nanoseconds - (START.to_r * 1_000_000_000)).fdiv(1_000_000_000)
end
