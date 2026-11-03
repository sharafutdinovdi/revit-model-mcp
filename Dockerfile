# Dockerfile for Glama MCP introspection.
#
# The revit-model-mcp server answers `initialize` + `tools/list` over stdio
# WITHOUT a live Revit connection — only tool *calls* need Revit, introspection
# does not. Glama builds this image, runs it, and reads the tool list to score
# the server. Build context is the repository root.
FROM python:3.15-slim@sha256:94d3426f1def7db4a106a232ac121bfd975393e5dfd40a619b955fae45fe7913

WORKDIR /app
COPY server/ /app/
RUN pip install --no-cache-dir --require-hashes -r requirements.docker.txt \
    && pip install --no-cache-dir --no-deps --editable .

# 'local' host is a no-op at startup (no connection is opened); it only matters
# when a tool is actually invoked, which Glama's introspection never does.
ENV REVIT_MCP_HOST=local

ENTRYPOINT ["revit-model-mcp"]
