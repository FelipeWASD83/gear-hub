# Instala (ou atualiza) o Gear Hub em %LOCALAPPDATA%\Programs\GearHub, com atalho no Menu Iniciar.
# A cópia instalada grava sozinha o "abrir com o Windows" (HKCU\...\Run, valor GearHub) quando abre.
# Ajustes e perfis ficam em %USERPROFILE%\.gear-hub e não são tocados.
#
# Uso (na pasta do projeto):
#   powershell -ExecutionPolicy Bypass -File .\ferramentas\instalar.ps1 [-NaoAbrir]
#   powershell -ExecutionPolicy Bypass -File .\ferramentas\instalar.ps1 -Desinstalar
#
# Rodar num terminal normal, não de dentro do app do Claude: processo aberto pelo app (pacote MSIX) enxerga um
# AppData e um registro virtuais, e a instalação iria para a cópia virtual.

param([switch]$NaoAbrir, [switch]$Desinstalar)
$ErrorActionPreference = 'Stop'
$destino = Join-Path $env:LOCALAPPDATA 'Programs\GearHub'
$programas = [Environment]::GetFolderPath('Programs')
$atalhos = @((Join-Path $programas 'Central de Periféricos.lnk'), (Join-Path $programas 'Gear Hub.lnk'))
$pt = (Get-Culture).TwoLetterISOLanguageName -eq 'pt'
$atalho = if ($pt) { $atalhos[0] } else { $atalhos[1] }
$temp = Join-Path $env:TEMP 'gear-hub-instalar'

function Fechar-App {
    $abertos = Get-Process GearHub -ErrorAction SilentlyContinue
    if (-not $abertos) { return }
    Write-Host 'Fechando o Gear Hub aberto...'
    $exeAberto = $abertos[0].Path
    if ($exeAberto) { Start-Process $exeAberto -ArgumentList '--quit' -Wait -ErrorAction SilentlyContinue }
    foreach ($p in $abertos) { $p.WaitForExit(10000) | Out-Null }
    Get-Process GearHub -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($Desinstalar) {
    Fechar-App
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $valor = (Get-ItemProperty -Path $run -Name 'GearHub' -ErrorAction SilentlyContinue).GearHub
    if ($valor -and $valor -like '*GearHub.exe*') { Remove-ItemProperty -Path $run -Name 'GearHub' }
    foreach ($a in $atalhos) { if (Test-Path $a) { Remove-Item $a -Force } }
    if (Test-Path $destino) { Remove-Item $destino -Recurse -Force }
    Write-Host 'Gear Hub desinstalado (os ajustes em %USERPROFILE%\.gear-hub ficaram).'
    exit 0
}

Write-Host 'Compilando (com o teste do Smart App Control)...'
if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'compilar.ps1') -Publicar $temp
if ($LASTEXITCODE -ne 0) { throw 'A compilação falhou ou o Smart App Control barrou todas as tentativas (veja acima).' }
if (-not (Test-Path (Join-Path $temp 'icone.ico'))) { throw 'O icone.ico não foi junto na publicação.' }

Fechar-App

Write-Host "Instalando em $destino"
if (Test-Path $destino) { Get-ChildItem $destino -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $destino | Out-Null
Copy-Item (Join-Path $temp '*') $destino -Recurse -Force
Remove-Item $temp -Recurse -Force

$exe = Join-Path $destino 'GearHub.exe'
foreach ($a in $atalhos) { if ($a -ne $atalho -and (Test-Path $a)) { Remove-Item $a -Force } }
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($atalho)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $destino
$lnk.IconLocation = "$(Join-Path $destino 'icone.ico'),0"
$lnk.Description = if ($pt) { 'Central de Periféricos' } else { 'Gear Hub' }
$lnk.Save()
Write-Host "Atalho no Menu Iniciar: $atalho"

if (-not $NaoAbrir) {
    Start-Process $exe
    Write-Host 'Gear Hub aberto (ele grava o abrir com o Windows sozinho).'
}
Write-Host 'Pronto.'
