# Publica a versão atual do Gear Hub como release no GitHub (é ela que o "Procurar agora" do app baixa).
# Antes: subir a versão no csproj, no versao.json e no CHANGELOG.md, commitar e dar push.
# Precisa do gh logado com permissão no repositório. Publicar é decisão do Felipe.
#
# -SoGerar <pasta>: não fala com o GitHub nem exige commit enviado. Monta GearHub-<versão>.zip na pasta, compilado
# do último commit (trabalho não commitado fica de fora), com os arquivos na raiz do zip: é o que o atualizador do
# app e o setup do Desktop Glow usam.
param([string]$SoGerar = '')
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz

function Rodar([string]$exe, [string[]]$argumentos) {
    # no PowerShell 5.1, com 'Stop', qualquer linha no stderr vira erro fatal mesmo dando certo
    $ErrorActionPreference = 'Continue'
    $saida = & $exe @argumentos 2>&1 | ForEach-Object { "$_" }
    return [pscustomobject]@{ codigo = $LASTEXITCODE; saida = (($saida -join "`n").Trim()) }
}

$info = Get-Content (Join-Path $raiz 'versao.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$versao = $info.versao
$repo = $info.repositorio
$csproj = [IO.File]::ReadAllText((Join-Path $raiz 'src\GearHub\GearHub.csproj'))
if ($csproj -notmatch "<Version>$([regex]::Escape($versao))</Version>") { throw "O GearHub.csproj não está na versão $versao do versao.json." }
$changelog = Get-Content (Join-Path $raiz 'CHANGELOG.md') -Raw -Encoding UTF8
if ($changelog -notmatch "## $([regex]::Escape($versao)) ") { throw "O CHANGELOG.md não tem a seção da versão $versao." }

if (-not $SoGerar) {
    if ((Rodar git @('status', '--porcelain')).saida) { throw 'Há mudanças não commitadas. Commite antes de publicar.' }
    $r = Rodar git @('fetch', '-q', 'origin')
    if ($r.codigo -ne 0) { throw "Não deu para falar com o GitHub (git fetch): $($r.saida)" }
    $local = (Rodar git @('rev-parse', 'HEAD')).saida
    $r = Rodar git @('rev-parse', '@{u}')
    if ($r.codigo -ne 0) { throw 'Este ramo não está ligado ao GitHub. Rode: git push -u origin main' }
    if ($local -ne $r.saida) { throw 'O commit atual ainda não foi enviado (git push) ou está atrás do GitHub.' }
    if ((Rodar gh @('release', 'view', "v$versao", '--repo', $repo)).codigo -eq 0) { throw "A release v$versao já existe." }
}

$tmp = if ($SoGerar) { [IO.Path]::GetFullPath($SoGerar) } else { Join-Path $env:TEMP "gear-hub-release-$versao" }
New-Item -ItemType Directory -Force $tmp | Out-Null
$fonte = Join-Path $tmp 'fonte'
$app = Join-Path $tmp 'app'
foreach ($p in @($fonte, $app)) { if (Test-Path $p) { Remove-Item $p -Recurse -Force } }
New-Item -ItemType Directory -Force $fonte | Out-Null

# do último commit, sem nada que esteja só na pasta
$r = Rodar git @('archive', '--format=tar', '-o', (Join-Path $tmp 'fonte.tar'), 'HEAD')
if ($r.codigo -ne 0) { throw "Não deu para exportar o commit: $($r.saida)" }
$r = Rodar tar @('-xf', (Join-Path $tmp 'fonte.tar'), '-C', $fonte)
if ($r.codigo -ne 0) { throw "Não deu para abrir o commit exportado: $($r.saida)" }
Remove-Item (Join-Path $tmp 'fonte.tar') -Force

# compila com o laço do carimbo contra o Smart App Control (o compilar.ps1 testa o GearHub.exe com --ping)
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fonte 'ferramentas\compilar.ps1') -Publicar $app | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'A compilação falhou ou o Windows barrou todas as tentativas.' }
Get-ChildItem $app -Filter *.pdb | Remove-Item -Force
if (-not (Test-Path (Join-Path $app 'GearHub.exe'))) { throw 'A compilação não deixou o GearHub.exe.' }
Copy-Item (Join-Path $raiz 'versao.json') $app

$zip = Join-Path $tmp "GearHub-$versao.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# entrada por entrada, com barra normal (o ZipFile do PowerShell 5.1 grava com barra invertida)
$arquivo = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    $corte = (Resolve-Path $app).Path.TrimEnd('\').Length + 1
    foreach ($f in Get-ChildItem $app -Recurse -File) {
        $nome = $f.FullName.Substring($corte).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($arquivo, $f.FullName, $nome, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $arquivo.Dispose() }
Remove-Item $fonte -Recurse -Force
Remove-Item $app -Recurse -Force

if ($SoGerar) {
    Write-Host "Pacote: $zip"
    exit 0
}

# notas: só a seção desta versão do CHANGELOG
$linhas = $changelog -split "`r?`n"
$achou = $linhas | Where-Object { $_ -match "^## $([regex]::Escape($versao)) " } | Select-Object -First 1
$inicio = [array]::IndexOf($linhas, $achou)
$notas = @()
for ($i = $inicio + 1; $i -lt $linhas.Length; $i++) { if ($linhas[$i] -match '^## ') { break }; $notas += $linhas[$i] }
$arqNotas = Join-Path $tmp 'notas.md'
[IO.File]::WriteAllText($arqNotas, (($notas -join "`n").Trim()), [Text.UTF8Encoding]::new($false))

$r = Rodar gh @('release', 'create', "v$versao", $zip, '--repo', $repo, '--title', "Gear Hub $versao", '--notes-file', $arqNotas)
if ($r.codigo -ne 0) { throw "O gh não criou a release: $($r.saida)" }
Write-Host "Release v$versao publicada em $repo."
