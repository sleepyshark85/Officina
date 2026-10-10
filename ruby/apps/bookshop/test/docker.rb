# frozen_string_literal: true

require 'open3'

# Docker, which runs the servers the end-to-end tests share, each in a container of its own that is removed when the
# tests end.
module Docker
  # Starts a container named +name+ from the image, with the arguments, and removes it when the tests end.
  def self.start(name, *, image)
    run('run', '--detach', '--rm', '--name', name, '--label', 'officina-test', *, image)
    Minitest.after_run { Open3.capture2e('docker', 'rm', '--force', name) }
  end

  # The port on this machine that the container's port, such as "5432/tcp", is published at.
  def self.port(container, port) = run('port', container, port).lines.first.split(':').last.strip

  # Runs docker with the arguments and returns its output, or raises with it when docker fails.
  def self.run(*arguments)
    output, status = Open3.capture2e('docker', *arguments)
    raise "docker #{arguments.first} failed: #{output}" unless status.success?

    output
  end
end
