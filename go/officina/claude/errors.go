package claude

import (
	"encoding/json/v2"
	"errors"
	"fmt"
	"math/rand/v2"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/anthropics/anthropic-sdk-go"
)

// The classes of a call's failure, once retries are over or cannot help. Check them with errors.Is.
var (
	// ErrTransient is a rate limit, overload, server or network error that persisted through every attempt.
	ErrTransient = errors.New("claude: transient failure")
	// ErrAuthentication is an API key that is missing, wrong, or not allowed to do this.
	ErrAuthentication = errors.New("claude: authentication failed")
	// ErrInvalidRequest is a request the API rejected as it is; sending it again cannot help.
	ErrInvalidRequest = errors.New("claude: invalid request")
)

// apiError returns the API's error in err, if there is one: an error response, or an error event mid-stream.
func apiError(err error) (*anthropic.Error, bool) {
	var apiErr *anthropic.Error
	ok := errors.As(err, &apiErr)
	return apiErr, ok
}

// transient reports whether another attempt may pass. An error event mid-stream comes with the status of the
// response it ended (200), so its type decides first.
func transient(err error) bool {
	apiErr, ok := apiError(err)
	if !ok {
		// Not the API's answer: the network, or a stream cut short.
		return true
	}
	switch apiErr.Type() {
	case "overloaded_error", "api_error", "rate_limit_error", "timeout_error":
		return true
	}
	switch apiErr.StatusCode {
	case http.StatusRequestTimeout, http.StatusConflict, http.StatusTooManyRequests:
		return true
	default:
		return apiErr.StatusCode >= http.StatusInternalServerError
	}
}

// classify wraps an error retrying cannot fix in its class.
func classify(err error) error {
	apiErr, _ := apiError(err)
	switch {
	case apiErr != nil && (apiErr.StatusCode == http.StatusUnauthorized || apiErr.StatusCode == http.StatusForbidden ||
		apiErr.Type() == "authentication_error" || apiErr.Type() == "permission_error"):
		return fmt.Errorf("%w: %w", ErrAuthentication, err)
	default:
		return fmt.Errorf("%w: %w", ErrInvalidRequest, err)
	}
}

// promptTooLong reports whether err rejects a prompt longer than the context window. The API gives it no type of
// its own, only an invalid request whose message says so: the one place this package reads an error's text.
func promptTooLong(err error) bool {
	apiErr, ok := apiError(err)
	if !ok || apiErr.StatusCode != http.StatusBadRequest {
		return false
	}
	var body struct {
		Error struct {
			Message string `json:"message"`
		} `json:"error"`
	}
	if json.Unmarshal([]byte(apiErr.RawJSON()), &body) != nil {
		return false
	}
	return strings.Contains(strings.ToLower(body.Error.Message), "prompt is too long")
}

// retryAfter returns the wait a failed response asks for in its Retry-After header, in seconds or as a date; zero
// when it asks for none.
func retryAfter(err error) time.Duration {
	apiErr, ok := apiError(err)
	if !ok || apiErr.Response == nil {
		return 0
	}
	value := apiErr.Response.Header.Get("Retry-After")
	if seconds, err := strconv.ParseFloat(value, 64); err == nil {
		return time.Duration(seconds * float64(time.Second))
	}
	if date, err := http.ParseTime(value); err == nil {
		return time.Until(date)
	}
	return 0
}

// backoff returns the wait before the attempt after attempt: what the API asked for, capped so a server cannot hold
// a call for ever, or else an exponential backoff with jitter.
func backoff(attempt int, asked time.Duration) time.Duration {
	if asked > 0 {
		return min(asked, longestWait)
	}
	exponential := min(longestWait, firstBackoff<<(attempt-1))
	return time.Duration(float64(exponential) * (0.5 + rand.Float64()/2))
}
