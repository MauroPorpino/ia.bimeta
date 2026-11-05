<#
.SYNOPSIS
    A "Graph API" da Meta local para a demonstracao do atendimento: recebe o envio de mensagens do Sender
    (POST /{versao}/{numero}/messages), IMPRIME cada mensagem que sairia pelo WhatsApp e responde que a Meta
    aceitou. Nada sai da maquina. Roda ate Ctrl-C.

.DESCRIPTION
    Para o Worker enviar para ca, inicie-o com a variavel de ambiente
      Channels__Providers__WhatsApp__BaseAddress = http://127.0.0.1:5198/
    (veja tools\dev\README-demo-atendimento.md). Este script e o "celular do cliente": o que aparece aqui e o
    que o cliente leria no WhatsApp (a resposta da IA, o aviso de transferencia e a resposta do atendente).

.EXAMPLE
    .\tools\dev\Start-WhatsAppSimulado.ps1
#>
[CmdletBinding()]
param(
    [int]$Porta = 5198
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ouvinte = New-Object System.Net.HttpListener
$ouvinte.Prefixes.Add('http://127.0.0.1:' + $Porta + '/')
$ouvinte.Start()
Write-Host ('WhatsApp simulado ouvindo em http://127.0.0.1:' + $Porta + '/  (Ctrl-C para parar)')
Write-Host 'Aqui aparece o que o cliente recebe no celular.'
Write-Host ''

$contador = 0
try {
    while ($ouvinte.IsListening) {
        $contexto = $ouvinte.GetContext()
        $leitor = New-Object System.IO.StreamReader($contexto.Request.InputStream, [System.Text.Encoding]::UTF8)
        $corpo = $leitor.ReadToEnd()

        # A SONDA do teste de prontidao: GET /{versao}/{phone_number_id} (so leitura, nenhuma mensagem). Responde como a Meta responde ao dono do numero.
        if ($contexto.Request.HttpMethod -eq 'GET') {
            $numero = ($contexto.Request.Url.AbsolutePath.Trim('/') -split '/')[-1]
            Write-Host ((Get-Date).ToString('HH:mm:ss') + '  [sonda] leitura do numero ' + $numero + ' (nenhuma mensagem)')
            $leitura = @{ id = $numero; display_phone_number = '+55 11 3000-0000'; verified_name = 'Laboratorio de Demonstracao'; quality_rating = 'GREEN' }
            $bytesLeitura = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $leitura -Compress))
            $contexto.Response.StatusCode = 200
            $contexto.Response.ContentType = 'application/json'
            $contexto.Response.ContentLength64 = $bytesLeitura.Length
            $contexto.Response.OutputStream.Write($bytesLeitura, 0, $bytesLeitura.Length)
            $contexto.Response.Close()
            continue
        }

        $contador++

        $para = '?'
        $texto = ''
        try {
            $json = ConvertFrom-Json -InputObject $corpo
            $para = [string]$json.to
            $texto = [string]$json.text.body
        }
        catch {
        }

        Write-Host ((Get-Date).ToString('HH:mm:ss') + '  WhatsApp -> ' + $para + ' :')
        Write-Host ('    ' + $texto)
        Write-Host ''

        $resposta = @{
            messaging_product = 'whatsapp'
            contacts = @(@{ input = $para; wa_id = $para })
            messages = @(@{ id = ('wamid.SIMULADA' + $contador.ToString('D4')) })
        }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $resposta -Depth 5 -Compress))
        $contexto.Response.StatusCode = 200
        $contexto.Response.ContentType = 'application/json'
        $contexto.Response.ContentLength64 = $bytes.Length
        $contexto.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $contexto.Response.Close()
    }
}
finally {
    $ouvinte.Stop()
}
