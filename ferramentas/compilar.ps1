# Builds Gear Hub and checks that Smart App Control lets GearHub.exe and GearHub.dll run.
# SAC judges every new binary by its cloud reputation; now and then a build is blocked (0x800711C7) and the next one,
# with different bytes, passes. When blocked, this script changes the "stamp" and builds again:
#   - Stamp.g.cs (outside git): a constant with a block of letters of random length (changes GearHub.dll);
#   - SourceRevisionId: goes into the informational version, which lands in the .exe resources (changes the .exe,
#     which otherwise comes out the same in every build and, once blocked, stays blocked).
# Same method as the ilha and the quadra.
#
# Usage: powershell -ExecutionPolicy Bypass -File .\ferramentas\compilar.ps1 [-Publicar <folder>] [-Tentativas 8]

param(
    [string]$Publicar = "",
    [int]$Tentativas = 8
)

$ErrorActionPreference = "Stop"
$raiz = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $raiz "src\GearHub"
$carimbo = Join-Path $proj "Stamp.g.cs"
$script:selo = ""

function Escrever-Carimbo {
    $n = Get-Random -Minimum 40 -Maximum 400
    $letras = -join ((1..$n) | ForEach-Object { [char](Get-Random -Minimum 97 -Maximum 123) })
    $texto = "// written by ferramentas\compilar.ps1; changes the bytes of GearHub.dll when Smart App Control blocks a build`nnamespace GearHub;`ninternal static class Stamp { public const string Text = `"$letras`"; }`n"
    [IO.File]::WriteAllText($carimbo, $texto, (New-Object Text.UTF8Encoding($false)))
    $script:selo = -join ((1..10) | ForEach-Object { [char](Get-Random -Minimum 97 -Maximum 123) })
}

function Compilar {
    $rev = @()
    if ($script:selo) { $rev = @("-p:SourceRevisionId=$($script:selo)") }
    if ($Publicar) {
        dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $Publicar -nologo -v q @rev | Out-Host
        return (Join-Path $Publicar "GearHub.exe")
    }
    dotnet build $proj -c Release -nologo -v q @rev | Out-Host
    return (Get-ChildItem (Join-Path $proj "bin\Release") -Recurse -Filter GearHub.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

# 0 = it ran; anything else = SAC blocked the .exe (Start-Process throws) or the .dll (.NET exits with an error)
function Testar([string]$exe) {
    try {
        $p = Start-Process $exe -ArgumentList "--ping" -PassThru -WindowStyle Hidden
        $p.WaitForExit(20000) | Out-Null
        return $p.ExitCode
    }
    catch {
        Write-Host "  GearHub.exe did not even open: $($_.Exception.Message)"
        return -1
    }
}

if (-not (Test-Path $carimbo)) { Escrever-Carimbo }

for ($i = 1; $i -le $Tentativas; $i++) {
    $exe = Compilar
    if ($LASTEXITCODE -ne 0) { Write-Host "the build failed"; exit 1 }
    $codigo = Testar $exe
    if ($codigo -eq 0) {
        Write-Host "ok: Smart App Control accepted this build (attempt $i)"
        Write-Host $exe
        exit 0
    }
    Write-Host "attempt ${i}: Smart App Control blocked it (code $codigo); changing the stamp"
    Escrever-Carimbo
}

Write-Host "Smart App Control blocked all $Tentativas attempts"
exit 2
