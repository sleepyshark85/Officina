# frozen_string_literal: true

require 'open3'

module Sleepyshark
  module Officina
    module Mcp
      # A stdio server's process and its pipes: messages are written to its input one line at a time; a thread reads
      # its output and hands over each line; another keeps the end of its error output, which often says why it
      # ended. Stopping it reaps it with what it started and ends both threads.
      class ChildProcess
        # How long a closing server may take to exit once its input is closed before it is killed, and how long its
        # pipes may stay open once it has gone, in seconds.
        EXIT_WAIT = 5
        # How long a failed write waits for the output to end, so the reason it ended is known, in seconds.
        ERRORS_WAIT = 1
        # How much of the end of the error output is kept, in bytes.
        ERRORS_KEPT = 4096
        WINDOWS = Gem.win_platform?
        # On Unix the server leads a process group of its own, which its children join, so stopping the group stops
        # them too, and the terminal's Ctrl+C, sent to the foreground group, does not reach it.
        GROUP = (WINDOWS ? { new_pgroup: true } : { pgroup: true }).freeze

        # A thread reading one of the server's pipes until it ends.
        class Reader
          # Reads pipe with the block, on a thread of its own.
          def initialize(pipe, &)
            @pipe = pipe
            @thread = Thread.new(pipe, &)
          end

          # Whether the read ends within timeout seconds.
          def ended_within?(timeout) = !@thread.join(timeout).nil?

          # Waits for the read to end, as it does once the server has gone; a process outside the group, or one on
          # Windows, may hold the pipe open, so after EXIT_WAIT seconds the pipe is closed, which ends the read.
          def stop
            @pipe.close unless ended_within?(EXIT_WAIT)
            @thread.join
            @pipe.close
          end
        end

        # The end of the server's error output, which often says why it ended.
        class ErrorTail
          def initialize
            @mutex = Mutex.new
            @bytes = String.new(encoding: Encoding::BINARY)
          end

          # Reads pipe until it ends, keeping its last ERRORS_KEPT bytes.
          def read(pipe)
            loop do
              chunk = pipe.readpartial(ERRORS_KEPT)
              @mutex.synchronize do
                bytes = @bytes + chunk
                @bytes = bytes.byteslice([bytes.bytesize - ERRORS_KEPT, 0].max..).to_s
              end
            end
          rescue IOError
            # The error output ended, or was closed.
            nil
          end

          # Its last line that is not blank; nil if none.
          def last_line
            @mutex.synchronize { @bytes.dup }.force_encoding(Encoding::UTF_8).scrub.lines.map(&:strip)
                  .reject(&:empty?).last
          end
        end
        private_constant :Reader, :ErrorTail

        # Starts server's command. Each line of its output, a message, goes to on_message, without its line end; once
        # the output ends, or a message is longer than Wire::MAX_MESSAGE bytes (and the server is killed), on_end gets
        # why. Both are called on the output's thread. Raises Mcp::Error when the command cannot start.
        def initialize(server, on_message:, on_end:)
          @writing = Mutex.new
          @error_tail = ErrorTail.new
          @input, output, errors, @exit = spawn_server(server)
          @errors_reader = Reader.new(errors) { @error_tail.read(it) }
          @output_reader = Reader.new(output) { read_output(it, on_message, on_end) }
        end

        # Writes message as one line of the server's input, whole even when several threads write. A server that
        # cannot be written to is killed, and IOError or SystemCallError raised once its output has ended, so on_end
        # has said why, or ERRORS_WAIT has passed.
        def write(message)
          # Not the lock of the caller's own state: a write that waits for the server to read must not keep the
          # output's thread from handing over messages meanwhile.
          @writing.synchronize { @input.write(message, "\n") }
        rescue IOError, SystemCallError
          kill
          @output_reader.ended_within?(ERRORS_WAIT)
          raise
        end

        # Closes the input, so the server can exit by itself, as a container must to be removed. One that has not
        # within EXIT_WAIT seconds, by clock, is killed. Returns once it has exited and been reaped, the rest of its
        # group killed, and both threads have ended.
        def stop(clock:)
          @input.close
          kill unless exited_within?(EXIT_WAIT, clock:)
          @exit.join
          # What is left of its group, such as a child that ignored the end of its input or holds its output.
          signal(-@exit.pid) unless WINDOWS
          @output_reader.stop
          @errors_reader.stop
        end

        private

        # Returns this side's ends of the server's input, output and error output, and the thread that reaps it.
        def spawn_server(server)
          program, *arguments = server.command.to_a
          # [program, program] runs it without a shell, even with no arguments; Open3's signature leaves that form
          # out.
          Open3.popen3(server.env, [program, program], *arguments, **GROUP) # steep:ignore
        rescue SystemCallError => e
          raise Error, "MCP server #{server.name} could not be started (#{program}): #{e.message}"
        end

        def read_output(output, on_message, on_end)
          # A message and its line end, at most; a longer line is cut there, and is then too long.
          while (line = output.gets(Wire::MAX_MESSAGE + 1))
            message = line.chomp
            return too_long(on_end) if message.bytesize > Wire::MAX_MESSAGE

            # A last line without its line end is incomplete.
            on_message.call(message) if line.end_with?("\n")
          end
          on_end.call(['it closed its connection', last_error_line].compact.join(': '))
        rescue IOError
          on_end.call('the connection was closed')
        end

        def too_long(on_end)
          on_end.call("it sent a message longer than #{Wire::MAX_MESSAGE_MB} MB")
          kill
        end

        # The last line of the error output that is not blank, once it has ended or ERRORS_WAIT has passed; nil if
        # none.
        def last_error_line
          @errors_reader.ended_within?(ERRORS_WAIT)
          @error_tail.last_line
        end

        def exited_within?(seconds, clock:)
          deadline = clock.call + seconds
          until @exit.join(POLL)
            return false if clock.call >= deadline
          end
          true
        end

        # Kills the server; on Windows, where it has no group to kill once it has gone, with what it started.
        def kill
          return signal(@exit.pid) unless WINDOWS

          # The standard library has no job objects: taskkill finds what the server started from it.
          system('taskkill', '/T', '/F', '/PID', @exit.pid.to_s, out: File::NULL, err: File::NULL)
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
