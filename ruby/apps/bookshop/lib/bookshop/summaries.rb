# frozen_string_literal: true

module Bookshop
  # One console's session summaries: written as a session is left, and for a few of those left without one (after a
  # crash, or a summary that failed) as /sessions lists them. Each is stored with its cost added to the session's,
  # and said to the staff member, as is a summary that fails, which never stops the console. Without a summarizer,
  # sessions keep no title.
  class Summaries
    Officina = Sleepyshark::Officina
    # How many sessions left without a summary one listing summarizes; later listings do the rest.
    PER_LISTING = 3
    private_constant :Officina, :PER_LISTING

    # @param summarizer [Summarizer, nil]
    # @param store [SessionStore] where the summaries are kept
    # @param terminal [Terminal] where they are said
    def initialize(summarizer:, store:, terminal:)
      @summarizer = summarizer
      @store = store
      @terminal = terminal
      # The sessions whose summary failed in this console, which listings do not try again.
      @failed = Set.new
    end

    # Summarizes the session as it is left, unless nothing was said in it since this console took it up.
    #
    # @param session [Session]
    def leave(session)
      return unless @summarizer && session.changed?

      summary = summarize(session.id, session.conversation)
      @terminal.write_line("Session #{session.id} summarized: #{summary.title}") if summary
    end

    # The listings, with a summary written for up to PER_LISTING of those left without one: never the session in use,
    # which is summarized as it is left, nor one whose summary failed or which could not be read in this console.
    #
    # @param listed [Array<SessionListing>]
    # @param current [Session] the session in use
    # @return [Array<SessionListing>]
    def fill(listed, current)
      return listed unless @summarizer

      left = listed.select { left_without_summary?(it, current) }
      summarizing = left.first(PER_LISTING)
      announce(summarizing.size, left.size)
      written = summarizing.to_h { [it.id, stored_summary(it.id)] }.compact
      listed.map { |listing| written[listing.id]&.then { summarized(listing, it) } || listing }
    end

    private

    def left_without_summary?(listing, current)
      listing.stale && listing.id != current.id && !@failed.include?(listing.id)
    end

    def summarized(listing, summary)
      listing.with(title: summary.title, summary: summary.summary, changes: summary.changes)
    end

    def announce(summarizing, left)
      if summarizing < left
        @terminal.write_line("Summarizing #{summarizing} of #{left} sessions left without a summary; " \
                             '/sessions again does more…')
      elsif summarizing.positive?
        @terminal.write_line("Summarizing #{summarizing} session#{'s' if summarizing > 1} left without a summary…")
      end
    end

    # The summary of the stored session with the id, nil when there is none or it cannot be read; one the database
    # failed to load is tried again by the next listing.
    def stored_summary(id)
      stored = @store.load(id)
      stored && summarize(id, stored.conversation)
    rescue Officina::Error => e
      @failed << id
      unread(id, e)
    rescue PG::Error => e
      unread(id, e)
    end

    def unread(id, error)
      @terminal.write_line("[Session #{id} could not be read: #{error.message.strip}]")
      nil
    end

    # Summarizes the conversation and stores the summary, adding its cost to the session's. Returns the summary, also
    # when it could not be stored; nil, and says why, when there is none.
    def summarize(id, conversation)
      case (result = @summarizer.summarize(conversation))
      in Officina::Completed(output: SessionSummary => summary) then store(id, summary, result)
      in Officina::Stopped(reason:) then failed(id, "stopped: #{reason}")
      in Officina::Failed(detail:) then failed(id, detail)
      end
    rescue Officina::Error => e
      failed(id, e.message)
    end

    def store(id, summary, result)
      @store.save_summary(id, summary, usage: result.usage, cost: result.cost)
      summary
    rescue PG::Error => e
      @terminal.write_line("[The summary of session #{id} could not be saved: #{e.message.strip}]")
      summary
    end

    def failed(id, reason)
      @failed << id
      @terminal.write_line("[Session #{id} could not be summarized: #{reason}]")
      nil
    end
  end
  private_constant :Summaries
end
