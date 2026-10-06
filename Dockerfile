# Dockerfile for Glama MCP introspection.
#
# The revit-model-mcp server answers `initialize` + `tools/list` over stdio
# WITHOUT a live Revit connection — only tool *calls* need Revit, introspection
# does not. Glama builds this image, runs it, and reads the tool list to score
# the server. Build context is the repository root.
FROM python:3.12-slim@sha256:ddb0207ae1f0356c2b724d740769b0c5f5f51cc54a0525178f721825f78fe74c

WORKDIR /app
COPY server/ /app/
RUN pip install --no-cache-dir --require-hashes -r requirements.docker.txt \
    && pip install --no-cache-dir --no-deps .

# 'local' host is a no-op at startup (no connection is opened); it only matters
# when a tool is actually invoked, which Glama's introspection never does.
ENV REVIT_MCP_HOST=local

ENTRYPOINT ["revit-model-mcp"]
