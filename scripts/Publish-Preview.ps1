[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildRoot,
      [Parameter(Mandatory=$true)][string]$SourceCommit,
      [Parameter(Mandatory=$true)][string]$OutputRoot,
      [switch]$PrepareOnly)
$ErrorActionPreference='Stop'
$repository='TorofSkiy/cino-aos-windows-native'
$tag='v0.4.0-preview.1'
if($SourceCommit -cnotmatch '^[a-f0-9]{40}$'){throw 'Exact source commit required'}
if(-not $PrepareOnly -and ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REPOSITORY -cne $repository -or $env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_SHA -cne $SourceCommit)){throw 'Publishing is restricted to the reviewed main-branch GitHub build'}
$BuildRoot=(Resolve-Path -LiteralPath $BuildRoot).Path
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $OutputRoot){throw 'Release output must be new'}
$portable=Join-Path $BuildRoot 'portable'
$receipt=Get-Content -LiteralPath (Join-Path $BuildRoot 'build-receipt.json') -Raw|ConvertFrom-Json
if($receipt.schema -cne 'cino.opensource.build.v1' -or $receipt.version -cne '0.4.0-network.2' -or $receipt.windows_authenticode_signed){throw 'Unexpected preview build receipt'}
$files=@(Get-ChildItem -LiteralPath $portable -Recurse -File)
if($files.Count -ne @($receipt.files).Count){throw 'Build membership mismatch'}
foreach($file in $files){
 $relative=$file.FullName.Substring($portable.Length+1).Replace('\','/')
 $expected=@($receipt.files|Where-Object path -CEQ $relative)
 if($expected.Count -ne 1 -or $file.Length -ne $expected[0].size_bytes -or (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant() -cne $expected[0].sha256){throw ('Build changed: '+$relative)}
}
foreach($path in @('host/Cino.NativeHost.exe','workbench/Cino.Workbench.exe')){
 if((Get-AuthenticodeSignature -LiteralPath (Join-Path $portable $path)).Status -ne 'NotSigned'){throw 'Unsigned-preview label does not match first-party binary status'}
}
[void][IO.Directory]::CreateDirectory($OutputRoot)
$archiveName='CINO-AOS-Windows-Native-0.4.0-preview.1-win-x64-unsigned.zip'
$archive=Join-Path $OutputRoot $archiveName
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($portable,$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
Copy-Item -LiteralPath (Join-Path $BuildRoot 'build-receipt.json') -Destination $OutputRoot
$runUrl=if($env:GITHUB_ACTIONS -ceq 'true'){'https://github.com/'+$repository+'/actions/runs/'+$env:GITHUB_RUN_ID}else{$null}
$provenance=[ordered]@{schema='cino.public.preview.v1';repository=$repository;source_commit=$SourceCommit;tag=$tag;workflow_run=$runUrl;build_version=$receipt.version;stage='unsigned-portable-preview';windows_authenticode_signed=$false;target_deployment_included=$false;production_ready=$false;prepared_at=[DateTimeOffset]::UtcNow.ToString('o')}
$provenance|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputRoot 'provenance.json') -Encoding UTF8
$notes=@'
## Unsigned Windows x64 portable preview / 未签名预览版

Apache-2.0 Windows Host and Workbench core, built from the linked public commit with locked dependencies. The build runs 9 workspace behavior checks and 6 checks against an actual isolated Host process. License texts, a dependency inventory, build receipt and checksums are included.

This is a developer preview, not a signed production installer. It does not install a service, update task or driver. The private network installer, management backend, enrollment credentials, models and inference engine are not bundled. Physical target installation and upgrade acceptance have not been performed for this release.

Keep Windows protections enabled. If a binary is blocked, do not disable protection to run it. Free signing sponsorship has not been granted; acceptance is subject to external review. SHA-256 checksums verify file integrity and are not a publisher signature.

For local use, model prerequisites, data locations and removal, see README.md inside the ZIP. Save work and close the portable processes before removing its extracted folder; existing user artifacts are retained.

[Code signing policy](https://github.com/TorofSkiy/cino-aos-windows-native/blob/main/CODE_SIGNING_POLICY.md) · [Privacy](https://github.com/TorofSkiy/cino-aos-windows-native/blob/main/PRIVACY.md)
'@
$notes+="`n`nSource commit: $SourceCommit`nBuild: $runUrl`n"
[IO.File]::WriteAllText((Join-Path $OutputRoot 'RELEASE_NOTES.md'),$notes,[Text.UTF8Encoding]::new($false))
$checksums=@(Get-ChildItem -LiteralPath $OutputRoot -File|Sort-Object Name|ForEach-Object {((Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()+'  '+$_.Name)})
[IO.File]::WriteAllLines((Join-Path $OutputRoot 'SHA256SUMS.txt'),$checksums,[Text.UTF8Encoding]::new($false))
if($PrepareOnly){Write-Output 'Prepared unsigned preview locally; nothing published.';return}
# Use Release assets, not metered Actions artifacts or cache. Never overwrite
# a published tag, binary, or checksum. Failure leaves a draft for inspection.
$existing=& gh release list --repo $repository --limit 100 --json tagName | ConvertFrom-Json
if($LASTEXITCODE -ne 0){throw 'Could not verify existing releases'}
if(@($existing|Where-Object tagName -CEQ $tag).Count){throw 'Release already exists; no overwrite performed'}
$assets=@(Get-ChildItem -LiteralPath $OutputRoot -File|ForEach-Object FullName)
& gh release create $tag @assets --repo $repository --target $SourceCommit --draft --prerelease --latest=false --title 'CINO-AOS Windows Native 0.4.0 — unsigned preview' --notes-file (Join-Path $OutputRoot 'RELEASE_NOTES.md')
if($LASTEXITCODE -ne 0){throw 'Draft release creation failed'}
$download=Join-Path $OutputRoot 'verified-download'
& gh release download $tag --repo $repository --dir $download
if($LASTEXITCODE -ne 0){throw 'Draft asset verification download failed'}
if(@(Get-ChildItem -LiteralPath $download -File).Count -ne $assets.Count){throw 'Uploaded asset membership mismatch'}
foreach($path in $assets){if((Get-FileHash -LiteralPath $path).Hash -cne (Get-FileHash -LiteralPath (Join-Path $download ([IO.Path]::GetFileName($path)))).Hash){throw 'Uploaded asset digest mismatch'}}
& gh release edit $tag --repo $repository --draft=false --prerelease --latest=false
if($LASTEXITCODE -ne 0){throw 'Publishing verified draft failed'}
Write-Output ('Published verified unsigned preview: https://github.com/'+$repository+'/releases/tag/'+$tag)
