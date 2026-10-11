# Security questionnaire answers

Short answers for vendor security reviews in the style of CAIQ and SIG Lite. Each answer states a status and points to evidence. "Gap" means the control does not exist today. Details are in the [security whitepaper](security-whitepaper.md).

Model MCP is an open-source add-in and a local server. There is no hosted service, so questions about a provider's data center, tenants or staff do not apply. Controls that depend on the machine policy are described in the [policy reference](policy-reference.md).

## Data classification and flow

| Question | Answer | Status |
| --- | --- | --- |
| Does the vendor receive customer data? | No. The project operates no server that receives model data, logs or telemetry. | Met |
| Where does data go? | To the MCP client the customer chose. That client may send it to its own AI model provider. | Customer-controlled |
| Is there a data flow diagram? | Yes, in the [whitepaper](security-whitepaper.md#trust-boundaries-and-data-flow). | Met |
| Which data classes can tool results contain? | Model and document names, file paths, element parameters, geometry summaries, warnings, exported images and files. | Documented |
| Data residency? | Data stays on the customer's machines. The project stores nothing. | N/A |
| Subprocessors? | None. | N/A |
| Controller or processor statement? | The project is neither: it never receives personal or model data. The customer's MCP client and model provider are the processors. | Documented |

## Encryption

| Question | Answer | Status |
| --- | --- | --- |
| Encryption at rest | No encryption by the project. Files are plain text in the user profile. Rely on disk encryption such as BitLocker. | Gap, mitigated by platform |
| Encryption in transit | Named pipe and file channel are local. HTTP has no TLS; use loopback, an SSH tunnel or a TLS proxy. SSH uses OpenSSH. | Partial |
| Key management | No keys are managed. The HTTP token is random, per user, stored with a user-only ACL. | Documented |

## Identity and access

| Question | Answer | Status |
| --- | --- | --- |
| Authentication | Pipe: Windows user ACL. HTTP: bearer token with a nonce proof. SSH: customer's OpenSSH setup. No single sign-on. | Partial |
| Least privilege | The add-in runs as the Revit user and gains no rights. The per-user MSI needs no administrator rights. | Met |
| Administrator-enforced restrictions | Machine policy for read-only, tool deny and allow lists, code execution, HTTP and update mode. | Met |
| Separation of duties for risky actions | Confirmation token per risky call. A client can confirm on its own, so use read-only or policy for unattended use. | Partial |
| Remote and multi-user access | The pipe is per user; the HTTP token is per user. Remote use goes through SSH or an HTTP host that the customer configures. | Documented |

## Logging and monitoring

| Question | Answer | Status |
| --- | --- | --- |
| Are actions logged? | Yes: activity pane and log with command and client; add-in log; one named undo entry per change; audit file with SHA-256 for executed code. | Met |
| Are logs tamper-proof or forwarded? | No. No Event Log or SIEM sink. Local files owned by the user. | Gap |
| Log retention | Count-based: 14 add-in log files, 5 MiB activity log with one copy, 500 code audit files. Age-based retention through the `LogRetentionDays` policy value. | Partial |
| Is sensitive data in logs? | Logs can contain model names, paths and error text. Redaction needs the Revit-side setting or the `RedactPaths` policy value. | Documented |

## Vulnerability and change management

| Question | Answer | Status |
| --- | --- | --- |
| Secure development lifecycle | Public repository, pull request checks, CODEOWNERS, release-please, CodeQL, Scorecard. | Met |
| Dependency management | Lock files, Dependabot, SHA-pinned CI actions. No vulnerability scan gate and no SBOM yet. | Partial |
| Vulnerability disclosure | Private advisory form and email in the [security policy](../security-policy.md). No response-time commitment. | Partial |
| Patch process | New releases through GitHub, PyPI and WinGet. Per-machine installs are updated by the administrator; per-user installs can update themselves. | Documented |
| Penetration test, SOC 2, ISO 27001 | None. Open source: the code can be reviewed. | N/A |
| Threat model | The whitepaper lists trust boundaries and residual risks. | Documented |

## Supply chain

| Question | Answer | Status |
| --- | --- | --- |
| Are releases signed? | No. Authenticode signing through SignPath is prepared and not active. | Gap |
| Build provenance | GitHub build provenance attestations and `SHA256SUMS.txt`. | Met |
| SBOM | Not published yet. | Gap |
| Third-party components | Listed in the third-party notices; transitive Python packages are not listed. | Partial |

## Endpoint and network

| Question | Answer | Status |
| --- | --- | --- |
| Outbound connections | `api.github.com`, `github.com`, `release-assets.githubusercontent.com`, `pypi.org`, `files.pythonhosted.org`, all HTTPS, no model data. Full table in the [whitepaper](security-whitepaper.md#outbound-network). | Documented |
| Can all outbound traffic be disabled? | Yes: `UPDATECHECK=0` or `UpdateMode=off` for the add-in, `REVIT_MCP_NO_UPDATE_CHECK=1` for the server, and a pinned or wheelhouse install. | Met |
| Telemetry | None. | Met |
| Listening ports | None by default. HTTP is optional and binds to loopback. | Met |
| Application allow-listing | Binaries are unsigned: use path or hash rules. | Gap |
| Proxy support | System proxy for the add-in; standard proxy variables for the server and uv. | Documented |

## Business continuity and privacy

| Question | Answer | Status |
| --- | --- | --- |
| Backup and disaster recovery | N/A. A local tool with no service to recover. Models are backed up by the customer. | N/A |
| Incident response | No formal runbook. Report through the security policy. | Gap |
| Privacy contact | `sharafutdinov.di.dev@outlook.com`, see the [privacy policy](../privacy.md). | Met |
| Data deletion | Uninstall and remove the folders listed in [data retention](../privacy.md#data-retention). | Documented |
| Children or regulated data | Not targeted. The customer decides which models a client may read. | N/A |

## Model change controls

| Question | Answer | Status |
| --- | --- | --- |
| Can the tool change models? | Yes by default. Read-only mode and policy turn this off. | Documented |
| Can changes be undone? | Each change is one named undo entry, and `revit_undo_last` exists. Link removal and document-level operations (open, close, save, synchronize) have no undo entry. | Documented |
| Can arbitrary code run? | Yes through `revit_execute_code`, unsandboxed, with a confirmation token. Policy can disable it. | Documented |
