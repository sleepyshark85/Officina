# The reference filesystem MCP server, which speaks stdio only, behind supergateway, which serves it over Streamable
# HTTP on port 8000 with one server process per MCP session. It sees only /projects/exports. Message logging is off, as
# it would copy the exported data into the container's log.
FROM mcp/filesystem@sha256:35fcf0217ca0d5bf7b0a5bd68fb3b89e08174676c0e0b4f431604512cf7b3f67
RUN npm install --global --omit=dev supergateway@4.1.0
EXPOSE 8000
ENTRYPOINT ["supergateway", "--stdio", "node /app/dist/index.js /projects/exports", "--outputTransport", "streamableHttp", \
    "--stateful", "--port", "8000", "--healthEndpoint", "/healthz", "--logLevel", "none"]
