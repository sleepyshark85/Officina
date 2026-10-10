# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # The stdio transport: the server is a child process, and each message is one line of its input or output.
      # Responses are matched to requests by id, so requests may overlap. When the process ends, every waiting request
      # fails, with the last line of its error output, which often says why.
      #
      # Its lock guards the table of waiting requests and the reason the server was lost. Mutation testing leaves out
      # the methods that take it: a test cannot make two threads meet inside a lock under the VM lock, and what else
      # survives there changes only which entries the table keeps, which shows in memory alone.
      class Stdio
        # A request waiting for its response, which the output's thread hands to its queue.
        Pending = Data.define(:transport, :id, :queue)

        # Reopened rather than given a block, which Steep would not read as the class's body.
        class Pending
          # The response, ACCEPTED for a notification once written, or nil if none came within timeout seconds.
          # Raises Mcp::Error once the server has gone, which closes only the queues of requests still waiting.
          # mutant:disable -- its one survivor swaps Queue#pop and Queue#shift, which are the same method
          def take(timeout)
            answer = queue.pop(timeout:)
            raise Error, transport.lost if queue.closed?

            answer
          end

          # mutant:disable -- see the class: the table's entries
          def release = transport.forget(id)
        end
        private_constant :Pending

        # Starts server's command; see ChildProcess. clock times its waits.
        def initialize(server, clock:)
          @name = server.name
          @mutex = Mutex.new
          @waiting = {}
          @process = ChildProcess.new(server, clock:, on_message: ->(message) { dispatch(message) },
                                              on_end: ->(reason) { lose(reason) })
        end

        # Sends text, a request with that id, or a notification when id is nil, and returns what to wait on.
        # Raises Mcp::Error once the server has gone.
        def post(text, id, _version)
          pending = register(id)
          write(text)
          pending.queue.push(ACCEPTED) unless id
          pending
        end

        # Whether the server can no longer be reached.
        # mutant:disable -- see the class: the lock
        def lost? = @mutex.synchronize { !@lost.nil? }

        # Why the server can no longer be reached, once it cannot.
        # mutant:disable -- see the class: the lock
        def lost = @mutex.synchronize { lost_reason }

        # Stops waiting for the response to request id, and returns its queue; nil if none waits.
        # mutant:disable -- see the class: the lock, and the table's entries
        def forget(id) = @mutex.synchronize { @waiting.delete(id) }

        # Stops the server (see ChildProcess#stop), and returns once every thread started for it has ended.
        def close
          lose('the connection was closed')
          @process.stop
        end

        private

        # What request id waits on, its queue in the table where its response finds it; raises Mcp::Error once the
        # server has gone.
        # mutant:disable -- see the class: the lock, and the table's entries (a notification's under nil, or a
        #   request's forgotten under nil)
        def register(id)
          queue = Queue.new
          @mutex.synchronize do
            raise Error, lost_reason if @lost

            @waiting[id] = queue if id
          end
          Pending.new(self, id, queue)
        end

        # A server that cannot be written to is lost, for the reason its output gave when it ended, if it has by now.
        def write(text)
          @process.write(text)
        rescue IOError, SystemCallError => e
          lose("it could not be written to: #{e}")
          raise Error, lost
        end

        # Hands a response to the request waiting for it; anything else is skipped.
        def dispatch(message)
          case Wire.response(message)
          in [id, response] then forget(id)&.push(response)
          else nil
          end
        end

        # Marks the server gone, once, and wakes every waiting request. A queue leaves the table as it is closed, so a
        # response that arrives meanwhile finds none to push to.
        # mutant:disable -- see the class: the lock; and the clear, which keeps a response that arrives as the
        #   connection is lost from a closed queue, a race no test can time
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
