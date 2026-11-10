<#
.SYNOPSIS
    ENSAIO de restauracao: faz o backup do banco de origem (e, com -PastaDeAnexos, dos anexos), restaura em um banco ISOLADO (e os anexos numa pasta NOVA),
    confere a integridade e as contagens, e remove o que criou. Prova que o backup restaura e mede o tempo. Nao altera o banco nem a pasta de origem.

.DESCRIPTION
    Modos (-Modo):
      Instancia (padrao)  Sobe uma INSTANCIA PostgreSQL descartavel (initdb numa pasta temporaria dentro da pasta de backups,
                          porta livre, so loopback, senha aleatoria) e restaura nela. Nao exige privilegio no servidor do
                          projeto e nao toca em nenhum banco real; a instancia inteira e apagada no fim.
      Servidor            Cria o banco iabimeta_ensaio_<data-hora> no proprio servidor de -Origem (exige um papel com
                          CREATEDB) e o remove no fim. So remove o banco que este ensaio criou, e so com esse prefixo.

    Com -Manter, nada e apagado no fim (a instancia fica ligada em uma porta local e o banco fica para inspecao): remova
    depois, por uma pessoa. O backup do ensaio fica na pasta de backups e vale como backup verificado.

    ANEXOS (-PastaDeAnexos): o backup leva tambem a pasta de anexos (depois do dump); o ensaio restaura SO DO BACKUP os anexos numa pasta nova e temporaria
    (<pasta de backups>\_ensaio-anexos-<data>, apagada no fim) e confere o SHA-256 de cada objeto restaurado contra o manifesto E contra a pasta de origem.
    -SimularPerdaDaOrigem prova a recuperacao depois da PERDA da pasta original: renomeia a pasta de origem para <nome>.perdida-ensaio-<data> durante toda a
    restauracao (qualquer leitura dela falharia), a devolve no fim e so entao compara com a origem. Nunca apaga nada; se nao conseguir devolver, avisa o caminho.
    Use -SimularPerdaDaOrigem com o Api e o Worker PARADOS (a pasta some do nome durante o ensaio).

    Para a comparacao exata de contagens, o banco de origem deve estar em repouso (Api e Worker parados) durante o ensaio.

.EXAMPLE
    .\tools\ops\Ensaiar-Restauracao.ps1 -Origem Teste
    .\tools\ops\Ensaiar-Restauracao.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -PastaDeAnexos D:\ia.bimeta\anexos -SimularPerdaDaOrigem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Desenvolvimento', 'Teste', 'Ambiente')][string]$Origem,
    [string]$VariavelDeConexao,
    [ValidateSet('Instancia', 'Servidor')][string]$Modo = 'Instancia',
    [switch]$Manter,

    # O NOME da variavel de ambiente com a frase de cifra: o ensaio cifra o backup, verifica, decifra e restaura (a frase nunca e impressa).
    [string]$ChaveEnv,

    # A pasta privada de anexos: entra no backup do ensaio, e os anexos sao restaurados numa pasta temporaria nova e conferidos.
    [string]$PastaDeAnexos,

    # Renomeia a pasta de anexos de origem durante a restauracao (prova a recuperacao depois da perda da pasta original) e a devolve no fim. Exige -PastaDeAnexos.
    [switch]$SimularPerdaDaOrigem,
    [string]$PastaDeBackups = 'C:\Projetos\ferramentas\backups\ia.bimeta',
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Backup.ps1')

if ($SimularPerdaDaOrigem -and -not $PastaDeAnexos) { Write-Host 'FALHA: -SimularPerdaDaOrigem exige -PastaDeAnexos.'; exit 2 }

$conexao = Get-ConexaoDeOrigem -Origem $Origem -VariavelDeConexao $VariavelDeConexao
$marca = Get-Date -Format 'yyyyMMddHHmmss'
$bancoDeEnsaio = 'iabimeta_ensaio_' + $marca
$pastaDosAnexosRestaurados = Join-Path $PastaDeBackups ('_ensaio-anexos-' + $marca)
$instancia = $null
$falhou = $false
$pasta = $null
$dump = $null
$resultado = $null
$perda = $null
$anexosRestaurados = $null
$comparacao = $null
$segundosDoBackup = 0
$cronometro = [Diagnostics.Stopwatch]::StartNew()

try {
    Write-Host ('1/4 Backup de ' + $conexao.Banco + $(if ($PastaDeAnexos) { ' e dos anexos (o dump primeiro, depois os arquivos)' } else { '' }) + ' ...')
    $argumentos = @{ Origem = $Origem; Rotulo = 'ensaio'; PastaDeBackups = $PastaDeBackups; PostgresBin = $PostgresBin }
    if ($VariavelDeConexao) { $argumentos['VariavelDeConexao'] = $VariavelDeConexao }
    if ($PastaDeAnexos) { $argumentos['PastaDeAnexos'] = $PastaDeAnexos }
    $pasta = & (Join-Path $PSScriptRoot 'Backup-Banco.ps1') @argumentos | Select-Object -Last 1
    $segundosDoBackup = [Math]::Round($cronometro.Elapsed.TotalSeconds, 1)

    if ($ChaveEnv) {
        Write-Host ('1b/4 Cifrando o backup (frase na variavel ' + $ChaveEnv + ') e conferindo a ida e volta ...')
        Protect-Backup -Pasta $pasta -ChaveEnv $ChaveEnv | Out-Null
    }

    Write-Host '2/4 Integridade do backup ...'
    $manifesto = Test-IntegridadeDoBackup -Pasta $pasta -ChaveEnv $ChaveEnv
    $verificacaoDosAnexos = Test-AnexosDoBackup -Pasta $pasta -Manifesto $manifesto
    if (-not $verificacaoDosAnexos.Ok) { throw ('Os anexos do backup nao passaram na verificacao: ' + (Join-ErrosDeAnexos -Erros $verificacaoDosAnexos.Erros)) }
    if ($PastaDeAnexos -and -not $verificacaoDosAnexos.TemAnexos) { throw 'O backup nao registrou os anexos no manifesto.json.' }
    $dump = Get-DumpParaRestaurar -Pasta $pasta -Manifesto $manifesto -ChaveEnv $ChaveEnv

    if ($SimularPerdaDaOrigem) {
        Write-Host ('2b/4 Simulando a PERDA da pasta de anexos de origem: ela sera renomeada durante a restauracao e devolvida no fim (so o backup e lido).')
        $perda = Suspend-PastaDeAnexosDeOrigem -Pasta $PastaDeAnexos
        Write-Host ('     origem renomeada para ' + $perda.Renomeada)
    }

    if ($Modo -eq 'Instancia') {
        Write-Host '3/4 Subindo a instancia descartavel e restaurando ...'
        $instancia = New-InstanciaDescartavel -Pasta (Join-Path $PastaDeBackups ('_instancia-ensaio-' + $marca))
        $destino = $instancia.Conexao
    }
    else {
        Write-Host ('3/4 Restaurando no banco temporario ' + $bancoDeEnsaio + ' do servidor de origem ...')
        $destino = $conexao
    }
    $resultado = Restore-EmBancoNovo -Conexao $destino -ArquivoDoDump $dump.Arquivo -Manifesto $manifesto -BancoDeDestino $bancoDeEnsaio

    if ($verificacaoDosAnexos.TemAnexos) {
        Write-Host '3b/4 Restaurando os anexos numa pasta nova (so do backup) ...'
        $anexosRestaurados = Restore-Anexos -Pasta $pasta -Destino $pastaDosAnexosRestaurados -JaVerificado
    }

    # A origem volta ao nome assim que a restauracao termina (antes das comparacoes), para nunca ficar renomeada por mais tempo que o necessario.
    if ($null -ne $perda) {
        Resume-PastaDeAnexosDeOrigem -Perda $perda
        Write-Host '     pasta de anexos de origem devolvida ao nome original.'
    }

    Write-Host '4/4 Conferencias ...'
    if ($resultado.Diferencas.Count -gt 0) {
        $falhou = $true
        Write-Host 'FALHA: contagens diferentes do manifesto:'
        foreach ($linha in $resultado.Diferencas) { Write-Host ('  - ' + $linha) }
    }
    if ($resultado.RestricoesNaoValidadas -gt 0) { $falhou = $true; Write-Host ('FALHA: ' + $resultado.RestricoesNaoValidadas + ' restricao(oes) nao validada(s).') }
    if ($null -ne $anexosRestaurados) {
        $comparacao = Compare-AnexosComOrigem -Restaurados $pastaDosAnexosRestaurados -Origem $PastaDeAnexos
        if ($comparacao.Diferentes.Count -gt 0) {
            $falhou = $true
            Write-Host 'FALHA: anexos restaurados com SHA-256 DIFERENTE da origem:'
            foreach ($chave in ($comparacao.Diferentes | Select-Object -First 10)) { Write-Host ('  - ' + $chave) }
        }
        if ($comparacao.AusentesNaOrigem -gt 0) { Write-Host ('AVISO: ' + $comparacao.AusentesNaOrigem + ' objeto(s) do backup nao existem mais na origem (apagados pela retencao depois do backup).') }
        if ($comparacao.NovosNaOrigem -gt 0) { Write-Host ('AVISO: a origem ganhou ' + $comparacao.NovosNaOrigem + ' objeto(s) depois do backup (orfaos inofensivos; entram no proximo backup).') }
    }
}
catch {
    $falhou = $true
    Write-Host ('FALHA: ' + $_.Exception.Message)
}
finally {
    Remove-DumpTemporario -Dump $dump
    # Rede de seguranca: a pasta de origem nunca fica renomeada (se ja foi devolvida, nao faz nada; se nao conseguir, diz o caminho).
    if ($null -ne $perda -and -not $perda.Devolvida) {
        try { Resume-PastaDeAnexosDeOrigem -Perda $perda; Write-Host 'Pasta de anexos de origem devolvida ao nome original.' }
        catch { $falhou = $true; Write-Host ('ATENCAO: ' + $_.Exception.Message) }
    }
    if ($Manter) {
        if ($null -ne $instancia) { Write-Host ('Instancia mantida (porta ' + $instancia.Conexao.Porta + ', pasta ' + $instancia.Pasta + '): pare com pg_ctl stop -D "' + $instancia.Dados + '" e apague a pasta, manualmente.') }
        elseif ((Test-Path variable:script:BancoCriadoNestaExecucao) -and $script:BancoCriadoNestaExecucao -eq $bancoDeEnsaio) { Write-Host ("Banco temporario '" + $bancoDeEnsaio + "' mantido para inspecao: remova-o depois, manualmente.") }
        if (Test-Path -LiteralPath $pastaDosAnexosRestaurados) { Write-Host ("Anexos restaurados mantidos em '" + $pastaDosAnexosRestaurados + "': remova-os depois, manualmente.") }
    }
    else {
        if (Test-Path -LiteralPath $pastaDosAnexosRestaurados) {
            try { Remove-AnexosDoEnsaio -Pasta $pastaDosAnexosRestaurados -PastaDeBackups $PastaDeBackups; Write-Host 'Pasta temporaria dos anexos restaurados apagada.' }
            catch { Write-Host ('ATENCAO: ' + $_.Exception.Message) }
        }
        if ($null -ne $instancia) {
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
}

if ($falhou -or $null -eq $resultado) { Write-Host 'ENSAIO REPROVADO.'; exit 1 }
$textoDosAnexos = ''
if ($null -ne $anexosRestaurados) {
    $textoDosAnexos = ' Anexos: ' + $anexosRestaurados.Objetos + ' objeto(s), ' + [Math]::Round($anexosRestaurados.TamanhoTotalEmBytes / 1MB, 2) + ' MB restaurados so do backup, SHA-256 de cada um igual ao manifesto e a origem (' + $comparacao.Iguais + ' conferidos)' + $(if ($SimularPerdaDaOrigem) { ', com a pasta de origem ausente durante a restauracao' } else { '' }) + '.'
}
Write-Host ('ENSAIO APROVADO (modo ' + $Modo + '): backup de ' + $segundosDoBackup + ' s, restore de ' + $resultado.SegundosDoRestore + ' s, ' + $resultado.Tabelas + ' tabelas identicas ao manifesto, nenhuma restricao pendente.' + $textoDosAnexos + ' Backup: ' + $pasta)
exit 0
