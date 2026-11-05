<#
.SYNOPSIS
    Sobe o simulador de IA: o Api (perfil de execucao "simulador-ia": Development, simulador ligado e IA de
    demonstracao preparada) e, depois que ele responde, o Worker (relay, executor de turnos de IA). Cada um
    abre na sua janela do PowerShell; feche as janelas para parar.

.DESCRIPTION
    Pre-requisitos: PostgreSQL de desenvolvimento no ar e a string de conexao nos user-secrets do Api
    (ConnectionStrings:Tenancy ou Default), a mesma que o Worker usa (mesmo UserSecretsId).
    ATENCAO: ao subir em Development o Api APLICA as migrations pendentes (inclusive as do AiGateway e do Agents)
    no banco configurado. Faca o backup que quiser antes. Subir o Api nao faz nenhuma chamada a OpenAI.

.EXAMPLE
    .\tools\dev\Start-SimuladorDeIa.ps1
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://localhost:5155',
    [int]$EsperaDoApiEmSegundos = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# O SQL do EF em nivel Information afoga o que importa (os turnos de IA): so avisos e erros.
$semSql = '${env:Logging__LogLevel__Microsoft.EntityFrameworkCore} = ''Warning''; '
$comandoDoApi = 'Set-Location ''' + $raiz + '''; ' + $semSql + 'dotnet run --project backend\host\IaBimeta.Api --launch-profile simulador-ia'
Write-Host 'Subindo o Api (a primeira vez compila: pode demorar)...'
Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoExit', '-ExecutionPolicy', 'Bypass', '-Command', $comandoDoApi) | Out-Null

$limite = (Get-Date).AddSeconds($EsperaDoApiEmSegundos)
$noAr = $false
while ((Get-Date) -lt $limite) {
    Start-Sleep -Seconds 2
    try {
        $saude = Invoke-RestMethod -Method Get -Uri ($Url + '/health')
        if ($saude.status -eq 'ok') { $noAr = $true; break }
    }
    catch {
        # ainda subindo (compilando ou aplicando migrations)
    }
}

if (-not $noAr) {
    Write-Host ('FALHA: o Api nao respondeu em ' + $EsperaDoApiEmSegundos + ' s. Veja a janela dele (banco inacessivel? porta ocupada?).')
    exit 1
}

Write-Host 'Api no ar. Subindo o Worker...'
$comandoDoWorker = 'Set-Location ''' + $raiz + '''; ' + $semSql + '$env:DOTNET_ENVIRONMENT = ''Development''; dotnet run --project backend\host\IaBimeta.Worker'
Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoExit', '-ExecutionPolicy', 'Bypass', '-Command', $comandoDoWorker) | Out-Null

Write-Host ''
Write-Host 'Simulador no ar.'
Write-Host ('  Situacao da IA : ' + $Url + '/simulator/ai/status  (cabecalho X-Dev-Tenant: demo-ia)')
Write-Host '  Proximo passo  : tools\dev\Set-ChaveDaOpenAI.ps1 (cadastra a chave) e depois tools\dev\Invoke-TesteDeIa.ps1 (SO apos autorizado: faz a chamada paga).'
