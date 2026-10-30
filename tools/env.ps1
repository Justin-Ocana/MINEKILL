# Dot-source this (". .\tools\env.ps1") to use the project's portable toolchain without touching the system.
$MK = Split-Path -Parent $PSScriptRoot
# Portable .NET SDK 8 / JDK 25 / Gradle in .tools\ if present; otherwise the system's own are used.
if (Test-Path "$MK\.tools\dotnet") { $env:DOTNET_ROOT = "$MK\.tools\dotnet"; $env:PATH = "$MK\.tools\dotnet;$env:PATH" }
if (Test-Path "$MK\.tools\jdk25") { $env:JAVA_HOME = "$MK\.tools\jdk25"; $env:PATH = "$MK\.tools\jdk25\bin;$env:PATH" }
if (Test-Path "$MK\.tools\gradle-9.7.1") { $env:PATH = "$MK\.tools\gradle-9.7.1\bin;$env:PATH" }
# Gradle/Loom caches (Minecraft jars, libraries, assets) stay inside the project's tools folder.
if (Test-Path "$MK\.tools") { $env:GRADLE_USER_HOME = "$MK\.tools\gradle-home" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
# Game folder: set ULTRAKILL_DIR, or put it in tools\local.ps1 (not committed), e.g.
#   $env:ULTRAKILL_DIR = "E:\Games\ULTRAKILL"
if (Test-Path "$PSScriptRoot\local.ps1") { . "$PSScriptRoot\local.ps1" }
if (-not $env:ULTRAKILL_DIR) { $env:ULTRAKILL_DIR = "C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL" }
