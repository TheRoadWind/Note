param(
    [string]$File = "Note_previous.apk",
    [string]$DeviceId = "",
    [string]$AdbPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$backupDir = Join-Path $root "backups"
$package = "com.memo.note"

function Fail($msg) {
    Write-Host ""
    Write-Host "[FAILED] $msg" -ForegroundColor Red
    exit 1
}

# ---------- locate adb (same discovery as deploy.ps1) ----------
function Resolve-AdbPath {
    param([string]$Explicit)

    $candidates = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        $candidates.Add($Explicit)
    }

    $fromPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($fromPath) { $candidates.Add($fromPath.Source) }

    foreach ($name in @('ANDROID_HOME', 'ANDROID_SDK_ROOT')) {
        $sdkRoot = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($sdkRoot)) {
            $candidates.Add((Join-Path $sdkRoot 'platform-tools\adb.exe'))
        }
    }

    $pf86 = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($pf86)) {
        $vswhere = Join-Path $pf86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vswhere) {
            $installs = @(& $vswhere -latest -products * -property installationPath 2>$null)
            foreach ($install in $installs) {
                if ([string]::IsNullOrWhiteSpace($install)) { continue }
                $candidates.Add((Join-Path $install 'Shared\Android\android-sdk\platform-tools\adb.exe'))
            }
        }
        $candidates.Add((Join-Path $pf86 'Microsoft Visual Studio\Shared\Android\android-sdk\platform-tools\adb.exe'))
    }

    if ($env:LOCALAPPDATA) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'))
    }
    if ($env:ProgramFiles) {
        $candidates.Add((Join-Path $env:ProgramFiles 'Android\platform-tools\adb.exe'))
    }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

$adb = Resolve-AdbPath -Explicit $AdbPath
if (-not $adb) {
    Write-Host ""
    Write-Host "[adb] adb / SDK not found. Run tools\deploy.ps1 once to download it automatically." -ForegroundColor Yellow
    Write-Host "      Manual download: https://dl.google.com/android/repository/platform-tools-latest-windows.zip" -ForegroundColor Yellow
    Write-Host "      Then set ANDROID_HOME, add platform-tools to PATH, or pass -AdbPath." -ForegroundColor Yellow
    Fail "adb unavailable."
}
Write-Host "[adb] $adb" -ForegroundColor DarkGray

$target = Join-Path $backupDir $File
if (-not (Test-Path $target)) { Fail "Backup not found: $target" }

$deviceLines = & $adb devices | Select-String "^\S+\s+device$"
if (-not $deviceLines) { Fail "No Android device connected." }
$devices = @($deviceLines | ForEach-Object { ($_.ToString().Trim() -split "\s+")[0] })
if (-not $DeviceId) {
    if ($devices.Count -gt 1) { Fail "Multiple devices: $($devices -join ', '). Use -DeviceId." }
    $DeviceId = $devices[0]
}

Write-Host "Installing $File ..." -ForegroundColor Green
& $adb -s $DeviceId install -r $target
if ($LASTEXITCODE -ne 0) { Fail "Rollback install failed." }

& $adb -s $DeviceId shell am force-stop $package | Out-Null
& $adb -s $DeviceId shell monkey -p $package -c android.intent.category.LAUNCHER 1 | Out-Null
Write-Host "DONE: rolled back to $File" -ForegroundColor Cyan
