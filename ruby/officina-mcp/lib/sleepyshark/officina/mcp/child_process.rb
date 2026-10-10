# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # A stdio server's process: started with pipes for its input and output, the end of its error output kept to say
      # why it ended, and stopped and reaped with what it started.
      class ChildProcess
        # How long a closing server may take to exit once its input is closed before it is killed, in seconds.
        EXIT_WAIT = 5
        # How much of the end of the error output is kept, in bytes.
        ERRORS_KEPT = 4096
        WINDOWS = Gem.win_platform?
        # On Unix the server leads a process group of its own, which its children join, so stopping the group stops
        # them too, and the terminal's Ctrl+C, sent to the foreground group, does not reach it.
        GROUP = (WINDOWS ? { new_pgroup: true } : { pgroup: true }).freeze

        # The write end of the server's input, and the read end of its output.
        attr_reader :input, :output

        # Starts server's command. Raises Mcp::Error when it cannot start.
        def initialize(server)
          @mutex = Mutex.new
          @errors_tail = String.new(encoding: Encoding::BINARY)
          @pid = spawn(server)
          @input.sync = true
          # Bytes as sent: on Windows a pipe in text mode converts line ends, slowly, and messages are JSON either way.
          [@input, @output, @errors].each(&:binmode)
          @exit = Process.detach(@pid)
          @errors_reader = Thread.new { read_errors }
        end

        # The last line of the error output that is not blank, waiting up to timeout seconds for it to end; nil if
        # none.
        def last_error_line(timeout)
          @errors_reader.join(timeout)
          @mutex.synchronize { @errors_tail.dup }.force_encoding(Encoding::UTF_8).scrub.lines.map(&:strip)
                .reject(&:empty?).last
        end

        # Closes the input, so the server can exit by itself, as a container must to be removed. One that has not
        # within EXIT_WAIT seconds, by clock, is killed with its process group. Returns once it has exited and been
        # reaped, and the error output has been read.
        def stop(clock:)
          @input.close
          kill unless exited_within?(EXIT_WAIT, clock:)
          @exit.join
          # What is left of its group, such as a child that ignored the end of its input or holds its output.
          signal(-@pid) unless WINDOWS
          finish(@errors_reader, @errors)
        end

        # Waits for reader to end, as it does once the server has gone; a process outside the group, or one on
        # Windows, may hold the pipe open, so after EXIT_WAIT seconds the pipe is closed, which ends the read. Then
        # closes the pipe.
        def finish(reader, pipe)
          pipe.close unless reader.join(EXIT_WAIT)
          reader.join
          pipe.close
        end

        # Kills the server and what it started.
        def kill
          if WINDOWS
            # The standard library has no job objects: taskkill finds what the server started from it.
            system('taskkill', '/T', '/F', '/PID', @pid.to_s, out: File::NULL, err: File::NULL)
          else
            signal(-@pid)
          end
          signal(@pid)
        end

        private

        def spawn(server)
          input, @input = IO.pipe
          @output, output = IO.pipe
          @errors, errors = IO.pipe
          child_ends = { in: input, out: output, err: errors }
          begin
            run(server, child_ends)
          ensure
            # The server holds these ends now; were they open here too, the reads would never end.
            child_ends.each_value(&:close)
          end
        end

        def run(server, child_ends)
          program, *arguments = server.command.to_a
          # [program, program] runs it without a shell, even with no arguments; Process.spawn's signature leaves
          # that form out.
          Process.spawn(server.env, [program, program], *arguments, **child_ends, **GROUP) # steep:ignore
        rescue SystemCallError => e
          [@input, @output, @errors].each(&:close)
          raise Error, "MCP server #{server.name} could not be started (#{program}): #{e.message}"
        end

        def read_errors
          loop do
            chunk = @errors.readpartial(ERRORS_KEPT)
            @mutex.synchronize do
              tail = @errors_tail + chunk
              @errors_tail = tail.byteslice([tail.bytesize - ERRORS_KEPT, 0].max..).to_s
            end
          end
        rescue IOError
          # The output ended, or was closed.
          nil
        end

        def exited_within?(seconds, clock:)
          deadline = clock.call + seconds
          until @exit.join(POLL)
            return false if clock.call >= deadline
          end
          true
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
