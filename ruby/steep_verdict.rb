# frozen_string_literal: true

# What the Rakefile's steep task makes of a Steep run. Steep exits 0 after logging a FATAL and skipping a file, so its
# log is read as well; the log line follows the progress dots on the same line, so it is not anchored to a line start.
module SteepVerdict
  LOG_PROBLEM = /\d\d:\d\d:\d\d\.\d+: (FATAL|ERROR): /

  # @return [String, nil] why the run failed, or nil when it did not
  def self.failure(output, success)
    return 'Steep failed.' unless success

    'Steep logged a FATAL or ERROR line (above): it skipped a file.' if output.match?(LOG_PROBLEM)
  end
end
