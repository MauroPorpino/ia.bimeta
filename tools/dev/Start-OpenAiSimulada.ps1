<#
.SYNOPSIS
    Uma "OpenAI" local para a demonstracao do atendimento SEM credito e SEM chamada paga: escuta em 127.0.0.1 e
    responde a Responses API no formato que o adaptador espera. Roda ate Ctrl-C.

.DESCRIPTION
    Regras simples sobre a ULTIMA mensagem do cliente: se ela pede um atendente ou fala de preco, a decisao e
    TRANSFERIR para o humano (motivo customer_request ou price); senao, a decisao e RESPONDER com um texto simulado.
    O consumo informado e fixo (so para o ledger ter numeros). Nada sai da maquina e nenhuma chave e validada.

    Para o Api e o Worker usarem esta "OpenAI", inicie os dois com a variavel de ambiente
      AiGateway__Providers__OpenAi__BaseAddress = http://127.0.0.1:5199/v1/
    (veja tools\dev\README-demo-atendimento.md). O tenant precisa ter uma chave cadastrada (qualquer texto serve).

.EXAMPLE
    .\tools\dev\Start-OpenAiSimulada.ps1
#>
[CmdletBinding()]
param(
    [int]$Porta = 5199
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ouvinte = New-Object System.Net.HttpListener
$ouvinte.Prefixes.Add('http://127.0.0.1:' + $Porta + '/')
$ouvinte.Start()
Write-Host ('OpenAI simulada ouvindo em http://127.0.0.1:' + $Porta + '/v1/  (Ctrl-C para parar)')

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
            $ultima = [string]@($json.input)[-1].content
        }
        catch {
        }

        $decisao = $null
        if ($ultima -match 'atendente|humano|pessoa|falar com') {
            $decisao = @{ action = 'handoff'; reply_text = ''; handoff_reason = 'customer_request' }
        }
        elseif ($ultima -match 'pre.o|custa|valor|quanto') {
            $decisao = @{ action = 'handoff'; reply_text = ''; handoff_reason = 'price' }
        }
        else {
            $decisao = @{ action = 'reply'; reply_text = 'Resposta simulada da IA: recebi a sua mensagem e posso ajudar com o atendimento.'; handoff_reason = 'none' }
        }

        Write-Host ((Get-Date).ToString('HH:mm:ss') + ' modelo=' + $modelo + ' cliente="' + $ultima + '" -> ' + $decisao.action)

        $resposta = @{
            id = 'resp_simulada'
            status = 'completed'
            output = @(@{ type = 'message'; content = @(@{ type = 'output_text'; text = (ConvertTo-Json -InputObject $decisao -Compress) }) })
            usage = @{ input_tokens = 900; input_tokens_details = @{ cached_tokens = 0 }; output_tokens = 40; output_tokens_details = @{ reasoning_tokens = 0 } }
        }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $resposta -Depth 8 -Compress))
        $contexto.Response.StatusCode = 200
        $contexto.Response.ContentType = 'application/json'
        $contexto.Response.AddHeader('x-request-id', 'req_simulada')
        $contexto.Response.ContentLength64 = $bytes.Length
        $contexto.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $contexto.Response.Close()
    }
}
finally {
    $ouvinte.Stop()
}
