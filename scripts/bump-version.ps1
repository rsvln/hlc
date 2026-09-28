# bump-version.ps1
# Поднимает patch в <Project>/version.txt, если исходники компонента изменились с прошлого bump.
#
# Компоненты (версии независимые):
#   HomeLabControl       — веб-UI
#   HomeLabControlAgent  — агент (+ ShutdownDialog, он поставляется с агентом)
#
# Обнаружение изменений: SHA256 каждого файла, хранится в <Project>/.src-hash.
#
# Использование:
#   .\scripts\bump-version.ps1 -Project HomeLabControlAgent             - bump, если исходники изменились
#   .\scripts\bump-version.ps1 -Project HomeLabControlAgent -Force      - bump в любом случае
#   .\scripts\bump-version.ps1 -Project HomeLabControlAgent -Diagnose   - показать изменённые файлы, без bump
#   .\scripts\bump-version.ps1 -Project HomeLabControlAgent -Init       - зафиксировать текущие исходники как базу, без bump

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('HomeLabControl', 'HomeLabControlAgent')]
    [string]$Project,
    [switch]$Force,
    [switch]$Diagnose,
    [switch]$Init
)

$root        = Split-Path -Parent $PSScriptRoot
$projectDir  = Join-Path $root $Project
$versionFile = Join-Path $projectDir 'version.txt'
$hashFile    = Join-Path $projectDir '.src-hash'
$utf8NoBom   = [Text.UTF8Encoding]::new($false)

# Каталоги, исходники которых входят в компонент
$sourceDirs = @($Project)
if ($Project -eq 'HomeLabControlAgent') { $sourceDirs += 'ShutdownDialog' }

$extensions = '.cs', '.csproj', '.props', '.json', '.js', '.razor', '.cshtml', '.css', '.ps1', '.sh', '.cmd', '.service'

function Get-FileHash256([string]$path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    return [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($path))).Replace('-', '').ToLower()
}

# относительный путь -> hash
function Get-SourceFileHashes {
    $abs = [IO.Path]::GetFullPath($root)
    $result = [ordered]@{}
    foreach ($dir in $sourceDirs) {
        Get-ChildItem (Join-Path $root $dir) -Recurse -File |
            Where-Object { $_.Extension -in $extensions -or $_.Name -eq 'Dockerfile' } |
            Sort-Object FullName |
            ForEach-Object {
                $rel = $_.FullName.Substring($abs.Length).TrimStart('\', '/')
                if ($rel -match '[\\/](bin|obj|Properties)[\\/]') { return }
                # Исходник YAML-редактора (npm): в приложение попадает только собранный wwwroot/js/yaml-editor.js
                if ($rel -match '[\\/]webui[\\/]') { return }
                $result[$rel] = Get-FileHash256 $_.FullName
            }
    }
    return $result
}

function Read-StoredHashes {
    $result = [ordered]@{}
    if (-not (Test-Path $hashFile)) { return $result }
    foreach ($line in (Get-Content $hashFile)) {
        $eq = $line.IndexOf('=')
        if ($eq -gt 0) { $result[$line.Substring(0, $eq)] = $line.Substring($eq + 1) }
    }
    return $result
}

$version = (Get-Content $versionFile -Raw).Trim()
$current = Get-SourceFileHashes
$stored  = Read-StoredHashes

$changed = @()
foreach ($f in $current.Keys) {
    if (-not $stored.Contains($f))        { $changed += "+ $f" }
    elseif ($stored[$f] -ne $current[$f]) { $changed += "~ $f" }
}
foreach ($f in $stored.Keys) {
    if (-not $current.Contains($f))       { $changed += "- $f" }
}

if ($Diagnose) {
    Write-Host "$Project v$version"
    if ($changed.Count -eq 0) { Write-Host "  No changes." } else { $changed | ForEach-Object { Write-Host "  $_" } }
    exit 0
}

function Write-Hashes {
    [IO.File]::WriteAllText($hashFile, (($current.Keys | ForEach-Object { "$_=$($current[$_])" }) -join "`n"), $utf8NoBom)
}

if ($Init) {
    Write-Hashes
    Write-Host "  ${Project}: $version (baseline recorded)"
    exit 0
}

if ($changed.Count -eq 0 -and -not $Force) {
    Write-Host "  ${Project}: $version (no changes, skipped)"
    exit 0
}

$parts = $version.Split('.')
$parts[2] = [int]$parts[2] + 1
$newVersion = $parts -join '.'
[IO.File]::WriteAllText($versionFile, $newVersion, $utf8NoBom)
Write-Hashes
Write-Host "  ${Project}: $version -> $newVersion"
