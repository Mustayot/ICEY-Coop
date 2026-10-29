[CmdletBinding()]
param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([string]::IsNullOrEmpty($OutputDirectory)){$OutputDirectory=Join-Path $root 'artifacts\dist'}
$packageRoot=Join-Path $root 'installer'
$manifest=[IO.File]::ReadAllText((Join-Path $packageRoot 'manifest.json')) | ConvertFrom-Json
$name='ICEYCoop-GameHealth-20261029-'+$manifest.buildId
$stage=Join-Path $OutputDirectory $name
$zipPath=$stage+'.zip'
if((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $zipPath)){throw 'Release output already exists; refusing to replace evidence'}
$top=@('Setup.cmd','Setup.ps1','Installer.Core.ps1','Setup.Gui.ps1','Setup.Web.ps1','Setup.ui.html',
 'manifest.json','build-info.json','README.md','THIRD-PARTY.txt','LICENSE','VALIDATION.md','GameHealth.md')
$paths=@($top)
foreach($file in $manifest.files){
 $path='payload/'+$file.path.Replace('\','/')
 if((Get-FileHash -LiteralPath (Join-Path $packageRoot $path) -Algorithm SHA256).Hash -ne $file.sha256){throw ('Payload hash mismatch: '+$path)}
 $paths+=$path
}
foreach($folder in @('licenses','third-party-source')){
 foreach($file in @(Get-ChildItem -LiteralPath (Join-Path $packageRoot $folder) -File)){$paths+=($folder+'/'+$file.Name)}
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$index=@()
foreach($relative in $paths){
 $from=if($relative -eq 'LICENSE'){Join-Path $root $relative}else{Join-Path $packageRoot $relative}
 $target=Join-Path $stage $relative
 New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
 Copy-Item -LiteralPath $from -Destination $target
 $index+=[pscustomobject]@{path=$relative;bytes=(Get-Item -LiteralPath $target).Length;sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash}
}
$indexText=$index | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $stage 'release-files.json'),$indexText,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $stage 'release-files.json') -Destination (Join-Path $packageRoot 'release-files.json')
$all=@($index)+@([pscustomobject]@{path='release-files.json';bytes=(Get-Item -LiteralPath (Join-Path $stage 'release-files.json')).Length;sha256=(Get-FileHash -LiteralPath (Join-Path $stage 'release-files.json') -Algorithm SHA256).Hash})
$archive=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
try{
 foreach($row in $all){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $stage $row.path),$row.path,[IO.Compression.CompressionLevel]::Optimal)}
}finally{$archive.Dispose()}
$archive=[IO.Compression.ZipFile]::OpenRead($zipPath)
try{
 if($archive.Entries.Count -ne $all.Count){throw 'Archive file count mismatch'}
 foreach($row in $all){
  $entry=$archive.GetEntry($row.path)
  if($null -eq $entry -or $entry.Length -ne $row.bytes){throw ('Archive length mismatch: '+$row.path)}
  $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
  try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
  if($hash -ne $row.sha256){throw ('Archive hash mismatch: '+$row.path)}
 }
}finally{$archive.Dispose()}
$zipHash=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($zipPath+'.sha256'),($zipHash+'  '+[IO.Path]::GetFileName($zipPath)+[Environment]::NewLine),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{package=$zipPath;sha256=$zipHash;files=$all.Count;payloadFiles=@($manifest.files).Count;bytes=(Get-Item -LiteralPath $zipPath).Length;status='ALL_ARCHIVE_ENTRIES_VERIFIED'} | ConvertTo-Json
