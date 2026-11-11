param(
    [string]$DeviceId = "",
    [string]$AdbPath = "",
    [switch]$NoBump,
    [switch]$NoBackup,
    # Internal: used when this script re-launches itself elevated
    [switch]$ConfigureSdkEnv,
    [string]$SdkRoot = "",
    [string]$AdbDir = "",
    [switch]$SetSdkVars
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "Note.csproj"
$apkDir = Join-Path $root "bin\Release\net10.0-android"
$apk = Join-Path $apkDir "com.memo.note-Signed.apk"
$backupDir = Join-Path $root "backups"
$prevBackup = Join-Path $backupDir "Note_previous.apk"
$package = "com.memo.note"
$log = Join-Path $PSScriptRoot "build.log"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$phoneDir = "/sdcard/Download"

function Fail($msg) {
    Write-Host ""
    Write-Host "[FAILED] $msg" -ForegroundColor Red
    exit 1
}

# Internal elevated mode: persist machine-wide environment variables, then exit.
if ($ConfigureSdkEnv) {
    try {
        if ($SetSdkVars) {
            [Environment]::SetEnvironmentVariable('ANDROID_HOME', $SdkRoot, 'Machine')
            [Environment]::SetEnvironmentVariable('ANDROID_SDK_ROOT', $SdkRoot, 'Machine')
        }
        $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
        if (-not $machinePath) { $machinePath = '' }
        if (-not $machinePath.Contains($AdbDir)) {
            $newPath = if ($machinePath) { $machinePath.TrimEnd(';') + ';' + $AdbDir } else { $AdbDir }
            [Environment]::SetEnvironmentVariable('Path', $newPath, 'Machine')
        }
        exit 0
    } catch {
        Write-Error $_
        exit 1
    }
}

# ---------- locate adb (no hard-coded absolute paths) ----------
function Resolve-AdbPath {
    param([string]$Explicit)

    $candidates = New-Object System.Collections.Generic.List[string]

    # 1) Explicit path from caller
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        $candidates.Add($Explicit)
    }

    # 2) PATH
    $fromPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($fromPath) { $candidates.Add($fromPath.Source) }

    # 3) Android SDK environment variables
    foreach ($name in @('ANDROID_HOME', 'ANDROID_SDK_ROOT')) {
        $sdkRoot = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($sdkRoot)) {
            $candidates.Add((Join-Path $sdkRoot 'platform-tools\adb.exe'))
        }
    }

    # 4) Visual Studio Android SDK located via vswhere
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

    # 5) Common default locations derived from environment variables
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

# ---------- auto install platform-tools when adb / SDK is missing ----------
$script:AdbInstallError = 'unknown error'

function Test-SdkVarsConfigured {
    foreach ($name in @('ANDROID_HOME', 'ANDROID_SDK_ROOT')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $true }
    }
    return $false
}

function Invoke-ElevatedSdkEnv {
    param([string]$Root, [string]$Dir, [bool]$SetVars)

    try {
        $switchArg = if ($SetVars) { ' -SetSdkVars' } else { '' }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'powershell.exe'
        $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $PSCommandPath + '"' +
                         ' -ConfigureSdkEnv -SdkRoot "' + $Root + '" -AdbDir "' + $Dir + '"' + $switchArg
        $psi.Verb = 'RunAs'
        $psi.UseShellExecute = $true
        $psi.WindowStyle = 'Hidden'
        $proc = [System.Diagnostics.Process]::Start($psi)
        $proc.WaitForExit()
        return ($proc.ExitCode -eq 0)
    } catch {
        return $false
    }
}

function Set-AdbEnvironment {
    param([string]$Root, [string]$Dir, [bool]$SetVars)

    # Current process, so this deployment can continue immediately.
    if ($SetVars) {
        $env:ANDROID_HOME = $Root
        $env:ANDROID_SDK_ROOT = $Root
    }
    if (-not $env:Path) { $env:Path = '' }
    if (-not $env:Path.Contains($Dir)) {
        $env:Path = $env:Path.TrimEnd(';') + ';' + $Dir
    }

    # Persist for current user; normally no elevation required.
    try {
        if ($SetVars) {
            [Environment]::SetEnvironmentVariable('ANDROID_HOME', $Root, 'User')
            [Environment]::SetEnvironmentVariable('ANDROID_SDK_ROOT', $Root, 'User')
        }
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (-not $userPath) { $userPath = '' }
        if (-not $userPath.Contains($Dir)) {
            $newUserPath = if ($userPath) { $userPath.TrimEnd(';') + ';' + $Dir } else { $Dir }
            [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
        }
        Write-Host "[adb] Environment persisted for current user" -ForegroundColor Green
        return
    } catch {
        Write-Host "[adb] User-level environment failed; requesting elevation..." -ForegroundColor Yellow
    }

    if (Invoke-ElevatedSdkEnv -Root $Root -Dir $Dir -SetVars $SetVars) {
        Write-Host "[adb] Environment persisted machine-wide (elevated)" -ForegroundColor Green
    } else {
        Write-Host "[WARN] Could not persist environment; adb still works for this run." -ForegroundColor Yellow
    }
}

function Install-PlatformTools {
    # Always install into a user-writable SDK root; never replace an existing SDK.
    $sdkRoot = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    $adbDir = Join-Path $sdkRoot 'platform-tools'
    $adbExe = Join-Path $adbDir 'adb.exe'
    $setVars = -not (Test-SdkVarsConfigured)

    if (Test-Path -LiteralPath $adbExe -PathType Leaf) {
        Set-AdbEnvironment -Root $sdkRoot -Dir $adbDir -SetVars $setVars
        return $adbExe
    }

    $url = 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip'
    $zip = Join-Path $env:TEMP 'platform-tools-latest-windows.zip'
    $ProgressPreference = 'SilentlyContinue'

    Write-Host "[adb] adb / SDK not found. Downloading Android SDK Platform-Tools..." -ForegroundColor Yellow
    Write-Host "       $url" -ForegroundColor DarkGray

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -TimeoutSec 180
        New-Item -ItemType Directory -Path $sdkRoot -Force | Out-Null
        Expand-Archive -Path $zip -DestinationPath $sdkRoot -Force
    } catch {
        $script:AdbInstallError = $_.Exception.Message
        return $null
    } finally {
        if (Test-Path -LiteralPath $zip) {
            Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        }
    }

    if (-not (Test-Path -LiteralPath $adbExe -PathType Leaf)) {
        $script:AdbInstallError = 'platform-tools\adb.exe not found after extraction'
        return $null
    }

    Set-AdbEnvironment -Root $sdkRoot -Dir $adbDir -SetVars $setVars
    Write-Host "[adb] Platform-Tools installed to $adbDir" -ForegroundColor Green
    return $adbExe
}

function Show-AdbManualSteps {
    param([string]$Reason)

    Write-Host ""
    Write-Host "[adb] Automatic download failed: $Reason" -ForegroundColor Red
    Write-Host "Install Android SDK Platform-Tools manually:" -ForegroundColor Yellow
    Write-Host "  1) Download : https://dl.google.com/android/repository/platform-tools-latest-windows.zip" -ForegroundColor Yellow
    Write-Host "  2) Extract  : into your Android SDK root (platform-tools\adb.exe)" -ForegroundColor Yellow
    Write-Host "  3) Set ANDROID_HOME / ANDROID_SDK_ROOT and add platform-tools to PATH" -ForegroundColor Yellow
    Write-Host "  4) Or run   : powershell -File tools\deploy.ps1 -AdbPath <full path to adb.exe>" -ForegroundColor Yellow
    Write-Host "  Android Studio: SDK Manager > SDK Tools > Android SDK Platform-Tools" -ForegroundColor Yellow
}

$adb = Resolve-AdbPath -Explicit $AdbPath
if (-not $adb) {
    $adb = Install-PlatformTools
    if (-not $adb) {
        Show-AdbManualSteps -Reason $script:AdbInstallError
        Fail "adb unavailable. Install platform-tools manually or pass -AdbPath."
    }
}
Write-Host "[adb] $adb" -ForegroundColor DarkGray

# ---------- pick device ----------
$deviceLines = & $adb devices | Select-String "^\S+\s+device$"
if (-not $deviceLines) { Fail "No Android device connected. Please connect the phone with USB debugging enabled." }
$devices = @($deviceLines | ForEach-Object { ($_.ToString().Trim() -split "\s+")[0] })
if ($DeviceId) {
    if ($devices -notcontains $DeviceId) { Fail "Device $DeviceId not found. Connected: $($devices -join ', ')" }
} else {
    if ($devices.Count -gt 1) { Fail "Multiple devices connected ($($devices -join ', ')). Run with -DeviceId <serial>." }
    $DeviceId = $devices[0]
}
Write-Host "[1/8] Device: $DeviceId" -ForegroundColor Green

# ---------- version (UTF-8 safe: do not use Get-Content/Set-Content here) ----------
$xml = [System.IO.File]::ReadAllText($csproj, [System.Text.Encoding]::UTF8)
$verMatch = [regex]::Match($xml, "<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>")
$codeMatch = [regex]::Match($xml, "<ApplicationVersion>(\d+)</ApplicationVersion>")
if (-not $verMatch.Success -or -not $codeMatch.Success) { Fail "Version fields not found in Note.csproj" }

$oldVer = $verMatch.Groups[1].Value.Trim()
$oldCode = [int]$codeMatch.Groups[1].Value
$newVer = $oldVer
$newCode = $oldCode
if (-not $NoBump) {
    $parts = $oldVer.Split(".")
    if ($parts.Count -ge 3) { $parts[2] = [string]([int]$parts[2] + 1) }
    elseif ($parts.Count -eq 2) { $parts = @($parts[0], $parts[1], "1") }
    else { $parts = @($oldVer, "0", "1") }
    $newVer = $parts -join "."
    $newCode = $oldCode + 1
    $xml = $xml -replace "<ApplicationDisplayVersion>[^<]+</ApplicationDisplayVersion>", "<ApplicationDisplayVersion>$newVer</ApplicationDisplayVersion>"
    $xml = $xml -replace "<ApplicationVersion>\d+</ApplicationVersion>", "<ApplicationVersion>$newCode</ApplicationVersion>"
    [System.IO.File]::WriteAllText($csproj, $xml, $utf8NoBom)
}
Write-Host "[2/8] Version: $oldVer ($oldCode) -> $newVer ($newCode)" -ForegroundColor Green

# ---------- keep only one backup: the previous version ----------
if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir | Out-Null }
if (-not $NoBackup -and (Test-Path $apk)) {
    Copy-Item $apk $prevBackup -Force
    Write-Host "[3/8] Backup previous APK: Note_previous.apk (v$oldVer)" -ForegroundColor Green
} else {
    Write-Host "[3/8] Backup previous APK: skipped" -ForegroundColor DarkGray
}

# ---------- build ----------
Write-Host "[4/8] Building Release APK ..." -ForegroundColor Green
Push-Location $root
try {
    Write-Host "  (dotnet build output below; also saved to tools\build.log)" -ForegroundColor DarkGray
    & dotnet build "Note.csproj" -f net10.0-android -c Release -nodeReuse:false --nologo -v m 2>&1 | Tee-Object -FilePath $log
    $buildExit = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($buildExit -ne 0) {
    Get-Content $log | Select-String "error" | Select-Object -First 10 | ForEach-Object { Write-Host $_.Line -ForegroundColor Red }
    Fail "Build failed. Full log: $log"
}
if (-not (Test-Path $apk)) { Fail "APK not found: $apk" }

# only one backup file is kept on the PC
Get-ChildItem $backupDir -Filter "*.apk" |
    Where-Object { $_.FullName -ne $prevBackup } | Remove-Item -Force

# ---------- install & launch ----------
Write-Host "[5/8] Installing to phone ..." -ForegroundColor Green
& $adb -s $DeviceId install -r $apk
if ($LASTEXITCODE -ne 0) { Fail "Install failed. Previous APK keeps working on the phone." }

& $adb -s $DeviceId shell am force-stop $package | Out-Null
& $adb -s $DeviceId shell monkey -p $package -c android.intent.category.LAUNCHER 1 | Out-Null

# ---------- phone: keep ONLY the current version installer APK ----------
Write-Host "[6/8] Removing old installer APKs from phone ..." -ForegroundColor Green
$removed = 0
foreach ($dir in @($phoneDir, "/sdcard")) {
    $listing = & $adb -s $DeviceId shell "ls $dir/Note_*.apk 2>/dev/null" 2>$null
    foreach ($line in $listing) {
        $item = ([string]$line).Trim()
        if (-not $item.StartsWith("/") -or -not $item.EndsWith(".apk")) { continue }
        & $adb -s $DeviceId shell "rm -f '$item'" | Out-Null
        $removed++
    }
}
Write-Host "  removed $removed old installer(s)" -ForegroundColor DarkGray

# ---------- copy current installer to phone ----------
$phoneApk = "$phoneDir/Note_v$newVer.apk"
Write-Host "[7/8] Copying current installer to phone: $phoneApk" -ForegroundColor Green
& $adb -s $DeviceId push $apk $phoneApk
if ($LASTEXITCODE -ne 0) { Write-Host "  [WARN] push failed, but app is already installed." -ForegroundColor Yellow }

# ---------- verify: only one Note_*.apk remains ----------
$remaining = @()
foreach ($dir in @($phoneDir, "/sdcard")) {
    $listing = & $adb -s $DeviceId shell "ls $dir/Note_*.apk 2>/dev/null" 2>$null
    foreach ($line in $listing) {
        $item = ([string]$line).Trim()
        if ($item.StartsWith("/") -and $item.EndsWith(".apk")) { $remaining += $item }
    }
}
Write-Host "[8/8] Phone installer(s): $($remaining.Count) -> $($remaining -join ', ')" -ForegroundColor Green

Write-Host ""
Write-Host "DONE: v$newVer ($newCode) deployed to $DeviceId" -ForegroundColor Cyan
Write-Host "Phone installer : $phoneApk (only current version kept)" -ForegroundColor Yellow
Write-Host "Rollback        : powershell -File tools\rollback.ps1" -ForegroundColor Yellow
Write-Host "Backup          : backups\Note_previous.apk" -ForegroundColor Yellow
