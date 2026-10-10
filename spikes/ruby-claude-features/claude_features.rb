# frozen_string_literal: true

# Spike Ruby S02: the S02 live check rerun on the official Anthropic Ruby SDK (the `anthropic` gem), beta surface,
# streamed, with Opus 5.5. Throwaway code, not part of `ruby/` or any build.
#
#   bundle exec ruby claude_features.rb [step…]   (steps: offline compact clear memory updates midsys structured;
#                                                  none runs them all)
#
# The conversation is held as frozen strings of raw JSON, one per message, each block in the canonical form (compact,
# `<` `>` `&` escaped). Everything else in the request is a typed parameter. The messages go out as `JSON::Fragment`s
# through the one raw request field `request_options: {extra_body: {messages: [...]}}`, which the SDK merges after its
# typed dump and its JSON writer copies verbatim. A middleware records each request body as the SDK encodes it, stops a
# call that could take the spend over the cap, and every call checks each stored message appears in that body byte for
# byte. The `offline` step does the same against a local server with bytes chosen to break a re-encoder (no API cost).

require "json"
require "socket"
require "stringio"
require "anthropic"

MODEL = :"claude-opus-5-5"
COST_CAP = 1.0 # US dollars, for the whole run, prior steps included (PRIOR_SPEND)
PRIOR_SPEND = Float(ENV.fetch("R02_PRIOR_SPEND", "0"))

# Escapes the HTML-sensitive characters as Go's and .NET's encoders do. They occur only inside JSON strings, so a
# substitution over compact JSON is safe; every other escape stays as written.
HTML_ESCAPES = { "<" => "\\u003c", ">" => "\\u003e", "&" => "\\u0026" }.freeze

def canonical(json) = json.gsub(/[<>&]/, HTML_ESCAPES).freeze

def short(text, max)
  text = text.to_s.gsub("\n", "\\n")
  text.length <= max ? text : "#{text[0, max]}…(+#{text.length - max})"
end

# Prices Opus 5.5 per million tokens: $4 input, $20 output, cache read $0.20, 5-minute write 1.25x, 1-hour write 2x.
# When usage.iterations is present the top-level counts cover only the message iteration, so the iterations are summed.
class Ledger
  attr_reader :total

  def initialize(prior)
    @total = prior
    @tokens = { in: 0, out: 0, read: 0, w5: 0, w1: 0 }
  end

  def add(usage)
    parts = usage.iterations.to_a.empty? ? [usage] : usage.iterations
    parts.sum { |part| price(part) }.tap { |cost| @total += cost }
  end

  def to_s
    format("input=%<in>d output=%<out>d cache_read=%<read>d write5m=%<w5>d write1h=%<w1>d cost=$%<cost>.4f",
           **@tokens, cost: @total)
  end

  private

  def price(part)
    w5 = part.cache_creation&.ephemeral_5m_input_tokens.to_i
    w1 = part.cache_creation&.ephemeral_1h_input_tokens.to_i
    w5 = part.cache_creation_input_tokens.to_i if (w5 + w1).zero?
    counts = { in: part.input_tokens.to_i, out: part.output_tokens.to_i, read: part.cache_read_input_tokens.to_i,
               w5:, w1: }
    counts.each { |key, value| @tokens[key] += value }
    ((counts[:in] * 4) + (counts[:out] * 20) + (counts[:read] * 0.20) + (w5 * 5) + (w1 * 8)) / 1e6
  end
end

CapReached = Class.new(StandardError)

# Records the body of each request as the SDK's terminal encodes it: `JSON.generate` of the dumped Hash, which writes a
# `JSON::Fragment` verbatim.
class Tap
  attr_reader :last

  def call(req, nxt)
    @last = JSON.generate(req.body) if req.body.is_a?(Hash)
    nxt.call(req)
  end
end

# The spike's steps, one method each, with the same prompts, catalog and thresholds as the .NET and Go spikes.
class Spike
  STEPS = %w[offline compact clear memory updates midsys structured].freeze

  def initialize
    @ledger = Ledger.new(PRIOR_SPEND)
    @tap = Tap.new
    @client = Anthropic::Client.new(middleware: @tap, timeout: 600)
    @checked = @bad = @blocks = 0
  end

  def run(steps)
    STEPS.each do |step|
      next unless steps.include?(step)

      puts "\n===== #{step} ====="
      begin
        send(step)
      rescue CapReached => e
        puts "!! STOPPED before the call: #{e.message}"
        break
      rescue Anthropic::Errors::APIError => e
        puts "!! #{step} failed: #{e.class}: #{short(e.message, 400)}"
      end
    end
    puts "\nROUND TRIP: replayed messages checked=#{@checked} mismatched=#{@bad}; assistant blocks stored=#{@blocks}"
    puts "TOTAL: #{@ledger}"
  end

  private

  # ---------- messages, stored as canonical raw JSON

  def user_text(text) = canonical(JSON.generate({ role: "user", content: text }))

  def tool_results(results)
    content = results.map { |id, text| { type: "tool_result", tool_use_id: id, content: text } }
    canonical(JSON.generate({ role: "user", content: }))
  end

  # The SDK keeps no raw block JSON: each block is written from the accumulated typed model.
  def assistant(message)
    blocks = message.content.map { |block| canonical(block.to_json) }
    @blocks += blocks.size
    %({"role":"assistant","content":[#{blocks.join(',')}]}).freeze
  end

  def summary(block)
    case block.type
    when :thinking then "thinking(text=#{block.thinking.length}ch, sig=#{block.signature.length}ch)"
    when :text then "text(#{short(block.text, 80).inspect})"
    when :tool_use then "tool_use(#{block.name} #{JSON.generate(block.input)})"
    when :compaction then "compaction(content=#{block.content.to_s.length}ch)"
    else block.type.to_s
    end
  end

  # ---------- one streamed call; params carry everything but the messages

  def call(label, params, messages, &on_event)
    raw = { extra_body: { messages: messages.map { JSON::Fragment.new(it) } } }
    within_cap!(params, raw)
    stream = @client.beta.messages.stream(**params, model: MODEL, messages: [], request_options: raw)
    stream.each { |event| on_event&.call(event) }
    message = stream.accumulated_message
    check_wire(messages)
    report(label, message, messages.size)
    message
  end

  COUNTED = %i[betas system_ tools tool_choice thinking context_management].freeze

  # Stops before a call whose worst case (every input token, counted for free, written to cache at 1.25x, plus
  # max_tokens of output) could take the spend over the cap. The output schema is left out of the count (a few hundred
  # tokens at most here).
  def within_cap!(params, raw)
    tokens = @client.beta.messages.count_tokens(**params.slice(*COUNTED), model: MODEL, messages: [],
                                                                          request_options: raw).input_tokens
    worst = ((tokens * 5) + (params.fetch(:max_tokens) * 20)) / 1e6
    return if @ledger.total + worst <= COST_CAP

    raise CapReached, format("next call could cost up to $%<w>.4f (%<t>d input tokens), spent $%<s>.4f, cap $%<c>.2f",
                             w: worst, t: tokens, s: @ledger.total, c: COST_CAP)
  end

  def check_wire(messages)
    bad = messages.reject { |m| @tap.last.include?(m) }
    @checked += messages.size
    @bad += bad.size
    bad.each { |m| puts "  !! message not byte-identical on the wire: #{short(m, 200)}" }
  end

  def report(label, message, sent)
    cost = @ledger.add(message.usage)
    usage = message.usage
    puts "  [#{label}] stop=#{message.stop_reason} blocks=[#{message.content.map { summary(it) }.join(', ')}]"
    puts format("  [%<l>s] usage in=%<in>d out=%<out>d cache_read=%<r>d cache_write=%<w>d | call $%<c>.4f " \
                "running $%<t>.4f | replayed msgs checked=%<n>d",
                l: label, in: usage.input_tokens, out: usage.output_tokens, r: usage.cache_read_input_tokens.to_i,
                w: usage.cache_creation_input_tokens.to_i, c: cost, t: @ledger.total, n: sent)
    report_iterations(label, usage.iterations)
    edits = message.context_management&.applied_edits
    return if edits.nil?

    typed = edits.map do |edit|
      "{#{edit.type} cleared_input_tokens=#{edit.cleared_input_tokens} cleared_tool_uses=#{edit.cleared_tool_uses}}"
    end
    puts "  [#{label}] context_management.applied_edits (typed)=[#{typed.join(' ')}]"
  end

  def report_iterations(label, iterations)
    return if iterations.to_a.empty?

    its = iterations.map do |it|
      "{#{it.type} in=#{it.input_tokens} cache_read=#{it.cache_read_input_tokens} " \
        "cache_write=#{it.cache_creation_input_tokens} out=#{it.output_tokens}}"
    end
    puts "  [#{label}] usage.iterations (typed)=#{its.join(' ')}"
  end

  def context_json(params) = Anthropic::Beta::BetaContextManagementConfig.new(**params[:context_management]).to_json

  def tool_uses(message) = message.content.select { it.type == :tool_use }

  # Instructions long enough to pass the cache minimum, frozen, and the same as the .NET and Go spikes'.
  def instructions
    policies = (1..60).map do |i|
      "Policy #{i}: when a customer asks about topic #{i}, check the catalog first, never invent stock levels, " \
        "and quote prices in euros."
    end
    ["You are the assistant of a small bookshop called «Café Libro». Answer briefly and precisely.", *policies].join("\n")
  end

  def system_blocks = [{ type: :text, text: instructions, cache_control: { type: :ephemeral } }]

  # ---------- 0: offline replay check against a local server (no API cost)

  # Bytes a re-encoder would change: \u escapes, literal UTF-8, HTML characters, an escaped slash, key order, spacing.
  # The JSON escape for "é" (backslash, u00e9) is built from its character code so no editor or tool decodes it.
  BACKSLASH = 92.chr
  RAW = %({"role":"assistant","content":[{"type":"text","text":"Caf#{BACKSLASH}u00e9 «Muñoz» <b>&amp;</b> ) +
        %(a#{BACKSLASH}/b +x"} , {"text":"z","type":"text"}]})

  def offline
    received = Thread::Queue.new
    server = TCPServer.new("127.0.0.1", 0)
    thread = Thread.new { 4.times { serve_once(server, received) } }
    client = Anthropic::Client.new(base_url: "http://127.0.0.1:#{server.addr[1]}", api_key: "offline",
                                   middleware: @tap, max_retries: 0)
    base = { model: MODEL, max_tokens: 10 }
    hi = user_text("hi")

    raw = ->(*messages) { { extra_body: { messages: messages.map { JSON::Fragment.new(it) } } } }

    # A: the stored message as a JSON::Fragment through extra_body.
    client.beta.messages.create(**base, messages: [], request_options: raw.call(hi, RAW))
    wire = received.pop
    puts "  A extra_body + JSON::Fragment, sent: #{RAW}"
    puts "  A wire (server):                     #{wire}"
    puts "  A raw message byte-identical on the wire: #{wire.include?(RAW)}; middleware saw the wire bytes: " \
         "#{@tap.last == wire}"

    # B: the same message through the typed parameter, as a parsed Hash: the SDK re-encodes it.
    client.beta.messages.create(**base, messages: [JSON.parse(hi), JSON.parse(RAW)])
    wire = received.pop
    puts "  B typed messages (parsed Hash), wire: #{wire}"
    puts "  B raw message byte-identical on the wire: #{wire.include?(RAW)}"

    # C: a whole body swapped in by a request middleware. A String body is JSON-encoded as a string; a StringIO is
    # sent as is.
    whole = %({"model":"claude-opus-5-5","max_tokens":10,"messages":[#{hi},#{RAW}]})
    swap = ->(req, nxt) { nxt.call(req.with(body: StringIO.new(whole))) }
    client.beta.messages.create(**base, messages: [], request_options: { middleware: swap })
    wire = received.pop
    puts "  C body StringIO via request middleware, wire: #{wire}"
    puts "  C byte-identical: #{wire == whole}"

    # D: canonical form of the stored message, and the typed form of the same text.
    puts "  D canonical (escape < > & only): #{canonical(RAW)}"
    text = "Café «Muñoz» <b>&amp;</b> a/b +x"
    typed = Anthropic::Beta::BetaMessageParam.new(role: :assistant, content: [{ type: :text, text: }])
    puts "  D typed param to_json: #{typed.to_json}"
    client.beta.messages.create(**base, messages: [], request_options: raw.call(hi, canonical(RAW)))
    puts "  D canonical message byte-identical on the wire: #{received.pop.include?(canonical(RAW))}"
  ensure
    thread&.join
    server&.close
  end

  # Reads one HTTP request, hands its body over, and answers with a fixed message.
  def serve_once(server, received)
    socket = server.accept
    length = 0
    while (line = socket.gets) && line != "\r\n"
      length = Integer(line.split(":", 2).last) if line.downcase.start_with?("content-length:")
    end
    received << socket.read(length).force_encoding(Encoding::UTF_8)
    reply = '{"id":"msg_x","type":"message","role":"assistant","model":"m","content":[{"type":"text","text":"ok"}],' \
            '"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}'
    socket.write("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: #{reply.bytesize}\r\n" \
                 "Connection: close\r\n\r\n#{reply}")
  ensure
    socket&.close
  end

  # ---------- 1: server-side compaction (compact_20260112)

  def catalog(count)
    genres = %w[mystery history poetry science travel cooking]
    (1..count).map do |i|
      format('Item %<i>04d: "Volume %<i>d of the %<g>s series" by Author %<a>d, shelf %<s>s%<n>d, ' \
             "price %<e>d.%<c>02d EUR.",
             i:, g: genres[i % 6], a: i * 7 % 997, s: ("A".ord + (i % 26)).chr, n: i % 40, e: 5 + (i % 30), c: i % 100)
    end.join("\n")
  end

  def compact
    count = @client.beta.messages.count_tokens(model: MODEL, system_: system_blocks,
                                               messages: [{ role: :user, content: catalog(2000) }])
    lines = (2000 * 52_000.0 / count.input_tokens).to_i
    puts "count_tokens: 2000 lines = #{count.input_tokens} tokens -> using #{lines} lines (~52k)"

    params = {
      max_tokens: 8000, betas: [Anthropic::AnthropicBeta::COMPACT_2026_01_12], system_: system_blocks,
      cache_control: { type: :ephemeral }, output_config: { effort: :low },
      context_management: { edits: [{ type: :compact_20260112, trigger: { type: :input_tokens, value: 50_000 } }] }
    }
    puts "context_management json: #{context_json(params)}"

    messages = [user_text("#{catalog(lines)}\n\nWhat is the title of item 0042? Reply with the title only.")]
    deltas = 0
    first = call("compact#1", params, messages) do |event|
      if event.type == :content_block_start && event.content_block.type == :compaction
        puts "  stream: content_block_start compaction #{short(event.content_block.to_json, 120)}"
      end
      deltas += 1 if event.type == :content_block_delta && event.delta.type == :compaction_delta
    end
    puts "  stream: compaction_delta events=#{deltas}"
    first.content.grep(Anthropic::Beta::BetaCompactionBlock).each do |block|
      # The typed `encrypted_content` reader raises here: content_block_start carried no such key, the coercion error
      # it recorded outlives the delta that set it, so the field is read raw.
      puts "  compaction block typed: #{block.class} content=#{block.content.to_s.length}ch " \
           "encrypted_content=#{block[:encrypted_content].inspect[0, 40]}"
      puts "  compaction block stored (first 400 chars): #{short(canonical(block.to_json), 400)}"
    end

    messages += [assistant(first), user_text("Which shelf is item 0042 on? Answer briefly.")]
    second = call("compact#2", params, messages)
    messages += [assistant(second), user_text("And its price? Answer briefly.")]
    call("compact#3", params, messages)
  end

  # ---------- 2: tool-result clearing (clear_tool_uses_20250919)

  def lookup(id)
    reviews = (1..70).map do |i|
      "Review #{i} of book #{id}: readers praised chapter #{i} for its pacing, its setting and the careful translation."
    end
    "Book #{id}: \"The Quiet Harbour, part #{id}\" by Mara Linde.\n#{reviews.join("\n")}"
  end

  def clear
    params = {
      max_tokens: 4000, betas: [Anthropic::AnthropicBeta::CONTEXT_MANAGEMENT_2025_06_27], system_: system_blocks,
      cache_control: { type: :ephemeral }, output_config: { effort: :low },
      tools: [{ name: "lookup_book", description: "Look up one book by id. Returns its record and reviews.",
                input_schema: { type: :object, properties: { id: { type: "integer", description: "Book id" } },
                                required: ["id"] } }],
      tool_choice: { type: :auto, disable_parallel_tool_use: true },
      context_management: { edits: [{ type: :clear_tool_uses_20250919, trigger: { type: :tool_uses, value: 2 },
                                       keep: { type: :tool_uses, value: 1 } }] }
    }
    puts "context_management json: #{context_json(params)}"
    messages = [user_text("Look up books 1, 2, 3 and 4 with lookup_book, one call per reply, in that order. " \
                          "Then tell me the author.")]
    loop_tools("clear", params, messages, 6) { |use| lookup(use.input.fetch(:id)) }
  end

  # Runs a tool loop of at most `turns` calls, answering each tool use with the block's result.
  def loop_tools(label, params, messages, turns, &answer)
    (1..turns).each do |i|
      message = call("#{label}##{i}", params, messages)
      messages << assistant(message)
      return messages unless message.stop_reason == :tool_use

      messages << tool_results(tool_uses(message).map { [it.id, answer.call(it)] })
    end
    messages
  end

  # ---------- 3: memory tool (memory_20250818), answered client-side

  def handle_memory(store, input)
    path = input[:path]
    case input[:command]
    when "view"
      if store.key?(path)
        store[path].split("\n").each_with_index.map { |line, i| format("%6d\t%s\n", i + 1, line) }.join
      else
        prefix = "#{path.delete_suffix('/')}/"
        entries = store.select { |key, _| key.start_with?(prefix) }.map { |key, text| "\n#{text.length}\t#{key}" }
        "Here are the files and directories up to 2 levels deep in #{path}:#{entries.join}"
      end
    when "create"
      store[path] = input[:file_text]
      "File created successfully at: #{path}"
    when "str_replace"
      return "Error: The path #{path} does not exist." unless store.key?(path)

      store[path] = store[path].sub(input[:old_str], input[:new_str])
      "The memory file has been edited."
    when "insert"
      return "Error: The path #{path} does not exist." unless store.key?(path)

      lines = store[path].split("\n")
      lines.insert(input[:insert_line].to_i.clamp(0, lines.size), input[:insert_text])
      store[path] = lines.join("\n")
      "The file #{path} has been edited."
    when "delete"
      store.delete(path)
      "Successfully deleted #{path}"
    when "rename"
      store[input[:new_path]] = store.delete(input[:old_path])
      "Renamed."
    else
      "Error: unknown command #{input[:command]}"
    end
  end

  def memory
    store = {}
    tool = Anthropic::Beta::BetaMemoryTool20250818.new
    params = { max_tokens: 4000, system_: system_blocks, output_config: { effort: :medium }, tools: [tool] }
    puts "tools json: [#{tool.to_json}]"
    answer = lambda do |use|
      handle_memory(store, use.input).tap { puts "    memory #{JSON.generate(use.input)} -> #{short(it, 120)}" }
    end
    loop_tools("memA", params, [user_text("Hi, I'm Ana. For future conversations, please remember that I love " \
                                          "mystery novels and dislike horror.")], 6, &answer)
    puts "  store after A: #{JSON.generate(store)}"
    loop_tools("memB", params, [user_text("Hello again. Which genre should I browse today? One sentence.")], 6, &answer)
  end

  # ---------- 4: thinking display "updates" in a tool loop

  def updates
    title = { type: :object, properties: { title: { type: "string" } }, required: ["title"] }
    params = {
      max_tokens: 4000, betas: [Anthropic::AnthropicBeta::THINKING_DISPLAY_UPDATES_2026_08_18], system_: system_blocks,
      cache_control: { type: :ephemeral }, thinking: { type: :adaptive, display: :updates },
      output_config: { effort: :medium },
      tools: [{ name: "check_stock", description: "Number of copies in stock for a title.", input_schema: title },
              { name: "get_price", description: "Price in euros for a title.", input_schema: title }]
    }
    puts "thinking json: #{Anthropic::Beta::BetaThinkingConfigAdaptive.new(**params[:thinking].except(:type)).to_json}"
    messages = [user_text('For "The Name of the Rose", "Gaudy Night" and "The Daughter of Time": check stock and ' \
                          "price of each, one title at a time, and keep me posted on what you are doing between " \
                          "the lookups. Then tell me which in-stock title is cheapest.")]
    (1..8).each do |i|
      seen = { thinking: 0, signature: 0, text: +"" }
      message = call("updates##{i}", params, messages) do |event|
        next unless event.type == :content_block_delta

        case event.delta
        in Anthropic::Beta::BetaThinkingDelta => delta
          seen[:thinking] += 1
          seen[:text] << "[#{delta.thinking}]"
        in Anthropic::Beta::BetaSignatureDelta
          seen[:signature] += 1
        else
        end
      end
      puts "    stream: thinking_delta=#{seen[:thinking]} signature_delta=#{seen[:signature]} " \
           "deltas=#{short(seen[:text], 300)}"
      message.content.select { it.type == :thinking }.each do |block|
        puts "    thinking block stored: #{short(canonical(block.to_json), 260)}"
      end
      messages << assistant(message)
      break unless message.stop_reason == :tool_use

      messages << tool_results(tool_uses(message).map do |use|
        name = use.input.fetch(:title)
        result = use.name == "check_stock" ? "#{name.length % 4} copies" : "#{10 + (name.length % 7)}.90 EUR"
        [use.id, result]
      end)
    end
  end

  # ---------- 5: mid-conversation system message carrying run context, with caching

  def midsys
    params = { max_tokens: 2000, system_: system_blocks, cache_control: { type: :ephemeral },
               output_config: { effort: :low } }
    typed = Anthropic::Beta::BetaMessageParam.new(
      role: :system,
      content: [{ type: :text, text: "Run context: today is 2026-10-09; the customer is Ana Muñoz (loyalty member)." }]
    )
    sys = canonical(typed.to_json)
    puts "  typed system message to_json: #{typed.to_json}"
    messages = [user_text("Hi! Greet me by name and tell me today's date."), sys]
    first = call("midsys#1", params, messages)
    puts "    request messages: #{short(@tap.last[@tap.last.index('"messages"')..], 300)}"
    puts "    stored text block: #{short(canonical(first.content.last.to_json), 200)}"
    messages += [assistant(first), user_text("Recommend one mystery novel, in one sentence."), sys]
    second = call("midsys#2", params, messages)
    messages += [assistant(second), user_text("And one more, also one sentence."), sys]
    call("midsys#3", params, messages)
  end

  # ---------- 6: structured output

  # What the core's validator subset (types, properties, required, closed objects, items, anyOf, enum, pattern,
  # length and item bounds, minimum and maximum, annotations) produces for a book pick.
  def subset_schema(bounds:)
    year = { type: "integer", description: "Year of first publication" }
    tags = { type: "array", items: { type: "string" }, minItems: 1 }
    year.merge!(minimum: 1400, maximum: 2026) if bounds
    tags[:maxItems] = 5 if bounds
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema", type: "object",
      properties: {
        title: { type: "string", minLength: 1 }, author: { type: "string" }, year:, price: { type: "number" },
        genre: { enum: %w[Mystery Horror History] }, tags:,
        isbn: { anyOf: [{ type: "string", pattern: "^[0-9-]{10,17}$" }, { type: "null" }] },
        note: { type: %w[object null], properties: { text: { type: "string" } }, required: ["text"],
                additionalProperties: false }
      },
      required: %w[title author year price genre tags isbn note], additionalProperties: false
    }
  end

  # The same shape as an SDK input-schema model, for the SDK's own schema generator.
  class BookNote < Anthropic::BaseModel
    required :text, String
  end

  class BookPick < Anthropic::BaseModel
    required :title, String
    required :author, String
    required :year, Integer, doc: "Year of first publication"
    required :price, Float
    required :genre, Anthropic::EnumOf[:Mystery, :Horror, :History]
    required :tags, Anthropic::ArrayOf[String]
    required :isbn, String, nil?: true
    required :note, BookNote, nil?: true
  end

  def structured
    try_format("subset", { type: :json_schema, schema: subset_schema(bounds: false) })
    try_format("subset-with-bounds", { type: :json_schema, schema: subset_schema(bounds: true) })
    try_format("sdk-from-model", BookPick)
  end

  def try_format(label, format)
    params = { max_tokens: 2000, output_config: { effort: :low, format: } }
    message = call(label, params, [user_text("Pick one mystery novel for Ana.")])
    puts "  #{label} output_config.format on the wire: #{wire_format}"
    puts "    #{label} accepted; output: #{short(message.content.last.text, 300)}; " \
         "parsed_output: #{short(message.parsed_output.inspect, 200)}"
  rescue Anthropic::Errors::APIStatusError => e
    puts "  #{label} output_config.format on the wire: #{wire_format}"
    puts "    #{label} REJECTED #{e.status}: #{short(JSON.generate(e.body), 400)}"
  end

  def wire_format = short(JSON.generate(JSON.parse(@tap.last).dig("output_config", "format")), 900)
end

Spike.new.run(ARGV.empty? ? Spike::STEPS : ARGV)
