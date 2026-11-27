<#
.SYNOPSIS
    Um "Ollama" local para validar o adaptador conversacional SEM instalar o Ollama e SEM baixar modelo: escuta em
    127.0.0.1 e responde GET /api/version, GET /api/tags, POST /api/show e POST /api/chat no formato documentado.
    Roda ate Ctrl-C.

.DESCRIPTION
    Regras simples sobre a ULTIMA mensagem do cliente: se ela pede um atendente ou fala de preco, a decisao e
    TRANSFERIR para o humano (customer_request ou price); senao, RESPONDER com um texto simulado. Devolve contagens
    fixas (prompt_eval_count e eval_count), done_reason "stop" e um campo "thinking" (que o adaptador nunca repassa).
    Nada sai da maquina. NAO prova compatibilidade com o Ollama real: so a validacao real (docs\operations\ETAPA-04-OLLAMA.md) prova.

    Para o Api e o Worker usarem este servidor, inicie os dois (e o comando provision) com
      AiGateway__Providers__Ollama__BaseAddress = http://127.0.0.1:5201/
      AiGateway__Catalog__Models__3__Status     = Active
    O Novo-TenantDeOllamaDeDemonstracao.ps1 e o roteiro da etapa mostram o passo a passo.

.EXAMPLE
    .\tools\dev\Start-OllamaSimulado.ps1
#>
[CmdletBinding()]
param(
    [int]$Porta = 5201
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ouvinte = New-Object System.Net.HttpListener
$ouvinte.Prefixes.Add('http://127.0.0.1:' + $Porta + '/')
$ouvinte.Start()
Write-Host ('Ollama simulado ouvindo em http://127.0.0.1:' + $Porta + '/  (Ctrl-C para parar)')

function Send-Json {
    param($Contexto, [int]$Status, $Corpo)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Corpo -Depth 8 -Compress))
    $Contexto.Response.StatusCode = $Status
    $Contexto.Response.ContentType = 'application/json'
    $Contexto.Response.ContentLength64 = $bytes.Length
    $Contexto.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Contexto.Response.Close()
}

try {
    while ($ouvinte.IsListening) {
        $contexto = $ouvinte.GetContext()
        $caminho = $contexto.Request.Url.AbsolutePath
        $leitor = New-Object System.IO.StreamReader($contexto.Request.InputStream, [System.Text.Encoding]::UTF8)
        $corpo = $leitor.ReadToEnd()

        if ($caminho -eq '/api/version') { Send-Json $contexto 200 @{ version = '0.0.0-simulado' }; continue }
        if ($caminho -eq '/api/tags') {
            Send-Json $contexto 200 @{ models = @(@{ name = 'qwen3:4b'; model = 'qwen3:4b'; size = 0; details = @{ family = 'simulado' } }) }
            continue
        }
        if ($caminho -eq '/api/show') {
            Send-Json $contexto 200 @{ capabilities = @('completion', 'thinking'); model_info = @{ 'simulado.context_length' = 40960 } }
            continue
        }
        if ($caminho -ne '/api/chat') { Send-Json $contexto 404 @{ error = 'rota inexistente no simulador' }; continue }

        $ultima = ''
        $modelo = ''
        try {
            $json = ConvertFrom-Json -InputObject $corpo
            $modelo = [string]$json.model
            $ultima = [string]@($json.messages)[-1].content
        }
        catch {
        }

        if ($modelo -ne 'qwen3:4b') {
            Write-Host ((Get-Date).ToString('HH:mm:ss') + ' modelo=' + $modelo + ' NAO instalado -> 404')
            Send-Json $contexto 404 @{ error = ('model ''' + $modelo + ''' not found') }
            continue
        }

        if ($ultima -match 'atendente|humano|pessoa|falar com') {
            $decisao = @{ action = 'handoff'; reply_text = ''; handoff_reason = 'customer_request' }
        }
        elseif ($ultima -match 'pre.o|custa|valor|quanto') {
            $decisao = @{ action = 'handoff'; reply_text = ''; handoff_reason = 'price' }
        }
        else {
            $decisao = @{ action = 'reply'; reply_text = 'Resposta simulada da IA (Ollama): recebi a sua mensagem e posso ajudar com o atendimento.'; handoff_reason = 'none' }
        }

        Write-Host ((Get-Date).ToString('HH:mm:ss') + ' modelo=' + $modelo + ' cliente="' + $ultima + '" -> ' + $decisao.action)
        Send-Json $contexto 200 @{
            model = $modelo
            created_at = (Get-Date).ToUniversalTime().ToString('o')
            message = @{ role = 'assistant'; content = (ConvertTo-Json -InputObject $decisao -Compress); thinking = 'raciocinio simulado que nunca vai ao cliente' }
            done = $true
            done_reason = 'stop'
            prompt_eval_count = 300
            eval_count = 40
        }
    }
}
finally {
    $ouvinte.Stop()
}
