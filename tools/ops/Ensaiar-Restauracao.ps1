<#
.SYNOPSIS
    ENSAIO de restauracao: faz o backup do banco de origem, restaura em um banco ISOLADO, confere a integridade e as
    contagens, e remove o que criou. Prova que o backup restaura e mede o tempo. Nao altera o banco de origem.

.DESCRIPTION
    Modos (-Modo):
      Instancia (padrao)  Sobe uma INSTANCIA PostgreSQL descartavel (initdb numa pasta temporaria dentro da pasta de backups,
                          porta livre, so loopback, senha aleatoria) e restaura nela. Nao exige privilegio no servidor do
                          projeto e nao toca em nenhum banco real; a instancia inteira e apagada no fim.
      Servidor            Cria o banco iabimeta_ensaio_<data-hora> no proprio servidor de -Origem (exige um papel com
                          CREATEDB) e o remove no fim. So remove o banco que este ensaio criou, e so com esse prefixo.

    Com -Manter, nada e apagado no fim (a instancia fica ligada em uma porta local e o banco fica para inspecao): remova
    depois, por uma pessoa. O backup do ensaio fica na pasta de backups e vale como backup verificado.

    Para a comparacao exata de contagens, o banco de origem deve estar em repouso (Api e Worker parados) durante o ensaio.

.EXAMPLE
    .\tools\ops\Ensaiar-Restauracao.ps1 -Origem Teste
    .\tools\ops\Ensaiar-Restauracao.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Desenvolvimento', 'Teste', 'Ambiente')][string]$Origem,
    [string]$VariavelDeConexao,
    [ValidateSet('Instancia', 'Servidor')][string]$Modo = 'Instancia',
    [switch]$Manter,
    [string]$PastaDeBackups = 'C:\Projetos\ferramentas\backups\ia.bimeta',
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Comum.ps1')

$conexao = Get-ConexaoDeOrigem -Origem $Origem -VariavelDeConexao $VariavelDeConexao
$marca = Get-Date -Format 'yyyyMMddHHmmss'
$bancoDeEnsaio = 'iabimeta_ensaio_' + $marca
$instancia = $null
$falhou = $false
$pasta = $null
$resultado = $null
$segundosDoBackup = 0
$cronometro = [Diagnostics.Stopwatch]::StartNew()

try {
    Write-Host ('1/4 Backup de ' + $conexao.Banco + ' ...')
    $argumentos = @{ Origem = $Origem; Rotulo = 'ensaio'; PastaDeBackups = $PastaDeBackups; PostgresBin = $PostgresBin }
    if ($VariavelDeConexao) { $argumentos['VariavelDeConexao'] = $VariavelDeConexao }
    $pasta = & (Join-Path $PSScriptRoot 'Backup-Banco.ps1') @argumentos | Select-Object -Last 1
    $segundosDoBackup = [Math]::Round($cronometro.Elapsed.TotalSeconds, 1)

    Write-Host '2/4 Integridade do backup ...'
    $manifesto = Test-IntegridadeDoBackup -Pasta $pasta

    if ($Modo -eq 'Instancia') {
        Write-Host '3/4 Subindo a instancia descartavel e restaurando ...'
        $instancia = New-InstanciaDescartavel -Pasta (Join-Path $PastaDeBackups ('_instancia-ensaio-' + $marca))
        $destino = $instancia.Conexao
    }
    else {
        Write-Host ('3/4 Restaurando no banco temporario ' + $bancoDeEnsaio + ' do servidor de origem ...')
        $destino = $conexao
    }
    $resultado = Restore-EmBancoNovo -Conexao $destino -Pasta $pasta -Manifesto $manifesto -BancoDeDestino $bancoDeEnsaio

    Write-Host '4/4 Conferencias ...'
    if ($resultado.Diferencas.Count -gt 0) {
        $falhou = $true
        Write-Host 'FALHA: contagens diferentes do manifesto:'
        foreach ($linha in $resultado.Diferencas) { Write-Host ('  - ' + $linha) }
    }
    if ($resultado.RestricoesNaoValidadas -gt 0) { $falhou = $true; Write-Host ('FALHA: ' + $resultado.RestricoesNaoValidadas + ' restricao(oes) nao validada(s).') }
}
catch {
    $falhou = $true
    Write-Host ('FALHA: ' + $_.Exception.Message)
}
finally {
    if ($Manter) {
        if ($null -ne $instancia) { Write-Host ('Instancia mantida (porta ' + $instancia.Conexao.Porta + ', pasta ' + $instancia.Pasta + '): pare com pg_ctl stop -D "' + $instancia.Dados + '" e apague a pasta, manualmente.') }
        elseif ((Test-Path variable:script:BancoCriadoNestaExecucao) -and $script:BancoCriadoNestaExecucao -eq $bancoDeEnsaio) { Write-Host ("Banco temporario '" + $bancoDeEnsaio + "' mantido para inspecao: remova-o depois, manualmente.") }
    }
    elseif ($null -ne $instancia) {
        try { Remove-InstanciaDescartavel -Instancia $instancia -PastaDeBackups $PastaDeBackups; Write-Host 'Instancia descartavel parada e apagada.' }
        catch { Write-Host ('ATENCAO: ' + $_.Exception.Message) }
    }
    elseif ($Modo -eq 'Servidor' -and (Test-Path variable:script:BancoCriadoNestaExecucao) -and $script:BancoCriadoNestaExecucao -eq $bancoDeEnsaio -and $bancoDeEnsaio.StartsWith('iabimeta_ensaio_')) {
        # So o banco que ESTE ensaio criou (a funcao de restauracao registra isso so depois do CREATE DATABASE) e com o prefixo do ensaio.
        try {
            Invoke-Psql -Conexao $conexao -Banco 'postgres' -Sql ('DROP DATABASE IF EXISTS "' + $bancoDeEnsaio + '";') | Out-Null
            Write-Host ("Banco temporario '" + $bancoDeEnsaio + "' removido.")
        }
        catch { Write-Host ("ATENCAO: nao consegui remover o banco temporario '" + $bancoDeEnsaio + "': " + $_.Exception.Message) }
    }
}

if ($falhou -or $null -eq $resultado) { Write-Host 'ENSAIO REPROVADO.'; exit 1 }
Write-Host ('ENSAIO APROVADO (modo ' + $Modo + '): backup de ' + $segundosDoBackup + ' s, restore de ' + $resultado.SegundosDoRestore + ' s, ' + $resultado.Tabelas + ' tabelas identicas ao manifesto, nenhuma restricao pendente. Backup: ' + $pasta)
exit 0
