import asyncio
import json
import shutil
import struct
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
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
                        "processId": state.get("processId", 42),
                        "startedUtc": state.get("startedUtc", "2026-09-16T00:00:00Z"),
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
    health = asyncio.run(host.health())
    assert health["readOnly"] is True
    assert health["startedUtc"] == "2026-09-16T00:00:00Z"
    instances = asyncio.run(host.list_revit_instances("mod"))
    assert instances[0]["processId"] == 42
    assert instances[0]["pluginResponding"] is True
    assert asyncio.run(host.list_revit_instances("other")) == []
    assert all(request[2] is None for request in state["requests"])


def test_job_round_trip(endpoint):
    host, state = endpoint
    result = asyncio.run(RevitReadChannel(host).execute(ReadJob.ping()))
    assert result["data"] == "pong"
    assert state["payload"]["command"] == "ping"
    assert len(state["payload"]["correlationId"]) == 32
    assert state["requests"] == [
        ("GET", "/health", None),
        ("GET", "/health", None),
        ("POST", "/jobs?timeout=0", "Bearer test-token"),
    ]
    assert state["payload"]["targetProcessId"] == 42


@pytest.mark.parametrize(
    "status, message", [(401, "bearer token"), (409, "busy"), (403, "allow-write")]
)
def test_http_errors(endpoint, status, message):
    host, state = endpoint
    state["status"] = status
    with pytest.raises(RevitChannelError, match=message):
        asyncio.run(RevitReadChannel(host).execute(ReadJob.ping()))
    assert len(state["requests"]) == 3


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
    assert len(state["requests"]) == 4
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
    assert state["requests"] == [("GET", "/health", None), ("GET", "/health", None)]


def test_redirect_does_not_forward_token(endpoint):
    host, state = endpoint
    state.update(status=302, redirect=host.host + "/health")
    with pytest.raises(RevitChannelError, match="HTTP 302"):
        asyncio.run(RevitReadChannel(host).execute(ReadJob.ping()))
    assert len(state["requests"]) == 3


REPOSITORY = Path(__file__).resolve().parents[2]


def test_addin_advertises_bound_http_endpoint_identity():
    source = (REPOSITORY / "src/RevitModelMcp.Addin/Control/HttpChannel.cs").read_text()
    application = (REPOSITORY / "src/RevitModelMcp.Addin/Application.cs").read_text()
    assert "public int? BoundPort { get; private set; }" in source
    start = source.split("public void Start()", 1)[1].split("private async Task", 1)[0]
    disabled, enabled = start.split("_listener.Start();", 1)
    assert "if (!_settings.HttpEnabled)" in disabled
    assert "return;" in disabled
    assert "BoundPort =" not in disabled
    assert enabled.lstrip().startswith("BoundPort = _settings.HttpPort;")
    assert source.count("BoundPort =") == 1
    after_start = application.split("_httpChannel.Start();", 1)[1]
    assert after_start.lstrip().startswith(
        "_instanceHeartbeat.UpdateHttpPort(_httpChannel.BoundPort);"
    )
    assert "HttpPort = _httpPort" in application
    health = source.split('path == "/health"', 1)[1].split("return;", 1)[0]
    assert '["startedUtc"] = SnapshotFileWriter.StartedUtc' in health
    assert "StartedUtc = Output.SnapshotFileWriter.StartedUtc" in application


def run_powershell(script):
    executable = shutil.which("pwsh")
    if not executable:
        pytest.skip("PowerShell 7 is required for add-in and installer regression checks")
    result = subprocess.run(
        [executable, "-NoProfile", "-NonInteractive", "-Command", script],
        cwd=REPOSITORY,
        capture_output=True,
        text=True,
    )
    assert result.returncode == 0, result.stdout + result.stderr


def test_addin_http_settings_default_and_opt_in():
    run_powershell(
        r"""
$ErrorActionPreference = 'Stop'
$source = Get-Content src/RevitModelMcp.Addin/Control/HttpChannel.cs -Raw
$imports = @"
#nullable enable
using System; using System.IO; using System.Linq; using System.Collections.Generic;
using System.Runtime.Serialization; using System.Runtime.Serialization.Json;
using System.Security.AccessControl; using System.Security.Cryptography;
using System.Security.Principal; using System.Net; using System.Text;
"@
$source = $imports + $source.Substring($source.IndexOf('[DataContract]'))
Add-Type -TypeDefinition $source.Replace('internal sealed record', 'public sealed record')
if ([HttpSettings]::new().HttpEnabled) { throw 'Constructor enables HTTP by default' }
$serializer = [Runtime.Serialization.Json.DataContractJsonSerializer]::new([HttpSettings])
foreach ($json in '{}', '{"httpEnabled":false}', '{"httpEnabled":true}') {
    $stream = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($json))
    try { $settings = $serializer.ReadObject($stream) } finally { $stream.Dispose() }
    if ($settings.HttpEnabled -ne ($json -eq '{"httpEnabled":true}')) {
        throw "Wrong stored HTTP value for $json"
    }
    if ($settings.HttpPort -ne 53110 -or $settings.HttpBind -ne '127.0.0.1') {
        throw 'Bind or port default changed'
    }
}
# Load applies protected Windows file ACLs; exercise it on Windows only.
if ($env:OS -eq 'Windows_NT') {
    $directory = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
    try {
        foreach ($name in 'HTTP_ENABLED', 'HTTP_BIND', 'HTTP_PORT', 'TOKEN') {
            [Environment]::SetEnvironmentVariable("REVIT_MCP_$name", $null)
        }
        if ([HttpSettings]::Load($directory).HttpEnabled) { throw 'First load enabled HTTP' }
        $env:REVIT_MCP_HTTP_ENABLED = '1'
        $env:REVIT_MCP_HTTP_PORT = '53112'
        $env:REVIT_MCP_HTTP_BIND = '127.0.0.2'
        $env:REVIT_MCP_TOKEN = 'test-override-token'
        $settings = [HttpSettings]::Load($directory)
        if (!$settings.HttpEnabled -or $settings.HttpPort -ne 53112 -or
            $settings.HttpBind -ne '127.0.0.2' -or $settings.Token -ne 'test-override-token') {
            throw 'Environment overrides were not applied'
        }
        $path = Join-Path $directory 'settings.json'
        $stored = Get-Content $path -Raw | ConvertFrom-Json
        if ($stored.httpEnabled) { throw 'Environment override persisted' }
        $stored.httpEnabled = $true
        $stored | ConvertTo-Json | Set-Content $path
        $env:REVIT_MCP_HTTP_ENABLED = $null
        if (![HttpSettings]::Load($directory).HttpEnabled) { throw 'Stored opt-in lost' }
        $env:REVIT_MCP_HTTP_ENABLED = '0'
        if ([HttpSettings]::Load($directory).HttpEnabled) { throw 'Disable override ignored' }
    }
    finally { Remove-Item $directory -Recurse -Force }
}
"""
    )


def test_script_http_url_acl_opt_in_and_ownership():
    run_powershell(
        r"""
$ErrorActionPreference = 'Stop'
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PWD 'install.ps1'), [ref]$null, [ref]$null)
$ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst]
}, $false) | ForEach-Object { Invoke-Expression $_.Extent.Text }
function Get-HttpIdentity { return @{ Elevated = $true; Account = 'test-user' } }
function Join-Path($Path, $ChildPath) {
    if ($ChildPath -eq 'System32\netsh.exe') { return 'Invoke-TestNetsh' }
    Microsoft.PowerShell.Management\Join-Path $Path $ChildPath
}
$calls = [Collections.Generic.List[string]]::new()
$script:exists = $false
$script:failAdd = $false
function Invoke-TestNetsh {
    $calls.Add(($args -join ' '))
    $global:LASTEXITCODE = 0
    switch ($args[1]) {
        'show' { if (!$script:exists) { $global:LASTEXITCODE = 1 } }
        'add' {
            if ($script:failAdd) { $global:LASTEXITCODE = 1 }
            else { $script:exists = $true }
        }
        'delete' { $script:exists = $false }
    }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
$env:APPDATA = $root
$env:SystemRoot = $root
$addins = Join-Path $root 'Autodesk\Revit\Addins'
$marker = Join-Path $addins 'RevitModelMcp-http-urlacl.txt'
New-Item $addins -ItemType Directory -Force | Out-Null
try {
    $EnableHttp = $false; $Uninstall = $false; $HttpBind = '127.0.0.1'; $HttpPort = 53112
    Update-HttpUrlAcl
    $Uninstall = $true
    Update-HttpUrlAcl
    if ($calls.Count) { throw 'Default install/uninstall invoked netsh' }
    $Uninstall = $false; $EnableHttp = $true; $script:exists = $true
    Update-HttpUrlAcl
    if (Test-Path $marker) { throw 'Claimed an existing reservation' }
    $script:exists = $false; $script:failAdd = $true
    Update-HttpUrlAcl
    if (Test-Path $marker) { throw 'Claimed a failed reservation' }
    $script:failAdd = $false
    Update-HttpUrlAcl
    if ((Get-Content $marker -Raw).Trim() -ne 'http://127.0.0.1:53112/') {
        throw 'Wrong ownership prefix'
    }
    $year = Join-Path $addins '2026'
    New-Item $year -ItemType Directory | Out-Null
    $manifest = Join-Path $year 'RevitModelMcp.addin'
    Set-Content $manifest 'installed'
    $Uninstall = $true; $EnableHttp = $false; $HttpPort = 53110
    $before = $calls.Count
    Update-HttpUrlAcl
    if ($calls.Count -ne $before) { throw 'Deleted ACL while another year is installed' }
    Remove-Item $manifest
    Update-HttpUrlAcl
    if ($calls[$calls.Count - 1] -ne 'http delete urlacl url=http://127.0.0.1:53112/') {
        throw 'Did not remove exactly the owned prefix'
    }
    if (Test-Path $marker) { throw 'Ownership record not removed' }
    $Uninstall = $false; $EnableHttp = $true; $HttpBind = '0.0.0.0'
    Update-HttpUrlAcl
    if ((Get-Content $marker -Raw).Trim() -ne 'http://+:53110/') {
        throw 'Wildcard bind was not mapped to the listener prefix'
    }
}
finally { Remove-Item $root -Recurse -Force }
"""
    )


def test_msi_http_url_acl_requires_opt_in_and_owned_prefix():
    source = (REPOSITORY / "build/install/Installer.cs").read_text()
    assert 'new Property("HTTP_ENABLED", "0")' in source
    register = source.split('new Id("RegisterHttpUrlAcl")', 1)[1].split(
        'new Id("RemoveHttpUrlAcl")', 1
    )[0]
    assert 'HTTP_ENABLED=\\"1\\" AND NOT Installed' in register
    assert "Return.check" in register
    assert "Step.WriteRegistryValues" in register
    assert "url=[HTTP_OWNED_PREFIX]" in register
    remove = source.split('new Id("RemoveHttpUrlAcl")', 1)[1]
    assert 'REMOVE=\\"ALL\\" AND HTTP_OWNED_PREFIX' in remove
    assert "http delete urlacl url=[HTTP_OWNED_PREFIX]" in remove
    assert 'new RegValueProperty("HTTP_OWNED_PREFIX"' in source
    assert "HttpUrlAcl\\[ProductCode]" in source


@pytest.mark.parametrize("changed", [{"processId": 84}, {"startedUtc": "2026-09-16T01:00:00Z"}])
def test_endpoint_identity_change_rejected_before_post(endpoint, changed):
    host, state = endpoint

    async def submit():
        selected, job = await host.select_job(ReadJob.ping())
        state.update(changed)
        await selected.prepare_job("unused.tmp", job.to_json(), job.command)

    with pytest.raises(RevitChannelError, match="identity changed"):
        asyncio.run(submit())
    assert all(method == "GET" for method, _, _ in state["requests"])


def test_http_action_keeps_inactive_document_address(endpoint):
    host, state = endpoint
    job = ReadJob("select", {"command": "select", "targetDocument": "Inactive", "elementIds": [1]})
    asyncio.run(RevitReadChannel(host).execute(job))
    assert state["payload"]["targetDocument"] == "Inactive"
    assert state["payload"]["targetProcessId"] == 42
