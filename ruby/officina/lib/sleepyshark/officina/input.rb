# frozen_string_literal: true

module Sleepyshark
  module Officina
    # Declares a tool's input, or an agent's typed output: one declaration gives the value class the handler or the
    # host receives, and its JSON schema.
    #
    # @example
    #   SearchBooks = Officina::Input.define do
    #     string :title, 'Part of the title.', optional: true
    #     integer :max_price, 'The highest price.', optional: true, minimum: 0
    #   end
    #   SearchBooks.schema.to_s # => {"type":"object","properties":{"title":{"description":…
    #   SearchBooks.from_json({ 'title' => 'Dune' }) # => #<data SearchBooks title="Dune", max_price=nil>
    module Input
      # Builds the schema and value class from a declaration's block, in which each call declares one member.
      #
      # A member's JSON name is its name in camel case (`max_price` is `maxPrice`). The schema is written as the .NET
      # implementation writes the same type's (keys in its order, strings escaped as its encoder does), so the same
      # tools give the same prefix, and a session one implementation saved resumes in another.
      #
      # @return [Class] a frozen `Data` class with a member per declared one, in their order, and two class methods:
      #   `schema`, the {Schema} of its JSON form, and `from_json(object)`, which turns a JSON object (as JSON.parse
      #   returns it, valid against that schema) into an instance, an absent member being nil.
      # @raise [ArgumentError] when a declaration is malformed.
      def self.define(&)
        fields = Declaration.new.tap { |declaration| declaration.instance_exec(&) }.fields
        value_class(fields, Schema.new(object_schema(fields)))
      end

      def self.value_class(fields, schema)
        input = Data.define(*fields.map(&:name))
        input.define_singleton_method(:schema) { schema }
        input.define_singleton_method(:from_json) do |object|
          input.new(*fields.map { |field| field.read.call(object[field.json_name]) })
        end
        input.freeze
      end

      def self.object_schema(fields)
        properties = fields.map { |field| "#{DotnetJson.string(field.json_name)}:#{field.schema}" }.join(',')
        required = fields.reject(&:optional).map { |field| DotnetJson.string(field.json_name) }.join(',')
        %({"type":"object","properties":{#{properties}},"required":[#{required}],"additionalProperties":false})
      end
      private_class_method :value_class, :object_schema

      # A declared member: its name, its JSON name, its schema as JSON text, whether it may be absent, and how its
      # JSON value becomes the member's.
      Field = Data.define(:name, :json_name, :schema, :optional, :read)

      # The receiver of a declaration's block: each method declares a member of that JSON type, named by a snake-case
      # symbol, with an optional description the model reads. `nullable: true` also allows null; `optional: true` lets
      # the member be absent.
      class Declaration
        SCALARS = %i[string integer number boolean].freeze
        SAME = ->(value) { value }
        # JSON Schema counts 1.0 as an integer, so an integer member may arrive as an integral Float.
        WHOLE = ->(value) { value.is_a?(Float) ? value.to_i : value }
        private_constant :SCALARS, :SAME, :WHOLE

        attr_reader :fields

        def initialize
          @fields = []
        end

        # A string member, or with `enum:` one of the given strings.
        # @param enum [Array<String>, nil] the only values allowed, written without a type, as .NET writes an enum.
        def string(name, description = nil, optional: false, nullable: false, enum: nil)
          return add(name, description, optional, type('string', nullable)) unless enum
          raise ArgumentError, "#{name}: an enum cannot be nullable" if nullable

          add(name, description, optional, %("enum":[#{enum.map { |value| DotnetJson.string(value) }.join(',')}]))
        end

        # A whole number member; 1.0 counts as one.
        # @param minimum [Integer, nil] the smallest value allowed.
        def integer(name, description = nil, optional: false, nullable: false, minimum: nil)
          add(name, description, optional, type('integer', nullable) + bound(name, minimum), WHOLE)
        end

        # A number member.
        # @param minimum [Integer, nil] the smallest value allowed.
        def number(name, description = nil, optional: false, nullable: false, minimum: nil)
          add(name, description, optional, type('number', nullable) + bound(name, minimum))
        end

        # A true or false member.
        def boolean(name, description = nil, optional: false, nullable: false)
          add(name, description, optional, type('boolean', nullable))
        end

        # An array of one scalar type (`of: :string`), or of objects whose members the block declares; their values are
        # instances of a class of their own.
        def array(name, description = nil, of: nil, optional: false, nullable: false, &items)
          raise ArgumentError, "#{name}: give either of: #{SCALARS.join(', ')} or a block" unless items.nil? ^ of.nil?
          return array_of_objects(name, description, optional, nullable, &items) if items
          raise ArgumentError, "#{name}: of: must be one of #{SCALARS.join(', ')}" unless SCALARS.include?(of)

          array_of_scalars(name, description, optional, nullable, of)
        end

        private

        def array_of_scalars(name, description, optional, nullable, of)
          each = of == :integer ? WHOLE : SAME
          add(name, description, optional, %(#{type('array', nullable)},"items":{"type":"#{of}"}),
              ->(value) { value&.map(&each)&.freeze })
        end

        def array_of_objects(name, description, optional, nullable, &)
          element = Input.define(&)
          add(name, description, optional, %(#{type('array', nullable)},"items":#{element.schema}),
              ->(value) { value&.map { |object| element.from_json(object) }&.freeze })
        end

        def add(name, description, optional, body, read = SAME)
          described = (%("description":#{DotnetJson.string(description)},) if description)
          first, *rest = name.to_s.split('_')
          @fields << Field.new(name:, json_name: "#{first}#{rest.map(&:capitalize).join}",
                               schema: "{#{described}#{body}}", optional:, read:)
        end

        def type(name, nullable) = nullable ? %("type":["#{name}","null"]) : %("type":"#{name}")

        def bound(name, minimum)
          return '' if minimum.nil?
          raise ArgumentError, "#{name}: minimum must be an Integer" unless minimum.is_a?(Integer)

          %(,"minimum":#{minimum})
        end
      end
      private_constant :Field, :Declaration
    end
  end
end
