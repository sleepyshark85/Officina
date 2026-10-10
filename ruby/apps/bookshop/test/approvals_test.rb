# frozen_string_literal: true

require 'bookshop'
require 'test_helper'

# The chat agent's approver on its own: the console's answers reach the call they name.
class ApprovalsTest < Minitest::Test
  Officina = Sleepyshark::Officina
  # Private to the application, which hands it only to the agent and the console.
  Approvals = Bookshop.const_get(:Approvals)

  def test_app06_each_answer_reaches_the_call_it_names_and_one_left_over_answers_no_other
    approvals = Approvals.new
    first, second = %w[c1 c2].map { Officina::ToolCall.new(id: it, name: 'restock_book', input: '{}') }
    approvals.answer(first, Officina::Approval.new(approved: false, reason: 'not that one'))
    approvals.answer(second, Officina::Approval.new(approved: true, reason: nil))

    assert_predicate approvals.approve(nil, second, cancel: Officina::Cancellation.new), :approved
  end

  def test_app03_a_cancelled_reply_withdraws_the_call_waiting_for_approval
    cancel = Officina::Cancellation.new
    cancel.cancel
    call = Officina::ToolCall.new(id: 'c1', name: 'restock_book', input: '{}')

    withdrawn = Approvals.new.approve(nil, call, cancel:)

    refute_predicate withdrawn, :approved
    assert_equal 'the reply was cancelled while waiting for the staff member', withdrawn.reason
  end
end
