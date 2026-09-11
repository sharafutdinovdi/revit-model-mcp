# Third-party notices

This project is licensed under MIT.
Dependencies retain their own licenses and copyright notices.
These entries record package metadata inspected on 2026-09-11.
Floating version ranges can resolve to other versions during a future restore.

## Runtime and test dependencies

| Package | Inspected version | License evidence |
|---|---|---|
| Nice3point.Revit.Toolkit | 2026.1.0 | MIT, `License.md` inside the [NuGet package](https://www.nuget.org/packages/Nice3point.Revit.Toolkit/2026.1.0) |
| Nice3point.Revit.Extensions | 2026.1.4 | MIT, `LICENSE.md` inside the [NuGet package](https://www.nuget.org/packages/Nice3point.Revit.Extensions/2026.1.4) |
| Nice3point.Revit.Api.RevitAPI | 2026.4.10 | MIT, `License.md` inside the [NuGet package](https://www.nuget.org/packages/Nice3point.Revit.Api.RevitAPI/2026.4.10) |
| Nice3point.Revit.Api.RevitAPIUI | 2022.1.80 | MIT, `License.md` inside the [NuGet package](https://www.nuget.org/packages/Nice3point.Revit.Api.RevitAPIUI/2022.1.80) |
| MCP Python SDK (`mcp`) | 2.2.0 | MIT, [PyPI metadata](https://pypi.org/pypi/mcp/2.2.0/json) |
| TUnit | 1.66.27 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/tunit/1.66.27/tunit.nuspec) |

The Revit API packages include a Nice3point MIT license file.
Verify Autodesk terms for redistribution of Autodesk binaries before packaging a release.
The package license label does not establish those redistribution rights.

## Other direct dependencies

| Package | Inspected version | License evidence |
|---|---|---|
| Sourcy.DotNet | 1.1.1 | MIT, [license at the package repository commit](https://github.com/thomhurst/Sourcy/blob/38190825402139a7b1afceb0f5f79aeab5b3cae9/LICENSE) |
| Shouldly | 4.3.0 | BSD-3-Clause, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/shouldly/4.3.0/shouldly.nuspec) |
| ModularPipelines | 3.2.8 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/modularpipelines/3.2.8/modularpipelines.nuspec) |
| ModularPipelines.Git | 3.2.8 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/modularpipelines.git/3.2.8/modularpipelines.git.nuspec) |
| ModularPipelines.DotNet | 3.2.8 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/modularpipelines.dotnet/3.2.8/modularpipelines.dotnet.nuspec) |
| Microsoft.VisualStudio.SolutionPersistence | 1.0.52 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/microsoft.visualstudio.solutionpersistence/1.0.52/microsoft.visualstudio.solutionpersistence.nuspec) |
| Microsoft.Extensions.Options.DataAnnotations | 10.0.10 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.options.dataannotations/10.0.10/microsoft.extensions.options.dataannotations.nuspec) |
| Autodesk.PackageBuilder | 2.0.2 | MIT License, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/autodesk.packagebuilder/2.0.2/autodesk.packagebuilder.nuspec) |
| JetBrains.Annotations | 2026.2.0 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/jetbrains.annotations/2026.2.0/jetbrains.annotations.nuspec) |
| ILRepack | 2.0.46 | Apache-2.0, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/ilrepack/2.0.46/ilrepack.nuspec) |
| Polyfill | 11.0.1 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/polyfill/11.0.1/polyfill.nuspec) |
| WixSharp.Core | 2.14.1 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/wixsharp.core/2.14.1/wixsharp.core.nuspec) |
| WixSharp.Msi.Core | 2.14.1 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/wixsharp.msi.core/2.14.1/wixsharp.msi.core.nuspec) |
| Nice3point.Revit.Sdk | 6.2.3 | MIT License, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/nice3point.revit.sdk/6.2.3/nice3point.revit.sdk.nuspec) |

## Release verification

Sourcy.DotNet 1.1.1 has no license element in its NuGet metadata.
Its license was confirmed at the repository commit recorded in that metadata.
Verify the complete transitive dependency list after the Windows restore.
The installer invokes WiX 7 and accepts its EULA in the build pipeline.
Verify WiX 7 licensing for the release environment before running installer packaging.
Retain applicable license files when distributing dependency binaries.
