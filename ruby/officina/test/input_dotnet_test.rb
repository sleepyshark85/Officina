# frozen_string_literal: true

require 'test_helper'

# The schema DSL writes the bytes the .NET implementation writes for the same type, as the prefix takes each tool's
# schema as given: a session one implementation saved resumes in another only if they match.
class InputDotnetTest < Minitest::Test
  cover 'Sleepyshark::Officina::Input*'
  cover 'Sleepyshark::Officina::DotnetJson*'

  Input = Sleepyshark::Officina::Input

  # What .NET printed, by tool or type name.
  DOTNET = File.readlines(File.join(__dir__, 'fixtures/dotnet-schemas.tsv'), chomp: true)
               .reject { it.start_with?('#') }.to_h { it.split("\t", 2) }.freeze

  def test_tool01_bookshops_search_books_input_has_dotnets_schema
    search_books = Input.define do
      string :title, 'Part of the title.', optional: true
      string :author, "Part of the author's name.", optional: true
      string :genre, 'The genre: Fantasy, Science Fiction, Mystery, Romance, History, Biography, Poetry, Horror, ' \
                     'Children, Cookery, Travel or Philosophy.', optional: true
      number :max_price, 'The highest price.', optional: true, nullable: true
      boolean :in_stock, 'Only books with copies in stock.', optional: true
      integer :limit, 'The most books to return, 20 if not given.', optional: true
    end

    assert_equal DOTNET.fetch('search_books'), search_books.schema.to_s
  end

  def test_tool01_bookshops_place_order_input_with_an_array_of_objects_has_dotnets_schema
    place_order = Input.define do
      integer :customer_id, "The customer's id."
      array :lines, 'The books and copies to order, each book once.' do
        integer :book_id, "The book's id."
        integer :quantity, 'How many copies, at least 1.'
      end
    end

    assert_equal DOTNET.fetch('place_order'), place_order.schema.to_s
  end

  def test_out01_bookshops_session_summary_output_with_an_array_of_strings_has_dotnets_schema
    summary = Input.define do
      string :title, 'A title of a few words, naming the customers, books or orders the session was about.'
      string :summary, 'One to three sentences on what the staff member asked and what came of it.'
      array :changes, "Each change made to the shop's data, such as a customer added, an order placed or cancelled, " \
                      'or a restock, with its ids. Empty when nothing changed.', of: :string
    end

    assert_equal DOTNET.fetch('SessionSummary'), summary.schema.to_s
  end

  def test_out01_a_samples_output_with_enums_and_a_nullable_string_has_dotnets_schema
    triage = Input.define do
      string :category, 'What the message is about.', enum: %w[Billing Delivery Product Account Other]
      string :urgency, 'High when the customer is blocked or has been charged wrongly; low for a question that can ' \
                       'wait.', enum: %w[Low Normal High]
      string :order_number, 'The order number the message gives, such as A-1042, or null when it gives none.',
             nullable: true
      string :summary, 'One sentence on what the customer wants.'
    end

    assert_equal DOTNET.fetch('Triage'), triage.schema.to_s
  end

  def test_tool01_descriptions_are_escaped_as_dotnets_encoder_escapes_them
    # The shared prefix holds a text .NET wrote with every kind of escape: controls, quotes, HTML-sensitive
    # characters, non-ASCII, a surrogate pair and U+2028.
    prefix = File.read(File.expand_path('../../../testdata/session/prefix.json', __dir__))
    written = prefix[/^  "instructions": (".*"),$/, 1]
    input = Input.define { string :note, JSON.parse(prefix).fetch('instructions') }

    assert_equal %({"type":"object","properties":{"note":{"description":#{written},"type":"string"}},) \
                 '"required":["note"],"additionalProperties":false}', input.schema.to_s
  end
end
