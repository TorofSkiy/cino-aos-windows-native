# Dependencies and redistribution

First-party license proposal: Apache License 2.0 (see LICENSE and NOTICE). Do not apply it to third-party code.

The Host directly references Microsoft.Extensions.Hosting.WindowsServices 8.0.1. Both components use Microsoft .NET 8.0.30 and associated Windows desktop/ASP.NET runtime packs. NuGet lockfiles record exact resolved dependencies and package hashes. Third-party packages retain their own licenses (predominantly MIT); the build copies their actual LICENSE and third-party notice files without rewriting the authorship.

The build generates `dependencies.json` from resolved packages and includes `third-party-licenses/`. This is a dependency/license inventory, not an assertion that every legal obligation or vulnerability has been independently audited. Check the generated inventory before release.

No model weights, llama.cpp engine, LLVM runtime, Windows installation media, activation keys or fonts are redistributed by this source snapshot. Future additions require their own license and notice review. Third-party upstream-signed binaries must keep their original signatures; do not submit them as CINO-authored files for replacement signing.

Development tooling (not bundled): .NET SDK, PowerShell, GitHub Actions checkout/setup-dotnet/upload-artifact. Their respective repositories/licenses govern usage.

