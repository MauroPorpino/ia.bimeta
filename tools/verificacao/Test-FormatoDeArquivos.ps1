<#
.SYNOPSIS
    Confere o formato dos arquivos de texto. Somente leitura.
    ERRO (reprova): fim de linha CRLF ou misto no que sera commitado (indice; para arquivo novo nao
    rastreado, o proprio arquivo), BOM, ausencia de nova linha final ou caractere nao ASCII em .ps1
    quando o arquivo e NOVO; qualquer .ps1 com caractere nao ASCII.
    AVISO (nao reprova): BOM ou falta de nova linha final em arquivo que ja existia no HEAD. Sao
    pendencias herdadas dos templates do dotnet, nao regressao da etapa.
    O CRLF da copia de trabalho nao conta: o .gitattributes (text eol=lf) normaliza para LF no commit.
    Por padrao verifica os arquivos adicionados, modificados ou renomeados em relacao ao HEAD.

.PARAMETER Todos
    Verifica todos os arquivos rastreados (todos tratados como ja existentes no HEAD).

.PARAMETER Arquivos
    Lista explicita de caminhos relativos a raiz do repositorio (tratados como novos).
#>
[CmdletBinding()]
param(
    [string]$Raiz = '',
    [switch]$Todos,
    [string[]]$Arquivos = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Comum.ps1"

if (-not $Raiz) { $Raiz = Get-RaizDoRepositorio }

# Fim de linha de cada arquivo no INDICE (o que sera commitado).
$eolDoIndice = @{}
foreach ($linha in @(git -C $Raiz --no-optional-locks ls-files --eol)) {
    $partes = $linha -split "`t", 2
    if ($partes.Count -eq 2) {
        $m = [regex]::Match($partes[0], '^i/(\S+)')
        if ($m.Success) { $eolDoIndice[$partes[1]] = $m.Groups[1].Value }
    }
}

# Lista de entradas: caminho e se o arquivo e novo ('Novo') ou ja existia no HEAD ('Existente').
$entradas = @()
if ($Arquivos.Count -gt 0) {
    foreach ($a in $Arquivos) { $entradas += [pscustomobject]@{ Caminho = $a; Situacao = 'Novo' } }
}
elseif ($Todos) {
    foreach ($a in @(git -C $Raiz --no-optional-locks ls-files)) { $entradas += [pscustomobject]@{ Caminho = $a; Situacao = 'Existente' } }
}
else {
    foreach ($alt in (Get-AlteracoesDaArvore -Raiz $Raiz)) {
        if ($alt -match '^A (.+)$') { $entradas += [pscustomobject]@{ Caminho = $Matches[1]; Situacao = 'Novo' } }
        elseif ($alt -match '^M (.+)$') { $entradas += [pscustomobject]@{ Caminho = $Matches[1]; Situacao = 'Existente' } }
        elseif ($alt -match '^R .+ => (.+)$') { $entradas += [pscustomobject]@{ Caminho = $Matches[1]; Situacao = 'Existente' } }
    }
}

$extensoesDeTexto = @('.cs', '.csproj', '.props', '.targets', '.json', '.md', '.ps1', '.slnx', '.sln', '.config', '.yml', '.yaml', '.txt', '.editorconfig', '.gitattributes', '.gitignore')
$erros = @()
$avisos = @()
$verificados = 0
foreach ($e in $entradas) {
    $rel = $e.Caminho
    $extensao = [System.IO.Path]::GetExtension($rel).ToLowerInvariant()
    if ($extensoesDeTexto -notcontains $extensao) { continue }
    $completo = Join-Path $Raiz ($rel.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $completo -PathType Leaf)) { continue }
    $verificados++
    $bytes = [System.IO.File]::ReadAllBytes($completo)
    if ($bytes.Length -eq 0) { continue }

    $temBom = ($bytes.Length -ge 3) -and ($bytes[0] -eq 239) -and ($bytes[1] -eq 187) -and ($bytes[2] -eq 191)
    $cr = 0
    $naoAscii = 0
    foreach ($b in $bytes) {
        if ($b -eq 13) { $cr++ }
        if ($b -gt 127) { $naoAscii++ }
    }

    if ($eolDoIndice.ContainsKey($rel)) {
        $eol = $eolDoIndice[$rel]
        if (($eol -eq 'crlf') -or ($eol -eq 'mixed')) { $erros += ($rel + ': no indice o fim de linha e ' + $eol + ' (esperado lf)') }
    }
    elseif ($cr -gt 0) {
        $erros += ($rel + ': arquivo novo com ' + $cr + ' caractere(s) CR (esperado LF)')
    }

    if ($temBom) {
        if ($e.Situacao -eq 'Novo') { $erros += ($rel + ': arquivo novo com BOM UTF-8') }
        else { $avisos += ($rel + ': BOM UTF-8 (ja existia no HEAD)') }
    }
    if ($bytes[$bytes.Length - 1] -ne 10) {
        if ($e.Situacao -eq 'Novo') { $erros += ($rel + ': arquivo novo sem nova linha no fim') }
        else { $avisos += ($rel + ': sem nova linha no fim (ja existia no HEAD)') }
    }
    if (($extensao -eq '.ps1') -and ($naoAscii -gt 0)) { $erros += ($rel + ': script com caractere nao ASCII') }
}

Write-Host ('Arquivos de texto verificados: ' + $verificados)
foreach ($a in $avisos) { Write-Host ('AVISO  ' + $a) }
$resultados = @(New-Resultado -Nome 'Formato: LF no que sera commitado, sem BOM nem falta de nova linha em arquivo novo, scripts ASCII' -Ok ($erros.Count -eq 0) -Detalhe ($erros -join "`n"))
$falhas = Write-Resultados -Resultados $resultados
if ($falhas -gt 0) { exit 1 }
exit 0
