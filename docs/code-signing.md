# Code signing policy

Status: releases are not signed yet. This policy takes effect with the first signed release, and the release notes will say so.

## Signing service

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What is signed

Each release signs these files with Authenticode:

- the two MSI installers
- `RevitModelMcp.dll` and `RevitModelMcp.Core.dll`
- `RevitModelMcp.Updater.exe`
- `RevitModelMcp.BatchSupervisor.exe`

The signed files are the same ones inside the MSIs, the per-year add-in ZIP files and the managed Revit bundle.

Third-party libraries that ship in the packages are not signed by this project. Autodesk Revit assemblies are never included in any release asset.
The Python package, the MCPB bundle and `SHA256SUMS.txt` are not Authenticode-signed; use the checks in [verify downloads](#verify-downloads).

## Team roles

| Role | Who | Responsibility |
| --- | --- | --- |
| Author | [Dinar Sharafutdinov](https://github.com/sharafutdinovdi) | Writes and commits the source code. |
| Reviewer | [Dinar Sharafutdinov](https://github.com/sharafutdinovdi) | Reviews every pull request from other contributors before it is merged. |
| Approver | [Dinar Sharafutdinov](https://github.com/sharafutdinovdi) | Approves each signing request. |

The maintainer uses multi-factor authentication for GitHub and for SignPath.
Contributors without commit rights open pull requests, and the reviewer merges them only after review and passing checks.
Dependency updates and release pull requests from automation run the same checks.

## How a release is signed

1. The maintainer pushes a release tag.
2. The [release workflow](https://github.com/sharafutdinovdi/revit-model-mcp/blob/main/.github/workflows/release.yml) builds the add-in, the installers and the bundle on GitHub-hosted runners.
3. The workflow uploads the unsigned installers and archives as a workflow artifact and submits them to SignPath. SignPath checks that the build ran in this repository on GitHub.
4. The approver reviews the signing request and approves it. Every release needs manual approval.
5. SignPath signs the files listed above. The artifact configuration is kept in [`.signpath/artifact-configuration.xml`](https://github.com/sharafutdinovdi/revit-model-mcp/blob/main/.signpath/artifact-configuration.xml). It restricts the product name to Model MCP and the product version to the release build.
6. The workflow downloads the signed files, checks every signature, computes `SHA256SUMS.txt`, attests the assets and publishes the release.

## Privacy policy

See the [privacy policy](privacy.md).
Signing adds no software behavior: the add-in sends no information to other networked systems beyond the update checks described there, and you can turn those off.

## Verify downloads

Every release publishes `SHA256SUMS.txt` and a build provenance attestation for its assets.
See [Verify downloads](security.md#verify-downloads) for the commands.
`install.ps1` and the add-in updater check `SHA256SUMS.txt` before they install anything.

## Revit's unsigned add-in prompt

Revit asks whether to load an unsigned add-in the first time it loads the DLL, and again whenever the DLL content or its path changes.
Choose **Always Load** and close Revit normally; Revit keeps the decision only after a graceful exit.
Each [automatic update](updates.md) therefore causes one prompt at the next Revit start until releases are signed.

Organizations that deploy their own code-signing certificate can sign the installed DLLs with `install.ps1 -SignThumbprint <thumbprint>`; see [transport](transport.md).

## Reporting a problem

If you think a signed file violates this policy, report it through the process in [SECURITY.md](https://github.com/sharafutdinovdi/revit-model-mcp/blob/main/SECURITY.md).
