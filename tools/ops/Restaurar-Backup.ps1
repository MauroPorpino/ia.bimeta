<#
.SYNOPSIS
    RESTAURA um backup (Backup-Banco.ps1) em um banco NOVO, confere a integridade antes e compara as contagens depois.
    NUNCA restaura sobre banco existente e nunca apaga nada.

.DESCRIPTION
    1. Confere o backup (manifesto, tamanho, SHA-256, pg_restore --list).
    2. Recusa se -BancoDeDestino ja existir (nao ha opcao para sobrescrever: para "restaurar no lugar", restaure em um
       banco novo, confira, e so depois troque a string de conexao ou renomeie os bancos com ALTER DATABASE, por uma
       pessoa, de forma consciente; veja docs\operations\BACKUP-E-RESTAURACAO.md).
    3. Cria o banco novo e roda o pg_restore (--no-owner --no-privileges --exit-on-error).
    4. Compara a contagem de linhas de cada tabela com a do manifesto e confere se nao ha restricao nao validada.

    O servidor de destino vem de -Origem/-VariavelDeConexao (so usuario, senha, host e porta; o nome do banco da string
    nao e usado). A senha vai so numa variavel de ambiente DESTE processo, durante cada chamada.

.EXAMPLE
    .\tools\ops\Restaurar-Backup.ps1 -Pasta C:\...\backups\ia.bimeta\homologacao-20261106-130000 -BancoDeDestino iabimeta_hml_restaurado -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Pasta,
    [Parameter(Mandatory)][string]$BancoDeDestino,
    [Parameter(Mandatory)][ValidateSet('Desenvolvimento', 'Teste', 'Ambiente')][string]$Origem,
    [string]$VariavelDeConexao,
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Comum.ps1')

try {
    $manifesto = Test-IntegridadeDoBackup -Pasta $Pasta
    Write-Host ('Backup integro (' + $manifesto.arquivo + ', sha256 conferido).')
    $conexao = Get-ConexaoDeOrigem -Origem $Origem -VariavelDeConexao $VariavelDeConexao
    $resultado = Restore-EmBancoNovo -Conexao $conexao -Pasta $Pasta -Manifesto $manifesto -BancoDeDestino $BancoDeDestino
}
catch {
    Write-Host ('FALHA: ' + $_.Exception.Message)
    exit 1
}

Write-Host ("Restaurado em '" + $resultado.Banco + "' em " + $resultado.SegundosDoRestore + ' s (' + $resultado.Tabelas + ' tabelas).')
if ($resultado.Diferencas.Count -gt 0) {
    Write-Host 'ATENCAO: contagens diferentes do manifesto (se o banco de origem recebeu gravacoes durante o backup, e esperado; senao, investigue):'
    foreach ($linha in $resultado.Diferencas) { Write-Host ('  - ' + $linha) }
    exit 1
}
if ($resultado.RestricoesNaoValidadas -gt 0) { Write-Host ('ATENCAO: ' + $resultado.RestricoesNaoValidadas + ' restricao(oes) nao validada(s) no banco restaurado.'); exit 1 }
Write-Host 'Contagens identicas as do manifesto e nenhuma restricao pendente.'
exit 0
