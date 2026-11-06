<#
.SYNOPSIS
    Prova, SEM falar com a Meta, que o webhook publico esta certo de ponta a ponta: HTTPS valido ate o seu Api, o desafio
    de verificacao (verify token), a assinatura do corpo (App Secret) e a recusa do que nao deve entrar. Faz so chamadas
    a SUA URL publica (o tunel); nenhuma mensagem real e enviada e nenhum dado real e gravado.

.DESCRIPTION
    Verificacoes (todas contra o callback que voce informa):
      1. GET com o verify token certo e um desafio        -> deve responder 200 com o mesmo desafio.
      2. GET com o verify token errado                    -> deve recusar (403).
      3. POST assinado com o App Secret, de um numero DESCONHECIDO (Phone Number ID fake) -> 200 (descartado, nada gravado).
      4. POST com assinatura errada                       -> 401.
      5. POST num identificador de webhook que nao existe -> 404.
    O App Secret e o verify token sao pedidos sem eco e so servem para assinar/verificar nesta execucao. Se a 3 falhar com
    401, o App Secret digitado aqui (ou o que esta cadastrado no ia.bimeta) esta errado: corrija ANTES de testar com a Meta.

.PARAMETER CallbackUrl
    A URL de callback completa, a mesma que vai para a Meta: https://<publico>/webhooks/whatsapp/<id-opaco>.

.PARAMETER PermitirHttp
    So para ensaio local (http://localhost:5156/...): aceita HTTP. Com a Meta, a URL TEM de ser HTTPS valido.

.EXAMPLE
    .\tools\ops\Test-WebhookPublico.ps1 -CallbackUrl https://abc-def.trycloudflare.com/webhooks/whatsapp/6f61d83d-f8f8-4362-9738-c20e844674bc
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CallbackUrl,
    [System.Security.SecureString]$VerifyToken,
    [System.Security.SecureString]$AppSecret,
    [switch]$PermitirHttp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-TextoPlano {
    param([System.Security.SecureString]$Segura)
    $ponteiro = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Segura)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ponteiro) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ponteiro) }
}

$uri = $null
if (-not [System.Uri]::TryCreate($CallbackUrl, [System.UriKind]::Absolute, [ref]$uri)) { throw 'CallbackUrl invalida.' }
if ($uri.Scheme -ne 'https' -and -not ($PermitirHttp -and $uri.Scheme -eq 'http')) { throw 'A Meta exige HTTPS com certificado valido. Use https:// (ou -PermitirHttp so em ensaio local).' }
if ($uri.AbsolutePath -notmatch '^/webhooks/whatsapp/[0-9a-fA-F-]{36}/?$') { throw 'O caminho precisa ser /webhooks/whatsapp/<id-opaco> (o que o Conectar-ContaWhatsApp.ps1 imprimiu).' }

if ($null -eq $VerifyToken) { $VerifyToken = Read-Host -Prompt 'Verify token do webhook (o impresso pelo Conectar-ContaWhatsApp.ps1; nao aparece)' -AsSecureString }
if ($null -eq $AppSecret) { $AppSecret = Read-Host -Prompt 'App Secret do aplicativo da Meta (nao aparece)' -AsSecureString }
$verifyTexto = ConvertTo-TextoPlano $VerifyToken
$segredoTexto = ConvertTo-TextoPlano $AppSecret
$base = $uri.GetLeftPart([System.UriPartial]::Path).TrimEnd('/')

function Invoke-Http {
    param([string]$Metodo, [string]$Url, [byte[]]$Corpo = $null, [hashtable]$Cabecalhos = @{})
    $parametros = @{ Method = $Metodo; Uri = $Url; UseBasicParsing = $true; TimeoutSec = 30; Headers = $Cabecalhos }
    if ($null -ne $Corpo) { $parametros['Body'] = $Corpo; $parametros['ContentType'] = 'application/json' }
    try {
        $resposta = Invoke-WebRequest @parametros
        return [pscustomobject]@{ Status = [int]$resposta.StatusCode; Corpo = [string]$resposta.Content; Falha = $null }
    }
    catch {
        if ($null -ne $_.Exception.Response) { return [pscustomobject]@{ Status = [int]$_.Exception.Response.StatusCode; Corpo = ''; Falha = $null } }
        return [pscustomobject]@{ Status = 0; Corpo = ''; Falha = $_.Exception.Message }
    }
}

$resultados = New-Object System.Collections.Generic.List[object]
function Add-Resultado {
    param([string]$Nome, [bool]$Ok, [string]$Detalhe)
    $resultados.Add([pscustomobject]@{ Nome = $Nome; Ok = $Ok; Detalhe = $Detalhe })
    $marca = if ($Ok) { '[ OK  ]' } else { '[FALHA]' }
    Write-Host ($marca + ' ' + $Nome)
    if (-not $Ok) { Write-Host ('         ' + $Detalhe) }
}

Write-Host ''
Write-Host ('Testando ' + $uri.Scheme + '://' + $uri.Authority + '/webhooks/whatsapp/<id> (nenhuma chamada a Meta)')
Write-Host ''

# 1 e 2: o desafio de verificacao.
$desafio = 'desafio-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$r1 = Invoke-Http -Metodo Get -Url ($base + '?hub.mode=subscribe&hub.verify_token=' + [System.Uri]::EscapeDataString($verifyTexto) + '&hub.challenge=' + $desafio)
if ($r1.Status -eq 0) { Add-Resultado 'Alcanca o webhook por HTTPS' $false ('sem resposta: ' + $r1.Falha + ' (tunel fora do ar? certificado invalido?)') }
else { Add-Resultado 'Alcanca o webhook por HTTPS' $true '' }
Add-Resultado 'Verificacao: verify token certo devolve o desafio (200)' (($r1.Status -eq 200) -and ($r1.Corpo.Trim() -eq $desafio)) ('HTTP ' + $r1.Status + ' (esperado 200 com o desafio). Verify token errado? A conta/webhook existe?')

$r2 = Invoke-Http -Metodo Get -Url ($base + '?hub.mode=subscribe&hub.verify_token=token-errado-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '&hub.challenge=' + $desafio)
Add-Resultado 'Verificacao: verify token errado e recusado (403)' ($r2.Status -eq 403) ('HTTP ' + $r2.Status + ' (esperado 403)')

# 3: POST assinado de um numero desconhecido (fake): so prova a assinatura, nada e gravado.
$agora = [DateTimeOffset]::UtcNow
$payload = @{
    object = 'whatsapp_business_account'
    entry = @(@{
        id = '000000000000000'
        changes = @(@{
            field = 'messages'
            value = @{
                messaging_product = 'whatsapp'
                metadata = @{ display_phone_number = '550000000000'; phone_number_id = '000000000000000' }
                contacts = @(@{ profile = @{ name = 'Teste de homologacao' }; wa_id = '550000000000' })
                messages = @(@{ from = '550000000000'; id = ('wamid.TESTE' + $agora.ToUnixTimeMilliseconds()); timestamp = [string]$agora.ToUnixTimeSeconds(); type = 'text'; text = @{ body = 'teste-de-webhook' } })
            }
        })
    })
}
$corpo = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $payload -Depth 10 -Compress))
$hmac = New-Object System.Security.Cryptography.HMACSHA256 (, [System.Text.Encoding]::UTF8.GetBytes($segredoTexto))
$assinatura = 'sha256=' + ([BitConverter]::ToString($hmac.ComputeHash($corpo)).Replace('-', '').ToLowerInvariant())

$r3 = Invoke-Http -Metodo Post -Url $base -Corpo $corpo -Cabecalhos @{ 'X-Hub-Signature-256' = $assinatura }
Add-Resultado 'Assinatura: corpo assinado com o App Secret e aceito (200)' ($r3.Status -eq 200) ('HTTP ' + $r3.Status + ' (esperado 200). 401 = o App Secret digitado aqui difere do cadastrado no ia.bimeta.')

# 4: assinatura errada.
$r4 = Invoke-Http -Metodo Post -Url $base -Corpo $corpo -Cabecalhos @{ 'X-Hub-Signature-256' = ('sha256=' + ('0' * 64)) }
Add-Resultado 'Assinatura: corpo com assinatura errada e recusado (401)' ($r4.Status -eq 401) ('HTTP ' + $r4.Status + ' (esperado 401)')

# 5: identificador desconhecido.
$desconhecido = $uri.GetLeftPart([System.UriPartial]::Authority) + '/webhooks/whatsapp/' + [guid]::NewGuid()
$r5 = Invoke-Http -Metodo Post -Url $desconhecido -Corpo $corpo -Cabecalhos @{ 'X-Hub-Signature-256' = $assinatura }
Add-Resultado 'Identificador de webhook desconhecido responde 404' ($r5.Status -eq 404) ('HTTP ' + $r5.Status + ' (esperado 404)')

$verifyTexto = $null
$segredoTexto = $null
$falhas = @($resultados | Where-Object { -not $_.Ok }).Count
Write-Host ''
if ($falhas -eq 0) { Write-Host 'WEBHOOK PRONTO: HTTPS, verify token e assinatura estao certos. Pode cadastrar o callback na Meta.'; exit 0 }
Write-Host ('WEBHOOK COM PROBLEMAS: ' + $falhas + ' verificacao(oes) falharam. Corrija antes de testar com a Meta.')
exit 1
