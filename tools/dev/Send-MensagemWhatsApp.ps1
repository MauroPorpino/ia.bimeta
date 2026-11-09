<#
.SYNOPSIS
    Faz o papel da Meta: envia ao Receiver do Api um webhook de mensagem de cliente do WhatsApp, ASSINADO como a
    Meta assina (HMAC-SHA256 do corpo com o App Secret, em X-Hub-Signature-256), para o canal de demonstracao do
    tenant demo-ia. O que acontece depois (IA, transferencia, atendente) e o fluxo de verdade.

.DESCRIPTION
    Le o caminho do webhook e o App Secret SINTETICO de desenvolvimento em GET /simulator/whatsapp/status (so existe em
    Development). O Api precisa estar no ar com o perfil simulador-ia.

.EXAMPLE
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Ola, tudo bem?'
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Quero falar com um atendente' -Nome 'Pedro' -Numero 5511988887777

    # Uma conversa longa (130 mensagens do mesmo cliente), para ver o historico em paginas na interface:
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'mensagem' -Nome 'Historia' -Numero 5511999990301 -Quantidade 130

    # Uma conversa com a janela de 24 horas FECHADA (a ultima mensagem do cliente tem 26 horas): so modelo aprovado:
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Quero falar com um atendente' -Nome Mauro -Numero 5511999990301 -HorasAtras 26
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Texto,
    [string]$Nome = 'Mauro',
    [string]$Numero = '5511999990001',
    [string]$Url = 'http://localhost:5155',
    [string]$Tenant = 'demo-ia',

    # Quantas mensagens enviar (com mais de uma, cada texto ganha " (i/N)" e o id do provedor e unico).
    [ValidateRange(1, 1000)][int]$Quantidade = 1,

    # A mensagem do cliente tem ESTA idade pelo relogio da Meta (padrao: agora). Com mais de 24 horas, a janela de resposta
    # livre do WhatsApp esta fechada e o atendente so pode responder com um modelo de mensagem aprovado.
    [ValidateRange(0, 720)][double]$HorasAtras = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $canal = Invoke-RestMethod -Method Get -Uri ($Url + '/simulator/whatsapp/status') -Headers @{ 'X-Dev-Tenant' = $Tenant }
}
catch {
    Write-Host 'FALHA: o Api nao respondeu em /simulator/whatsapp/status. Ele esta no ar com o perfil simulador-ia?'
    exit 1
}

$textoBase = $Texto
$lote = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
for ($indice = 1; $indice -le $Quantidade; $indice++) {
$Texto = $textoBase
if ($Quantidade -gt 1) { $Texto = $textoBase + ' (' + $indice + '/' + $Quantidade + ')' }
$agora = [DateTimeOffset]::UtcNow.AddHours(-1 * $HorasAtras)
$idDaMensagem = 'wamid.SIM' + $lote + '-' + $indice
$payload = @{
    object = 'whatsapp_business_account'
    entry = @(@{
        id = [string]$canal.wabaId
        changes = @(@{
            field = 'messages'
            value = @{
                messaging_product = 'whatsapp'
                metadata = @{ display_phone_number = '551130000000'; phone_number_id = $canal.phoneNumberId }
                contacts = @(@{ profile = @{ name = $Nome }; wa_id = $Numero })
                messages = @(@{ from = $Numero; id = $idDaMensagem; timestamp = [string]$agora.ToUnixTimeSeconds(); type = 'text'; text = @{ body = $Texto } })
            }
        })
    })
}

$corpo = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $payload -Depth 10 -Compress))
$hmac = New-Object System.Security.Cryptography.HMACSHA256 (, [System.Text.Encoding]::UTF8.GetBytes([string]$canal.appSecret))
$assinatura = 'sha256=' + ([BitConverter]::ToString($hmac.ComputeHash($corpo)).Replace('-', '').ToLowerInvariant())

try {
    $resposta = Invoke-WebRequest -Method Post -Uri ($Url + $canal.webhookPath) -Body $corpo -ContentType 'application/json' -Headers @{ 'X-Hub-Signature-256' = $assinatura } -UseBasicParsing
    Write-Host ('Webhook entregue ao Receiver (HTTP ' + [int]$resposta.StatusCode + '). Mensagem de ' + $Nome + ' (' + $Numero + '): ' + $Texto)
}
catch {
    $codigo = 0
    if ($null -ne $_.Exception.Response) { $codigo = [int]$_.Exception.Response.StatusCode }
    Write-Host ('FALHA: o Receiver respondeu HTTP ' + $codigo + '.')
    exit 1
}
}
