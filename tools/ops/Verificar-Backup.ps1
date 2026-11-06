<#
.SYNOPSIS
    Confere a INTEGRIDADE de um backup gerado por Backup-Banco.ps1: manifesto, tamanho, SHA-256 e leitura do dump
    (pg_restore --list). Somente leitura; nao abre o banco.

.EXAMPLE
    .\tools\ops\Verificar-Backup.ps1 -Pasta C:\Projetos\ferramentas\backups\ia.bimeta\homologacao-20261106-130000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Pasta,
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Comum.ps1')

try {
    $manifesto = Test-IntegridadeDoBackup -Pasta $Pasta
}
catch {
    Write-Host ('FALHA: ' + $_.Exception.Message)
    exit 1
}
Write-Host ('Backup integro: ' + $manifesto.arquivo + ', ' + [Math]::Round($manifesto.tamanhoEmBytes / 1MB, 2) + ' MB, ' + $manifesto.entradasDoDump + ' entradas, ' + @($manifesto.contagens).Count + ' tabelas, criado em ' + $manifesto.criadoEmUtc + '.')
exit 0
