<#
.SYNOPSIS
    Cria a empresa SINTETICA "demo-ollama" (IA = Ollama, modelo qwen3:4b, SEM chave) para validar a Etapa 04 contra o
    "Ollama" SIMULADO local (tools\dev\Start-OllamaSimulado.ps1). Nenhum modelo e baixado e nenhuma API paga e chamada.

.DESCRIPTION
    Nao toca em nenhuma empresa existente (demo-ia, demo-usuarios, demo-anthropic e as da plataforma). Repetir e seguro.
    Usa o comando "provision" (tools\ops\Novo-Tenant.ps1 + tools\dev\empresa-ollama-demo.json). O modelo do Ollama vem
    DESATIVADO no catalogo (nada fica disponivel de fabrica): so para ESTE processo o script o ativa com
      AiGateway__Catalog__Models__3__Status = Active
    O Api e o Worker precisam ser iniciados com a mesma variavel e com
      AiGateway__Providers__Ollama__BaseAddress = http://127.0.0.1:5201/
    (veja docs\operations\ETAPA-04-OLLAMA.md). O Ollama nao tem chave: nenhuma credencial e cadastrada.

.EXAMPLE
    .\tools\dev\Novo-TenantDeOllamaDeDemonstracao.ps1
#>
[CmdletBinding()]
param(
    [string]$SenhaDoAdministrador = 'DevLocal-AdminOllama-2026!'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# Variaveis so deste processo (nada e gravado em disco nem em configuracao global).
$env:DEMO_OLLAMA_SENHA_ADMIN = $SenhaDoAdministrador
$env:AiGateway__Catalog__Models__3__Status = 'Active'

& (Join-Path $raiz 'tools\ops\Novo-Tenant.ps1') -Arquivo (Join-Path $raiz 'tools\dev\empresa-ollama-demo.json')
if ($LASTEXITCODE -ne 0) { Write-Host 'FALHA: o provisionamento da empresa nao terminou. Veja a saida acima.'; exit 1 }

Write-Host ''
Write-Host 'Empresa demo-ollama pronta (IA: ollama / qwen3:4b, sem chave).'
Write-Host '  Administrador : admin@demo-ollama.local  (senha de desenvolvimento em -SenhaDoAdministrador)'
Write-Host '  Mensagem      : .\tools\dev\Send-MensagemWhatsApp.ps1 -Tenant demo-ollama -Texto ''Ola, tudo bem?'' -Nome Marta -Numero 5511999990701'
Write-Host '  Consumo       : GET /simulator/ai/requests  (cabecalho X-Dev-Tenant: demo-ollama)'
