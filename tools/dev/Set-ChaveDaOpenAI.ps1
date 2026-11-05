<#
.SYNOPSIS
    Cadastra (ou revoga) a chave da OpenAI do tenant de demonstracao, pelo servico da plataforma: a chave e
    CIFRADA (AES-256-GCM) no banco e nunca volta em nenhuma resposta. O Api precisa estar no ar (Start-SimuladorDeIa.ps1).

.DESCRIPTION
    A chave e pedida sem eco (Read-Host -AsSecureString), vai so no corpo de um PUT para o Api local e a variavel
    e descartada em seguida. Nao a coloque em arquivo, em variavel de ambiente persistente nem em commit.
    Se o Api responder 503, a chave mestra nao esta configurada: rode Set-ConfiguracaoDeIa.ps1.

.PARAMETER Revogar
    Em vez de cadastrar, revoga a chave ativa do tenant (apaga o texto cifrado). Use no fim do teste.

.EXAMPLE
    .\tools\dev\Set-ChaveDaOpenAI.ps1
#>
[CmdletBinding()]
param(
    [string]$Tenant = 'demo-ia',
    [string]$Url = 'http://localhost:5155',
    [System.Security.SecureString]$Chave,
    [switch]$Revogar
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$cabecalhos = @{ 'X-Dev-Tenant' = $Tenant }

function Write-FalhaHttp {
    param($Erro)
    $codigo = ''
    if ($null -ne $Erro.Exception.Response) { $codigo = [int]$Erro.Exception.Response.StatusCode }
    $detalhe = ''
    if ($null -ne $Erro.ErrorDetails -and -not [string]::IsNullOrEmpty($Erro.ErrorDetails.Message)) { $detalhe = $Erro.ErrorDetails.Message }
    Write-Host ('FALHA: o Api respondeu ' + $codigo + '. ' + $detalhe)
    if ($codigo -eq 503) { Write-Host 'A chave mestra nao esta configurada: rode tools\dev\Set-ConfiguracaoDeIa.ps1 e reinicie o Api e o Worker.' }
    if ($codigo -eq 0) { Write-Host 'O Api esta no ar? Rode tools\dev\Start-SimuladorDeIa.ps1.' }
}

if ($Revogar) {
    try {
        $resposta = Invoke-RestMethod -Method Delete -Uri ($Url + '/simulator/ai/credential?providerId=openai') -Headers $cabecalhos
        Write-Host ('Chave revogada: ' + $resposta.revoked + ' (false = nao havia chave ativa).')
        exit 0
    }
    catch {
        Write-FalhaHttp $_
        exit 1
    }
}

if ($null -eq $Chave) {
    $Chave = Read-Host -Prompt 'Cole a chave da OpenAI (nao aparece na tela)' -AsSecureString
}

$ponteiro = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Chave)
try {
    $texto = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ponteiro)
    $json = ConvertTo-Json -InputObject @{ providerId = 'openai'; apiKey = $texto } -Compress
    $corpo = [System.Text.Encoding]::UTF8.GetBytes($json)
    $situacao = Invoke-RestMethod -Method Put -Uri ($Url + '/simulator/ai/credential') -Headers $cabecalhos -ContentType 'application/json; charset=utf-8' -Body $corpo
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
