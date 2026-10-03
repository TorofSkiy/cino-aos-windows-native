[CmdletBinding()]
param([string]$OutputRoot='', [switch]$UpdateLocks)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(-not $OutputRoot){$OutputRoot=Join-Path $root 'artifacts'}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $OutputRoot){throw 'Build output must be new'}
Push-Location $root
try {
 $expected=(Get-Content global.json -Raw|ConvertFrom-Json).sdk.version
 $sdk=(& dotnet --version).Trim()
 if($LASTEXITCODE -ne 0 -or $sdk -ne $expected){throw 'Install the exact SDK from global.json'}
 New-Item -ItemType Directory -Path $OutputRoot | Out-Null
 $projects=@('host/Cino.NativeHost.csproj','workbench/Cino.Workbench.csproj','tests/workbench/Cino.Workbench.Tests.csproj')
 foreach($project in $projects){
  if($UpdateLocks){& dotnet restore $project --force-evaluate}else{& dotnet restore $project --locked-mode}
  if($LASTEXITCODE -ne 0){throw ('Locked restore failed: '+$project)}
 }
 foreach($component in @('host','workbench')){
  $project=$projects[(@('host','workbench').IndexOf($component))]
  & dotnet publish $project --no-restore -c Release -r win-x64 --self-contained true -o (Join-Path $OutputRoot ('portable/'+$component))
  if($LASTEXITCODE -ne 0){throw ('Publish failed: '+$component)}
 }
 & dotnet build $projects[2] --no-restore -c Release
 if($LASTEXITCODE -ne 0){throw 'Workspace test build failed'}
 & dotnet run --project $projects[2] --no-build --no-restore -c Release -- (Join-Path $OutputRoot 'tests/workspace')
 if($LASTEXITCODE -ne 0){throw 'Workspace tests failed'}
 & (Join-Path $PSScriptRoot 'Test-Host.ps1') -Executable (Join-Path $OutputRoot 'portable/host/Cino.NativeHost.exe') -OutputRoot (Join-Path $OutputRoot 'tests/host')
 foreach($name in @('LICENSE','NOTICE','README.md','PRIVACY.md','THIRD_PARTY_NOTICES.md','CODE_SIGNING_POLICY.md','host.example.json','workbench.example.json')){
  Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $OutputRoot 'portable')
 }
 & (Join-Path $PSScriptRoot 'Inventory-Dependencies.ps1') -OutputRoot $OutputRoot
 $artifacts=@(Get-ChildItem (Join-Path $OutputRoot 'portable') -Recurse -File|ForEach-Object{
  [ordered]@{path=$_.FullName.Substring((Join-Path $OutputRoot 'portable').Length+1).Replace('\','/');size_bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
 })
 [ordered]@{schema='cino.opensource.build.v1';version='0.4.0-network.2';sdk=$sdk;runtime='8.0.30';windows_authenticode_signed=$false;public_release_approved=$false;target_installed=$false;production_ready=$false;files=$artifacts} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputRoot 'build-receipt.json') -Encoding UTF8
 Write-Output 'Local review build and tests complete. Unsigned; no publication or target installation.'
}finally{Pop-Location}

