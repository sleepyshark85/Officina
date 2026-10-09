// Package officina is part of a dependency check fixture.
package officina

import (
	_ "context"

	_ "example.com/fixture/officina/mcp"
	_ "example.com/thirdparty"
	_ "github.com/anthropics/anthropic-sdk-go"
	_ "go.opentelemetry.io/otel"
	_ "go.opentelemetry.io/otel/sdk/trace"
	_ "go.opentelemetry.io/otel/semconv/v1.37.0"
)
