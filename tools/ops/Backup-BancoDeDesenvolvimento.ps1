<#
.SYNOPSIS
    Faz o BACKUP (pg_dump, formato custom) do banco de desenvolvimento do Api, antes de subir uma versao nova que
    aplica migracoes nele. Valida o arquivo (pg_restore --list) e imprime o caminho e o SHA-256.

.DESCRIPTION
    Le a string de conexao do user-secret ConnectionStrings:Tenancy do Api (nunca a imprime). A senha vai so numa
    variavel de ambiente DESTE processo, durante o pg_dump. Somente leitura no banco. O destino padrao fica fora do
    repositorio: C:\Projetos\ferramentas\backups\ia.bimeta\<rotulo>-<data>\.

.EXAMPLE
    .\tools\ops\Backup-BancoDeDesenvolvimento.ps1 -Rotulo pre-homologacao-meta
#>
[CmdletBinding()]
param(
    [string]$Rotulo = 'dev',
    [string]$PastaDeBackups = 'C:\Projetos\ferramentas\backups\ia.bimeta',
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projetoDoApi = Join-Path $raiz 'backend\host\IaBimeta.Api'

$conexao = $null
foreach ($linha in @(& dotnet user-secrets list --project $projetoDoApi)) {
    if ($linha.StartsWith('ConnectionStrings:Tenancy = ')) { $conexao = $linha.Substring('ConnectionStrings:Tenancy = '.Length) }
}
if ([string]::IsNullOrEmpty($conexao)) { throw 'ConnectionStrings:Tenancy nao encontrada nos user-secrets do Api.' }

$partes = @{}
foreach ($par in ($conexao -split ';')) {
    $i = $par.IndexOf('=')
    if ($i -gt 0) { $partes[$par.Substring(0, $i).Trim().ToLowerInvariant()] = $par.Substring($i + 1).Trim() }
}
$servidor = $partes['host']; if (-not $servidor) { $servidor = $partes['server'] }
$porta = $partes['port']; if (-not $porta) { $porta = '5432' }
$banco = $partes['database']
$usuario = $partes['username']; if (-not $usuario) { $usuario = $partes['user id'] }
if ([string]::IsNullOrEmpty($banco) -or [string]::IsNullOrEmpty($usuario)) { throw 'A string de conexao nao tem database ou usuario.' }
if ($porta -ne '5433') { throw ('Porta inesperada (' + $porta + '): o PostgreSQL do projeto e o da porta 5433.') }

$pasta = Join-Path $PastaDeBackups ($Rotulo + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $pasta | Out-Null
$arquivo = Join-Path $pasta ($banco + '.dump')

$env:PGPASSWORD = $partes['password']
try {
    & (Join-Path $PostgresBin 'pg_dump.exe') -h $servidor -p $porta -U $usuario -Fc -f $arquivo $banco
    if ($LASTEXITCODE -ne 0) { throw ('pg_dump saiu com ' + $LASTEXITCODE) }
    & (Join-Path $PostgresBin 'pg_restore.exe') --list $arquivo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'pg_restore --list recusou o arquivo gerado.' }
}
finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }

$hash = (Get-FileHash -Algorithm SHA256 -Path $arquivo).Hash
Write-Host ('Backup ok: ' + $arquivo + '  ' + [Math]::Round((Get-Item $arquivo).Length / 1MB, 1) + ' MB  sha256 ' + $hash)
