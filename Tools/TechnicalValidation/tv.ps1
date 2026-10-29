# Level 2 technical-validation helper for the Quest (Windows PowerShell 5.1).
#
#   .\Tools\TechnicalValidation\tv.ps1 push   -Scenario TV2 -Mode presentation -Physiology nominal [-SensorStabilisationSeconds 10] [-ParticipantId X]
#   .\Tools\TechnicalValidation\tv.ps1 launch
#   .\Tools\TechnicalValidation\tv.ps1 record -Seconds 180          (adb screenrecord fallback; no passthrough)
#   .\Tools\TechnicalValidation\tv.ps1 pull   -Out .\eval_out\level2
#   .\Tools\TechnicalValidation\tv.ps1 off                           (remove the config: participant mode again)
#   .\Tools\TechnicalValidation\tv.ps1 status
#
# The config file is the ONLY activation path of validation mode (see TECHNICAL_VALIDATION.md).
param(
    [Parameter(Position = 0, Mandatory = $true)][ValidateSet("push", "launch", "record", "pull", "off", "status")][string]$Action,
    [string]$Scenario = "TV1",
    [ValidateSet("validation", "presentation")][string]$Mode = "validation",
    [ValidateSet("nominal", "elevated", "unavailable")][string]$Physiology = "nominal",
    [string]$ParticipantId = "",
    [double]$SensorStabilisationSeconds = -1,
    [double]$DelayScale = -1,
    [int]$Seconds = 180,
    [string]$Out = ".\eval_out\level2",
    [string]$Package = "com.vuforia.engine.VuforiaEngine",
    [string]$Adb = "C:\Program Files\Unity\Hub\Editor\6000.0.57f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
)

if (-not (Test-Path $Adb)) { $cmd = Get-Command adb -ErrorAction SilentlyContinue; if ($cmd) { $Adb = $cmd.Source } else { throw "adb not found: pass -Adb <path>" } }
$files = "/sdcard/Android/data/$Package/files/AdaptiveAR"
$configDir = "$files/config"
$configPath = "$configDir/technical_validation.json"

function Invoke-Adb { param([string[]]$Arguments) & $Adb @Arguments }

switch ($Action) {
    "push" {
        $cfg = [ordered]@{
            enabled = $true; scenario = $Scenario; mode = $Mode; physiology = $Physiology
            participantId = $(if ($ParticipantId -ne "") { $ParticipantId } else { $null })
            delayScale = $(if ($DelayScale -ge 0) { $DelayScale } else { $null })
            minDelaySeconds = $null; postTransitionHoldSeconds = $null; finishDelaySeconds = $null
            startDelaySeconds = 3
            sensorStabilisationSeconds = $(if ($SensorStabilisationSeconds -ge 0) { $SensorStabilisationSeconds } else { $null })
            overlay = $true; verbose = $true
        }
        $local = Join-Path $env:TEMP "technical_validation.json"
        ($cfg | ConvertTo-Json -Depth 3) | Out-File -FilePath $local -Encoding ascii
        Write-Host "config:"; Get-Content $local
        Invoke-Adb @("shell", "mkdir", "-p", $configDir)
        Invoke-Adb @("push", $local, $configPath)
        Write-Host "pushed to $configPath. Launch the app (tv.ps1 launch); the TECHNICAL VALIDATION overlay confirms activation."
    }
    "launch" {
        Invoke-Adb @("shell", "am", "force-stop", $Package)
        Start-Sleep -Milliseconds 800
        Invoke-Adb @("shell", "monkey", "-p", $Package, "-c", "android.intent.category.LAUNCHER", "1")
        Write-Host "launched $Package. Put the headset on, complete sensor setup / placement / Begin Assembly; the script starts 3 s after the first step."
    }
    "record" {
        $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
        $remote = "/sdcard/tv_${Scenario}_$stamp.mp4"
        Write-Host "screenrecord fallback (one eye, no passthrough) for $Seconds s -> $remote. Prefer the headset's own recorder for presentation video."
        Invoke-Adb @("shell", "screenrecord", "--size", "1024x1024", "--bit-rate", "8000000", "--time-limit", "$Seconds", $remote)
        Write-Host "done: pull with tv.ps1 pull (VideoShots) or: adb pull $remote"
    }
    "pull" {
        New-Item -ItemType Directory -Force -Path $Out | Out-Null
        Invoke-Adb @("pull", "$files/sessions", (Join-Path $Out "sessions"))
        Invoke-Adb @("pull", "$files/technical_validation", (Join-Path $Out "technical_validation"))
        Invoke-Adb @("pull", "/sdcard/Oculus/VideoShots", (Join-Path $Out "video"))
        Invoke-Adb @("shell", "ls", "/sdcard/tv_*.mp4") 2>$null | ForEach-Object { if ($_ -and -not ($_ -like "*No such file*")) { Invoke-Adb @("pull", $_.Trim(), (Join-Path $Out "video")) } }
        $agg = Join-Path (Join-Path $Out "technical_validation") "level2_summary.md"
        if (Test-Path $agg) { Write-Host "`n--- level2_summary.md ---"; Get-Content $agg }
    }
    "off" {
        Invoke-Adb @("shell", "rm", "-f", $configPath)
        Write-Host "removed $configPath: the next launch is a participant session."
    }
    "status" {
        Write-Host "device:"; Invoke-Adb @("devices")
        Write-Host "config on device:"; Invoke-Adb @("shell", "cat", $configPath)
        Write-Host "summaries on device:"; Invoke-Adb @("shell", "ls", "-la", "$files/technical_validation")
    }
}
