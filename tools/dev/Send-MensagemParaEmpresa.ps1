<#
.SYNOPSIS
    Faz o papel da Meta para uma EMPRESA colocada em funcionamento pelo comando provision (conta de WhatsApp SIMULADA):
    envia ao Receiver do Api um webhook de mensagem de cliente, ASSINADO como a Meta assina (HMAC-SHA256 do corpo com o
    App Secret sintetico em X-Hub-Signature-256).

.DESCRIPTION
    Diferente de Send-MensagemWhatsApp.ps1 (que serve o tenant demo-ia e le o canal em /simulator/whatsapp/status, so
    existente em Development), este script serve QUALQUER empresa provisionada com "whatsApp": { "mode": "simulated" },
    inclusive com o Api fora do ambiente Development. Os identificadores do canal simulado sao derivados do slug (os mesmos
    de TenantProvisioner.SimulatedIdentifiers) e o caminho do webhook e o que o comando provision imprime
    ("Webhook do WhatsApp: /webhooks/whatsapp/<id>"). O App Secret e SINTETICO (o mesmo do canal de demonstracao):
    nenhum segredo real passa por aqui, e nada sai da maquina.

    Use texto sem acento ao passar -Texto pela linha de comando do Windows (a normalizacao do FAQ ignora acento, e o
    console do PowerShell 5.1 pode corromper a codificacao). Veja docs\operations\HOMOLOGACAO-DO-CONHECIMENTO.md.

.PARAMETER Slug
    O slug da empresa (o mesmo do arquivo do provision).

.PARAMETER WebhookPath
    O caminho do webhook impresso pelo provision (por exemplo /webhooks/whatsapp/3d5c3a92-7248-490e-9d26-9a68a47cbb8b).

.EXAMPLE
    .\tools\dev\Send-MensagemParaEmpresa.ps1 -Slug demo-conhecimento -WebhookPath /webhooks/whatsapp/3d5c3a92-7248-490e-9d26-9a68a47cbb8b -Texto 'Qual o horario de atendimento?' -Nome Maria -Numero 5511999990501
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Slug,
    [Parameter(Mandatory = $true)][string]$WebhookPath,
    [Parameter(Mandatory = $true)][string]$Texto,
    [string]$Nome = 'Cliente Demo',
    [string]$Numero = '5511999990501',
    [string]$Url = 'http://localhost:5155',
    [string]$AppSecret = 'dev-app-secret-whatsapp-demo'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Os identificadores do canal simulado do slug (a mesma regra do provisionamento): waba-sim-<slug> e "9" seguido de 11 digitos do SHA-256 do slug.
function Get-IdentificadoresSimulados {
    param([string]$SlugDaEmpresa)
    $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($SlugDaEmpresa))
    $digitos = '9'
    for ($i = 0; $i -lt 11; $i++) { $digitos += [string]($hash[$i] % 10) }
    return @{ WabaId = ('waba-sim-' + $SlugDaEmpresa); PhoneNumberId = $digitos }
}

$ids = Get-IdentificadoresSimulados -SlugDaEmpresa $Slug
$agora = [DateTimeOffset]::UtcNow
$mensagem = @{ from = $Numero; id = ('wamid.DEMO' + [Guid]::NewGuid().ToString('N')); timestamp = [string]$agora.ToUnixTimeSeconds(); type = 'text'; text = @{ body = $Texto } }
$payload = @{
    object = 'whatsapp_business_account'
    entry = @(@{
        id = $ids.WabaId
        changes = @(@{
            field = 'messages'
            value = @{
                messaging_product = 'whatsapp'
                metadata = @{ display_phone_number = '551130000000'; phone_number_id = $ids.PhoneNumberId }
                contacts = @(@{ profile = @{ name = $Nome }; wa_id = $Numero })
                messages = @($mensagem)
            }
        })
    })
}

$corpo = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $payload -Depth 10 -Compress))
$hmac = New-Object System.Security.Cryptography.HMACSHA256 (, [System.Text.Encoding]::UTF8.GetBytes($AppSecret))
$assinatura = 'sha256=' + ([BitConverter]::ToString($hmac.ComputeHash($corpo)).Replace('-', '').ToLowerInvariant())

$resposta = Invoke-WebRequest -Method Post -Uri ($Url + $WebhookPath) -Body $corpo -ContentType 'application/json' -Headers @{ 'X-Hub-Signature-256' = $assinatura } -UseBasicParsing
Write-Host ('Webhook entregue ao Receiver (HTTP ' + [int]$resposta.StatusCode + '). ' + $Nome + ' (' + $Numero + '): ' + $Texto)
