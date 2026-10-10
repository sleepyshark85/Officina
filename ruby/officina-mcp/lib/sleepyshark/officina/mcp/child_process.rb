# frozen_string_literal: true

require 'open3'

module Sleepyshark
  module Officina
    module Mcp
      # A stdio server's process and its pipes: messages are written to its input one line at a time; a thread reads
      # its output and hands over each line; another reads its error output, whose last line often says why it ended.
      # Stopping it reaps it with what it started and ends both threads.
      class ChildProcess
        # How long a server may take to exit once its input is closed, or once it is killed, before it is killed
        # (again), in seconds.
        EXIT_WAIT = 5
        # How long the reason a server ended waits for its error output to end, in seconds.
        ERRORS_WAIT = 1
        # The longest piece of a line of the error output that is kept, in bytes: of a longer line, its last piece.
        ERRORS_KEPT = 4096
        WINDOWS = Gem.win_platform?
        # On Unix the server leads a process group of its own, which its children join, so stopping the group stops
        # them too, and the terminal's Ctrl+C, sent to the foreground group, does not reach it.
        GROUP = (WINDOWS ? { new_pgroup: true } : { pgroup: true }).freeze

        # Starts server's command. Each line of its output, a message, goes to on_message, without its line end; once
        # the output ends, or a message is longer than Wire::MAX_MESSAGE bytes (and the server is killed), on_end gets
        # why. Both are called on the output's thread. clock times every wait. Raises Mcp::Error when the command
        # cannot start.
        def initialize(server, clock:, on_message:, on_end:)
          @clock = clock
          @input, @output, @errors, @exit = spawn_server(server)
          @errors_reader = read(@errors) { last_line(it) }
          @output_reader = read(@output) { read_output(it, on_message, on_end) }
        end

        # Writes message as one line of the server's input, whole even when several threads write: one string, which
        # IO#write writes whole before another thread's write to the same IO starts. A server that cannot be written
        # to is killed, and IOError or SystemCallError raised once its output has ended, so on_end has said why, or
        # ERRORS_WAIT has passed.
        def write(message)
          @input.write("#{message}\n")
        rescue IOError, SystemCallError
          kill
          ended_within?(@output_reader, ERRORS_WAIT)
          raise
        end

        # Closes the input, so the server can exit by itself, as a container must to be removed. One that has not
        # within EXIT_WAIT seconds is killed. Returns once it has been reaped, the rest of its group killed, and its
        # pipes closed, which ends both threads even when a process outside the group still holds a pipe.
        def stop
          @input.close
          kill until ended_within?(@exit, EXIT_WAIT)
          kill_group
          # The error output first: the output's reader may be waiting for its reader.
          end_reader(@errors, @errors_reader)
          end_reader(@output, @output_reader)
        end

        private

        # Returns this side's ends of the server's input, output and error output, and the thread that reaps it.
        def spawn_server(server)
          # Server.new refuses an empty command.
          program, *arguments = server.command
          # [program, program] runs it without a shell, even with no arguments; Open3's signature leaves that form out.
          Open3.popen3(server.env, [program, program], *arguments, **GROUP) # steep:ignore
        rescue SystemCallError => e
          raise Error, "MCP server #{server.name} could not be started (#{program}): #{e}"
        end

        # A thread reading pipe with the block until it ends, or until stop closes it.
        def read(pipe)
          Thread.new do
            yield pipe
          rescue IOError
            # Closed by stop, which needs nothing more from it.
            nil
          end
        end

        def read_output(output, on_message, on_end)
          # A message and its line end, at most; a longer line is cut there, and is then too long.
          while (line = output.gets(Wire::MAX_MESSAGE + 1))
            message = line.chomp
            # A line cut at the limit has no line end, nor has a last line that is incomplete.
            if message.bytesize > Wire::MAX_MESSAGE then too_long(on_end)
            elsif line.end_with?("\n") then on_message.call(message)
            end
          end
          on_end.call(['it closed its connection', last_error_line].compact.join(': '))
        end

        def too_long(on_end)
          on_end.call("it sent a message longer than #{Wire::MAX_MESSAGE_MB} MB")
          kill
        end

        # The last line of the error output that is not blank, read until it ends; nil if none.
        def last_line(errors)
          # @type var last: String?
          last = nil
          # Lines of at most ERRORS_KEPT bytes; IO#each_line's signature leaves out the form with only a limit.
          errors.each_line(ERRORS_KEPT) do |line| # steep:ignore ArgumentTypeMismatch
            text = line.scrub.strip
            last = text unless text.empty?
          end
          last
        end

        # The last line of the error output that is not blank, once it has ended; nil if none, or if it has not
        # within ERRORS_WAIT seconds, as when a process the server started still holds it.
        def last_error_line = (@errors_reader.value if ended_within?(@errors_reader, ERRORS_WAIT))

        # Whether thread ends within seconds, by the clock.
        def ended_within?(thread, seconds)
          deadline = @clock.call + seconds
          until thread.join(POLL)
            return false if @clock.call >= deadline
          end
          true
        end

        # Closes this side of pipe, which ends its reader even when a process outside the group still holds the
        # other side, and waits for the reader.
        # mutant:disable -- its survivors leave out the close, which stop's open-files test shows, or the join: the
        #   reader ends by itself once its pipe is closed, too soon after for a test to see it still running
        def end_reader(pipe, reader)
          pipe.close
          reader.join
        end

        if WINDOWS
          # Kills the server with what it started: the standard library has no job objects, and taskkill finds what
          # the server started from it.
          def kill = system('taskkill', '/T', '/F', '/PID', @exit.pid.to_s, out: File::NULL, err: File::NULL)

          # A Windows process leaves no group behind.
          def kill_group = nil
        else
          def kill = signal(@exit.pid)

          # What is left of its group, such as a child that ignored the end of its input or holds its output.
          def kill_group = signal(-@exit.pid)
        end

        def signal(pid)
          Process.kill(:KILL, pid)
        rescue Errno::ESRCH, Errno::EPERM
          # Gone already.
          nil
        end
      end
      private_constant :ChildProcess
    end
  end
end
