[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$seen=@{};$rows=@()
$licenseRoot=Join-Path $OutputRoot 'portable/third-party-licenses'
New-Item -ItemType Directory -Path $licenseRoot -Force|Out-Null
foreach($component in @('host','workbench')){
 $assets=Get-Content (Join-Path $root ($component+'/obj/project.assets.json')) -Raw|ConvertFrom-Json
 $packageRoots=@($assets.packageFolders.PSObject.Properties.Name)
 $libraries=@($assets.libraries.PSObject.Properties)
 foreach($framework in $assets.project.frameworks.PSObject.Properties){
  foreach($download in $framework.Value.downloadDependencies){
   $version=$download.version.Trim('[',']').Split(',')[0].Trim()
   $packagePath=$download.name.ToLowerInvariant()+'/'+$version
   $libraries+= [pscustomobject]@{Name=$download.name+'/'+$version;Value=[pscustomobject]@{type='package';path=$packagePath;sha512=$null}}
  }
 }
 foreach($library in $libraries){
  if($library.Value.type -ne 'package' -or $seen.ContainsKey($library.Name)){continue}
  $seen[$library.Name]=$true
  $package=$null
  foreach($folder in $packageRoots){$candidate=Join-Path $folder $library.Value.path;if(Test-Path -LiteralPath $candidate){$package=$candidate;break}}
  if(-not $package){throw ('Dependency package missing: '+$library.Name)}
  $packageDigest=$library.Value.sha512
  if(-not $packageDigest){
   $hashFile=@(Get-ChildItem -LiteralPath $package -Filter '*.nupkg.sha512' -File)
   if($hashFile.Count -ne 1){throw 'Runtime package integrity metadata missing'}
   $packageDigest=([IO.File]::ReadAllText($hashFile[0].FullName)).Trim()
   $locked=Get-Content (Join-Path $root 'runtime-packs.lock.json') -Raw|ConvertFrom-Json
   $expected=@($locked.packages|Where-Object package -ceq $library.Name)
   if($expected.Count -ne 1 -or $expected[0].sha512 -cne $packageDigest){throw ('Runtime package hash changed: '+$library.Name)}
   $nupkg=@(Get-ChildItem -LiteralPath $package -Filter '*.nupkg' -File)
   if($nupkg.Count -ne 1){throw 'Runtime package archive missing'}
   $digest=[Security.Cryptography.SHA512]::Create()
   $stream=[IO.File]::OpenRead($nupkg[0].FullName)
   try{$actual=[Convert]::ToBase64String($digest.ComputeHash($stream))}finally{$stream.Dispose();$digest.Dispose()}
   if($actual -cne $packageDigest){throw 'Runtime archive hash mismatch'}
  }
  $spec=@(Get-ChildItem -LiteralPath $package -Filter '*.nuspec' -File)
  if($spec.Count -ne 1){throw 'Dependency metadata missing'}
  [xml]$xml=Get-Content -LiteralPath $spec[0].FullName -Raw
  $metadata=$xml.package.metadata
  $licenseExpression=[string]$metadata.license.InnerText
  $copies=@()
  foreach($notice in Get-ChildItem -LiteralPath $package -File|Where-Object {$_.Name -match '^(LICENSE|COPYING|THIRD.PARTY.NOTICES)'}){
   $relative=$library.Name.Replace('/','-')+'/'+$notice.Name
   $destination=Join-Path $licenseRoot $relative
   New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force|Out-Null
   Copy-Item -LiteralPath $notice.FullName -Destination $destination
   $copies+=$relative
  }
  $rows+=[ordered]@{package=$library.Name;license_expression=$licenseExpression;license_type=[string]$metadata.license.type;license_url=[string]$metadata.licenseUrl;project_url=[string]$metadata.projectUrl;package_sha512=$packageDigest;included_notices=$copies}
 }
}
$rows|Sort-Object package|ConvertTo-Json -Depth 6|Set-Content (Join-Path $OutputRoot 'portable/dependencies.json') -Encoding UTF8
if(@($rows|Where-Object {-not $_.license_expression -or -not $_.included_notices.Count}).Count){throw 'Dependency licensing inventory incomplete; review before sharing'}
Write-Output ('Dependency packages inventoried: '+$rows.Count)

