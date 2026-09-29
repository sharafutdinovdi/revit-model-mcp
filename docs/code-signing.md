# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

Signing covers the Windows release artifacts built by the release workflow in this repository: the MSI installers and the add-in assemblies.
Signing is being set up; releases published before it are unsigned.

## What is signed

Only artifacts built from this repository's source code by GitHub Actions are signed.
Third-party libraries shipped inside the installers keep their own signatures or stay unsigned.
Revit API assemblies are never shipped.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [sharafutdinovdi](https://github.com/sharafutdinovdi) |
| Approvers | [sharafutdinovdi](https://github.com/sharafutdinovdi) |

Every pull request from a contributor outside the team is reviewed by a team member before merge.
Every signing request is approved manually by an approver.
All team members use multi-factor authentication for GitHub and SignPath.

## Privacy

The [privacy policy](privacy.md) describes what the add-in and the server collect, where data goes, and how long it is kept.
This program does not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.
