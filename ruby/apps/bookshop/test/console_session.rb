# frozen_string_literal: true

require_relative 'database_server'
require_relative 'memory_telemetry'

# The end-to-end tests' harness: Bookshop.build's console, agent, core and tools against the real database. Only the
# model (scripted replies) and the staff member (scripted lines on a pipe, which answer the prompts, and Ctrl+C as a
# real SIGINT) are faked.
module ConsoleSession
  include DatabaseServer

  Officina = Sleepyshark::Officina
  ScriptedModel = Officina::Testing::ScriptedModel

  # The staff member at the console, and its output. Each time the console shows a prompt, the next line of the
  # script answers it through the input pipe; a Proc in the script runs first, and nil leaves the prompt unanswered.
  # Once the output first holds the interrupt text, the staff member presses Ctrl+C. The script's end closes the
  # input.
  class Staff
    PROMPT = %r{(?:Your name: |you> |Approve\? \[y/N\] )\z}

    attr_reader :input

    def initialize(script, interrupt_on:)
      @script = script.dup
      @interrupt_on = interrupt_on
      @transcript = +''
      @input, @keyboard = IO.pipe
    end

    def write(text)
      @transcript << text
      press_ctrl_c if @interrupt_on && @transcript.include?(@interrupt_on)
      answer if @transcript.match?(PROMPT)
      text.bytesize
    end

    def flush = self

    def transcript = @transcript.dup

    def close
      @keyboard.close unless @keyboard.closed?
      @input.close unless @input.closed?
    end

    private

    def press_ctrl_c
      @interrupt_on = nil
      Process.kill('INT', Process.pid)
    end

    def answer
      @script.shift.call while @script.first.is_a?(Proc)
      return @keyboard.close if @script.empty?

      line = @script.shift
      @keyboard.puts(line) if line
    end
  end

  # A model that streams the start of a reply and then waits, as a slow one does, until the run is cancelled; later
  # requests get the scripted model's replies.
  class StallingModel
    def initialize(text, later)
      @text = text
      @later = later
      @stalled = false
    end

    def settings = @later.settings
    def info = @later.info

    def stream(request, cancel:, &)
      return @later.stream(request, cancel:, &) if @stalled

      @stalled = true
      yield Officina::TextDelta.new(text: @text)
      woken = Thread::Queue.new
      cancel.on_cancel { woken << :cancelled }
      woken.pop
      nil
    end
  end

  private

  # Runs a console session on the test's database, the staff member following the script, and returns its transcript.
  # Its telemetry is kept in memory, cleared when the session ends.
  def session(model, *script, interrupt_on: nil, clock: -> { Time.now }, telemetry: MemoryTelemetry.new)
    staff = Staff.new(script, interrupt_on:)
    application = Bookshop.build(input: staff.input, output: staff, model:,
                                 env: { 'BOOKSHOP_DATABASE' => database_url }, clock:,
                                 telemetry: telemetry.telemetry)
    begin
      application.run
    ensure
      application.close
      staff.close
    end
    staff.transcript
  end

  # The steps of a reply that streams the text, then calls the tools of the blocks (made by #call).
  def say_then_call(text, *calls)
    [Officina::TextDelta.new(text:),
     Officina::Reply.new(blocks: [ScriptedModel.text_block(text), *calls], stop: :tool_use)]
  end

  def call(id, name, input) = ScriptedModel.tool_use_block(id, name, input)

  # The tool results the model received in request +index+.
  def results(model, index) = model.requests[index].messages.last.blocks.filter_map(&:tool_result)

  def book_title(id)
    connection = PG.connect(database_url)
    connection.exec_params('select title from books where id = $1', [id]).getvalue(0, 0)
  ensure
    connection&.close
  end

  def assert_in_order(transcript, *parts)
    parts.reduce(0) do |at, part|
      found = transcript.index(part, at)

      assert found, "The transcript lacks, after character #{at}: #{part.inspect}\n#{transcript}"
      found + part.length
    end
  end
end
