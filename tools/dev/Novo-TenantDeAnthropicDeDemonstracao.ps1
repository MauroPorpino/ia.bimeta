<#
.SYNOPSIS
    Cria a empresa SINTETICA "demo-anthropic" (IA = Anthropic, modelo claude-haiku-5-5) para validar a Etapa 03
    contra a "Anthropic" SIMULADA local (tools\dev\Start-AnthropicSimulada.ps1). NENHUMA chamada paga.

.DESCRIPTION
    Nao toca em nenhuma empresa existente (nem demo-ia, nem demo-usuarios, nem as da plataforma). Repetir e seguro.
    Usa o comando "provision" (tools\ops\Novo-Tenant.ps1 + tools\dev\empresa-anthropic-demo.json): tenant, administrador,
    organizacao, fila, agente, IA do tenant (provedor anthropic) com uma chave SINTETICA e WhatsApp simulado.

    A chave abaixo NAO e uma chave da Anthropic: e um texto de desenvolvimento. Contra a Anthropic simulada qualquer
    texto serve. Se o Api/Worker estiverem apontando para a API REAL por engano, a Anthropic recusa a chave sintetica
    (HTTP 401) e nada e cobrado. Para usar uma chave REAL e preciso autorizacao especifica
    (docs\operations\ETAPA-03-ANTHROPIC-API-REAL.md).

.EXAMPLE
    .\tools\dev\Novo-TenantDeAnthropicDeDemonstracao.ps1
#>
[CmdletBinding()]
param(
    [string]$SenhaDoAdministrador = 'DevLocal-AdminAnthropic-2026!'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# Variaveis so deste processo (nada e gravado em disco nem em configuracao global).
$env:DEMO_ANTHROPIC_SENHA_ADMIN = $SenhaDoAdministrador
$env:DEMO_ANTHROPIC_CHAVE_SINTETICA = 'sk-ant-SINTETICA-DE-DESENVOLVIMENTO-NAO-E-UMA-CHAVE-REAL'

& (Join-Path $raiz 'tools\ops\Novo-Tenant.ps1') -Arquivo (Join-Path $raiz 'tools\dev\empresa-anthropic-demo.json')
if ($LASTEXITCODE -ne 0) { Write-Host 'FALHA: o provisionamento da empresa nao terminou. Veja a saida acima.'; exit 1 }

Write-Host ''
Write-Host 'Empresa demo-anthropic pronta (IA: anthropic / claude-haiku-5-5, chave SINTETICA).'
Write-Host '  Administrador : admin@demo-anthropic.local  (senha de desenvolvimento em -SenhaDoAdministrador)'
Write-Host '  Mensagem      : .\tools\dev\Send-MensagemWhatsApp.ps1 -Tenant demo-anthropic -Texto ''Ola, tudo bem?'' -Nome Marta -Numero 5511999990601'
Write-Host '  Consumo       : GET /simulator/ai/requests  (cabecalho X-Dev-Tenant: demo-anthropic)'
