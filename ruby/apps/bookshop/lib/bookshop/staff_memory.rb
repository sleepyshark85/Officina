# frozen_string_literal: true

module Bookshop
  # What the assistant remembers for each staff member, in the memory store under their scope, and what /memory shows
  # of it, in .NET's and Go's words. A staff member's scope is their name in lower case, so case does not matter.
  # Memory follows who is at the counter, not who started the session: a session resumed by another staff member runs
  # in theirs.
  class StaffMemory
    Officina = Sleepyshark::Officina
    # Where the memory tool shows the model its files, and /memory shows the staff member.
    ROOT = '/memories'
    private_constant :Officina, :ROOT

    # @return [String] the staff member's memory scope
    def self.scope(staff_member) = staff_member.downcase

    # Whether the name can be a staff member's: its scope must be one the memory store accepts.
    def self.valid_name?(staff_member) = Officina::MemoryRules.valid_scope?(scope(staff_member))

    # @param store [Sleepyshark::Officina::_MemoryStore] the chat agent's
    def initialize(store)
      @store = store
      freeze
    end

    # Every file the staff member's memory holds, by path, each with its text indented under it; or that there is
    # none, or why it could not be read.
    def show(staff_member)
      scope = self.class.scope(staff_member)
      files = @store.list(scope).sort_by(&:path)
      return 'Nothing remembered yet.' if files.empty?

      ['Remembered:', *files.flat_map { file(scope, it.path) }].join("\n")
    rescue Officina::Error, SystemCallError => e
      "The memory could not be read: #{e.message}"
    end

    private

    # The file's path as the model sees it, then each line of its text.
    def file(scope, path)
      text = @store.read(scope, path).to_s.sub(/\n+\z/, '')
      ["#{ROOT}/#{path}", *text.split("\n", -1).map { "  #{it}" }]
    end
  end
  private_constant :StaffMemory
end
