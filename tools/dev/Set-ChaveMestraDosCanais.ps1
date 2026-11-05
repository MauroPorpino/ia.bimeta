<#
.SYNOPSIS
    Cria, nos user-secrets do Api e do Worker, a CHAVE MESTRA que cifra as credenciais dos canais (App Secret, token de
    acesso e token de verificacao do WhatsApp). So cria se ainda nao existir. Nunca imprime a chave. Roda uma vez, no
    SEU terminal.

.EXAMPLE
    .\tools\dev\Set-ChaveMestraDosCanais.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projetoDoApi = Join-Path $raiz 'backend\host\IaBimeta.Api'

$existentes = @(& dotnet user-secrets list --project $projetoDoApi)
if ($LASTEXITCODE -ne 0) { throw 'Nao foi possivel ler os user-secrets (o SDK do .NET esta instalado?).' }
if (@($existentes | Where-Object { $_ -like 'Channels:Secrets:ActiveKeyId*' }).Count -gt 0) {
    Write-Host 'Chave mestra dos canais: ja existe (mantida).'
    exit 0
}

$bytes = New-Object byte[] 32
$gerador = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $gerador.GetBytes($bytes) } finally { $gerador.Dispose() }
$base64 = [Convert]::ToBase64String($bytes)
& dotnet user-secrets set 'Channels:Secrets:Keys:dev-1' $base64 --project $projetoDoApi | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gravar a chave.' }
& dotnet user-secrets set 'Channels:Secrets:ActiveKeyId' 'dev-1' --project $projetoDoApi | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gravar o identificador da chave.' }
$base64 = $null
$bytes = $null
Write-Host 'Chave mestra dos canais: criada (id dev-1) e ativa. O valor nao e exibido.'
