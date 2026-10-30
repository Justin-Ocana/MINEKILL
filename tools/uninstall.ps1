# Removes exactly the files install.ps1 added (backups\installed-files.txt), then empty folders
# it created. Leaves BepInEx's generated config/logs folders unless -All is given.
#   powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1 [-PluginOnly] [-All]
param([string]$GameDir, [switch]$PluginOnly, [switch]$All)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\env.ps1"
if (-not $GameDir) { $GameDir = $env:ULTRAKILL_DIR }
if (Get-Process ULTRAKILL -ErrorAction SilentlyContinue) { throw "Close ULTRAKILL first." }
$record = "$MK\backups\installed-files.txt"
if (-not (Test-Path $record)) { throw "Nothing recorded in $record" }
$files = @(Get-Content $record)
$remove = if ($PluginOnly) { $files | Where-Object { $_ -like "BepInEx\plugins\Minekill\*" } } else { $files }
foreach ($rel in $remove) {
    $p = Join-Path $GameDir $rel
    if (Test-Path $p) { Remove-Item $p; Write-Host "removed $rel" }
}
$files | Where-Object { $remove -notcontains $_ } | Set-Content -Encoding utf8 $record
if ($All -and -not $PluginOnly) {
    foreach ($d in "BepInEx\config", "BepInEx\cache", "BepInEx\patchers", "BepInEx\plugins", "BepInEx\core", "BepInEx") {
        $p = Join-Path $GameDir $d
        if (Test-Path $p) { Remove-Item $p -Recurse; Write-Host "removed folder $d" }
    }
    Remove-Item (Join-Path $GameDir "BepInEx\LogOutput.log") -ErrorAction SilentlyContinue
}
Write-Host "Done."
