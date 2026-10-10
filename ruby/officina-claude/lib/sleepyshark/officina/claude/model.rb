# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    module Claude
      # Claude with fixed settings, a model for the core. Many runs may stream from one at once.
      #
      # Every request streams, on the SDK's beta messages API, with adaptive thinking and an explicit effort. It is
      # laid out for the cache: the tools in the order given (the core sorts them), then the instructions with a cache
      # point on them, then the conversation, which automatic caching follows to its end; an operator message, such as
      # the run context, goes as a system message where the conversation has it. Each block of a reply is kept as the
      # gem writes it, in the canonical form, and sent back byte for byte.
      #
      # Transient failures (rate limits, overload, server and network errors, also mid-stream) are retried, waiting as
      # long as Retry-After asks or backing off; what remains is raised as a TransientError, an AuthenticationError
      # or an InvalidRequestError.
      class Model
        EFFORTS = %i[low medium high xhigh max].freeze
        # Cache lifetimes, as the API names them. The longer one is for reads further apart (a prefix many
        # conversations share, users who reply slowly); its writes cost more.
        CACHE_LIFETIMES = %w[5m 1h].freeze
        # Attempts per call, the first included.
        ATTEMPTS = 5
        # Waits up to +seconds+, ending early once +cancel+ is cancelled. The callback it leaves with the cancellation,
        # at most one per retry, only closes a queue nobody waits on any more.
        WAIT = lambda { |seconds, cancel|
          woken = Thread::Queue.new
          cancel.on_cancel { woken.close }
          woken.pop(timeout: seconds)
        }.freeze
        # Prices by model, in US dollars per million tokens. A cache write is priced at five minutes' rate, 1.25 times
        # input; an hour's costs twice input.
        PRICES = {
          'claude-opus-5-5' => Price.new(input: BigDecimal(4), output: BigDecimal(20), cache_read: BigDecimal('0.20'),
                                         cache_write: BigDecimal(5))
        }.freeze
        private_constant :ATTEMPTS, :WAIT, :PRICES

        # The model and every setting that shapes its requests, in the words .NET's and Go's use, such as
        # "claude model=claude-opus-5-5 effort=medium max_tokens=64000 cache=5m thinking=adaptive"; the cache
        # lifetimes are one word when they are the same, else the prefix's then the conversation's ("1h/5m").
        attr_reader :settings

        # @param name [String] the model, such as "claude-opus-5-5"
        # @param effort [Symbol] one of EFFORTS: how hard Claude thinks and how much it spends
        # @param max_output_tokens [Integer] the longest reply, in tokens
        # @param prefix_cache [String] one of CACHE_LIFETIMES, for the tools and instructions; not shorter than
        #   +conversation_cache+, as the API needs longer-lived cache entries first
        # @param conversation_cache [String] one of CACHE_LIFETIMES, for the conversation, which its next call reads
        # @param api_key [String, nil] when nil, the SDK finds credentials as usual (ANTHROPIC_API_KEY…)
        # @param base_url [String, nil] the API's address; when nil, the SDK's default or ANTHROPIC_BASE_URL
        # @param wait [#call] waits between attempts, given the seconds and the call's Cancellation; tests pass one
        #   that waits for nothing
        # @raise [Error] when a setting is missing or unknown
        # mutant:disable -- its three survivors change nothing the API or a test can see: the SDK adds the cache
        #   control's only type, ephemeral, when it is left out; a max_retries of -1 retries no more than 0; and the
        #   settings hash is read only by this frozen model
        def initialize(name:, effort:, max_output_tokens: 64_000, prefix_cache: '5m', conversation_cache: '5m',
                       api_key: nil, base_url: nil, wait: WAIT)
          check(name, effort, max_output_tokens, prefix_cache, conversation_cache)
          cache = prefix_cache == conversation_cache ? prefix_cache : "#{prefix_cache}/#{conversation_cache}"
          @settings = -"claude model=#{name} effort=#{effort} max_tokens=#{max_output_tokens} cache=#{cache} " \
                       'thinking=adaptive'
          @fixed = { model: name, max_tokens: max_output_tokens, thinking: { type: :adaptive },
                     output_config: { effort: }, cache_control: { type: :ephemeral, ttl: conversation_cache } }.freeze
          @prefix_cache = prefix_cache
          # Retries are the model's own: they honour Retry-After mid-stream too, and the run is told of each.
          @client = Anthropic::Client.new(api_key:, base_url:, max_retries: 0)
          @wait = wait
          freeze
        end

        # The model as telemetry names it, with its price when this gem knows it.
        def info
          name = @fixed.fetch(:model)
          ModelInfo.new(provider: 'anthropic', name:, price: PRICES[name])
        end

        # Sends the request as one streamed call and yields its events, as the core's model contract says: text
        # deltas, the usage of each attempt, and Retried before each attempt after the first. A prompt longer than
        # the context window is a reply that stops for +:context_full+.
        # @return [Reply, nil] nil when +cancel+ stopped the call
        # @raise [TransientError, AuthenticationError, InvalidRequestError] when no attempt passed
        def stream(request, cancel:, &)
          params = params(request)
          attempt = 1
          begin
            Call.new(client: @client, params:, cancel:).reply(&) unless cancel.cancelled?
          rescue Anthropic::Errors::Error, Call::IncompleteError => e
            wait_to_retry(Failure.new(e), attempt, cancel, &)
            attempt += 1
            retry
          end
        end

        private

        # Waits before the attempt after +attempt+, or raises what remains when another attempt cannot pass or none is
        # left; called where the failure is rescued, so that is the cause of what it raises.
        def wait_to_retry(failure, attempt, cancel)
          raise failure.to_raise(attempt) unless failure.transient? && attempt < ATTEMPTS

          yield Retried.new
          @wait.call(failure.wait(attempt), cancel)
        end

        def check(name, effort, max_output_tokens, prefix_cache, conversation_cache)
          raise Error, 'A Claude model needs a name' if name.strip.empty?
          unless EFFORTS.include?(effort)
            raise Error, "Claude's effort is one of #{EFFORTS.join(', ')}, not #{effort.inspect}"
          end
          unless max_output_tokens.positive?
            raise Error, "Claude's max output tokens must be positive, not #{max_output_tokens}"
          end

          check_cache(prefix_cache, conversation_cache)
        end

        def check_cache(prefix, conversation)
          unless CACHE_LIFETIMES.include?(prefix) && CACHE_LIFETIMES.include?(conversation)
            raise Error, "Claude's cache lifetimes are #{CACHE_LIFETIMES.join(' or ')}, not #{prefix.inspect} and " \
                         "#{conversation.inspect}"
          end
          return unless prefix == '5m' && conversation == '1h'

          raise Error, "Claude's prefix cache may not be shorter than its conversation cache"
        end

        # The request as the API takes it. The messages go through the SDK's one raw field, each as a JSON fragment
        # it writes as it is: its typed messages parameter would write stored blocks anew.
        def params(request)
          { **@fixed,
            tools: request.tools.map { tool(it) },
            system: [{ type: :text, text: request.instructions,
                       cache_control: { type: :ephemeral, ttl: @prefix_cache } }],
            request_options: { extra_body: { messages: request.messages.map { JSON::Fragment.new(message(it)) } } } }
        end

        def tool(tool)
          { name: tool.name, description: tool.description, input_schema: JSON.parse(tool.input_schema),
            eager_input_streaming: true }
        end

        # A message as the API takes it, in the canonical form, which keeps a stored block's bytes. An operator
        # message is a system message mid-conversation, its text the content.
        def message(message)
          json = if message.role == :operator
                   JSON.generate({ role: 'system', content: message.text })
                 else
                   JSON.generate({ role: message.role, content: message.blocks.map { content(it) } })
                 end
          Block.canonical(json)
        end

        # A block with raw JSON goes as it is; a tool's result as a tool_result block; any other as a text block.
        def content(block)
          return JSON::Fragment.new(block.raw) if block.raw

          result = block.tool_result
          return { type: 'text', text: block.text } unless result

          { type: 'tool_result', tool_use_id: result.call_id, content: result.content }
            .merge(result.error? ? { is_error: true } : {})
        end
      end
    end
  end
end
