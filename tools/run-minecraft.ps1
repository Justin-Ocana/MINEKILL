# Starts MINEKILL's Minecraft (Fabric 26.3 development client, offline player "Steve", world
# "MINEKILL" created on first run). It links to ULTRAKILL by itself once a level is running.
# Its files live in mcmod\run (your own .minecraft is not touched).
#   powershell -ExecutionPolicy Bypass -File tools\run-minecraft.ps1
. "$PSScriptRoot\env.ps1"
Set-Location "$MK\mcmod"
.\gradlew.bat runClient --console=plain
