# Code signing

Release builds are not code-signed.
The MSI installers, the add-in assemblies and the updater are built by the release workflow in this repository and published without an Authenticode signature.

## Verify downloads

Every release publishes `SHA256SUMS.txt` and a build provenance attestation for its assets.
See [Verify downloads](security.md#verify-downloads) for the commands.
`install.ps1` and the add-in updater check `SHA256SUMS.txt` before they install anything.

## Revit's unsigned add-in prompt

Revit asks whether to load an unsigned add-in the first time it loads the DLL, and again whenever the DLL content or its path changes.
Choose **Always Load** and close Revit normally; Revit keeps the decision only after a graceful exit.
Each [automatic update](updates.md) therefore causes one prompt at the next Revit start.

Organizations that deploy their own code-signing certificate can sign the installed DLLs with `install.ps1 -SignThumbprint <thumbprint>`; see [transport](transport.md).

## Plans

I am looking for another certificate for open source builds; this page will list what is signed and how once that is in place.
