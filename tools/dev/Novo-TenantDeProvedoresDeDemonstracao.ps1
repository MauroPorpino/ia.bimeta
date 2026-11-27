<#
.SYNOPSIS
    Cria a empresa SINTETICA "demo-provedores" (IA padrao = Ollama simulado, sem nenhuma chave) para validar, pela interface,
    a Etapa 05 (Configuracoes -> Inteligencia artificial) com a OpenAI, a Anthropic e o Ollama SIMULADOS. Nenhuma chamada paga.

.DESCRIPTION
    Nao toca em nenhuma empresa existente. Repetir e seguro. Cria um administrador (todas as permissoes de IA) e uma atendente
    (nenhuma permissao de IA) para demonstrar a restricao. O modelo do Ollama vem DESATIVADO no catalogo; so para este
    processo o script o ativa com AiGateway__Catalog__Models__3__Status=Active (o Api e o Worker precisam da mesma variavel).
    Procedimento completo: docs\operations\ETAPA-05-PROVEDORES-IA.md.

.EXAMPLE
    .\tools\dev\Novo-TenantDeProvedoresDeDemonstracao.ps1
#>
[CmdletBinding()]
param(
    [string]$SenhaDoAdministrador = 'DevLocal-AdminProvedores-2026!',
    [string]$SenhaDaAtendente = 'DevLocal-AtendenteProvedores-2026!'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# Variaveis so deste processo (nada e gravado em disco nem em configuracao global).
$env:DEMO_PROVEDORES_SENHA_ADMIN = $SenhaDoAdministrador
$env:DEMO_PROVEDORES_SENHA_ATENDENTE = $SenhaDaAtendente
$env:AiGateway__Catalog__Models__3__Status = 'Active'
$env:AiGateway__Providers__Ollama__BaseAddress = 'http://127.0.0.1:5201/'

& (Join-Path $raiz 'tools\ops\Novo-Tenant.ps1') -Arquivo (Join-Path $raiz 'tools\dev\empresa-provedores-demo.json')
if ($LASTEXITCODE -ne 0) { Write-Host 'FALHA: o provisionamento da empresa nao terminou. Veja a saida acima.'; exit 1 }

Write-Host ''
Write-Host 'Empresa demo-provedores pronta (IA padrao: ollama / qwen3:4b simulado, sem chaves).'
Write-Host '  Interface   : http://localhost:5173  (empresa demo-provedores)'
Write-Host '  Administrador: admin@demo-provedores.local  (todas as permissoes de IA)'
Write-Host '  Atendente    : ana@demo-provedores.local   (sem permissao de IA)'
Write-Host '  Senhas       : as de desenvolvimento dos parametros do script (nao sao segredos reais)'
Write-Host '  Mensagem     : .\tools\dev\Send-MensagemWhatsApp.ps1 -Tenant demo-provedores -Texto ''Ola, tudo bem?'' -Nome Marta -Numero 5511999990801'
