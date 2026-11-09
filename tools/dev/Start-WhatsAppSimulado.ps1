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

    MIDIA (imagem, audio, documento): tambem serve os arquivos que Send-MensagemWhatsApp.ps1 -Midia deposita na
    pasta de midia, como a Graph API: GET /{versao}/{media-id} devolve os dados (url, tipo, resumo, tamanho) e
    GET /media-content/{media-id} devolve o conteudo. As duas exigem o token da conta no cabecalho Authorization
    (Bearer), como a Meta; o token NUNCA e impresso.

.EXAMPLE
    .\tools\dev\Start-WhatsAppSimulado.ps1
#>
[CmdletBinding()]
param(
    [int]$Porta = 5198,

    # Onde Send-MensagemWhatsApp.ps1 -Midia deposita os arquivos ({id}.bin e {id}.json). A mesma pasta nos dois scripts.
    [string]$PastaDeMidia = (Join-Path ([System.IO.Path]::GetTempPath()) 'iabimeta-midia-demo')
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

function Send-Bytes {
    param($Contexto, [int]$Status, [byte[]]$Bytes, [string]$Tipo)
    $Contexto.Response.StatusCode = $Status
    $Contexto.Response.ContentType = $Tipo
    $Contexto.Response.ContentLength64 = $Bytes.Length
    $Contexto.Response.OutputStream.Write($Bytes, 0, $Bytes.Length)
    $Contexto.Response.Close()
}

function Send-ErroDaGraph {
    param($Contexto, [int]$Status, [int]$Codigo)
    $erro = @{ error = @{ message = 'Erro simulado'; type = 'OAuthException'; code = $Codigo; fbtrace_id = 'SIMULADO' } }
    Send-Bytes $Contexto $Status ([System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $erro -Depth 5 -Compress))) 'application/json'
}

try {
    while ($ouvinte.IsListening) {
        $contexto = $ouvinte.GetContext()
        $leitor = New-Object System.IO.StreamReader($contexto.Request.InputStream, [System.Text.Encoding]::UTF8)
        $corpo = $leitor.ReadToEnd()

        # MIDIA recebida de um cliente (imagem, audio, documento): a consulta dos dados e o conteudo, como a Graph API. O token da conta e exigido nas duas
        # (Authorization: Bearer ...), e nunca e impresso. O identificador so pode ter letras, digitos e _.- (nada que saia da pasta de midia).
        $partes = @($contexto.Request.Url.AbsolutePath.Trim('/') -split '/')
        if ($contexto.Request.HttpMethod -eq 'GET' -and $partes.Count -eq 2) {
            $idDaMidia = $partes[1]
            $ehConteudo = ($partes[0] -eq 'media-content')
            $arquivoDeDados = Join-Path $PastaDeMidia ($idDaMidia + '.json')
            $marcaDeExpirada = Join-Path $PastaDeMidia ($idDaMidia + '.expirada')
            if ($idDaMidia -match '^[A-Za-z0-9_.-]{1,128}$' -and -not $ehConteudo -and (Test-Path -LiteralPath $marcaDeExpirada)) {
                # Midia que a Meta nao tem mais (o identificador vale 7 dias): 404, como a Graph API.
                Write-Host ((Get-Date).ToString('HH:mm:ss') + '  [midia] dados de ' + $idDaMidia + ': EXPIRADA (404)')
                Send-ErroDaGraph $contexto 404 100
                continue
            }
            if ($idDaMidia -match '^[A-Za-z0-9_.-]{1,128}$' -and (Test-Path -LiteralPath $arquivoDeDados)) {
                $autorizacao = [string]$contexto.Request.Headers['Authorization']
                $comToken = $autorizacao.StartsWith('Bearer ') -and $autorizacao.Length -gt 8
                $etapa = 'dados'
                if ($ehConteudo) { $etapa = 'conteudo' }
                Write-Host ((Get-Date).ToString('HH:mm:ss') + '  [midia] ' + $etapa + ' de ' + $idDaMidia + ' (token da conta no cabecalho: ' + $(if ($comToken) { 'sim' } else { 'NAO' }) + ')')
                if (-not $comToken) {
                    Send-ErroDaGraph $contexto 401 190
                    continue
                }

                $dados = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($arquivoDeDados))
                $arquivoDeConteudo = Join-Path $PastaDeMidia ($idDaMidia + '.bin')
                if ($ehConteudo) {
                    Send-Bytes $contexto 200 ([System.IO.File]::ReadAllBytes($arquivoDeConteudo)) ([string]$dados.mime_type)
                }
                else {
                    $resposta = @{
                        messaging_product = 'whatsapp'
                        url = ('http://127.0.0.1:' + $Porta + '/media-content/' + $idDaMidia)
                        mime_type = [string]$dados.mime_type
                        sha256 = [string]$dados.sha256
                        file_size = [string]$dados.file_size
                        id = $idDaMidia
                    }
                    Send-Bytes $contexto 200 ([System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $resposta -Compress))) 'application/json'
                }
                continue
            }
        }

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
