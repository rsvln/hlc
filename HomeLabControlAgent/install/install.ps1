<#
.SYNOPSIS
    Установка / обновление HomeLabControlAgent как службы Windows.

.DESCRIPTION
    Запускать из папки сборки (или install.cmd двойным щелчком — права администратора запросит сам):
        .\install.ps1                                   # обновить установленный агент или новая установка (спросит путь и порт)
        .\install.ps1 -InstallPath D:\hlca -Port 8118
        .\install.ps1 -GenerateKeys                     # включить ключи API у агента, который работал без них
        .\install.ps1 -Uninstall

    Уже установленный агент (служба с HomeLabControlAgent.exe) находится сам: путь и имя службы берутся у неё.
    Файлы копируются в InstallPath, кроме appsettings.Local.json и profiles.json (настройки машины).
    Ключ API (один на машину) создаётся при новой установке или с -GenerateKeys; существующие ключи
    не меняются никогда. В конце ключ печатается: вписать в HLC (Config -> Agents -> Add existing)
    и в rest_command HA (заголовок X-Api-Key).
#>
param(
    [string]$InstallPath = '',
    [string]$ServiceName = '',
    [int]$Port = 0,
    [switch]$GenerateKeys,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Перезапуск с повышением; -NoExit — чтобы окно с ключами не закрылось
    $arguments = @('-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($InstallPath) { $arguments += @('-InstallPath', "`"$InstallPath`"") }
    if ($ServiceName) { $arguments += @('-ServiceName', $ServiceName) }
    if ($Port -gt 0) { $arguments += @('-Port', $Port) }
    if ($GenerateKeys) { $arguments += '-GenerateKeys' }
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

$exeName = 'HomeLabControlAgent.exe'

# Уже установленный агент: служба с этим именем или любая служба, запускающая HomeLabControlAgent.exe
$existing = Get-CimInstance Win32_Service |
    Where-Object { ($ServiceName -and $_.Name -eq $ServiceName) -or (-not $ServiceName -and $_.PathName -match 'HomeLabControlAgent\.exe') } |
    Select-Object -First 1
if ($existing) {
    if (-not $ServiceName) { $ServiceName = $existing.Name }
    if (-not $InstallPath) { $InstallPath = Split-Path ($existing.PathName.Trim().Trim('"')) -Parent }
    Write-Host "Installed agent found: service $ServiceName in $InstallPath"
}
if (-not $ServiceName) { $ServiceName = 'homeLabControlAgent' }
if (-not $InstallPath) {
    if ($Uninstall) {
        Write-Host 'No installed homeLabControlAgent found - nothing to uninstall (or pass -InstallPath / -ServiceName)'
        return
    }
    # Новая установка: путь и порт спрашиваем, Enter — значение по умолчанию (D:\apps, если есть диск D:, иначе C:\apps)
    $hasD = [IO.DriveInfo]::GetDrives() | Where-Object { $_.Name -eq 'D:\' -and $_.DriveType -eq 'Fixed' }
    $defaultPath = if ($hasD) { 'D:\apps\homeLabControlAgent' } else { 'C:\apps\homeLabControlAgent' }
    $InstallPath = Read-Default 'Install path' $defaultPath
    if ($Port -le 0) {
        $answer = Read-Default 'Port' '8117'
        if ($answer -ne '8117') { $Port = [int]$answer }
    }
}
$isUpdate = [bool]$existing -or (Test-Path (Join-Path $InstallPath $exeName))

$exe = Join-Path $InstallPath $exeName
$localSettings = Join-Path $InstallPath 'appsettings.Local.json'
$firewallRule = 'HomeLab Control Agent'

# Тот же формат, что у HomeLabControlAgent --generate-key: 32 случайных байта, base64url
function New-ApiKey {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

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
elseif ($isUpdate -and -not $GenerateKeys) {
    # Агент работал без ключей: новые ключи сломали бы HA и HLC, которые ходят без них
    $local = $null
    if ($Port -gt 0) {
        [ordered]@{ ServicePort = $Port } | ConvertTo-Json | Set-Content $localSettings -Encoding UTF8
        $local = Get-Content $localSettings -Raw | ConvertFrom-Json
    }
    Write-Host 'WARNING: this agent has no API keys - the API stays OPEN as before.' -ForegroundColor Yellow
    Write-Host '         To enable keys run: install.cmd -GenerateKeys, then put the keys into HLC and HA.' -ForegroundColor Yellow
}
else {
    $local = [ordered]@{
        Auth = [ordered]@{
            ApiKeys = [ordered]@{
                homelabcontrol = New-ApiKey
            }
        }
    }
    if ($Port -gt 0) { $local.ServicePort = $Port }
    $local | ConvertTo-Json -Depth 10 | Set-Content $localSettings -Encoding UTF8
    $local = Get-Content $localSettings -Raw | ConvertFrom-Json
    Write-Host 'appsettings.Local.json created with a new API key'
}

$servicePort = if ($local -and $local.ServicePort) { [int]$local.ServicePort } elseif ($Port -gt 0) { $Port } else { 8117 }

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
Write-Host "API key: $($keys.homelabcontrol)"
Write-Host '  -> HLC: Config -> Agents -> Add existing; HA: rest_command header X-Api-Key'
