# frozen_string_literal: true

module Bookshop
  # The console's input and output. Output is written as it comes, so a reply streams, and kept in whole lines around
  # what is written as lines. A blocked read cannot be stopped, so each line is read on a thread of the terminal's
  # own; a read that a reply's cancellation abandons, at an approval prompt, serves the next prompt. Input that is not
  # a terminal, such as a script, is echoed after its prompt, as a terminal would show it.
  class Terminal
    # @param input [IO] read a line at a time
    # @param output [IO]
    def initialize(input:, output:)
      @input = input
      @output = output
      @echo = !input.tty?
      # Holds at most the line being read, and a cancellation for each read a reply cancelled: one it abandoned, or
      # one whose line came first, which the next read skips.
      @lines = Thread::Queue.new
      @reading = nil
      @held = nil
      @at_line_start = true
    end

    # Shows the prompt and reads a line, without its line ending.
    #
    # @param cancel [Sleepyshark::Officina::Cancellation, nil] stops the wait; a line that comes after it serves the
    #   next read
    # @return [String, nil] nil at the end of the input, or once cancelled
    def read(prompt, cancel: nil)
      end_line
      write(prompt)
      line = @held || next_line(cancel)
      @held = (line if cancel&.cancelled?)
      return if @held || line.nil?

      entered(line)
    end

    # Writes the text where the output is.
    def write(text)
      return if text.empty?

      @output.write(text)
      @output.flush
      @at_line_start = text.end_with?("\n")
    end

    # Writes the text as a line of its own.
    def write_line(text)
      end_line
      write("#{text}\n")
    end

    # Ends the line being written, if one is.
    def end_line
      write("\n") unless @at_line_start
    end

    # Ends the input, as its end would, from any thread: a read waiting for a line returns nil, and so does every
    # read after it.
    def end_input = @input.close

    # Stops a read still waiting for a line, by closing the input, and waits for it.
    def close
      reading = @reading
      return unless reading

      @input.close
      reading.join
    end

    private

    # The next line; nil at the end of the input, or once cancelled.
    def next_line(cancel)
      @reading ||= Thread.new { @lines << read_line }
      cancel&.on_cancel { @lines << cancel }
      loop do
        case @lines.pop
        in String | nil => line then return read_ended(line)
        in ^cancel then return
        else next # the cancellation of a read that got its line first
        end
      end
    end

    def read_ended(line)
      @reading&.join
      @reading = nil
      line
    end

    # Ends the prompt's line as the staff member's Enter did, or by echoing the line where no one typed it.
    def entered(line)
      if @echo
        write("#{line}\n")
      else
        @at_line_start = true
      end
      line
    end

    # Runs on the reading thread. Closing the input wakes it.
    def read_line
      @input.gets&.chomp
    rescue IOError
      nil
    end
  end
  private_constant :Terminal
end
