<#
.SYNOPSIS
    Aplica (ou remove) as sobrescritas de limite de IA da empresa demo-anthropic para o TESTE REAL da Anthropic,
    usando as colunas de limite que o AiGateway JA possui (nenhum mecanismo novo). So a empresa demo-anthropic e alterada.

.DESCRIPTION
    Limites aplicados (o AiGateway os impoe ANTES de chamar o provedor, incluindo a reserva de tokens):
      - no maximo 10 chamadas por periodo de 24 h (o periodo e o padrao da plataforma),
      - no maximo 40.000 tokens por periodo,
      - no maximo 8.000 tokens por chamada (reserva = entrada estimada + teto de saida).
    -Remover volta a empresa ao padrao da plataforma (colunas em NULL).

    A senha do banco vem dos user-secrets do Api, so para o processo do psql, e nunca e impressa.

.EXAMPLE
    .\tools\dev\Set-LimitesDoTesteAnthropic.ps1
    .\tools\dev\Set-LimitesDoTesteAnthropic.ps1 -Remover
#>
[CmdletBinding()]
param(
    [string]$Slug = 'demo-anthropic',
    [switch]$Remover
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Slug -ne 'demo-anthropic') { Write-Host 'Este script so altera a empresa demo-anthropic.'; exit 1 }

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$linha = dotnet user-secrets list --project (Join-Path $raiz 'backend\host\IaBimeta.Api') 2>$null | Where-Object { $_ -match '^ConnectionStrings:(Tenancy|Default)\s*=' } | Select-Object -First 1
if (-not $linha) { Write-Host 'FALHA: string de conexao nao encontrada nos user-secrets do Api.'; exit 1 }
$partes = @{}
foreach ($p in (($linha -split '=', 2)[1].Trim() -split ';')) { $kv = $p -split '=', 2; if ($kv.Count -eq 2) { $partes[$kv[0].Trim().ToLower()] = $kv[1].Trim() } }

$psql = Join-Path (Split-Path -Parent $raiz) 'postgresql-17.11\bin\psql.exe'
if (-not (Test-Path $psql)) { $psql = 'C:\Projetos\ferramentas\postgresql-17.11\bin\psql.exe' }

if ($Remover) { $valores = 'NULL, NULL, NULL' } else { $valores = '10, 40000, 8000' }
$sql = "UPDATE ai_gateway.ai_tenant_settings s SET max_calls_per_period = " + ($valores -split ', ')[0] + ", max_tokens_per_period = " + ($valores -split ', ')[1] + ", max_tokens_per_call = " + ($valores -split ', ')[2] + ", version = s.version + 1 FROM tenancy.tenants t WHERE t.id = s.tenant_id AND t.slug = 'demo-anthropic' RETURNING s.max_calls_per_period, s.max_tokens_per_period, s.max_tokens_per_call;"

$env:PGPASSWORD = $partes['password']
try {
    & $psql -h $partes['host'] -p $partes['port'] -U $partes['username'] -d $partes['database'] -c $sql
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
