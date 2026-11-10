<#
.SYNOPSIS
    Confere a INTEGRIDADE de um backup gerado por Backup-Banco.ps1: manifesto, tamanho, SHA-256 e leitura do dump
    (pg_restore --list) e, se o backup leva anexos, o anexos-manifesto.json e cada arquivo de anexo (tamanho e SHA-256;
    nenhum arquivo a mais ou a menos). Somente leitura; nao abre o banco.

.EXAMPLE
    .\tools\ops\Verificar-Backup.ps1 -Pasta C:\Projetos\ferramentas\backups\ia.bimeta\homologacao-20261106-130000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Pasta,

    # O NOME da variavel de ambiente com a frase de cifra: so ele e necessario para a verificacao completa de um backup cifrado (a frase nunca e impressa).
    [string]$ChaveEnv,
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Anexos.ps1')

try {
    $manifesto = Test-IntegridadeDoBackup -Pasta $Pasta -ChaveEnv $ChaveEnv
}
catch {
    Write-Host ('FALHA: ' + $_.Exception.Message)
    exit 1
}

# Os anexos (se o backup os leva): o manifesto de anexos contra o bloco do manifesto.json e cada arquivo contra o manifesto de anexos.
$anexos = Test-AnexosDoBackup -Pasta $Pasta -Manifesto $manifesto
if (-not $anexos.Ok) {
    Write-Host ('FALHA nos anexos do backup: ' + (Join-ErrosDeAnexos -Erros $anexos.Erros -Maximo 10))
    exit 1
}

if (-not $manifesto.VerificacaoCompleta) { Write-Host 'ATENCAO: backup CIFRADO verificado so pelo arquivo (tamanho e SHA-256); informe -ChaveEnv para a verificacao completa (autenticacao, decifragem e leitura do dump).' }
Write-Host ('Backup integro: ' + $manifesto.arquivo + ', ' + [Math]::Round($manifesto.tamanhoEmBytes / 1MB, 2) + ' MB, ' + $manifesto.entradasDoDump + ' entradas, ' + @($manifesto.contagens).Count + ' tabelas, criado em ' + $manifesto.criadoEmUtc + '.')
if ($anexos.TemAnexos) {
    Write-Host ('Anexos integros: ' + $anexos.Objetos + ' objeto(s), ' + [Math]::Round($anexos.TamanhoTotalEmBytes / 1MB, 2) + ' MB, ' + $anexos.Manifesto.tenants + ' tenant(s); ' + $anexos.Manifesto.cifradosNaOrigem + ' cifrado(s) na origem, ' + $anexos.Manifesto.legadosSemCifra + ' em texto claro (SHA-256 de cada arquivo conferido; nenhum arquivo a mais ou a menos).')
}
exit 0
