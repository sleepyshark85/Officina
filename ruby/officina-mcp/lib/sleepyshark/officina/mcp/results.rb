# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # What the results of tools/list and tools/call hold. JSON.parse makes each value an instance of the class
      # itself, never of a subclass, so each is checked with instance_of?, and each member is read once (values_at).
      module Results
        # The tools on a page of tools/list; nil when it cannot be read.
        def self.tools(page)
          tools = page['tools']
          return unless tools.instance_of?(Array)

          tools.map { tool(it) or return nil }
        end

        # The cursor of the page after this one; nil on the last page.
        def self.next_cursor(page)
          cursor = page['nextCursor']
          cursor if cursor.instance_of?(String) && !cursor.empty?
        end

        # What a tools/call result says: its content, one item a line, with any item that is not text named by its
        # type, and whether it is an error; nil when it cannot be read.
        def self.call_result(result)
          content = result['content']
          return unless content.instance_of?(Array)

          text = content.map { content_text(it) or return nil }
          CallResult.new(text: text.join("\n").freeze, error: result['isError'] == true)
        end

        def self.tool(item)
          return unless item.instance_of?(Hash)

          name, input_schema, description = item.values_at('name', 'inputSchema', 'description')
          return unless name.instance_of?(String) && input_schema.instance_of?(Hash)

          Tool.new(name:, description: description.to_s, input_schema:)
        end

        def self.content_text(item)
          return unless item.instance_of?(Hash)

          type, text = item.values_at('type', 'text')
          case type
          when 'text' then text if text.instance_of?(String)
          when String then "[#{type} content]"
          end
        end
        private_class_method :tool, :content_text
      end
      private_constant :Results
    end
  end
end
