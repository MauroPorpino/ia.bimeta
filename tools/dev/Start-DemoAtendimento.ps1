<#
.SYNOPSIS
    Sobe a DEMONSTRACAO do atendimento omnichannel de ponta a ponta, SEM nenhuma chamada paga: a "OpenAI" e a "Graph API
    da Meta" simuladas (locais), o Api e o Worker (perfil simulador-ia) e a interface React do atendente. Cada parte
    abre na sua janela do PowerShell; feche as janelas para parar.

.DESCRIPTION
    Pre-requisitos (uma vez): PostgreSQL de desenvolvimento no ar; tools\dev\Set-ChaveMestraDosCanais.ps1;
    tools\dev\Set-ConfiguracaoDeIa.ps1 (modelo e chave mestra de IA; qualquer modelo serve, a "OpenAI" e local);
    uma chave de IA cadastrada com tools\dev\Set-ChaveDaOpenAI.ps1 (qualquer texto serve, ela so vai para a maquina local);
    e, no frontend, npm install (veja tools\dev\README-demo-atendimento.md).
    ATENCAO: o Api aplica as migrations pendentes (inclusive as do Channels) no banco configurado.

.EXAMPLE
    .\tools\dev\Start-DemoAtendimento.ps1
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://localhost:5155',
    [int]$EsperaDoApiEmSegundos = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$semSql = '${env:Logging__LogLevel__Microsoft.EntityFrameworkCore} = ''Warning''; '

# O que o Api e o Worker herdam: os provedores externos APONTAM PARA AS SIMULACOES LOCAIS, e o reconciliador fica desligado (nao revive sessao antiga).
$ambiente = '$env:AiGateway__Providers__OpenAi__BaseAddress = ''http://127.0.0.1:5199/v1/''; ' +
    '$env:Channels__Providers__WhatsApp__BaseAddress = ''http://127.0.0.1:5198/''; ' +
    '$env:Agents__Options__ReconcileMinimumAge = ''3650.00:00:00''; '

function Start-Janela {
    param([string]$Comando)
    Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoExit', '-ExecutionPolicy', 'Bypass', '-Command', $Comando) | Out-Null
}

Write-Host 'Subindo a OpenAI simulada (5199) e o WhatsApp simulado (5198)...'
Start-Janela ('Set-Location ''' + $raiz + '''; .\tools\dev\Start-OpenAiSimulada.ps1')
Start-Janela ('Set-Location ''' + $raiz + '''; .\tools\dev\Start-WhatsAppSimulado.ps1')

Write-Host 'Subindo o Api (a primeira vez compila e aplica as migrations)...'
Start-Janela ('Set-Location ''' + $raiz + '''; ' + $semSql + $ambiente + 'dotnet run --project backend\host\IaBimeta.Api --launch-profile simulador-ia')

$limite = (Get-Date).AddSeconds($EsperaDoApiEmSegundos)
$noAr = $false
while ((Get-Date) -lt $limite) {
    Start-Sleep -Seconds 2
    try {
        $saude = Invoke-RestMethod -Method Get -Uri ($Url + '/health')
        if ($saude.status -eq 'ok') { $noAr = $true; break }
    }
    catch {
        # ainda subindo
    }
}
if (-not $noAr) {
    Write-Host ('FALHA: o Api nao respondeu em ' + $EsperaDoApiEmSegundos + ' s. Veja a janela dele.')
    exit 1
}

Write-Host 'Api no ar. Subindo o Worker e a interface...'
Start-Janela ('Set-Location ''' + $raiz + '''; ' + $semSql + $ambiente + '$env:DOTNET_ENVIRONMENT = ''Development''; dotnet run --project backend\host\IaBimeta.Worker')
Start-Janela ('Set-Location ''' + $raiz + '\frontend''; npm run dev')

Write-Host ''
Write-Host 'Demonstracao no ar.'
Write-Host '  Interface do atendente : http://localhost:5173   (tenant demo-ia, usuario admin@demo-ia.local, senha DevLocal-Admin-2026!)'
Write-Host '  Mandar mensagem de cliente : .\tools\dev\Send-MensagemWhatsApp.ps1 -Texto ''Ola, tudo bem?'''
Write-Host '  O que o cliente recebe : na janela do WhatsApp simulado.'
