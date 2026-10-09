module example.com/fixture

go 1.27

replace (
	example.com/thirdparty => ../stubs/thirdparty
	github.com/anthropics/anthropic-sdk-go => ../stubs/anthropic
	go.opentelemetry.io/otel => ../stubs/otel
	go.opentelemetry.io/otel/metric => ../stubs/otelmetric
	go.opentelemetry.io/otel/sdk => ../stubs/otelsdk
	go.opentelemetry.io/otel/trace => ../stubs/oteltrace
)

require github.com/anthropics/anthropic-sdk-go v0.0.0-00010101000000-000000000000
