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
      # @raise [ArgumentError] when a declaration is malformed, or declares two members with the same JSON name.
      def self.define(&)
        members = Members.new
        Declaration.new(members).instance_exec(&)
        fields = members.to_a
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

      # The members declared so far, by JSON name.
      class Members
        def initialize
          @fields = {}
        end

        # @param body [String] the member's schema after its description, without the braces.
        # @param read [#call] turns the member's JSON value into its value.
        # @raise [ArgumentError] when a member with the same JSON name is already declared.
        def add(name, description, optional:, body:, read: :itself.to_proc)
          json_name = json_name(name)
          raise ArgumentError, "#{name}: #{json_name} is declared twice" if @fields.key?(json_name)

          described = (%("description":#{DotnetJson.string(description)},) if description)
          @fields[json_name] = Field.new(name:, json_name:, schema: "{#{described}#{body}}", optional:, read:)
        end

        def to_a = @fields.values

        private

        def json_name(name)
          first, *rest = name.to_s.split('_')
          "#{first}#{rest.map(&:capitalize).join}"
        end
      end

      # The receiver of a declaration's block: each method declares a member of that JSON type, named by a snake-case
      # symbol, with an optional description the model reads. `nullable: true` also allows null; `optional: true` lets
      # the member be absent. Its helpers are class methods, so the block reaches these five methods only.
      class Declaration
        SCALARS = %i[string integer number boolean].freeze
        # JSON Schema counts 1.0 as an integer, so an integer member may arrive as an integral Float.
        WHOLE = ->(value) { value.is_a?(Float) ? value.to_i : value }
        private_constant :SCALARS, :WHOLE

        def initialize(members)
          @members = members
        end

        # A string member, or with `enum:` one of the given strings.
        # @param enum [Array<String>, nil] the only values allowed, written without a type, as .NET writes an enum.
        def string(name, description = nil, optional: false, nullable: false, enum: nil)
          raise ArgumentError, "#{name}: an enum cannot be nullable" if enum && nullable

          body = enum ? Declaration.enum(enum) : Declaration.type('string', nullable)
          @members.add(name, description, optional:, body:)
        end

        # A whole number member; 1.0 counts as one.
        # @param minimum [Integer, nil] the smallest value allowed.
        def integer(name, description = nil, optional: false, nullable: false, minimum: nil)
          body = Declaration.type('integer', nullable) + Declaration.minimum(name, minimum)
          @members.add(name, description, optional:, body:, read: WHOLE)
        end

        # A number member.
        # @param minimum [Integer, nil] the smallest value allowed.
        def number(name, description = nil, optional: false, nullable: false, minimum: nil)
          body = Declaration.type('number', nullable) + Declaration.minimum(name, minimum)
          @members.add(name, description, optional:, body:)
        end

        # A true or false member.
        def boolean(name, description = nil, optional: false, nullable: false)
          @members.add(name, description, optional:, body: Declaration.type('boolean', nullable))
        end

        # An array of one scalar type (`of: :string`), or of objects whose members the block declares; their values are
        # instances of a class of their own.
        def array(name, description = nil, of: nil, optional: false, nullable: false, &objects)
          raise ArgumentError, "#{name}: give either of: #{SCALARS.join(', ')} or a block" unless objects.nil? ^ of.nil?

          items, item = Declaration.items(name, of, &objects)
          body = %(#{Declaration.type('array', nullable)},"items":#{items})
          @members.add(name, description, optional:, body:, read: ->(value) { value&.map(&item).freeze })
        end

        def self.enum(values) = %("enum":[#{values.map { |value| DotnetJson.string(value) }.join(',')}])

        def self.type(name, nullable) = nullable ? %("type":["#{name}","null"]) : %("type":"#{name}")

        def self.minimum(name, minimum)
          return '' if minimum.nil?
          raise ArgumentError, "#{name}: minimum must be an Integer" unless minimum.is_a?(Integer)

          %(,"minimum":#{minimum})
        end

        # @return [Array(#to_s, #call)] the schema of an array's items, and what turns an item's JSON value into its
        #   value.
        # @raise [ArgumentError] when there is no block and the scalar type is not one.
        def self.items(name, scalar, &objects)
          if objects
            element = Input.define(&objects)
            [element.schema, element.method(:from_json)]
          else
            raise ArgumentError, "#{name}: of: must be one of #{SCALARS.join(', ')}" unless SCALARS.include?(scalar)

            [%({"type":"#{scalar}"}), scalar == :integer ? WHOLE : :itself.to_proc]
          end
        end
      end
      private_constant :Field, :Members, :Declaration
    end
  end
end
