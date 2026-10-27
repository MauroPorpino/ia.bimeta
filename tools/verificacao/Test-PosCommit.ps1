<#
.SYNOPSIS
    Gate APOS O COMMIT aprovado. Somente leitura.
    Regra: depois do commit aprovado a arvore esta limpa, o commit e unico sobre o pai esperado
    e contem exatamente as diferencas autorizadas.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PaiEsperado,
    [Parameter(Mandatory = $true)][string]$Autorizadas,
    [string]$Raiz = '',
    [string]$BranchEsperada = 'feature/migracao-modular',
    [string]$MainEsperada = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Comum.ps1"

if (-not $Raiz) { $Raiz = Get-RaizDoRepositorio }
$resultados = @()

$status = @(Get-StatusDaArvore -Raiz $Raiz)
$resultados += New-Resultado -Nome 'Arvore limpa depois do commit' -Ok ($status.Count -eq 0) -Detalhe ($status -join "`n")

$branch = ([string](git -C $Raiz --no-optional-locks rev-parse --abbrev-ref HEAD)).Trim()
$resultados += New-Resultado -Nome ('Branch atual = ' + $BranchEsperada) -Ok ($branch -eq $BranchEsperada) -Detalhe ('branch atual: ' + $branch)

$pai = ([string](git -C $Raiz --no-optional-locks rev-parse 'HEAD~1')).Trim()
$resultados += New-Resultado -Nome ('Pai do commit comeca por ' + $PaiEsperado) -Ok ($pai.StartsWith($PaiEsperado)) -Detalhe ('pai: ' + $pai)

$quantos = ([string](git -C $Raiz --no-optional-locks rev-list --count ($PaiEsperado + '..HEAD'))).Trim()
$resultados += New-Resultado -Nome 'Exatamente um commit sobre o pai esperado' -Ok ($quantos -eq '1') -Detalhe ('commits: ' + $quantos)

if ($MainEsperada) {
    $main = ([string](git -C $Raiz --no-optional-locks rev-parse main)).Trim()
    $resultados += New-Resultado -Nome ('main intocada (comeca por ' + $MainEsperada + ')') -Ok ($main.StartsWith($MainEsperada)) -Detalhe ('main atual: ' + $main)
}

$doCommit = @(Get-AlteracoesDoCommit -Raiz $Raiz -Ref 'HEAD')
$permitidas = @(Read-AlteracoesAutorizadas -Caminho $Autorizadas)
Write-Host ('Diferencas do commit: {0}; autorizadas: {1}' -f $doCommit.Count, $permitidas.Count)
foreach ($r in $doCommit) { Write-Host ('  ' + $r) }
$resultados += @(Compare-Alteracoes -Reais $doCommit -Permitidas $permitidas -ExigirTodas $true)

$falhas = Write-Resultados -Resultados $resultados
if ($falhas -gt 0) { exit 1 }
exit 0
