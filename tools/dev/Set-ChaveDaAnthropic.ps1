<#
.SYNOPSIS
    Cadastra (ou revoga) a chave da Anthropic do tenant de demonstracao, pelo servico da plataforma: a chave e
    CIFRADA (AES-256-GCM) no banco e nunca volta em nenhuma resposta. O Api precisa estar no ar (Start-SimuladorDeIa.ps1).

.DESCRIPTION
    A chave e pedida sem eco (Read-Host -AsSecureString), vai so no corpo de um PUT para o Api local e a variavel
    e descartada em seguida. Nao a coloque em arquivo, em variavel de ambiente persistente nem em commit.
    Se o Api responder 503, a chave mestra nao esta configurada: rode Set-ConfiguracaoDeIa.ps1.

.PARAMETER Revogar
    Em vez de cadastrar, revoga a chave ativa do tenant (apaga o texto cifrado). Use no fim do teste.

.EXAMPLE
    .\tools\dev\Set-ChaveDaAnthropic.ps1
#>
[CmdletBinding()]
param(
    [string]$Tenant = 'demo-anthropic',
    [string]$Url = 'http://localhost:5155',
    [System.Security.SecureString]$Chave,
    [switch]$Revogar,
    [switch]$SomenteValidarUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$cabecalhos = @{ 'X-Dev-Tenant' = $Tenant }

# A chave so pode ir para um destino confiavel: HTTP SOMENTE em loopback (localhost, 127.0.0.0/8, ::1); qualquer outro
# destino exige HTTPS. A URL e interpretada como URI (nunca por prefixo de texto) e validada ANTES de pedir a chave.
function Test-UrlDoApi {
    param([string]$Texto)
    $uri = $null
    if (-not [System.Uri]::TryCreate($Texto, [System.UriKind]::Absolute, [ref]$uri)) { return 'a URL e invalida' }
    if ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https') { return 'protocolo nao autorizado (use http em loopback ou https)' }
    if (-not [string]::IsNullOrEmpty($uri.UserInfo)) { return 'a URL nao pode conter usuario ou senha' }
    if ([string]::IsNullOrEmpty($uri.Host)) { return 'a URL nao tem host' }
    if ($uri.Scheme -eq 'https') { return $null }
    $hospedeiro = $uri.Host.Trim('[', ']')
    $ip = $null
    if ([System.Net.IPAddress]::TryParse($hospedeiro, [ref]$ip)) {
        if ([System.Net.IPAddress]::IsLoopback($ip)) { return $null }
    }
    elseif ($hospedeiro -ieq 'localhost') { return $null }
    return 'HTTP so e permitido em loopback (localhost, 127.0.0.1, ::1); para outro destino use HTTPS'
}

$motivoUrl = Test-UrlDoApi -Texto $Url
if ($null -ne $motivoUrl) {
    Write-Host ('RECUSADO: ' + $motivoUrl + '. Nenhuma chave foi solicitada nem enviada.')
    exit 2
}
if ($SomenteValidarUrl) { Write-Host 'URL aceita.'; exit 0 }

function Write-FalhaHttp {
    param($Erro)
    $codigo = ''
    if ($null -ne $Erro.Exception.PSObject.Properties['Response'] -and $null -ne $Erro.Exception.Response) { $codigo = [int]$Erro.Exception.Response.StatusCode }
    elseif ($Erro.Exception.Message -match 'redirect|redirecion') { $codigo = 302 }
    $detalhe = ''
    if ($null -ne $Erro.ErrorDetails -and -not [string]::IsNullOrEmpty($Erro.ErrorDetails.Message)) { $detalhe = $Erro.ErrorDetails.Message }
    Write-Host ('FALHA: o Api respondeu ' + $codigo + '. ' + $detalhe)
    if ($codigo -ge 300 -and $codigo -lt 400) { Write-Host 'Redirecionamento NAO seguido (a chave so vai para a URL informada).' }
    if ($codigo -eq 503) { Write-Host 'A chave mestra nao esta configurada: rode tools\dev\Set-ConfiguracaoDeIa.ps1 e reinicie o Api e o Worker.' }
    if ($codigo -eq 0) { Write-Host 'O Api esta no ar? Rode tools\dev\Start-SimuladorDeIa.ps1.' }
}

if ($Revogar) {
    try {
        $resposta = Invoke-RestMethod -Method Delete -Uri ($Url + '/simulator/ai/credential?providerId=anthropic') -Headers $cabecalhos -MaximumRedirection 0
        Write-Host ('Chave revogada: ' + $resposta.revoked + ' (false = nao havia chave ativa).')
        exit 0
    }
    catch {
        Write-FalhaHttp $_
        exit 1
    }
}

if ($null -eq $Chave) {
    $Chave = Read-Host -Prompt 'Cole a chave da Anthropic (nao aparece na tela)' -AsSecureString
}

$ponteiro = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Chave)
try {
    $texto = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ponteiro)
    $json = ConvertTo-Json -InputObject @{ providerId = 'anthropic'; apiKey = $texto } -Compress
    $corpo = [System.Text.Encoding]::UTF8.GetBytes($json)
    $situacao = Invoke-RestMethod -Method Put -Uri ($Url + '/simulator/ai/credential') -Headers $cabecalhos -ContentType 'application/json; charset=utf-8' -Body $corpo -MaximumRedirection 0
    Write-Host 'Chave cadastrada (cifrada). Situacao da IA do tenant:'
    Write-Host ('  provedor/modelo : ' + $situacao.providerId + ' / ' + $situacao.modelId)
    Write-Host ('  situacao        : ' + $situacao.status)
    Write-Host ('  chave presente  : ' + $situacao.credentialPresent + ' (impressao digital ' + $situacao.credentialFingerprint + ')')
    if (-not $situacao.configured) { Write-Host 'ATENCAO: o tenant ainda nao tem modelo. Rode tools\dev\Set-ConfiguracaoDeIa.ps1 e reinicie o Api.' }
}
catch {
    Write-FalhaHttp $_
    exit 1
}
finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ponteiro)
    $texto = $null
    $json = $null
    $corpo = $null
}
