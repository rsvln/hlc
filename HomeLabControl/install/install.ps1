<#
.SYNOPSIS
    Установка / обновление HomeLabControl (без Docker) как службы Windows.

.DESCRIPTION
    Запускать из папки сборки (или install.cmd двойным щелчком — права администратора запросит сам):
        .\install.ps1                                   # обновить установленный HLC или новая установка (спросит путь и порт)
        .\install.ps1 -InstallPath D:\hlc -Port 8080
        .\install.ps1 -AdminUser admin -AdminPassword 'secret123'
        .\install.ps1 -Uninstall                        # служба и программа; config\ остаётся

    Уже установленный HLC (служба с HomeLabControl.exe) находится сам: путь и имя службы берутся у неё.
    Не копируются и не меняются: config\ (конфиг, пользователи, ключи — если уже есть) и appsettings.Local.json
    (кроме переданных параметров -Port / -AdminUser / -AdminPassword).
    HLC_ADMIN_USER / HLC_ADMIN_PASSWORD — администратор, который создаётся / восстанавливается при каждом старте,
    как в docker-compose. Без него первый вход откроет /setup.
#>
param(
    [string]$InstallPath = '',
    [string]$ServiceName = '',
    [int]$Port = 0,
    [string]$AdminUser = '',
    [string]$AdminPassword = '',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Перезапуск с повышением; -NoExit — чтобы окно с результатом не закрылось
    $arguments = @('-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($InstallPath) { $arguments += @('-InstallPath', "`"$InstallPath`"") }
    if ($ServiceName) { $arguments += @('-ServiceName', $ServiceName) }
    if ($Port -gt 0) { $arguments += @('-Port', $Port) }
    if ($AdminUser) { $arguments += @('-AdminUser', "`"$AdminUser`"") }
    if ($AdminPassword) { $arguments += @('-AdminPassword', "`"$AdminPassword`"") }
    if ($Uninstall) { $arguments += '-Uninstall' }
    Start-Process powershell -Verb RunAs -ArgumentList $arguments
    return
}

# Вопрос с значением по умолчанию: Enter — принять его
function Read-Default([string]$Prompt, [string]$Default) {
    $answer = Read-Host "$Prompt [$Default]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim()
}

$exeName = 'HomeLabControl.exe'

# Уже установленный HLC: служба с этим именем или любая служба, запускающая HomeLabControl.exe (не агент)
$existing = Get-CimInstance Win32_Service |
    Where-Object { ($ServiceName -and $_.Name -eq $ServiceName) -or (-not $ServiceName -and $_.PathName -match '\\HomeLabControl\.exe') } |
    Select-Object -First 1
if ($existing) {
    if (-not $ServiceName) { $ServiceName = $existing.Name }
    if (-not $InstallPath) { $InstallPath = Split-Path ($existing.PathName.Trim().Trim('"')) -Parent }
    Write-Host "Installed HomeLabControl found: service $ServiceName in $InstallPath"
}
if (-not $ServiceName) { $ServiceName = 'homeLabControl' }
if (-not $InstallPath) {
    if ($Uninstall) {
        Write-Host 'No installed homeLabControl found - nothing to uninstall (or pass -InstallPath / -ServiceName)'
        return
    }
    # Новая установка: путь и порт спрашиваем, Enter — значение по умолчанию (D:\apps, если есть диск D:, иначе C:\apps)
    $hasD = [IO.DriveInfo]::GetDrives() | Where-Object { $_.Name -eq 'D:\' -and $_.DriveType -eq 'Fixed' }
    $defaultPath = if ($hasD) { 'D:\apps\homeLabControl' } else { 'C:\apps\homeLabControl' }
    $InstallPath = Read-Default 'Install path' $defaultPath
    if ($Port -le 0) {
        $answer = Read-Default 'Port' '8208'
        if ($answer -ne '8208') { $Port = [int]$answer }
    }
}

$exe = Join-Path $InstallPath $exeName
$localSettings = Join-Path $InstallPath 'appsettings.Local.json'
$firewallRule = 'HomeLab Control'
$keep = @('appsettings.Local.json', 'config')

function Stop-Hlc {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    }
    for ($i = 0; $i -lt 20; $i++) {
        $running = Get-Process -Name HomeLabControl -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path -like "$InstallPath*" }
        if (-not $running) { return }
        Start-Sleep -Milliseconds 500
    }
    $running | Stop-Process -Force
}

# ─── Удаление ────────────────────────────────────────────────────────────────

if ($Uninstall) {
    Stop-Hlc
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        sc.exe delete $ServiceName | Out-Null
        Write-Host "Service $ServiceName removed"
    }
    Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if (Test-Path $InstallPath) {
        Start-Sleep -Seconds 1
        Get-ChildItem $InstallPath -Force | Where-Object { $_.Name -notin $keep } | Remove-Item -Recurse -Force
        Write-Host "Program removed. Kept: $InstallPath\config and appsettings.Local.json (delete by hand if not needed)."
    }
    return
}

# ─── Проверки ────────────────────────────────────────────────────────────────

if (-not (Test-Path (Join-Path $PSScriptRoot $exeName))) {
    throw "$exeName not found next to the script ($PSScriptRoot) - run install.ps1 from the HomeLabControl build folder"
}
if (($AdminUser -or $AdminPassword) -and (-not $AdminUser -or $AdminPassword.Length -lt 8)) {
    throw '-AdminUser and -AdminPassword go together, password at least 8 characters'
}
if (-not (Get-Command ssh-keygen -ErrorAction SilentlyContinue)) {
    Write-Host 'WARNING: ssh-keygen not found - agent deploy and backup keys need it (Settings -> Optional features -> OpenSSH Client)' -ForegroundColor Yellow
}

# ─── Файлы ───────────────────────────────────────────────────────────────────

Stop-Hlc
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null

$source = (Resolve-Path $PSScriptRoot).Path.TrimEnd('\')
$target = (Resolve-Path $InstallPath).Path.TrimEnd('\')
if ($source -ne $target) {
    Get-ChildItem -Path $source -Force |
        Where-Object { $_.Name -notin $keep } |
        Copy-Item -Destination $target -Recurse -Force
    # config\ из сборки (пример конфига) — только при первой установке
    if (-not (Test-Path (Join-Path $target 'config\HomeLabControl.yaml')) -and (Test-Path (Join-Path $source 'config'))) {
        Copy-Item (Join-Path $source 'config') $target -Recurse -Force
        Write-Host "Sample config copied to $target\config - edit it in the UI: Config -> YAML"
    }
    Write-Host "Files copied to $target"
}

# ─── Порт и администратор: appsettings.Local.json ────────────────────────────

if (Test-Path $localSettings) {
    $local = Get-Content $localSettings -Raw | ConvertFrom-Json
}
else {
    $local = [pscustomobject]@{ Urls = 'http://0.0.0.0:8208' }
}
if ($Port -gt 0) { $local | Add-Member -NotePropertyName Urls -NotePropertyValue "http://0.0.0.0:$Port" -Force }
if ($AdminUser) {
    $local | Add-Member -NotePropertyName HLC_ADMIN_USER -NotePropertyValue $AdminUser -Force
    $local | Add-Member -NotePropertyName HLC_ADMIN_PASSWORD -NotePropertyValue $AdminPassword -Force
}
$local | ConvertTo-Json -Depth 10 | Set-Content $localSettings -Encoding UTF8
# Пароль администратора в файле — доступ только администраторам и SYSTEM
icacls $localSettings /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null

$servicePort = if ($local.Urls -match ':(\d+)\s*$') { [int]$Matches[1] } else { 8208 }

# ─── Служба и брандмауэр ─────────────────────────────────────────────────────

# sc.exe требует пробел после "binPath=", "start=" и т.п.
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    sc.exe config $ServiceName binPath= "`"$exe`"" start= auto | Out-Null
}
else {
    sc.exe create $ServiceName binPath= "`"$exe`"" start= auto DisplayName= 'HomeLab Control' | Out-Null
}
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/10000/restart/10000 | Out-Null

Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Protocol TCP -LocalPort $servicePort -Action Allow | Out-Null

Start-Service -Name $ServiceName

# ─── Проверка ────────────────────────────────────────────────────────────────

$ok = $false
for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
    Start-Sleep -Seconds 1
    try {
        Invoke-WebRequest "http://localhost:$servicePort/login" -UseBasicParsing -TimeoutSec 2 -MaximumRedirection 0 | Out-Null
        $ok = $true
    }
    catch {
        # 302 на /setup — тоже «работает»
        if ($_.Exception.Response) { $ok = $true }
    }
}

Write-Host ''
if ($ok) {
    Write-Host "HomeLabControl is running: http://$($env:COMPUTERNAME):$servicePort" -ForegroundColor Green
}
else {
    Write-Host "Service started, but HomeLabControl does not answer on port $servicePort - see Event Viewer -> Application" -ForegroundColor Yellow
}
if (-not $local.HLC_ADMIN_USER) { Write-Host 'No admin configured: the first visit opens /setup to create one.' }
