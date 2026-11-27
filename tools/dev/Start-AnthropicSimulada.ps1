<#
.SYNOPSIS
    Uma "Anthropic" local para validar o adaptador SEM credito e SEM chamada paga: escuta em 127.0.0.1 e responde
    a Messages API (POST /v1/messages) no formato que o adaptador espera. Roda ate Ctrl-C.

.DESCRIPTION
    Regras simples sobre a ULTIMA mensagem do cliente: se ela pede um atendente ou fala de preco, a decisao e
    TRANSFERIR para o humano (motivo customer_request ou price); senao, a decisao e RESPONDER com um texto simulado.
    O consumo informado e fixo (entrada em tres parcelas, como a API real: input_tokens, cache_creation_input_tokens e
    cache_read_input_tokens). Nada sai da maquina e nenhuma chave e validada.

    Para o Api e o Worker usarem esta "Anthropic", inicie os dois com a variavel de ambiente
      AiGateway__Providers__Anthropic__BaseAddress = http://127.0.0.1:5200/v1/
    (veja docs\operations\ETAPA-03-ANTHROPIC.md). O tenant precisa ter uma chave cadastrada (qualquer texto serve).

.EXAMPLE
    .\tools\dev\Start-AnthropicSimulada.ps1
#>
[CmdletBinding()]
param(
    [int]$Porta = 5200
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ouvinte = New-Object System.Net.HttpListener
$ouvinte.Prefixes.Add('http://127.0.0.1:' + $Porta + '/')
$ouvinte.Start()
Write-Host ('Anthropic simulada ouvindo em http://127.0.0.1:' + $Porta + '/v1/  (Ctrl-C para parar)')

try {
    while ($ouvinte.IsListening) {
        $contexto = $ouvinte.GetContext()
        $leitor = New-Object System.IO.StreamReader($contexto.Request.InputStream, [System.Text.Encoding]::UTF8)
        $corpo = $leitor.ReadToEnd()

        $ultima = ''
        $modelo = ''
        try {
            $json = ConvertFrom-Json -InputObject $corpo
            $modelo = [string]$json.model
            $ultima = [string]@($json.messages)[-1].content
        }
        catch {
        }

        $versao = $contexto.Request.Headers['anthropic-version']
        $autenticada = -not [string]::IsNullOrEmpty($contexto.Request.Headers['Authorization'])

        $decisao = $null
        if ($ultima -match 'atendente|humano|pessoa|falar com') {
            $decisao = @{ action = 'handoff'; reply_text = ''; handoff_reason = 'customer_request' }
        }
        elseif ($ultima -match 'pre.o|custa|valor|quanto') {
            $decisao = @{ action = 'handoff'; reply_text = ''; handoff_reason = 'price' }
        }
        else {
            $decisao = @{ action = 'reply'; reply_text = 'Resposta simulada da IA (Anthropic): recebi a sua mensagem e posso ajudar com o atendimento.'; handoff_reason = 'none' }
        }

        Write-Host ((Get-Date).ToString('HH:mm:ss') + ' modelo=' + $modelo + ' versao=' + $versao + ' autenticada=' + $autenticada + ' cliente="' + $ultima + '" -> ' + $decisao.action)

        $resposta = @{
            id = 'msg_simulada'
            type = 'message'
            role = 'assistant'
            model = $modelo
            content = @(@{ type = 'text'; text = (ConvertTo-Json -InputObject $decisao -Compress) })
            stop_reason = 'end_turn'
            stop_sequence = $null
            usage = @{ input_tokens = 300; cache_creation_input_tokens = 0; cache_read_input_tokens = 600; output_tokens = 40 }
        }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $resposta -Depth 8 -Compress))
        $contexto.Response.StatusCode = 200
        $contexto.Response.ContentType = 'application/json'
        $contexto.Response.AddHeader('request-id', 'req_simulada')
        $contexto.Response.ContentLength64 = $bytes.Length
        $contexto.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $contexto.Response.Close()
    }
}
finally {
    $ouvinte.Stop()
}
