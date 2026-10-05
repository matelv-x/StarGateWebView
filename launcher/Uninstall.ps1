param([switch]$Silent)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
try {
    $root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
    if ($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')) { throw 'Invalid installation folder.' }
    $manifest = Get-Content -LiteralPath (Join-Path $root 'launcher-package.json') -Raw | ConvertFrom-Json
    if ($manifest.PackageId -ne 'Stargate-x64') { throw 'This folder is not a StarGate installation.' }
    function Test-Tree([string]$path) {
        $item = Get-Item -LiteralPath $path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Folder links cannot be removed by this uninstaller.' }
        if ($item.PSIsContainer) { Get-ChildItem -LiteralPath $path -Force | ForEach-Object { Test-Tree $_.FullName } }
    }
    Test-Tree $root
    if (!$Silent -and [Windows.Forms.MessageBox]::Show("Usunąć StarGate z folderu:`n$root ?", 'Odinstaluj StarGate', 'YesNo', 'Question') -ne 'Yes') { exit }
    Get-Process | ForEach-Object {
        try { $exe = $_.Path } catch { $exe = $null }
        if ($exe -and $exe.StartsWith($root+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Zamknij StarGate i launcher, następnie ponownie uruchom odinstalowanie.' }
    }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Startup'))) {
        Get-ChildItem -LiteralPath $folder -Filter '*.lnk' | ForEach-Object {
            $target = $shell.CreateShortcut($_.FullName).TargetPath
            if ($target.StartsWith($root+'\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $_.FullName }
        }
    }
    $configRoot = Join-Path $env:LOCALAPPDATA 'Universal_Launcher'
    if (Test-Path -LiteralPath $configRoot) {
        Get-ChildItem -LiteralPath $configRoot -Directory | ForEach-Object {
            $json = Join-Path $_.FullName 'launcher.json'
            if (Test-Path -LiteralPath $json) {
                $cfg = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
                if ($cfg.InstalledBaseDir -and [IO.Path]::GetFullPath($cfg.InstalledBaseDir).TrimEnd('\') -eq $root) { Remove-Item -LiteralPath $json }
            }
        }
    }
    $reg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StarGateLauncher-Stargate-x64'
    if (Test-Path -LiteralPath $reg) {
        if ((Get-ItemProperty -LiteralPath $reg).InstallLocation.TrimEnd('\') -eq $root) { Remove-Item -LiteralPath $reg }
    }
    Remove-Item -LiteralPath $root -Recurse -Force
    if (!$Silent) { [Windows.Forms.MessageBox]::Show('StarGate został odinstalowany.', 'StarGate') | Out-Null }
} catch { if ($Silent) { Write-Error $_.Exception.Message; exit 1 }; [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Odinstalowanie — błąd', 'OK', 'Error') | Out-Null; exit 1 }
