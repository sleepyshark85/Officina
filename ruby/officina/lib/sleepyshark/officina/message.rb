# frozen_string_literal: true

module Sleepyshark
  module Officina
    Message = Data.define(:role, :blocks)

    # A role and at least one block. The role is +:user+, +:assistant+ or +:operator+: the host, with operator
    # authority, after the cached prefix, which carries the run context.
    class Message
      ROLES = %i[user assistant operator].freeze

      # @param role [Symbol] one of ROLES
      # @param blocks [Array<Block>] at least one
      # @raise [Error] when the role is unknown or there are no blocks
      def initialize(role:, blocks:)
        raise Error, "A message's role is one of #{ROLES.join(', ')}, not #{role.inspect}" unless ROLES.include?(role)
        raise Error, 'A message needs at least one block' if blocks.empty?

        super(role:, blocks: blocks.dup.freeze)
      end

      # The text of its blocks, joined.
      def text = blocks.map(&:text).join
    end
  end
end
