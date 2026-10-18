# Third-party notices

This project is licensed under MIT.
Dependencies retain their own licenses and copyright notices.
These entries record package metadata inspected on 2026-09-11, except openpyxl inspected on 2026-09-30, TUnit and Polyfill inspected on 2026-10-02, and Pillow inspected on 2026-10-04.
Floating version ranges can resolve to other versions during a future restore.

## Runtime and test dependencies

| Package | Inspected version | License evidence |
|---|---|---|
| Nice3point.Revit.Toolkit | 2026.1.0 | MIT, `License.md` inside the [NuGet package](https://www.nuget.org/packages/Nice3point.Revit.Toolkit/2026.1.0) |
| Nice3point.Revit.Extensions | 2026.1.4 | MIT, `LICENSE.md` inside the [NuGet package](https://www.nuget.org/packages/Nice3point.Revit.Extensions/2026.1.4) |
| Nice3point.Revit.Api.RevitAPI | 2026.4.10 | MIT, `License.md` inside the [NuGet package](https://www.nuget.org/packages/Nice3point.Revit.Api.RevitAPI/2026.4.10) |
| Nice3point.Revit.Api.RevitAPIUI | 2022.1.80 | MIT, `License.md` inside the [NuGet package](https://www.nuget.org/packages/Nice3point.Revit.Api.RevitAPIUI/2022.1.80) |
| MCP Python SDK (`mcp`) | 2.2.0 | MIT, [PyPI metadata](https://pypi.org/pypi/mcp/2.2.0/json) |
| openpyxl | 3.1.5 | MIT, [PyPI metadata](https://pypi.org/pypi/openpyxl/3.1.5/json) |
| Pillow | 12.3.0 | MIT-CMU, [license in the package source](https://github.com/python-pillow/Pillow/blob/12.3.0/LICENSE) |
| Microsoft.CodeAnalysis.CSharp | 4.11.0 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.csharp/4.11.0/microsoft.codeanalysis.csharp.nuspec) |
| Microsoft.CodeAnalysis.Common | 4.11.0 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.common/4.11.0/microsoft.codeanalysis.common.nuspec) |
| System.Collections.Immutable | 8.0.0 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/system.collections.immutable/8.0.0/system.collections.immutable.nuspec) |
| System.Reflection.Metadata | 8.0.0 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/system.reflection.metadata/8.0.0/system.reflection.metadata.nuspec) |
| Microsoft.CodeAnalysis.Analyzers | 3.3.4 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.analyzers/3.3.4/microsoft.codeanalysis.analyzers.nuspec) |
| TUnit | 1.72.10 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/tunit/1.72.10/tunit.nuspec) |

Microsoft.CodeAnalysis.CSharp compiles the code of `revit_execute_code` inside the add-in.
It and its runtime dependencies ship next to the add-in assembly and are excluded from ILRepack.
Microsoft.CodeAnalysis.Analyzers is a build-time analyzer package; it is listed because the restore resolves it.
Versions come from the R26 package lock file.

The Revit API packages include a Nice3point MIT license file.
The inspected Revit API packages contain reference assemblies under `ref/`, with no runtime assets.
CI and release packaging reject `RevitAPI*.dll` in the staged output.
Revit supplies these binaries on the workstation; the release does not distribute them.

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
| Polyfill | 11.4.5 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/polyfill/11.4.5/polyfill.nuspec) and [package-tag license](https://github.com/SimonCropp/Polyfill/blob/11.4.5/license.txt) |
| WixSharp.Core | 2.14.1 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/wixsharp.core/2.14.1/wixsharp.core.nuspec) |
| WixSharp.Msi.Core | 2.14.1 | MIT, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/wixsharp.msi.core/2.14.1/wixsharp.msi.core.nuspec) |
| Nice3point.Revit.Sdk | 6.3.2 | MIT License, [NuGet metadata](https://api.nuget.org/v3-flatcontainer/nice3point.revit.sdk/6.3.2/nice3point.revit.sdk.nuspec) |

## Python server transitive dependencies

These packages come in through `mcp`, `openpyxl` and `cryptography` and are installed by `uvx` or `pip`, not vendored.
Versions are from `server/uv.lock` on 2026-10-10; license text is from the PyPI metadata of that version.

| Package | Locked version | License |
|---|---|---|
| mcp-types | 2.3.0 | MIT, [PyPI metadata](https://pypi.org/pypi/mcp-types/2.3.0/json) |
| anyio | 4.15.1 | MIT, [PyPI metadata](https://pypi.org/pypi/anyio/4.15.1/json) |
| idna | 3.20 | BSD-3-Clause, [PyPI metadata](https://pypi.org/pypi/idna/3.20/json) |
| typing-extensions | 4.16.0 | PSF-2.0, [PyPI metadata](https://pypi.org/pypi/typing-extensions/4.16.0/json) |
| httpx2 | 2.13.1 | BSD-3-Clause, [PyPI metadata](https://pypi.org/pypi/httpx2/2.13.1/json) |
| httpcore2 | 2.13.1 | BSD-3-Clause, [PyPI metadata](https://pypi.org/pypi/httpcore2/2.13.1/json) |
| h11 | 0.16.0 | MIT, [PyPI metadata](https://pypi.org/pypi/h11/0.16.0/json) |
| truststore | 0.10.4 | MIT, [PyPI metadata](https://pypi.org/pypi/truststore/0.10.4/json) |
| jsonschema | 4.26.0 | MIT, [PyPI metadata](https://pypi.org/pypi/jsonschema/4.26.0/json) |
| attrs | 26.1.0 | MIT, [PyPI metadata](https://pypi.org/pypi/attrs/26.1.0/json) |
| jsonschema-specifications | 2025.9.1 | MIT, [PyPI metadata](https://pypi.org/pypi/jsonschema-specifications/2025.9.1/json) |
| referencing | 0.37.0 | MIT, [PyPI metadata](https://pypi.org/pypi/referencing/0.37.0/json) |
| rpds-py | 2026.6.3 | MIT, [PyPI metadata](https://pypi.org/pypi/rpds-py/2026.6.3/json) |
| pydantic | 2.13.5 | MIT, [PyPI metadata](https://pypi.org/pypi/pydantic/2.13.5/json) |
| annotated-types | 0.8.0 | MIT, [PyPI metadata](https://pypi.org/pypi/annotated-types/0.8.0/json) |
| pydantic-core | 2.46.5 | MIT, [PyPI metadata](https://pypi.org/pypi/pydantic-core/2.46.5/json) |
| typing-inspection | 0.4.4 | MIT, [PyPI metadata](https://pypi.org/pypi/typing-inspection/0.4.4/json) |
| opentelemetry-api | 1.45.0 | Apache-2.0, [PyPI metadata](https://pypi.org/pypi/opentelemetry-api/1.45.0/json) |
| pyjwt | 2.15.1 | MIT, [PyPI metadata](https://pypi.org/pypi/pyjwt/2.15.1/json) |
| cryptography | 50.0.2 | Apache-2.0 OR BSD-3-Clause, [PyPI metadata](https://pypi.org/pypi/cryptography/50.0.2/json) |
| cffi | 2.1.1 | MIT-0, [PyPI metadata](https://pypi.org/pypi/cffi/2.1.1/json) |
| pycparser | 3.0 | BSD-3-Clause, [PyPI metadata](https://pypi.org/pypi/pycparser/3.0/json) |
| python-multipart | 0.0.32 | Apache-2.0, [PyPI metadata](https://pypi.org/pypi/python-multipart/0.0.32/json) |
| sse-starlette | 3.5.0 | BSD-3-Clause, [PyPI metadata](https://pypi.org/pypi/sse-starlette/3.5.0/json) |
| starlette | 1.7.0 | BSD-3-Clause, [PyPI metadata](https://pypi.org/pypi/starlette/1.7.0/json) |
| uvicorn | 0.54.0 | BSD-3-Clause, [PyPI metadata](https://pypi.org/pypi/uvicorn/0.54.0/json) |
| click | 8.5.0 | BSD-3-Clause, [PyPI metadata](https://pypi.org/pypi/click/8.5.0/json) |
| et-xmlfile | 2.0.0 | MIT, [PyPI metadata](https://pypi.org/pypi/et-xmlfile/2.0.0/json) |

## Bundled server executable

The MSI installers ship the server as a self-contained Windows executable built with PyInstaller (`build/server-exe/build.py`).
It contains the CPython runtime and the Python packages listed above, installed from hash-pinned requirements.
Notice files travel with it in the `licenses` folder: `PYTHON-LICENSE.txt` (Python Software Foundation License, which covers the bundled CPython and its standard library), one folder per bundled Python package with the license files from its distribution metadata, and this file.
PyInstaller is GPL-2.0-or-later with the bootloader exception, which permits distributing the executables it produces under any license; its bootloader is the only part of PyInstaller that ships.
The runtime also carries libraries that CPython itself builds on (OpenSSL, SQLite, zlib, libffi and others); their licenses are listed in the CPython documentation under "Licenses and Acknowledgements".

## Release verification

Sourcy.DotNet 1.1.1 has no license element in its NuGet metadata.
Its license was confirmed at the repository commit recorded in that metadata.
Nice3point Toolkit and Extensions packages for Revit 2022-2026 were inspected in the local NuGet cache; their target-framework dependency groups are empty and their license files are MIT.
The other cached rows were checked against their `.nuspec` files; WixSharp.Core and WixSharp.Msi.Core were checked through NuGet's versioned metadata endpoints.
Python dependencies are declared in wheel metadata and are installed separately, not vendored.

The optional installer pipeline invokes WiX 7 and accepts its EULA.
WiX 7.0.0 metadata identifies `OSMFEULA.txt`, not MIT: [NuGet metadata](https://api.nuget.org/v3-flatcontainer/wix/7.0.0/wix.nuspec).
The [EULA at the package commit](https://github.com/wixtoolset/wix/blob/b8977d6f88e7b68e000bac226a2814f236770570/OSMFEULA.txt) distinguishes the source license, MS-RL, from the binary maintenance-fee agreement.
Its fee provisions depend on the user's revenue and use of the binary distribution.
The v0.1.0 release workflow creates ZIP archives and a Python wheel; it does not invoke or distribute WiX.

## Notices carried with add-in archives

Nice3point.Revit.Toolkit: Copyright (c) 2022 Nice3point.
Nice3point.Revit.Extensions: Copyright (c) 2021 Nice3point.
JetBrains.Annotations: Copyright (c) 2016-2025 JetBrains s.r.o.
Polyfill: Copyright (c) Simon Cropp.
These components use the MIT license below, including code merged or generated into the add-in.


Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
