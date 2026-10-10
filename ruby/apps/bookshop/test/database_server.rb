# frozen_string_literal: true

require 'bookshop'
require 'open3'
require 'pg'

# The PostgreSQL server the database tests share, started with Docker once per run, as the compose file runs it: its
# image, user and password, and the application's schema and seed, which .NET and Go read too. Included in a test,
# it gives each test its own copy of the seeded database, and the shop over it. The tests run on Linux: always in CI,
# where a missing Docker fails them rather than skipping them silently, and elsewhere when Docker is found.
module DatabaseServer
  IMAGE = 'mirror.gcr.io/library/postgres:17'
  USER = 'bookshop'
  PASSWORD = 'shelf-demo-41'
  SEEDED = 'bookshop'
  SCRIPTS = File.expand_path('../../../../apps/BookshopAssistant/database', __dir__)
  # Unique per run, so runs side by side, or the compose file's own database, do not clash.
  CONTAINER = "officina-ruby-bookshop-test-#{Process.pid}".freeze
  # Seconds the server may take to start, the image's download included.
  STARTUP = 180

  class << self
    # Where the database tests do not run, why; nil where they do.
    def skipped
      if !RUBY_PLATFORM.include?('linux')
        'the database tests need Linux and Docker (TEST-03)'
      elsif !ENV.key?('CI') && !ENV.key?('DOCKER_HOST') && !File.exist?('/var/run/docker.sock')
        'the database tests need Docker, which was not found'
      end
    end

    # A fresh copy of the seeded database, on the server started first if it is not yet.
    def copy
      @port ||= start
      @copies = (@copies || 0) + 1
      name = "test_#{@copies}"
      admin("create database #{name} template #{SEEDED}")
      name
    end

    def admin(sql)
      connection = PG.connect(url('postgres'))
      connection.exec(sql)
    ensure
      connection&.close
    end

    def url(name) = "postgres://#{USER}:#{PASSWORD}@127.0.0.1:#{@port}/#{name}"

    private

    # Starts the server and returns its port on this machine; the container is removed when the tests end.
    def start
      docker('run', '--detach', '--rm', '--name', CONTAINER, '--label', 'officina-test',
             '--env', "POSTGRES_USER=#{USER}", '--env', "POSTGRES_PASSWORD=#{PASSWORD}",
             '--env', "POSTGRES_DB=#{SEEDED}", '--publish', '127.0.0.1::5432',
             '--volume', "#{SCRIPTS}:/docker-entrypoint-initdb.d:ro", IMAGE)
      Minitest.after_run { Open3.capture2e('docker', 'rm', '--force', CONTAINER) }
      port = docker('port', CONTAINER, '5432/tcp').lines.first.split(':').last.strip
      wait_until_ready(port)
      port
    end

    # The server listens on TCP only once the scripts have run and it has restarted, so the first connection that
    # succeeds finds it ready.
    def wait_until_ready(port)
      deadline = Process.clock_gettime(Process::CLOCK_MONOTONIC) + STARTUP
      begin
        PG.connect("postgres://#{USER}:#{PASSWORD}@127.0.0.1:#{port}/#{SEEDED}").close
      rescue PG::ConnectionBad
        raise if Process.clock_gettime(Process::CLOCK_MONOTONIC) > deadline

        sleep 0.2
        retry
      end
    end

    def docker(*arguments)
      output, status = Open3.capture2e('docker', *arguments)
      raise "docker #{arguments.first} failed: #{output}" unless status.success?

      output
    end
  end

  def setup
    super
    reason = DatabaseServer.skipped
    skip reason if reason
    @database_name = DatabaseServer.copy
    @shop = Bookshop::Shop.open(database_url)
  end

  def teardown
    @shop&.close
    DatabaseServer.admin("drop database #{@database_name} with (force)") if @database_name
    super
  end

  private

  attr_reader :shop

  def database_url = DatabaseServer.url(@database_name)

  # Makes the test's database unreachable, as if its server had stopped: new connections are refused and open ones
  # ended.
  def take_database_down
    DatabaseServer.admin("alter database #{@database_name} allow_connections false")
    DatabaseServer.admin("select pg_terminate_backend(pid) from pg_stat_activity where datname = '#{@database_name}'")
  end

  def bring_database_back
    DatabaseServer.admin("alter database #{@database_name} allow_connections true")
  end

  # Runs a query of the test's own, outside the shop, and returns its one value as an Integer.
  def select_integer(sql, *params)
    connection = PG.connect(database_url)
    Integer(connection.exec_params(sql, params).getvalue(0, 0))
  ensure
    connection&.close
  end

  def stock(book_id) = select_integer('select quantity from stock where book_id = $1', book_id)
end
