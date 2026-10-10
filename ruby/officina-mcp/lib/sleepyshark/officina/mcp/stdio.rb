# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # The stdio transport: the server is a child process, and each message is one line of its input or output.
      # Responses are matched to requests by id, so requests may overlap. When the process ends, every waiting request
      # fails, with the last line of its error output, which often says why.
      class Stdio
        # A request waiting for its response, which the output's thread hands to its queue.
        Pending = Data.define(:transport, :id, :queue)

        # Reopened rather than given a block, which Steep would not read as the class's body.
        class Pending
          # The response, ACCEPTED for a notification once written, or nil if none came within timeout seconds.
          # Raises Mcp::Error once the server has gone.
          def take(timeout)
            answer = queue.pop(timeout:)
            raise Error, transport.lost.to_s if answer.nil? && queue.closed?

            answer
          end

          def release = transport.forget(id)
        end
        private_constant :Pending

        # Starts server's command; see ChildProcess. clock times the wait for it to exit.
        def initialize(server, clock:)
          @name = server.name
          @clock = clock
          @mutex = Mutex.new
          @waiting = {}
          @lost = nil
          @process = ChildProcess.new(server, on_message: ->(message) { dispatch(message) },
                                              on_end: ->(reason) { lose(reason) })
        end

        # Sends text, a request with that id, or a notification when id is nil, and returns what to wait on.
        # Raises Mcp::Error once the server has gone.
        def post(text, id, _version)
          queue = Thread::Queue.new
          @mutex.synchronize do
            raise Error, lost_reason if @lost

            @waiting[id] = queue if id
          end
          write(text)
          queue.push(ACCEPTED) unless id
          Pending.new(self, id, queue)
        end

        # Why the server can no longer be reached; nil while it can.
        def lost = @mutex.synchronize { lost_reason if @lost }

        # Stops waiting for the response to request id.
        def forget(id)
          @mutex.synchronize { @waiting.delete(id) }
        end

        # Stops the server (see ChildProcess#stop), and returns once every thread started for it has ended.
        def close
          lose('the connection was closed')
          @process.stop(clock: @clock)
        end

        private

        # A server that cannot be written to is lost, for the reason its output gave when it ended, if it has by now.
        def write(text)
          @process.write(text)
        rescue IOError, SystemCallError => e
          lose("it could not be written to: #{e.message}")
          raise Error, lost.to_s
        end

        # Hands a response to the request waiting for it; anything else is skipped.
        def dispatch(message)
          response = Wire.response(message) or return
          @mutex.synchronize { @waiting.delete(response['id']) }&.push(response)
        end

        # Marks the server gone, once, and wakes every waiting request.
        def lose(reason)
          @mutex.synchronize do
            @lost ||= reason
            @waiting.each_value(&:close)
            @waiting.clear
          end
        end

        def lost_reason = "MCP server #{@name} could not be reached: #{@lost}"
      end
      private_constant :Stdio
    end
  end
end
