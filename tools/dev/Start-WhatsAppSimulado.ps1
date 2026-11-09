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
        $modelo = $null
        $linhasDoModelo = @()
        try {
            $json = ConvertFrom-Json -InputObject $corpo
            $para = [string]$json.to
            if ([string]$json.type -eq 'template') {
                # Um MODELO de mensagem aprovado (o unico tipo que a Meta aceita fora da janela de 24 horas). O corpo e o da Cloud API:
                # template.name, template.language.code e os componentes (header, body, button) com os parametros de texto.
                $modelo = $json.template
                foreach ($componente in @($modelo.components)) {
                    foreach ($parametro in @($componente.parameters)) {
                        $nome = ''
                        if ($parametro.PSObject.Properties['parameter_name']) { $nome = ' (' + [string]$parametro.parameter_name + ')' }
                        $linhasDoModelo += ('      ' + [string]$componente.type + $nome + ' = ' + [string]$parametro.text)
                    }
                }
            }
            else {
                $texto = [string]$json.text.body
            }
        }
        catch {
        }

        # Como a Meta: um modelo sem nome ou sem idioma e um pedido invalido (nada e "entregue").
        if ($null -ne $modelo -and ([string]::IsNullOrWhiteSpace([string]$modelo.name) -or [string]::IsNullOrWhiteSpace([string]$modelo.language.code))) {
            Write-Host ((Get-Date).ToString('HH:mm:ss') + '  [recusado] modelo sem nome ou sem idioma: a Meta responderia 400.')
            Write-Host ''
            $recusa = @{ error = @{ message = 'Parametro invalido'; type = 'OAuthException'; code = 100; fbtrace_id = 'SIMULADO' } }
            $bytesRecusa = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $recusa -Depth 5 -Compress))
            $contexto.Response.StatusCode = 400
            $contexto.Response.ContentType = 'application/json'
            $contexto.Response.ContentLength64 = $bytesRecusa.Length
            $contexto.Response.OutputStream.Write($bytesRecusa, 0, $bytesRecusa.Length)
            $contexto.Response.Close()
            continue
        }

        if ($null -ne $modelo) {
            Write-Host ((Get-Date).ToString('HH:mm:ss') + '  WhatsApp -> ' + $para + ' :  [MODELO ' + [string]$modelo.name + ' / ' + [string]$modelo.language.code + ']')
            foreach ($linha in $linhasDoModelo) { Write-Host $linha }
        }
        else {
            Write-Host ((Get-Date).ToString('HH:mm:ss') + '  WhatsApp -> ' + $para + ' :')
            Write-Host ('    ' + $texto)
        }
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
