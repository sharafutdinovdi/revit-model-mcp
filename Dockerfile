# Dockerfile for Glama MCP introspection.
#
# The revit-model-mcp server answers `initialize` + `tools/list` over stdio
# WITHOUT a live Revit connection — only tool *calls* need Revit, introspection
# does not. Glama builds this image, runs it, and reads the tool list to score
# the server. Build context is the repository root.
FROM python:3.12-slim

WORKDIR /app
COPY server/ /app/
RUN pip install --no-cache-dir .

# 'local' host is a no-op at startup (no connection is opened); it only matters
# when a tool is actually invoked, which Glama's introspection never does.
ENV REVIT_MCP_HOST=local

ENTRYPOINT ["revit-model-mcp"]
