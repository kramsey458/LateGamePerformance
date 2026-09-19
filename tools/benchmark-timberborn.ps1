# Runs Timberborn's built-in benchmark with per-component tick metrics enabled.
# The game loads the save, warms up, samples, writes reports, then quits by itself.
# Output: Documents\Timberborn\Benchmarks\Standalone\<save> ... .log / .csv / " metrics.csv"
#
# Usage (save name is the file name without ".timber"):
#   .\benchmark-timberborn.ps1 -Settlement "My Colony" -Save "My Colony (12)"
#   .\benchmark-timberborn.ps1 -Settlement "My Colony" -Save "My Colony (12)" -Speed 7
#   add -Experimental for saves under Documents\Timberborn\ExperimentalSaves
param(
    [Parameter(Mandatory = $true)][string]$Settlement,
    [Parameter(Mandatory = $true)][string]$Save,
    [int]$Speed = 3,
    [int]$WarmUpSeconds = 30,
    [int]$SampleSeconds = 120,
    [switch]$Experimental
)

if (Get-Process Timberborn -ErrorAction SilentlyContinue) {
    Write-Error "Timberborn is already running. Close it first."
    exit 1
}

$steam = "C:\Program Files (x86)\Steam\steam.exe"
$gameArgs = @(
    "-applaunch", "1062090",
    "-settlementName", "`"$Settlement`"",
    "-saveName", "`"$Save`"",
    "-benchmarkLength", $SampleSeconds,
    "-benchmarkWarmUpLength", $WarmUpSeconds,
    "-benchmarkSpeed", $Speed,
    "-metrics"
)
if ($Experimental) { $gameArgs += "-experimental" }

Start-Process -FilePath $steam -ArgumentList $gameArgs
"Launched. Reports will appear in: $env:USERPROFILE\Documents\Timberborn\Benchmarks\Standalone"
