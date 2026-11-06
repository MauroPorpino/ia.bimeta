<#
.SYNOPSIS
    Cria um SEGUNDO atendente (papel comum) no tenant de demonstracao e o poe na fila de atendimento humano, para voce
    abrir a interface em duas abas (ou dois navegadores) e ver dois atendentes trabalhando ao mesmo tempo.

.DESCRIPTION
    Usa o Api local (perfil simulador-ia) como o administrador de demonstracao: le a organizacao e a fila em
    GET /simulator/ai/status, cria o usuario (POST /organizations/{id}/users, papel User) e o adiciona a fila como
    atendente (PUT /contact-center/queues/{id}/members/{userId}). Repetir e inofensivo: usuario que ja existe e so
    colocado na fila. A senha e SINTETICA e so vale neste banco de desenvolvimento.

    A sessao fica no sessionStorage da ABA: entre com um atendente em cada aba (ou use uma janela anonima).

.EXAMPLE
    .\tools\dev\Add-AtendenteDeDemonstracao.ps1
    .\tools\dev\Add-AtendenteDeDemonstracao.ps1 -Email carlos@demo-ia.local -Nome 'Carlos Atendente'
#>
[CmdletBinding()]
param(
    [string]$Tenant = 'demo-ia',
    [string]$Url = 'http://localhost:5155',
    [string]$Email = 'bruna@demo-ia.local',
    [string]$Nome = 'Bruna Atendente',
    [string]$Senha = 'DevLocal-Atendente-2026!',
    [string]$EmailDoAdministrador = 'admin@demo-ia.local',
    [string]$SenhaDoAdministrador = 'DevLocal-Admin-2026!'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$cabecalhosDev = @{ 'X-Dev-Tenant' = $Tenant }

try {
    $estado = Invoke-RestMethod -Method Get -Uri ($Url + '/simulator/ai/status') -Headers $cabecalhosDev
}
catch {
    Write-Host 'FALHA: o Api nao respondeu em /simulator/ai/status. Ele esta no ar com o perfil simulador-ia?'
    exit 1
}
$organizacao = $estado.organizationId
$fila = $estado.queueId
if ([string]::IsNullOrEmpty($organizacao) -or [string]::IsNullOrEmpty($fila)) {
    Write-Host 'FALHA: o tenant ainda nao tem organizacao e fila de demonstracao (suba o Api com o perfil simulador-ia).'
    exit 1
}

$corpoLogin = ConvertTo-Json -InputObject @{ tenant = $Tenant; email = $EmailDoAdministrador; password = $SenhaDoAdministrador } -Compress
$login = Invoke-RestMethod -Method Post -Uri ($Url + '/auth/login') -ContentType 'application/json' -Body $corpoLogin
$auth = @{ Authorization = ('Bearer ' + $login.token) }

# Lista de JSON no PowerShell 5.1 chega como um objeto so: ForEach-Object { $_ } espalha os itens.
$usuarios = @(Invoke-RestMethod -Method Get -Uri ($Url + '/organizations/' + $organizacao + '/users') -Headers $auth | ForEach-Object { $_ })
$existente = @($usuarios | Where-Object { $_.email -eq $Email })
if ($existente.Count -eq 0) {
    $corpo = ConvertTo-Json -InputObject @{ email = $Email; displayName = $Nome; password = $Senha; role = 'User' } -Compress
    $criado = Invoke-RestMethod -Method Post -Uri ($Url + '/organizations/' + $organizacao + '/users') -Headers $auth -ContentType 'application/json' -Body $corpo
    $usuarioId = $criado.id
    Write-Host ('Usuario criado: ' + $Email)
}
else {
    $usuarioId = $existente[0].userId
    if ($null -eq $usuarioId) { $usuarioId = $existente[0].id }
    Write-Host ('Usuario ja existia: ' + $Email)
}

$membro = ConvertTo-Json -InputObject @{ role = 'Agent' } -Compress
Invoke-RestMethod -Method Put -Uri ($Url + '/contact-center/queues/' + $fila + '/members/' + $usuarioId) -Headers $auth -ContentType 'application/json' -Body $membro | Out-Null

Write-Host ''
Write-Host ('Pronto. Segundo atendente: tenant ' + $Tenant + ', e-mail ' + $Email + ', senha ' + $Senha)
Write-Host 'Abra a interface em outra aba (ou janela anonima) e entre com ele; o primeiro atendente continua na aba original.'
