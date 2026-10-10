# frozen_string_literal: true

module Sleepyshark
  module Officina
    # Numbers in the run's sentences, as .NET writes them.
    module Figures
      # A count with commas between its thousands, such as 52,753.
      def self.thousands(count) = count.to_s.gsub(/\B(?=(\d{3})+\z)/, ',')
    end
    private_constant :Figures
  end
end
