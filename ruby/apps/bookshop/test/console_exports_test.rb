# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'
require_relative 'export_server'

# Exports through the console end to end, against the real filesystem MCP server over Streamable HTTP: the tools the
# allow-list offers, an approved export and a declined one.
class ConsoleExportsTest < Minitest::Test
  include ConsoleSession

  def setup
    super
    @exports = { 'BOOKSHOP_EXPORTS' => ExportServer.url }
  end

  def test_app12_mcp03_the_allow_list_offers_only_writing_a_file_with_approval_and_listing_the_folder
    model = ScriptedModel.new(ScriptedModel.text('Hello.'))

    session(model, 'Sam', 'Hi.', '/quit', env: @exports)
    offered = model.requests.first.tools.select { it.name.start_with?('filesystem__') }

    assert_equal([['filesystem__list_directory', :read, false], ['filesystem__write_file', :write, true]],
                 offered.map { [it.name, it.kind, it.needs_approval?] })
  end

  def test_app12_an_order_history_is_exported_as_csv_after_approval
    csv = "order_id,status,placed_on,total\n#{order_history(1)}\n"
    write = JSON.generate({ path: '/projects/exports/order-history-alice-martin.csv', content: csv })
    model = ScriptedModel.new(*export_replies(write))

    transcript = session(model, 'Sam', "Export Alice Martin's order history as CSV.", 'y', '/quit', env: @exports)

    assert_in_order transcript, %(  > list_customer_orders {"customerId":1}\n),
                    "  ? filesystem__write_file needs your approval. Its exact input:\n", "    #{write}\n",
                    "    Approve? [y/N] y\n", "  < filesystem__write_file: ok\n",
                    "  < filesystem__list_directory: ok\n",
                    'Exported to order-history-alice-martin.csv.'
    assert_equal csv, ExportServer.read('order-history-alice-martin.csv')
    assert_includes results(model, -1).first.content, '[FILE] order-history-alice-martin.csv'
  end

  def test_app12_a_declined_export_writes_no_file
    model = ScriptedModel.new(
      say_then_call("I'll write the file.", call('c1', 'filesystem__write_file',
                                                 '{"path":"/projects/exports/declined.csv","content":"id\n"}')),
      ScriptedModel.text('Understood, no file.')
    )

    transcript = session(model, 'Sam', 'Export it.', 'n', '/quit', env: @exports)

    assert_in_order transcript, "    Approve? [y/N] n\n",
                    "  < filesystem__write_file: error: The call was denied: the staff member declined\n",
                    'Understood, no file.'
    assert_nil ExportServer.read('declined.csv')
  end

  private

  # The replies that look up Alice Martin and her orders, write the file with the input +write+, list the folder and
  # say so.
  def export_replies(write)
    [say_then_call('Let me find Alice.', call('c1', 'find_customer', '{"nameOrEmail":"Alice Martin"}')),
     say_then_call('Reading her orders.', call('c2', 'list_customer_orders', '{"customerId":1}')),
     say_then_call("I'll write the file.", call('c3', 'filesystem__write_file', write)),
     say_then_call('Checking the folder.', call('c4', 'filesystem__list_directory', '{"path":"/projects/exports"}')),
     ScriptedModel.text('Exported to order-history-alice-martin.csv.')]
  end

  # The customer's orders as CSV rows, newest first, without the header, read outside the shop.
  def order_history(customer_id)
    select_row("select string_agg(format('%s,%s,%s,%s', id, status, to_char(placed_at at time zone 'UTC', " \
               "'YYYY-MM-DD'), total), E'\\n' order by placed_at desc, id desc) from orders where customer_id = $1",
               customer_id).first
  end
end
