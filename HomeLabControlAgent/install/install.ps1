<#
.SYNOPSIS
    Установка / обновление HomeLabControlAgent как службы Windows.

.DESCRIPTION
    Запускать из папки сборки (или install.cmd двойным щелчком — права администратора запросит сам):
        .\install.ps1                                   # C:\apps\homeLabControlAgent, порт 8117
        .\install.ps1 -InstallPath D:\hlca -Port 8118
        .\install.ps1 -Uninstall

    Файлы копируются в InstallPath, кроме appsettings.Local.json и profiles.json (настройки машины).
    appsettings.Local.json создаётся только при первой установке — с новыми ключами API
    homelabcontrol и homeassistant. В конце ключи печатаются: homelabcontrol вписать в HLC
    (Config -> Agents -> Add existing), homeassistant — в rest_command HA (заголовок X-Api-Key).
#>
param(
    [string]$InstallPath = 'C:\apps\homeLabControlAgent',
    [string]$ServiceName = 'homeLabControlAgent',
    [int]$Port = 0,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Перезапуск с повышением; -NoExit — чтобы окно с ключами не закрылось
    $arguments = @('-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
                   '-InstallPath', "`"$InstallPath`"", '-ServiceName', $ServiceName)
    if ($Port -gt 0) { $arguments += @('-Port', $Port) }
    if ($Uninstall) { $arguments += '-Uninstall' }
    Start-Process powershell -Verb RunAs -ArgumentList $arguments
    return
}

$exeName = 'HomeLabControlAgent.exe'
$exe = Join-Path $InstallPath $exeName
$localSettings = Join-Path $InstallPath 'appsettings.Local.json'
$firewallRule = 'HomeLab Control Agent'

function Stop-Agent {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    }
    Get-Process ShutdownDialog -ErrorAction SilentlyContinue | Stop-Process -Force
    # Дождаться освобождения файлов
    for ($i = 0; $i -lt 20; $i++) {
        $running = Get-Process -Name HomeLabControlAgent -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path -like "$InstallPath*" }
        if (-not $running) { return }
        Start-Sleep -Milliseconds 500
    }
    $running | Stop-Process -Force
}

# ─── Удаление ────────────────────────────────────────────────────────────────

if ($Uninstall) {
    Stop-Agent
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        sc.exe delete $ServiceName | Out-Null
        Write-Host "Service $ServiceName removed"
    }
    Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if (Test-Path $InstallPath) {
        Start-Sleep -Seconds 1
        Remove-Item $InstallPath -Recurse -Force
        Write-Host "$InstallPath removed"
    }
    Write-Host 'Done. Remove the host from HLC: Config -> Agents -> trash icon.'
    return
}

# ─── Файлы ───────────────────────────────────────────────────────────────────

if (-not (Test-Path (Join-Path $PSScriptRoot $exeName))) {
    throw "$exeName not found next to the script ($PSScriptRoot) - run install.ps1 from the agent build folder"
}

Stop-Agent
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null

$source = (Resolve-Path $PSScriptRoot).Path.TrimEnd('\')
$target = (Resolve-Path $InstallPath).Path.TrimEnd('\')
if ($source -ne $target) {
    Get-ChildItem -Path $source -Force |
        Where-Object { $_.Name -notin @('appsettings.Local.json', 'profiles.json') } |
        Copy-Item -Destination $target -Recurse -Force
    Write-Host "Files copied to $target"
}

# ─── Ключи и порт: appsettings.Local.json ────────────────────────────────────

if (Test-Path $localSettings) {
    try {
        $local = Get-Content $localSettings -Raw | ConvertFrom-Json
    }
    catch {
        # Windows PowerShell 5.1 не понимает комментарии в JSON
        Write-Host "WARNING: cannot parse appsettings.Local.json (comments?), keys are not shown: $($_.Exception.Message)" -ForegroundColor Yellow
        $local = $null
    }
    if ($Port -gt 0 -and $local) {
        $local | Add-Member -NotePropertyName ServicePort -NotePropertyValue $Port -Force
        $local | ConvertTo-Json -Depth 10 | Set-Content $localSettings -Encoding UTF8
    }
    Write-Host 'appsettings.Local.json exists - keys kept'
}
else {
    $local = [ordered]@{
        Auth = [ordered]@{
            ApiKeys = [ordered]@{
                homelabcontrol = (& $exe --generate-key).Trim()
                homeassistant  = (& $exe --generate-key).Trim()
            }
        }
    }
    if ($Port -gt 0) { $local.ServicePort = $Port }
    $local | ConvertTo-Json -Depth 10 | Set-Content $localSettings -Encoding UTF8
    $local = Get-Content $localSettings -Raw | ConvertFrom-Json
    Write-Host 'appsettings.Local.json created with new API keys'
}

$servicePort = if ($local -and $local.ServicePort) { [int]$local.ServicePort } else { 8117 }

# ─── Служба и брандмауэр ─────────────────────────────────────────────────────

# sc.exe требует пробел после "binPath=", "start=" и т.п.
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    sc.exe config $ServiceName binPath= "`"$exe`"" start= auto | Out-Null
}
else {
    sc.exe create $ServiceName binPath= "`"$exe`"" start= auto DisplayName= 'HomeLab Control Agent' | Out-Null
}
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/10000/restart/10000 | Out-Null

Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Protocol TCP -LocalPort $servicePort -Action Allow | Out-Null

Start-Service -Name $ServiceName

# ─── Проверка ────────────────────────────────────────────────────────────────

$info = $null
for ($i = 0; $i -lt 15 -and -not $info; $i++) {
    Start-Sleep -Seconds 1
    try { $info = Invoke-RestMethod "http://localhost:$servicePort/api/agent/info" -TimeoutSec 2 } catch { }
}

Write-Host ''
if ($info) {
    Write-Host "Agent v$($info.version) is running: http://$($env:COMPUTERNAME):$servicePort (Swagger)" -ForegroundColor Green
}
else {
    Write-Host "Service started, but the agent does not answer on port $servicePort - see Event Viewer -> Application" -ForegroundColor Yellow
}

$keys = if ($local -and $local.Auth) { $local.Auth.ApiKeys } else { $null }
if (-not $keys) { return }
Write-Host ''
Write-Host "HLC key  (Config -> Agents -> Add existing): $($keys.homelabcontrol)"
Write-Host "HA key   (rest_command header X-Api-Key):    $($keys.homeassistant)"
