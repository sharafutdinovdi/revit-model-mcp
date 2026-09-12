import asyncio
import json
import struct
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlsplit

import pytest

from revit_model_mcp.http_host import HttpHost
from revit_model_mcp.revit_channel import (
    ReadJob,
    ResponseTimeoutError,
    RevitChannelError,
    RevitReadChannel,
)
from revit_model_mcp.server import create_host

PNG = b"\x89PNG\r\n\x1a\n" + b"\0\0\0\rIHDR" + struct.pack("!II", 1600, 900)


@pytest.fixture
def endpoint():
    state = {"status": 200, "polls": 0, "requests": [], "payload": None, "forever": False}

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def reply(self, status, body, content_type="application/json"):
            if not isinstance(body, bytes):
                body = json.dumps(body).encode()
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("X-Revit-Job-Id", "job-1")
            self.end_headers()
            self.wfile.write(body)

        def handle_request(self):
            route = urlsplit(self.path)
            state["requests"].append((self.command, self.path, self.headers.get("Authorization")))
            if route.path == "/health":
                return self.reply(
                    200,
                    {
                        "ok": True,
                        "revitVersion": "2026",
                        "documentName": "Model",
                        "processId": 42,
                        "readOnly": True,
                    },
                )
            if self.headers.get("Authorization") != "Bearer test-token":
                return self.reply(401, {"error": "unauthorized"})
            if state["status"] == 302:
                self.send_response(302)
                self.send_header("Location", state["redirect"])
                self.end_headers()
                return
            if state["status"] not in (200, 202):
                return self.reply(state["status"], {"error": "error"})
            if self.command == "POST":
                state["payload"] = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if state["status"] == 202:
                    return self.reply(202, {"jobId": "job-1"})
            if route.path.startswith("/jobs/"):
                state["polls"] += 1
                if state["polls"] == 1 or state["forever"]:
                    return self.reply(202, {"jobId": "job-1"})
            if route.path.startswith("/views/"):
                assert unquote(route.path) == "/views/Plan 東京 Δ / A #1/image"
                assert parse_qs(route.query) == {
                    "pixel": ["1600"],
                    "jobId": ["job-1"],
                    "document": ["Model"],
                }
                return self.reply(200, PNG, "image/png")
            command = state["payload"]["command"]
            data = (
                {"fileName": "view.png", "width": 1600, "height": 900}
                if command == "export-view"
                else "pong"
            )
            return self.reply(200, {"command": command, "success": True, "data": data})

        do_GET = handle_request
        do_POST = handle_request

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    try:
        yield HttpHost(f"http://127.0.0.1:{server.server_port}", "test-token"), state
    finally:
        server.shutdown()
        server.server_close()
        worker.join()


def test_health_and_instance_discovery(endpoint):
    host, state = endpoint
    assert asyncio.run(host.health())["readOnly"] is True
    instances = asyncio.run(host.list_revit_instances("mod"))
    assert instances[0]["processId"] == 42
    assert instances[0]["pluginResponding"] is True
    assert asyncio.run(host.list_revit_instances("other")) == []
    assert all(request[2] is None for request in state["requests"])


def test_job_round_trip(endpoint):
    host, state = endpoint
    result = asyncio.run(RevitReadChannel(host).execute(ReadJob.ping()))
    assert result["data"] == "pong"
    assert state["payload"] == {"command": "ping"}
    assert state["requests"] == [("POST", "/jobs?timeout=0", "Bearer test-token")]


@pytest.mark.parametrize(
    "status, message", [(401, "bearer token"), (409, "busy"), (403, "allow-write")]
)
def test_http_errors(endpoint, status, message):
    host, state = endpoint
    state["status"] = status
    with pytest.raises(RevitChannelError, match=message):
        asyncio.run(RevitReadChannel(host).execute(ReadJob.ping()))
    assert len(state["requests"]) == 1


def test_wrong_token(endpoint):
    host, _ = endpoint
    host.token = "wrong"
    with pytest.raises(RevitChannelError, match="REVIT_MCP_TOKEN"):
        asyncio.run(RevitReadChannel(host).execute(ReadJob.ping()))


def test_pending_job_polls_without_resubmitting(endpoint):
    host, state = endpoint
    state["status"] = 202
    result = asyncio.run(RevitReadChannel(host).execute(ReadJob.ping()))
    assert result["data"] == "pong"
    assert state["polls"] == 2
    assert sum(method == "POST" for method, _, _ in state["requests"]) == 1


def test_timeout_keeps_late_job_id(endpoint):
    host, state = endpoint
    state.update(status=202, forever=True)
    with pytest.raises(ResponseTimeoutError, match="/jobs/job-1"):
        asyncio.run(RevitReadChannel(host).execute(ReadJob.ping(), timeout_seconds=0.1))
    assert sum(method == "POST" for method, _, _ in state["requests"]) == 1


def test_image_download_round_trips_non_ascii_mixed_scripts_and_preserves_metadata(
    endpoint, tmp_path
):
    host, state = endpoint
    target = tmp_path / "image.png"
    job = ReadJob.export_view("Plan 東京 Δ / A #1", save_to=str(target)).for_document("Model")
    result = asyncio.run(RevitReadChannel(host).execute(job))
    assert target.read_bytes() == PNG
    assert result["data"]["width"] == 1600
    assert result["data"]["localPath"] == str(target)
    assert len(state["requests"]) == 2
    with pytest.raises(RevitChannelError, match="already exists"):
        asyncio.run(RevitReadChannel(host).execute(job))
    assert target.read_bytes() == PNG


def test_connection_refused():
    server = ThreadingHTTPServer(("127.0.0.1", 0), BaseHTTPRequestHandler)
    port = server.server_port
    server.server_close()
    with pytest.raises(
        RevitChannelError,
        match="Revit endpoint not reachable at .*is Revit running with the add-in",
    ):
        asyncio.run(HttpHost(f"http://127.0.0.1:{port}").health())


def test_host_selection_and_token_override(monkeypatch):
    monkeypatch.setenv("REVIT_MCP_TOKEN", "environment-token")
    assert create_host("http://127.0.0.1:53110").token == "environment-token"
    assert create_host("https://example.com/revit", "explicit-token").token == "explicit-token"
    assert create_host("local").local is True
    assert create_host("ssh:revit-host").host == "revit-host"


@pytest.mark.parametrize(
    "url", ["http://user:secret@host", "http://host?token=secret", "http://host#secret"]
)
def test_credentials_are_not_allowed_in_urls(url):
    with pytest.raises(ValueError, match="without credentials"):
        HttpHost(url)


def test_missing_token_does_not_submit(endpoint):
    host, state = endpoint
    host.token = ""
    with pytest.raises(RevitChannelError, match="Set REVIT_MCP_TOKEN"):
        asyncio.run(RevitReadChannel(host).execute(ReadJob.ping()))
    assert state["requests"] == []


def test_redirect_does_not_forward_token(endpoint):
    host, state = endpoint
    state.update(status=302, redirect=host.host + "/health")
    with pytest.raises(RevitChannelError, match="HTTP 302"):
        asyncio.run(RevitReadChannel(host).execute(ReadJob.ping()))
    assert len(state["requests"]) == 1
