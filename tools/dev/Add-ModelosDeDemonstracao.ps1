<#
.SYNOPSIS
    Associa a conta de WhatsApp do tenant de demonstracao os MODELOS de mensagem (templates) de exemplo, como se ja
    tivessem sido aprovados na Meta, para voce ver na interface o envio de modelo fora da janela de 24 horas.

.DESCRIPTION
    Faz o papel do administrador: entra como admin@demo-ia.local e envia a pagina de modelos de
    tools\dev\modelos-demonstracao.json (no formato que a Graph API devolve em GET /{waba-id}/message_templates) para
    POST /channels/accounts/{id}/templates. NAO cria nem aprova nada na Meta (nem existe Meta aqui): so ASSOCIA os
    modelos a conta. Repetir e inofensivo (atualiza o que mudou). Um modelo que o MVP nao sabe enviar (cabecalho de
    imagem, copiar codigo, autenticacao) e recusado com o motivo, sem impedir os demais: o terceiro modelo do arquivo
    existe so para mostrar essa recusa. Use -Arquivo para associar os modelos reais da sua conta.

    Para fechar a janela de 24 horas de um cliente, envie a mensagem dele "velha":
      .\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Quero falar com um atendente' -Nome Mauro -Numero 5511999990301 -HorasAtras 26

.EXAMPLE
    .\tools\dev\Add-ModelosDeDemonstracao.ps1
#>
[CmdletBinding()]
param(
    [string]$Tenant = 'demo-ia',
    [string]$Url = 'http://localhost:5155',
    [string]$EmailDoAdministrador = 'admin@demo-ia.local',
    [string]$SenhaDoAdministrador = 'DevLocal-Admin-2026!',
    [string]$Arquivo = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Arquivo)) {
    $Arquivo = Join-Path $PSScriptRoot 'modelos-demonstracao.json'
}

if (-not (Test-Path -LiteralPath $Arquivo)) {
    Write-Host ('FALHA: o arquivo de modelos nao existe: ' + $Arquivo)
    exit 1
}
# O arquivo e UTF-8 (com acentos): le com a codificacao explicita e envia os BYTES, sem passar pela pagina de codigo do console.
$pagina = [System.IO.File]::ReadAllText($Arquivo, (New-Object System.Text.UTF8Encoding($false)))

$corpoLogin = ConvertTo-Json -InputObject @{ tenant = $Tenant; email = $EmailDoAdministrador; password = $SenhaDoAdministrador } -Compress
try {
    $login = Invoke-RestMethod -Method Post -Uri ($Url + '/auth/login') -ContentType 'application/json' -Body $corpoLogin
}
catch {
    Write-Host 'FALHA: nao foi possivel entrar como o administrador de demonstracao. O Api esta no ar com o perfil simulador-ia?'
    exit 1
}
$auth = @{ Authorization = ('Bearer ' + $login.token) }

# Lista de JSON no PowerShell 5.1 chega como um objeto so: ForEach-Object { $_ } espalha os itens.
$contas = @(Invoke-RestMethod -Method Get -Uri ($Url + '/channels/accounts') -Headers $auth | ForEach-Object { $_ })
if ($contas.Count -eq 0) {
    Write-Host 'FALHA: o tenant nao tem conta de WhatsApp cadastrada.'
    exit 1
}
$conta = $contas[0]

$bytes = [System.Text.Encoding]::UTF8.GetBytes($pagina)
$registro = Invoke-RestMethod -Method Post -Uri ($Url + '/channels/accounts/' + $conta.id + '/templates') -Headers $auth -ContentType 'application/json; charset=utf-8' -Body $bytes

Write-Host ('Conta: ' + $conta.name + ' (' + $conta.id + ')')
Write-Host ('Modelos novos: ' + @($registro.created).Count + '  atualizados: ' + @($registro.updated).Count + '  sem mudanca: ' + @($registro.unchanged).Count)
foreach ($recusado in @($registro.rejected)) {
    Write-Host ('Recusado: ' + $recusado)
}
Write-Host ''
Write-Host 'Pronto. Na interface, abra uma conversa (assumida) cuja ultima mensagem do cliente tem mais de 24 horas: o campo de texto livre fica bloqueado e a lista de modelos aparece.'
