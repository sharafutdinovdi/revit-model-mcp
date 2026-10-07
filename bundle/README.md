# Claude Desktop bundle

The release asset `revit-model-mcp-<version>.mcpb` registers the Python MCP server in Claude Desktop and presents its connection and privacy settings.
The client requires [uv](https://docs.astral.sh/uv/getting-started/installation/) on PATH before opening the bundle.
Install the Revit add-in separately on the Windows workstation, start Revit and open a model.

The workstation host defaults to `local` for a Windows client under the Revit user's account.
macOS and Linux clients use `ssh:<alias>`, `http://host:port` or `https://host:port` to reach Windows.
Use a loopback tunnel or TLS proxy for remote HTTP access.
The optional HTTP bearer token field is sensitive and maps to `REVIT_MCP_TOKEN`.
See [transport setup](https://sharafutdinovdi.github.io/revit-model-mcp/transport/) for workstation configuration.

Path redaction defaults to enabled.
Actions run by default; check Read-only mode to disable them without hiding the action tools.
Redaction leaves model names, parameter values, errors and exported image `localPath` values visible.

## Privacy policy

Revit Model MCP returns requested model data to the selected MCP client.
The bundle enables response path redaction by default.
The [privacy policy](https://sharafutdinovdi.github.io/revit-model-mcp/privacy/) covers collection, storage, sharing, retention and contact information.

## Build

The manifest follows [MCPB 0.3](https://github.com/anthropics/mcpb/blob/main/MANIFEST.md).
Generate its tool names and descriptions from the server registry after changing tools:

```sh
cd server
uv run python ../build/bundle_manifest.py
```

The committed manifest lists every tool but omits `inputSchema` because MCPB rejects that field.
The binary command is the external `uvx` executable on PATH.
The bundle contains metadata and the icon; uv downloads the Python package and dependencies at first launch.
The server accepts boolean environment values directly, with `1` and `0` as the canonical values.
User settings pass through environment variables.
The manifest declares a Claude Desktop minimum of `0.10.0`; Linux compatibility describes the server client platform, not availability of Claude Desktop for Linux.

From the repository root, with Node.js 22 and npm installed:

```sh
npx --yes @anthropic-ai/mcpb@2.1.2 validate bundle/manifest.json
npx --yes @anthropic-ai/mcpb@2.1.2 pack bundle /tmp/revit-model-mcp.mcpb
unzip -l /tmp/revit-model-mcp.mcpb
```

CI validates and packs the `0.0.0` placeholder and uploads the `bundle` artifact.
That artifact validates packaging and is not installable from PyPI.
CI also checks tool coverage, boolean forwarding and host/token forwarding with the MCPB configuration resolver.
The release workflow replaces both the manifest version and the package pin with the tag version.
Prerelease bundles reference the release wheel URL because prereleases are not published to PyPI.
Stable bundles require the corresponding PyPI publishing job to finish before first launch.
The workflow includes the bundle in `SHA256SUMS.txt` and attests all staged release assets.
For stable releases, the Smithery job copies the released desktop bundle into a separate ZIP archive with full tool input schemas, then publishes it as `sharafutdinovdi/revit-model-mcp` after the GitHub Release is published.
The Smithery archive is built with `build/bundle_manifest.py --smithery <output.mcpb> --from <desktop.mcpb>`.
Add the `SMITHERY_API_KEY` repository secret to enable publishing; an empty secret produces a notice and skips it.

The committed icon is the product mark on a navy tile. `build/render_brand_assets.py` renders it, together with the other brand images, from `docs/assets`:

```sh
uv run build/render_brand_assets.py
```

## Verify downloads

After downloading a release bundle:

```sh
gh attestation verify revit-model-mcp-<version>.mcpb --owner sharafutdinovdi
```

Confirm that the verified repository is `sharafutdinovdi/revit-model-mcp` and the signer workflow is `.github/workflows/release.yml`.
The attestation covers the bundle file, not dependencies downloaded by uv at runtime.
This is GitHub build provenance, not an MCPB certificate signature.
See [download verification](https://sharafutdinovdi.github.io/revit-model-mcp/security/#verify-downloads) for provenance boundaries and MSI verification.
