<#
.SYNOPSIS
    Executa os testes da solucao e confere falhas, aprovados e ignorados. Nao altera arquivos rastreados.
    Sem -Compilar usa os binarios existentes (dotnet test --no-build).
    Com -Compilar o dotnet test compila antes e grava apenas em bin/obj (ignorados pelo Git) e no cache do NuGet.

.DESCRIPTION
    Sempre registra (linhas INFO) os testes ignorados e a situacao do teste de aceitacao Mauro/Pedro, mas
    NAO exige que ele continue ignorado: ele pode ser ativado ou substituido. Exigencias opcionais:
    -AprovadosEsperados e -IgnoradosEsperados (-1 = nao verificar) e -ExigirSemIgnorados (zero ignorados).
#>
[CmdletBinding()]
param(
    [string]$Raiz = '',
    [string]$Solucao = 'backend\IaBimeta.slnx',
    [switch]$Compilar,
    [int]$AprovadosEsperados = -1,
    [int]$IgnoradosEsperados = -1,
    [int]$FalhasEsperadas = 0,
    [switch]$ExigirSemIgnorados
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Comum.ps1"

if (-not $Raiz) { $Raiz = Get-RaizDoRepositorio }
$sln = Join-Path $Raiz $Solucao

# Idioma fixo em ingles para o resumo do dotnet test (apenas neste processo).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:VSLANG = '1033'

if ($Compilar) {
    $saida = @(dotnet test $sln --nologo --verbosity minimal)
}
else {
    $saida = @(dotnet test $sln --no-build --nologo --verbosity minimal)
}
$codigo = $LASTEXITCODE
$texto = ($saida -join "`n")

$falhas = 0
$aprovados = 0
$ignorados = 0
$resumos = [regex]::Matches($texto, '(?:Failed|Com falha):\s*(\d+),\s*(?:Passed|Aprovado):\s*(\d+),\s*(?:Skipped|Ignorado):\s*(\d+)')
foreach ($m in $resumos) {
    $falhas += [int]$m.Groups[1].Value
    $aprovados += [int]$m.Groups[2].Value
    $ignorados += [int]$m.Groups[3].Value
}

Write-Host ('dotnet test: codigo de saida {0}; resumos lidos: {1}' -f $codigo, $resumos.Count)
foreach ($l in ($saida | Select-Object -Last 6)) { Write-Host ('  ' + $l) }

# Situacao registrada (nao reprova): testes ignorados e o teste de aceitacao Mauro/Pedro.
$linhasIgnoradas = @($saida | Where-Object { $_ -match '\[SKIP\]' })
Write-Host ('INFO   Testes ignorados nesta execucao: ' + $ignorados)
foreach ($l in $linhasIgnoradas) { Write-Host ('INFO     ' + ($l -replace '^\[xUnit\.net [0-9:.]+\]\s*', '')) }
if ($texto -match 'Contexto_de_um_atendimento_nao_vaza_para_outro.*\[SKIP\]') {
    Write-Host 'INFO   Teste Mauro/Pedro (nivel de conversa): IGNORADO. Deve ser ativado ou substituido antes da validacao funcional multi-tenant.'
}
else {
    Write-Host 'INFO   Teste Mauro/Pedro (Contexto_de_um_atendimento_nao_vaza_para_outro): nao esta ignorado (ativo, substituido ou removido).'
}

$resultados = @()
$resultados += New-Resultado -Nome 'dotnet test terminou com codigo 0' -Ok ($codigo -eq 0) -Detalhe ('codigo ' + $codigo)
$resultados += New-Resultado -Nome 'Resumo de testes encontrado na saida' -Ok ($resumos.Count -gt 0)
$resultados += New-Resultado -Nome ('Falhas = ' + $FalhasEsperadas) -Ok ($falhas -eq $FalhasEsperadas) -Detalhe ('encontradas: ' + $falhas)
if ($AprovadosEsperados -ge 0) {
    $resultados += New-Resultado -Nome ('Aprovados = ' + $AprovadosEsperados) -Ok ($aprovados -eq $AprovadosEsperados) -Detalhe ('encontrados: ' + $aprovados)
}
if ($IgnoradosEsperados -ge 0) {
    $resultados += New-Resultado -Nome ('Ignorados = ' + $IgnoradosEsperados) -Ok ($ignorados -eq $IgnoradosEsperados) -Detalhe ('encontrados: ' + $ignorados)
}
if ($ExigirSemIgnorados) {
    $resultados += New-Resultado -Nome 'Nenhum teste ignorado' -Ok ($ignorados -eq 0) -Detalhe ('ignorados: ' + $ignorados)
}
Write-Host ('INFO   Aprovados nesta execucao: ' + $aprovados)

$total = Write-Resultados -Resultados $resultados
if ($total -gt 0) { exit 1 }
exit 0
