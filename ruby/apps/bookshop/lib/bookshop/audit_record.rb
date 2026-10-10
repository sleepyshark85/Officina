# frozen_string_literal: true

module Bookshop
  # An audit entry as /audit reads it back from the audit table: when, in which run and trace (+trace_id+, in W3C hex
  # form), and what, with the kind as the table holds it (RunStarted, ToolEnded…); +duration+ in seconds, and on a
  # run's end its +usage+ and +cost+ in US dollars, nil when its price is not known.
  AuditRecord = Data.define(:time, :run, :trace_id, :kind, :tool, :outcome, :detail, :duration, :usage, :cost)
  private_constant :AuditRecord
end
