<#
.SYNOPSIS
    Gate de ENTRADA de uma etapa. Somente leitura.
    Regra: antes da etapa a arvore esta limpa (nenhuma diferenca, nem arquivo novo nao ignorado).

.EXAMPLE
    .\Test-EntradaDeEtapa.ps1 -BranchEsperada feature/migracao-modular -HeadEsperado 8f65104 -MainEsperada 8f65104 -Bundle C:\caminho\backup.bundle
#>
[CmdletBinding()]
param(
    [string]$Raiz = '',
    [string]$BranchEsperada = 'feature/migracao-modular',
    [string]$HeadEsperado = '',
    [string]$MainEsperada = '',
    [string]$Bundle = '',
    [string[]]$Legado = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Comum.ps1"

if (-not $Raiz) { $Raiz = Get-RaizDoRepositorio }
$resultados = @()

$branch = ([string](git -C $Raiz --no-optional-locks rev-parse --abbrev-ref HEAD)).Trim()
$resultados += New-Resultado -Nome ('Branch atual = ' + $BranchEsperada) -Ok ($branch -eq $BranchEsperada) -Detalhe ('branch atual: ' + $branch)

$head = ([string](git -C $Raiz --no-optional-locks rev-parse HEAD)).Trim()
if ($HeadEsperado) {
    $resultados += New-Resultado -Nome ('HEAD comeca por ' + $HeadEsperado) -Ok ($head.StartsWith($HeadEsperado)) -Detalhe ('HEAD atual: ' + $head)
}
if ($MainEsperada) {
    $main = ([string](git -C $Raiz --no-optional-locks rev-parse main)).Trim()
    $resultados += New-Resultado -Nome ('main intocada (comeca por ' + $MainEsperada + ')') -Ok ($main.StartsWith($MainEsperada)) -Detalhe ('main atual: ' + $main)
}

$status = @(Get-StatusDaArvore -Raiz $Raiz)
$resultados += New-Resultado -Nome 'Arvore limpa antes da etapa' -Ok ($status.Count -eq 0) -Detalhe ($status -join "`n")

if ($Bundle) {
    git -C $Raiz bundle verify $Bundle | Out-Null
    $resultados += New-Resultado -Nome 'Backup git bundle valido' -Ok ($LASTEXITCODE -eq 0) -Detalhe $Bundle
}

& "$PSScriptRoot\Test-SegurancaDeScript.ps1" -Caminho $PSScriptRoot | Out-Null
$resultados += New-Resultado -Nome 'Scripts de verificacao aprovados pelo parser AST' -Ok ($LASTEXITCODE -eq 0)

# Solucao: projetos listados pelo dotnet sln == arquivos .csproj rastreados.
$slnRel = @(git -C $Raiz --no-optional-locks ls-files -- '*.slnx' '*.sln')
if ($slnRel.Count -ge 1) {
    $slnCompleto = Join-Path $Raiz ($slnRel[0].Replace('/', '\'))
    $listados = @(dotnet sln $slnCompleto list | Where-Object { $_ -like '*.csproj' } | ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $slnCompleto) $_)) })
    $rastreados = @(git -C $Raiz --no-optional-locks ls-files -- '*.csproj' | ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path $Raiz $_.Replace('/', '\'))) })
    $soNaSolucao = @($listados | Where-Object { $rastreados -notcontains $_ })
    $soRastreados = @($rastreados | Where-Object { $listados -notcontains $_ })
    $detalhe = (@($soNaSolucao | ForEach-Object { 'so na solucao: ' + $_ }) + @($soRastreados | ForEach-Object { 'so rastreado: ' + $_ })) -join "`n"
    $resultados += New-Resultado -Nome 'Projetos da solucao == .csproj rastreados' -Ok (($soNaSolucao.Count -eq 0) -and ($soRastreados.Count -eq 0)) -Detalhe $detalhe
}
else {
    $resultados += New-Resultado -Nome 'Projetos da solucao == .csproj rastreados' -Ok $false -Detalhe 'nenhum .slnx/.sln rastreado'
}

& "$PSScriptRoot\Test-GrafoDeProjetos.ps1" -Raiz $Raiz -Legado $Legado | Out-Null
$resultados += New-Resultado -Nome 'Grafo de projetos conforme a matriz' -Ok ($LASTEXITCODE -eq 0) -Detalhe 'rode Test-GrafoDeProjetos.ps1 para o detalhe'

$falhas = Write-Resultados -Resultados $resultados
if ($falhas -gt 0) { exit 1 }
exit 0
