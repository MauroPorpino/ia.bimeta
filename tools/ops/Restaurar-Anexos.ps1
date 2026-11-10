<#
.SYNOPSIS
    Restaura SO os anexos (arquivos de midia do WhatsApp) de um backup, numa pasta NOVA. Nao abre o banco. Nunca sobrescreve nem apaga.

.DESCRIPTION
    Confere o backup (manifesto, tamanho, SHA-256 e leitura do dump) e cada arquivo de anexo (tamanho e SHA-256, nenhum arquivo a mais ou a menos),
    recusa -Destino existente e nao vazio, copia para <Destino>\<tenant>\<arquivo>.bin (.parcial + SHA-256 + renomeacao) e confere no fim o numero de objetos e o
    SHA-256 de cada um contra o manifesto. Serve para quando o banco ja foi restaurado sem -PastaDeAnexosDestino (Restaurar-Backup.ps1 avisa) ou para
    recuperar so a pasta de arquivos depois de perde-la.

    Os arquivos sao copiados como estao (os cifrados pelo Api continuam cifrados): o Api/Worker precisa da MESMA chave de cifra dos anexos da origem.

.EXAMPLE
    .\tools\ops\Restaurar-Anexos.ps1 -Pasta C:\Projetos\ferramentas\backups\ia.bimeta\agendado-20261109-023000 -Destino D:\ia.bimeta\anexos-restaurados
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Pasta,
    [Parameter(Mandatory)][string]$Destino,

    # O NOME da variavel de ambiente com a frase de cifra: so para a verificacao completa do dump de um backup cifrado (os anexos nao dependem dela).
    [string]$ChaveEnv,
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Anexos.ps1')

try {
    $manifesto = Test-IntegridadeDoBackup -Pasta $Pasta -ChaveEnv $ChaveEnv
    $bloco = Get-BlocoDeAnexos -Manifesto $manifesto
    if ($null -eq $bloco) { throw 'Este backup nao leva anexos (o manifesto.json nao tem o bloco anexos).' }
    $verificacao = Test-AnexosDoBackup -Pasta $Pasta -Manifesto $manifesto
    if (-not $verificacao.Ok) { throw ('Os anexos do backup nao passaram na verificacao: ' + (Join-ErrosDeAnexos -Erros $verificacao.Erros)) }
    Write-Host ('Anexos integros (' + $verificacao.Objetos + ' objeto(s), ' + [Math]::Round($verificacao.TamanhoTotalEmBytes / 1MB, 2) + ' MB, sha256 de cada arquivo conferido).')
    $restaurado = Restore-Anexos -Pasta $Pasta -Destino $Destino -JaVerificado
}
catch {
    Write-Host ('FALHA: ' + $_.Exception.Message)
    exit 1
}
Write-Host ("Anexos restaurados em '" + $restaurado.Destino + "': " + $restaurado.Objetos + ' objeto(s), ' + [Math]::Round($restaurado.TamanhoTotalEmBytes / 1MB, 2) + ' MB, SHA-256 de cada um conferido contra o manifesto.')
Write-Host '  Aponte Conversations:Attachments:RootPath do Api/Worker para essa pasta e use a MESMA chave de cifra dos anexos da origem (sem ela os arquivos cifrados nao abrem).'
exit 0
