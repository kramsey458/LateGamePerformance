# Runs Timberborn's built-in benchmark with per-component tick metrics enabled.
# The game loads the save, warms up, samples, writes reports, then quits by itself.
# Output: Documents\Timberborn\Benchmarks\Standalone\<save> ... .log / .csv / " metrics.csv"
#
# With -SaveCount N it runs the game's save benchmark instead (SavingBenchmarker in Timberborn.Benchmarking.dll):
# the game loads the save, waits -WarmUpSeconds, finishes the tick, saves N times into memory
# (GameSaver.BenchmarkSavingToMemory) and quits. -Speed and -SampleSeconds are not used then.
# Output: a "Finished saving benchmark:" block in Player.log (number of saves, average, median, 90th percentile,
# min and max, in seconds), and this mod's "Save to a stream" line for each save with the stages split.
#
# Usage (save name is the file name without ".timber"):
#   .\benchmark-timberborn.ps1 -Settlement "My Colony" -Save "My Colony (12)"
#   .\benchmark-timberborn.ps1 -Settlement "My Colony" -Save "My Colony (12)" -Speed 7
#   .\benchmark-timberborn.ps1 -Settlement "My Colony" -Save "My Colony (12)" -SaveCount 20
#   add -Experimental for saves under Documents\Timberborn\ExperimentalSaves
param(
    [Parameter(Mandatory = $true)][string]$Settlement,
    [Parameter(Mandatory = $true)][string]$Save,
    [int]$Speed = 3,
    [int]$WarmUpSeconds = 30,
    [int]$SampleSeconds = 120,
    [ValidateRange(0, 1000)][int]$SaveCount = 0,
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
    "-saveName", "`"$Save`""
)
if ($SaveCount -gt 0) {
    # The game runs the ordinary benchmark whenever -benchmarkLength and -benchmarkSpeed are both given, so they
    # are left out here.
    $gameArgs += @(
        "-benchmarkSaveCount", $SaveCount,
        "-benchmarkWarmUpLength", $WarmUpSeconds
    )
} else {
    $gameArgs += @(
        "-benchmarkLength", $SampleSeconds,
        "-benchmarkWarmUpLength", $WarmUpSeconds,
        "-benchmarkSpeed", $Speed,
        "-metrics"
    )
}
if ($Experimental) { $gameArgs += "-experimental" }

Start-Process -FilePath $steam -ArgumentList $gameArgs
if ($SaveCount -gt 0) {
    "Launched. Results will appear in: $env:USERPROFILE\AppData\LocalLow\Mechanistry\Timberborn\Player.log (""Finished saving benchmark"")"
} else {
    "Launched. Reports will appear in: $env:USERPROFILE\Documents\Timberborn\Benchmarks\Standalone"
}
