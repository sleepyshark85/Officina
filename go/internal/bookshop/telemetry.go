package bookshop

import (
	"context"
	"crypto/rand"
	"errors"
	"fmt"
	"log/slog"
	"time"

	"go.opentelemetry.io/contrib/bridges/otelslog"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploggrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlpmetric/otlpmetricgrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	sdklog "go.opentelemetry.io/otel/sdk/log"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
)

// scope names the application's own tracer and logger, as the .NET implementation names its activity source.
const scope = "BookshopAssistant"

// Telemetry exports the application's traces, metrics and logs over OTLP, to the dashboard of the compose file.
// Close it to send what is left.
type Telemetry struct {
	Traces  *sdktrace.TracerProvider
	Metrics *sdkmetric.MeterProvider
	logs    *sdklog.LoggerProvider
}

// NewTelemetry returns telemetry that exports to the OTLP/gRPC endpoint, a URL such as http://localhost:4317 (http
// sends it unencrypted). The exporters connect when they first export, so it succeeds with the dashboard down.
func NewTelemetry(ctx context.Context, endpoint string) (*Telemetry, error) {
	traces, err := otlptracegrpc.New(ctx, otlptracegrpc.WithEndpointURL(endpoint))
	if err != nil {
		return nil, fmt.Errorf("export traces: %w", err)
	}
	metrics, err := otlpmetricgrpc.New(ctx, otlpmetricgrpc.WithEndpointURL(endpoint))
	if err != nil {
		return nil, fmt.Errorf("export metrics: %w", err)
	}
	logs, err := otlploggrpc.New(ctx, otlploggrpc.WithEndpointURL(endpoint))
	if err != nil {
		return nil, fmt.Errorf("export logs: %w", err)
	}
	// The same service as the .NET implementation's, so the dashboard shows both as replicas of one application.
	res, err := resource.Merge(resource.Default(), resource.NewSchemaless(
		attribute.String("service.name", "bookshop-assistant"), attribute.String("service.instance.id", rand.Text())))
	if err != nil {
		return nil, fmt.Errorf("describe the service: %w", err)
	}
	return &Telemetry{
		Traces: sdktrace.NewTracerProvider(sdktrace.WithBatcher(traces), sdktrace.WithResource(res)),
		Metrics: sdkmetric.NewMeterProvider(sdkmetric.WithResource(res),
			sdkmetric.WithReader(sdkmetric.NewPeriodicReader(metrics, sdkmetric.WithInterval(5*time.Second)))),
		logs: sdklog.NewLoggerProvider(sdklog.WithProcessor(sdklog.NewBatchProcessor(logs)), sdklog.WithResource(res)),
	}, nil
}

// Logger returns the application's logger, whose records go to the dashboard, each in the trace of the context it
// is given.
func (t *Telemetry) Logger() *slog.Logger {
	return otelslog.NewLogger(scope, otelslog.WithLoggerProvider(t.logs))
}

// Close sends what is left and stops the exporters, waiting at most until ctx ends.
func (t *Telemetry) Close(ctx context.Context) error {
	err := errors.Join(t.Traces.Shutdown(ctx), t.Metrics.Shutdown(ctx), t.logs.Shutdown(ctx))
	if err != nil {
		return fmt.Errorf("close telemetry: %w", err)
	}
	return nil
}
