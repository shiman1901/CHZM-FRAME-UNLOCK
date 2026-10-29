#requires -Version 5.1
<#
Собирает portable-папку, ZIP и self-extracting Setup.exe.
Пакеты DLSS\ и ДОПОЛНЕНИЕ\ должны лежать в корне проекта.
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$portable = Join-Path $dist 'portable\CHZM FRAME UNLOCK'
$appPublish = Join-Path $dist 'app-publish'
$setupPublish = Join-Path $dist 'setup-publish'
$payloadZip = Join-Path $dist 'payload.zip'
$portableZip = Join-Path $dist 'CHZM FRAME UNLOCK portable.zip'
$setupExe = Join-Path $dist 'CHZM FRAME UNLOCK Setup.exe'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist, $portable, $appPublish, $setupPublish -Force | Out-Null

foreach ($required in @('DLSS', 'ДОПОЛНЕНИЕ')) {
    if (-not (Test-Path (Join-Path $root $required))) {
        throw "Не найдена папка $required рядом с проектом."
    }
}

Write-Host 'BUILD // app'
dotnet publish (Join-Path $root 'CHZMFRAMEUNLOCK1.csproj') -c Release -o $appPublish | Out-Null

Write-Host 'COPY // portable'
Copy-Item (Join-Path $appPublish 'CHZMFRAMEUNLOCK1.exe') (Join-Path $portable 'CHZM FRAME UNLOCK.exe') -Force
Copy-Item (Join-Path $root 'app.ico') $portable -Force
Copy-Item (Join-Path $root 'DLSS') $portable -Recurse -Force
Copy-Item (Join-Path $root 'ДОПОЛНЕНИЕ') $portable -Recurse -Force

$readme = @"
CHZM FRAME UNLOCK // ALPHA BUILD
================================
1. Запускай CHZM FRAME UNLOCK.exe от имени администратора.
2. НЕ УДАЛЯЙ папки DLSS и ДОПОЛНЕНИЕ.
3. LIVE FLOW: F9 или удержание Nitro [N].
4. CLASSIC / ENCORE / SMOOTH: выбери метод в правой панели.
5. Для античит-игр используй только LIVE FLOW.

ALPHA BUILD // возможны ошибки.
"@
[IO.File]::WriteAllText((Join-Path $portable 'README.txt'), $readme, (New-Object Text.UTF8Encoding($true)))

Write-Host 'ZIP // portable'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    (Split-Path -Parent $portable), $portableZip,
    [IO.Compression.CompressionLevel]::Fastest, $true)

Write-Host 'BUILD // setup stub'
dotnet publish (Join-Path $root 'tools\Setup\CHZMSetup.csproj') -c Release -o $setupPublish | Out-Null

Write-Host 'ZIP // setup payload'
[IO.Compression.ZipFile]::CreateFromDirectory(
    (Split-Path -Parent $portable), $payloadZip,
    [IO.Compression.CompressionLevel]::Fastest, $true)

$stub = Join-Path $setupPublish 'CHZM_FRAME_UNLOCK_Setup.exe'
if (-not (Test-Path $stub)) { throw 'Setup stub не собран.' }

Write-Host 'PACK // Setup.exe'
Copy-Item $stub $setupExe -Force
$payloadLength = (Get-Item $payloadZip).Length
$fs = [IO.File]::Open($setupExe, [IO.FileMode]::Append, [IO.FileAccess]::Write)
try {
    $zip = [IO.File]::OpenRead($payloadZip)
    try { $zip.CopyTo($fs) } finally { $zip.Close() }

    $bw = New-Object System.IO.BinaryWriter -ArgumentList $fs
    $bw.Write([int64]$payloadLength)
    $bw.Write([Text.Encoding]::ASCII.GetBytes('CHZM_PAYLOAD_001'))
    $bw.Flush()
    $bw.Close()
} finally {
    $fs.Close()
}

Remove-Item $payloadZip -Force
Remove-Item $appPublish -Recurse -Force
Remove-Item $setupPublish -Recurse -Force

Write-Host ''
Write-Host 'DONE'
Get-Item $portableZip, $setupExe | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
Get-FileHash $portableZip, $setupExe -Algorithm SHA256 |
    Select-Object Path, Hash | Format-Table -AutoSize
