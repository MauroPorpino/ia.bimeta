<#
.SYNOPSIS
    Gate de SAIDA de uma etapa, antes do commit. Somente leitura.
    Regra: depois das alteracoes, a arvore so pode diferir do HEAD pelas diferencas autorizadas
    (arquivo -Autorizadas). Arquivo ou pasta fora da lista, e qualquer exclusao nao listada, reprovam.

    Formato do arquivo -Autorizadas, uma diferenca por linha:
        A caminho/relativo           arquivo novo (staged ou nao rastreado)
        M caminho/relativo           arquivo modificado
        D caminho/relativo           arquivo excluido
        R origem => destino          renomeacao (use git mv, para o git reconhecer)
    Linhas vazias e iniciadas por # sao ignoradas.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Autorizadas,
    [string]$Raiz = '',
    [string]$BranchEsperada = 'feature/migracao-modular',
    [string]$MainEsperada = '',
    [bool]$ExigirTodas = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Comum.ps1"

if (-not $Raiz) { $Raiz = Get-RaizDoRepositorio }
$resultados = @()

$branch = ([string](git -C $Raiz --no-optional-locks rev-parse --abbrev-ref HEAD)).Trim()
$resultados += New-Resultado -Nome ('Branch atual = ' + $BranchEsperada) -Ok ($branch -eq $BranchEsperada) -Detalhe ('branch atual: ' + $branch)

if ($MainEsperada) {
    $main = ([string](git -C $Raiz --no-optional-locks rev-parse main)).Trim()
    $resultados += New-Resultado -Nome ('main intocada (comeca por ' + $MainEsperada + ')') -Ok ($main.StartsWith($MainEsperada)) -Detalhe ('main atual: ' + $main)
}

$reais = @(Get-AlteracoesDaArvore -Raiz $Raiz)
$permitidas = @(Read-AlteracoesAutorizadas -Caminho $Autorizadas)
Write-Host ('Diferencas reais em relacao ao HEAD: {0}; autorizadas: {1}' -f $reais.Count, $permitidas.Count)
foreach ($r in $reais) { Write-Host ('  ' + $r) }
$resultados += @(Compare-Alteracoes -Reais $reais -Permitidas $permitidas -ExigirTodas $ExigirTodas)

& "$PSScriptRoot\Test-FormatoDeArquivos.ps1" -Raiz $Raiz | Out-Null
$resultados += New-Resultado -Nome 'Formato dos arquivos alterados' -Ok ($LASTEXITCODE -eq 0) -Detalhe 'rode Test-FormatoDeArquivos.ps1 para o detalhe'

& "$PSScriptRoot\Test-SegurancaDeScript.ps1" -Caminho $PSScriptRoot | Out-Null
$resultados += New-Resultado -Nome 'Scripts de verificacao aprovados pelo parser AST' -Ok ($LASTEXITCODE -eq 0) -Detalhe 'rode Test-SegurancaDeScript.ps1 para o detalhe'

$falhas = Write-Resultados -Resultados $resultados
if ($falhas -gt 0) { exit 1 }
exit 0
