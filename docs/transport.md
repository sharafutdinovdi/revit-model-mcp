# Transport

`REVIT_MCP_HOST` selects `local`, `ssh:<alias>`, `http://host:port` or `https://host:port`.
`--host` overrides it.
HTTP connects directly to the add-in and requires no SSH server or remote file transfer.
The MCP client still communicates with the Python server over stdio.

## HTTP configuration

On first startup the add-in creates `%LOCALAPPDATA%\RevitModelMcp\settings.json`:

```json
{
  "httpEnabled": true,
  "httpBind": "127.0.0.1",
  "httpPort": 53110,
  "token": "<generated 32-byte base64url token>"
}
```

The token persists across restarts.
The add-in creates and restricts the file with a protected NTFS ACL granting full control only to the current Windows user.
Keep the token private and transfer it to the client's secret store through a trusted channel.
The add-in never logs it.
Do not put it in a URL, repository or shared shell history.

Revit environment variables override settings at startup: `REVIT_MCP_HTTP_ENABLED=0|1`, `REVIT_MCP_HTTP_BIND`, `REVIT_MCP_HTTP_PORT` and `REVIT_MCP_TOKEN`.
Overrides are not written back to the settings file.
Set `httpEnabled=false` or `REVIT_MCP_HTTP_ENABLED=0` to turn the listener off entirely.
Restart Revit after changing listener settings.
Invalid settings disable HTTP and leave the file channel available.

The client reads `REVIT_MCP_TOKEN`; `--token` overrides it.
Prefer the environment populated by a secret store.
With the token already available in the client environment:

```sh
export REVIT_MCP_HOST=http://127.0.0.1:53110
uv run --directory server revit-model-mcp
```

HTTP has no built-in TLS.
Use an SSH tunnel, Tailscale or a TLS reverse proxy; the client validates HTTPS certificates.
Redirects are rejected to prevent forwarding the bearer token to another endpoint.
`/health` is unauthenticated and reveals the Revit version, active document name, process ID and read-only state.
All other routes require `Authorization: Bearer <token>`.

| Request | Result |
|---|---|
| `GET /health` | `ok`, `revitVersion`, `documentName`, `processId`, `readOnly` |
| `POST /jobs?timeout=120` | File-channel job JSON in the body; final response JSON with HTTP 200 |
| `GET /jobs/{id}` | HTTP 202 while pending; final response with HTTP 200; HTTP 404 after expiry |
| `GET /views/{name}/image?pixel=1600` | PNG bytes from the same view exporter used by `revit_export_view` |

POST waits default to 120 seconds and accept 0-600 seconds.
HTTP 202 contains `jobId`; it means the accepted job is still queued or executing.
A timeout or client disconnect does not cancel a job.
Results expire ten minutes after completion.
Do not resubmit an action after a timeout without checking its result and the model.
The Python client submits once with `timeout=0`, then polls within `timeout_seconds`.
`pickup_timeout_seconds` applies only to file transports.

HTTP 401 means the token is missing or invalid.
HTTP 409 means another HTTP or file job owns the channel.
HTTP 403 rejects action jobs when the workstation `allow-write` gate is absent.
MCP action tools also require `REVIT_MCP_ALLOW_WRITE=1` in the Python process.
HTTP jobs use the existing ExternalEvent and share the file channel's single-job rule.
Jobs are limited to 1 MiB.

View names must be URL-encoded; `pixel` accepts 1-4000.
Image requests can return HTTP 202 with `jobId` after 120 seconds.
Poll `/jobs/{id}`, then append `jobId={id}` to the image URL to fetch that export without executing it again.
The optional `document` query must match the job's `targetDocument`.
The Python exporter follows this path and preserves response metadata and the local PNG path.
HTTP image artifacts fetched this way expire with the result.

Each HTTP endpoint belongs to one Revit process.
For several instances, configure a distinct port in each process environment before launch.
An occupied port disables HTTP for the later instance and produces a log message.
`revit_list_instances` reports the connected endpoint in HTTP mode.
`targetDocument` and `targetProcessId` are checked in the Revit API context before execution.

## Remote setups

For a corporate PC without administrator rights, use option 2 with IT-provisioned Tailscale or option 3 with existing SSH access.
Never expose the endpoint on the office LAN.
If neither service is available, IT must provision a route first; this add-in cannot bypass that requirement.

### 1. Same LAN

Use this only on a trusted network where the workstation owner permits direct access.
Set `httpBind` to `0.0.0.0` explicitly, or set `REVIT_MCP_HTTP_BIND=0.0.0.0` before starting Revit.
Run once in an elevated Windows command prompt, replacing `<user>` with the account running Revit, such as `DOMAIN\name`:

```bat
netsh http add urlacl url=http://+:53110/ user=<user>
netsh advfirewall firewall add rule name="Revit Model MCP" dir=in action=allow protocol=TCP localport=53110
```

On the Mac, with `REVIT_MCP_TOKEN` supplied by the secret store:

```sh
export REVIT_MCP_HOST=http://192.168.1.69:53110
uv run --directory server revit-model-mcp
```

The bearer token and model data are plaintext on this route.
Prefer a tunnel whenever possible.
The listener logs the required URL reservation command on access denied.
Windows may also require an explicit loopback reservation under a restricted account.
For a specific bind address, use that address in the URL ACL instead of `+`.
See [Microsoft HttpListener prefix guidance](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener?view=netframework-4.8.1).

### 2. Different networks: Tailscale

Install Tailscale on both machines and join the same permitted tailnet.
Windows installation requires local administrator access once; IT must also provision the URL ACL and any required firewall permission for the Tailscale interface.
Routine use then runs under the Revit user's account.
Bind to the workstation's Tailscale IPv4 address instead of `0.0.0.0` when possible.
Use that same address in the URL ACL and restrict firewall access to the permitted Tailscale peer.
See [Tailscale installation for Windows](https://tailscale.com/docs/install/windows).

```sh
export REVIT_MCP_HOST=http://100.101.102.103:53110
uv run --directory server revit-model-mcp
```

For a Mac without administrator rights, standalone `tailscaled` and `tailscale` binaries can run in userspace mode with an HTTP proxy and user-owned state.
With those binaries available in PATH:

```sh
mkdir -p "$HOME/.local/state/tailscale-revit"
tailscaled --tun=userspace-networking \
  --state="$HOME/.local/state/tailscale-revit/state" \
  --socket="$HOME/.local/state/tailscale-revit/socket" \
  --outbound-http-proxy-listen=127.0.0.1:1055
```

In another terminal:

```sh
tailscale --socket="$HOME/.local/state/tailscale-revit/socket" up
export http_proxy=http://127.0.0.1:1055
export https_proxy=http://127.0.0.1:1055
export REVIT_MCP_HOST=http://100.101.102.103:53110
uv run --directory server revit-model-mcp
```

The Python HTTP transport honors these standard proxy environment variables.
Userspace mode does not create a system VPN interface; the proxy supplies the route.
See [Tailscale userspace networking](https://tailscale.com/docs/concepts/userspace-networking).

### 3. Existing SSH: loopback port forward

This option keeps the workstation listener on `127.0.0.1` and is the safest setup when sshd is already available.
It opens no new office-LAN port.

```sh
ssh -N -L 53110:127.0.0.1:53110 user@host
```

In another terminal, with the bearer token in the environment:

```sh
export REVIT_MCP_HOST=http://127.0.0.1:53110
uv run --directory server revit-model-mcp
```

### 4. Legacy SSH file channel

The existing transport remains available without HTTP:

```sh
export REVIT_MCP_HOST=ssh:revit-host
uv run --directory server revit-model-mcp
```

Set `httpEnabled=false` on the workstation if only the file channel is needed.

## File channel

The default directory is `%LOCALAPPDATA%\RevitModelMcp` on the Windows account running Revit.
Set `REVIT_MCP_CHANNEL_DIR` to an absolute Windows path to override it.
The server and Revit must use the same directory.
The Revit environment must contain the override before Revit starts.

| File | Role |
|---|---|
| `mcp_<uuid>.tmp` | JSON job before publication |
| `trigger.txt` | Published job awaiting pickup |
| `response_<timestamp>_<command>.json` | Add-in response |
| `view_<timestamp>_<id>.png` | Exported view before download |
| `instance_<processId>.json` | Instance heartbeat |

The file transport operates independently of the optional HTTP listener.
Revit API work runs through ExternalEvent.
Pending files remain on disk if the server stops.
There is no automatic cancellation of a published job.
The channel relies on Windows file permissions.

## Local host

`REVIT_MCP_HOST=local` is the default.
The server invokes `powershell.exe -NoProfile -NonInteractive -EncodedCommand` on Windows.
The process checks Revit, publishes jobs and reads responses under the current Windows account.
This mode requires PowerShell and a running Revit instance with the add-in loaded.
macOS and Linux clients use HTTP or SSH to reach Windows.

## SSH host

`REVIT_MCP_HOST=ssh:<alias>` selects a host in the client's SSH configuration.
`--host ssh:<alias>` provides the same setting on the command line.
The client invokes `ssh` with batch mode and a 45-second connection timeout.
The remote command runs Windows PowerShell with a UTF-16LE base64-encoded script.
Host aliases are validated and PowerShell path literals escape single quotes.
SSH credentials and routing belong to the user's SSH configuration.

Responses and exported PNG files are transferred as base64 in the PowerShell result.
The server decodes the image into `save_to` or a new temporary directory.
An existing destination file produces an error.
The MCP result contains the image path and metadata without base64.

Each SSH invocation includes `-o ControlMaster=auto -o ControlPath=<dir>/mux-%C -o ControlPersist=600` by default.
Commands for the same connection reuse the master instead of opening a new TCP connection for each PowerShell call.
The master persists for 600 idle seconds.
`<dir>` is `$XDG_RUNTIME_DIR` when nonempty, otherwise `/tmp/revit-model-mcp-<uid>/`.
The directory is created or restricted to mode `0700` on macOS and Linux.
Keep the directory path short; `%C` provides a hashed connection identifier within the Unix socket path limit.

`REVIT_MCP_SSH_MUX=0` disables these built-in multiplexing options.
Use it on clients without multiplexing support, such as native Windows OpenSSH.
`REVIT_MCP_SSH_OPTIONS` appends shell-quoted arguments after built-in options and before the host, for example `-o ServerAliveInterval=30 -p 2222`.
OpenSSH uses the first value for an option.
A custom socket path or lifetime requires `REVIT_MCP_SSH_MUX=0` plus all three `Control*` options in `REVIT_MCP_SSH_OPTIONS`.
Local mode ignores both variables and creates no multiplexing directory.

SSH command starts retain a limit of five per rolling 30 seconds within one host object.
Polling waits ten seconds between attempts.
Job preparation and final response retrieval each use one command.
Transient failures during polling can be retried within the remaining timeout.
Local mode uses the same file operations and polling without the SSH connection limiter.

## Optional activation

Set `REVIT_MCP_ACTIVATE_TASK` to the name of an existing Windows scheduled task that activates Revit.
After 60 seconds without pickup the next check can invoke that task once.
Activation only occurs if `trigger.txt` still exists.
The server checks the task result and reports activation failure separately.
The task is optional and is never created by the server.
The default pickup timeout is 300 seconds, followed by a separate 120-second response timeout.

## Path redaction

`REVIT_MCP_REDACT_PATHS=1` or `--redact-paths` strips directories from response `documentPath` fields.
This applies to responder metadata and instance listings.
Image `localPath` remains available to the MCP client.
The option does not sanitize channel files or arbitrary strings in model data and errors.
See [server configuration](../server/README.md#configuration).
