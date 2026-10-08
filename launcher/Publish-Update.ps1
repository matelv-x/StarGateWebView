param([string]$Repository,[string]$AssetName,[string]$InstallerPath)
$ErrorActionPreference='Stop'
$credential=@('protocol=https','host=github.com','') | git credential fill
if($LASTEXITCODE -ne 0){throw 'Credential lookup failed'}
$entry=$credential | Where-Object {$_ -like 'password=*'} | Select-Object -First 1
if(!$entry){throw 'Credential unavailable'}
$headers=@{Authorization='Bearer '+$entry.Substring(9);Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'}
$base='https://api.github.com/repos/matelv-x/'+$Repository
$release=Invoke-RestMethod -Uri ($base+'/releases/latest') -Headers $headers
function ReplaceAsset([string]$name,[string]$file){
 $temporary=$name+'.updating-'+[guid]::NewGuid().ToString('N')
 $upload=$release.upload_url.Split('{')[0]+'?name='+[uri]::EscapeDataString($temporary)
 $created=Invoke-RestMethod -Uri $upload -Headers $headers -Method Post -InFile $file -ContentType 'application/octet-stream' -TimeoutSec 600
 $hash=(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
 if($created.digest -and $created.digest -ne ('sha256:'+$hash)){throw 'Uploaded checksum mismatch'}
 foreach($asset in ($release.assets | Where-Object name -EQ $name)){Invoke-RestMethod -Uri ($base+'/releases/assets/'+$asset.id) -Headers $headers -Method Delete | Out-Null}
 $updated=Invoke-RestMethod -Uri ($base+'/releases/assets/'+$created.id) -Headers $headers -Method Patch -ContentType 'application/json' -Body (@{name=$name}|ConvertTo-Json -Compress)
 Write-Output ('Published '+$Repository+' '+$updated.name+' sha256:'+ $hash)
}
$manifestPath=Join-Path (Split-Path $InstallerPath) ($Repository+'-update.json')
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if($manifest.Application -ne $Repository -or $manifest.Schema -ne 1 -or
   $manifest.InstallerSha256 -ne (Get-FileHash -LiteralPath $InstallerPath).Hash.ToLowerInvariant() -or
   $manifest.InstallerSize -ne (Get-Item -LiteralPath $InstallerPath).Length) {throw 'Installer and update manifest do not match'}
ReplaceAsset $AssetName $InstallerPath
$sumPath=Join-Path (Split-Path $InstallerPath) 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sumPath,((Get-FileHash -LiteralPath $InstallerPath).Hash.ToLowerInvariant()+'  '+$AssetName+[Environment]::NewLine),[Text.UTF8Encoding]::new($false))
ReplaceAsset 'SHA256SUMS.txt' $sumPath
$manifestPath=Join-Path (Split-Path $InstallerPath) ($Repository+'-update.json')
ReplaceAsset ($Repository+'-update.json') $manifestPath
$verified=Invoke-RestMethod -Uri ($base+'/releases/'+$release.id) -Headers $headers
Write-Output ('Verified release '+$verified.tag_name+' assets: '+($verified.assets.name -join ', '))
$headers=$null;$credential=$null;$entry=$null