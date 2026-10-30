# Installs BepInEx 5.4.23.5 (if missing) and the MINEKILL plugin into the ULTRAKILL folder.
# Only ADDS files: it refuses to run if any of them would overwrite an existing game file.
# Every file it adds is recorded in backups\installed-files.txt so uninstall.ps1 removes exactly those.
#   powershell -ExecutionPolicy Bypass -File tools\install.ps1 [-GameDir "D:\...\ULTRAKILL"]
param([string]$GameDir)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\env.ps1"
if (-not $GameDir) { $GameDir = $env:ULTRAKILL_DIR }
if (-not (Test-Path "$GameDir\ULTRAKILL.exe")) { throw "ULTRAKILL.exe not found in $GameDir" }
if (Get-Process ULTRAKILL -ErrorAction SilentlyContinue) { throw "Close ULTRAKILL first." }

$backup = Get-ChildItem "$MK\backups" -Directory -Filter "ultrakill-pre-bepinex-*" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $backup) { throw "No backup in $MK\backups (expected ultrakill-pre-bepinex-*). Make one before installing." }

$bep = "$MK\.tools\bepinex-pkg"
$plugin = "$MK\build\plugin\Minekill"
if (-not (Test-Path "$plugin\Minekill.dll")) { throw "Build the plugin first: dotnet build plugin\Minekill.Plugin -c Release" }

$plan = @()
if (-not (Test-Path "$GameDir\BepInEx\core\BepInEx.dll")) {
    Get-ChildItem $bep -Recurse -File | ForEach-Object { $plan += [pscustomobject]@{ Src = $_.FullName; Rel = $_.FullName.Substring($bep.Length + 1) } }
}
Get-ChildItem $plugin -File | ForEach-Object { $plan += [pscustomobject]@{ Src = $_.FullName; Rel = "BepInEx\plugins\Minekill\" + $_.Name } }

$record = "$MK\backups\installed-files.txt"
$ours = @(if (Test-Path $record) { Get-Content $record })
$clash = $plan | Where-Object { (Test-Path "$GameDir\$($_.Rel)") -and ($ours -notcontains $_.Rel) }
if ($clash) { throw "Refusing to overwrite files that MINEKILL didn't install:`n$($clash.Rel -join "`n")" }

foreach ($f in $plan) {
    $dst = Join-Path $GameDir $f.Rel
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item $f.Src $dst -Force
    Write-Host "added $($f.Rel)"
}
(@($ours) + @($plan.Rel)) | Sort-Object -Unique | Set-Content -Encoding utf8 $record
Write-Host "Done. $($plan.Count) files. Recorded in $record"
Write-Host "Log after starting the game: $GameDir\BepInEx\LogOutput.log"
