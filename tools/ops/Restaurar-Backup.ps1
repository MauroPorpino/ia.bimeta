<#
.SYNOPSIS
    RESTAURA um backup (Backup-Banco.ps1) em um banco NOVO, confere a integridade antes e compara as contagens depois.
    Se o backup leva ANEXOS, restaura tambem a pasta de anexos (-PastaDeAnexosDestino), numa pasta NOVA.
    NUNCA restaura sobre banco ou pasta existente e nunca apaga nada.

.DESCRIPTION
    1. Confere o backup (manifesto, tamanho, SHA-256, pg_restore --list) e, se houver anexos, cada arquivo de anexo (tamanho e SHA-256).
    2. Recusa se -BancoDeDestino ja existir (nao ha opcao para sobrescrever: para "restaurar no lugar", restaure em um
       banco novo, confira, e so depois troque a string de conexao ou renomeie os bancos com ALTER DATABASE, por uma
       pessoa, de forma consciente; veja docs\operations\BACKUP-E-RESTAURACAO.md). O mesmo vale para -PastaDeAnexosDestino:
       existente e nao vazia e recusada ANTES de o banco ser criado.
    3. Cria o banco novo e roda o pg_restore (--no-owner --no-privileges --exit-on-error).
    4. Com -PastaDeAnexosDestino, restaura os anexos (depois do banco) e confere o SHA-256 de cada objeto contra o manifesto.
       Sem o parametro, um backup com anexos restaura so o banco e o script AVISA que os arquivos nao foram restaurados.
    5. Compara a contagem de linhas de cada tabela com a do manifesto e confere se nao ha restricao nao validada.

    Os anexos sao copiados como estao (os cifrados pelo Api continuam cifrados): para abri-los, o Api/Worker do ambiente novo precisa da MESMA chave de cifra dos
    anexos da origem (configuracao do Api), guardada fora da maquina junto da frase de cifra do backup.

    O servidor de destino vem de -Origem/-VariavelDeConexao (so usuario, senha, host e porta; o nome do banco da string
    nao e usado). A senha vai so numa variavel de ambiente DESTE processo, durante cada chamada.

.EXAMPLE
    .\tools\ops\Restaurar-Backup.ps1 -Pasta C:\...\backups\ia.bimeta\homologacao-20261106-130000 -BancoDeDestino iabimeta_hml_restaurado -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -PastaDeAnexosDestino D:\ia.bimeta\anexos-restaurados
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Pasta,
    [Parameter(Mandatory)][string]$BancoDeDestino,
    [Parameter(Mandatory)][ValidateSet('Desenvolvimento', 'Teste', 'Ambiente')][string]$Origem,
    [string]$VariavelDeConexao,

    # O NOME da variavel de ambiente com a frase de cifra (obrigatorio para restaurar um backup cifrado; a frase nunca e impressa).
    [string]$ChaveEnv,

    # A pasta NOVA (ou vazia) onde restaurar os anexos do backup. Nunca sobrescreve nem apaga.
    [string]$PastaDeAnexosDestino,
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Anexos.ps1')

$dump = $null
$resultado = $null
$restauracaoDosAnexos = $null
$bloco = $null
try {
    $manifesto = Test-IntegridadeDoBackup -Pasta $Pasta -ChaveEnv $ChaveEnv
    Write-Host ('Backup integro (' + $manifesto.arquivo + ', sha256 conferido' + $(if ($manifesto.VerificacaoCompleta) { '' } else { '; cifrado' }) + ').')

    # Os anexos: verifica tudo e o destino ANTES de criar o banco (falhar cedo, sem deixar um banco restaurado e a pasta recusada).
    $bloco = Get-BlocoDeAnexos -Manifesto $manifesto
    if ($null -ne $bloco) {
        $verificacao = Test-AnexosDoBackup -Pasta $Pasta -Manifesto $manifesto
        if (-not $verificacao.Ok) { throw ('Os anexos do backup nao passaram na verificacao: ' + (Join-ErrosDeAnexos -Erros $verificacao.Erros)) }
        Write-Host ('Anexos integros (' + $verificacao.Objetos + ' objeto(s), ' + [Math]::Round($verificacao.TamanhoTotalEmBytes / 1MB, 2) + ' MB, sha256 de cada arquivo conferido).')
        if ($PastaDeAnexosDestino) { Confirm-DestinoDeAnexosLivre -Destino $PastaDeAnexosDestino -PastaDoBackup $Pasta | Out-Null }
    }
    elseif ($PastaDeAnexosDestino) {
        Write-Host 'AVISO: este backup NAO leva anexos; -PastaDeAnexosDestino foi ignorado.'
    }

    $conexao = Get-ConexaoDeOrigem -Origem $Origem -VariavelDeConexao $VariavelDeConexao
    $dump = Get-DumpParaRestaurar -Pasta $Pasta -Manifesto $manifesto -ChaveEnv $ChaveEnv
    $resultado = Restore-EmBancoNovo -Conexao $conexao -ArquivoDoDump $dump.Arquivo -Manifesto $manifesto -BancoDeDestino $BancoDeDestino

    # Os arquivos vem DEPOIS do banco (a verificacao ja aconteceu).
    if ($null -ne $bloco -and $PastaDeAnexosDestino) {
        $restauracaoDosAnexos = Restore-Anexos -Pasta $Pasta -Destino $PastaDeAnexosDestino -JaVerificado
    }
}
catch {
    Write-Host ('FALHA: ' + $_.Exception.Message)
    if ($null -ne $resultado) { Write-Host ("O banco '" + $BancoDeDestino + "' JA foi restaurado; os anexos NAO foram restaurados. A pasta de destino dos anexos, se existir, ficou como esta: confira e remova-a manualmente antes de tentar de novo.") }
    exit 1
}
finally { Remove-DumpTemporario -Dump $dump }

Write-Host ("Restaurado em '" + $resultado.Banco + "' em " + $resultado.SegundosDoRestore + ' s (' + $resultado.Tabelas + ' tabelas).')
$falhou = $false
if ($resultado.Diferencas.Count -gt 0) {
    Write-Host 'ATENCAO: contagens diferentes do manifesto (se o banco de origem recebeu gravacoes durante o backup, e esperado; senao, investigue):'
    foreach ($linha in $resultado.Diferencas) { Write-Host ('  - ' + $linha) }
    $falhou = $true
}
if ($resultado.RestricoesNaoValidadas -gt 0) { Write-Host ('ATENCAO: ' + $resultado.RestricoesNaoValidadas + ' restricao(oes) nao validada(s) no banco restaurado.'); $falhou = $true }

if ($null -ne $restauracaoDosAnexos) {
    Write-Host ("Anexos restaurados em '" + $restauracaoDosAnexos.Destino + "': " + $restauracaoDosAnexos.Objetos + ' objeto(s), ' + [Math]::Round($restauracaoDosAnexos.TamanhoTotalEmBytes / 1MB, 2) + ' MB, SHA-256 de cada um conferido contra o manifesto.')
    Write-Host '  Aponte Conversations:Attachments:RootPath do Api/Worker para essa pasta e use a MESMA chave de cifra dos anexos da origem (sem ela os arquivos cifrados nao abrem).'
    Write-Host '  Depois rode o comando attachments check do Api para listar linhas "disponiveis" sem arquivo (por exemplo, apagadas pela retencao depois do backup).'
}
elseif ($null -ne $bloco) {
    Write-Host ('AVISO: este backup inclui ' + $bloco.objetos + ' anexo(s) (' + [Math]::Round([long]$bloco.tamanhoTotalEmBytes / 1MB, 2) + ' MB) e eles NAO foram restaurados: so o banco foi. Sem os arquivos, as linhas de anexo aparecem como disponiveis e o Api responde 404 ao abri-los.')
    Write-Host ('       Para restaurar so os arquivos (sem mexer no banco ja restaurado): .\tools\ops\Restaurar-Anexos.ps1 -Pasta "' + $Pasta + '" -Destino <pasta NOVA>')
}
if ($falhou) { exit 1 }
Write-Host 'Contagens identicas as do manifesto e nenhuma restricao pendente.'
exit 0
