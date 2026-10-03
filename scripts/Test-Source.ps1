[CmdletBinding()]
param([string]$Root=(Split-Path $PSScriptRoot -Parent),[switch]$CreateManifest,[switch]$AllowGitMetadata)
$ErrorActionPreference='Stop'
$Root=(Resolve-Path -LiteralPath $Root).Path.TrimEnd('\','/')
$entries=@()
$children=@(Get-ChildItem -LiteralPath $Root -Force | Where-Object {-not ($AllowGitMetadata -and $_.Name -ceq '.git')})
$files=@($children|Where-Object {-not $_.PSIsContainer})
foreach($child in $children|Where-Object PSIsContainer){$files+=@(Get-ChildItem -LiteralPath $child.FullName -Force -Recurse -File)}
$blockedPath='(^|/)(\.git|bin|obj|private|evidence|\.test-runs|\.preview|node_modules|artifacts)(/|$)|(^|/)\.env($|\.)|\.(pfx|p12|key|secret|gguf|sqlite|db|exe|dll|zip)$|(^|/)(host-state|management-link|enrollment|workbench)\.json$'
# Construct detectors without embedding private values in the public scanner.
$privateIp='\b(?:192\.168|10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b'
$privateKey='<RSA'+'KeyValue>[\s\S]*<'+'D>|-----BEGIN [A-Z ]*PRIVATE KEY-----'
$credential='(?i)(?:ghp|gho|github_pat)_[A-Za-z0-9_]{20,}|\b(?:sk|AKIA)-?[A-Za-z0-9]{24,}'
$personal='(?i)\b[A-Z0-9._%+-]+@(?:proton\.me|gmail\.com|outlook\.com)\b|[A-Z]:[\\/]Users[\\/](?!Public(?:[\\/]|$))[^\\/\s]+'
foreach($directory in @($children|Where-Object PSIsContainer)+@($children|Where-Object PSIsContainer|ForEach-Object {Get-ChildItem -LiteralPath $_.FullName -Force -Recurse -Directory})){
 if($directory.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Directory redirection in source tree'}
}
foreach($file in $files){
 $relative=$file.FullName.Substring($Root.Length+1).Replace('\','/')
 if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'File redirection in source tree'}
 if($relative -match $blockedPath){throw ('Private/generated path rejected: '+$relative)}
 if($relative -eq 'source-manifest.json'){continue}
 if($file.Length -gt 1500000){throw ('Oversized source file rejected: '+$relative)}
 $content=[IO.File]::ReadAllText($file.FullName)
 if($content -match $privateIp -or $content -match $privateKey -or $content -match $credential -or $content -match $personal){throw ('Potential private data rejected: '+$relative)}
 $entries+=[ordered]@{path=$relative;size_bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()}
}
$entries=@($entries|Sort-Object path)
$manifestPath=Join-Path $Root 'source-manifest.json'
if($CreateManifest){
 [ordered]@{schema='cino.opensource.source.v1';version='0.4.0-network.2';stage='source-preview';license='Apache-2.0';public_release_approved=$true;files=$entries}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $manifestPath -Encoding UTF8
}else{
 $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
 if($manifest.schema -ne 'cino.opensource.source.v1' -or $manifest.files.Count -ne $entries.Count){throw 'Source manifest membership mismatch'}
 foreach($entry in $entries){$matches=@($manifest.files|Where-Object path -CEQ $entry.path);if($matches.Count -ne 1 -or $matches[0].sha256 -cne $entry.sha256 -or $matches[0].size_bytes -ne $entry.size_bytes){throw ('Source manifest mismatch: '+$entry.path)}}
}
Write-Output ('PASS source membership, hashes and private-data checks: '+$entries.Count+' files')

