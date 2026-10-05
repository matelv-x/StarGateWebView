param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin\setup-win-x64'), [string]$AppProject = '')
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path $PSScriptRoot) 'app\StarGateWebView.csproj'

if ($AppProject) { $source = [IO.Path]::GetFullPath($AppProject) }
if (!(Test-Path -LiteralPath $source)) { throw 'Application project not found.' }
$bootstrapper = Join-Path $PSScriptRoot 'redist\MicrosoftEdgeWebview2Setup.exe'
if (!(Test-Path -LiteralPath $bootstrapper)) {
    New-Item -ItemType Directory -Force (Split-Path $bootstrapper) | Out-Null
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper
}
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'Microsoft bootstrapper signature verification failed.' }
$stage = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj\evergreen-build-'+[guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Force $stage | Out-Null
try {
    $payload = Join-Path $stage 'app-x64'
    dotnet publish $source -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    if (Test-Path -LiteralPath (Join-Path $payload 'WebView2')) { throw 'Unexpected fixed runtime in Evergreen payload.' }
    $package = Join-Path $stage 'package'
    dotnet publish (Join-Path $PSScriptRoot 'StarGateLauncherAll.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false "-p:AppPayloadDirectory=$payload" -o $package
    if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $stage 'payload.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    dotnet publish (Join-Path $PSScriptRoot 'Setup\StarGateSetup.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false "-p:PayloadPath=$zip" -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
} finally {
    $objRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'obj')).TrimEnd('\')+'\'
    if (!$stage.StartsWith($objRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid build cleanup path.' }
    if ((Get-Item -LiteralPath $stage).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing cleanup through a folder link.' }
    if (Get-ChildItem -LiteralPath $stage -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Refusing build cleanup containing links.' }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
Write-Output ('Installer: '+(Join-Path $OutputDirectory 'StarGateSetup.exe'))
