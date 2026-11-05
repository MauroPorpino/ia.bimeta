<#
.SYNOPSIS
    Confirma que um identificador de modelo EXISTE na sua conta da OpenAI (GET /v1/models/{id}), antes da primeira
    chamada de geracao. Nao presuma identificador: modelo inexistente falha o teste.

.DESCRIPTION
    Faz UMA consulta de metadados a api.openai.com com a SUA chave. A consulta nao gera texto e nao consome
    tokens, mas e uma chamada real: rode so depois de autorizado. A chave e pedida sem eco, vai so no cabecalho
    Authorization e nunca e impressa. O corpo de erro do provedor nao e exibido (so o codigo HTTP).
    Se o modelo nao existir, lista os identificadores da conta que comecam com o prefixo -Prefixo (so nomes).

.EXAMPLE
    .\tools\dev\Test-ModeloDaOpenAI.ps1 -ModeloId o-id-que-voce-quer-usar
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModeloId,
    [string]$Prefixo = 'gpt',
    [System.Security.SecureString]$Chave,
    [string]$BaseDaApi = 'https://api.openai.com/v1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

if ($ModeloId -notmatch '^[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}$') {
    throw 'O identificador do modelo tem formato invalido.'
}
if ($BaseDaApi -notmatch '^https://') {
    throw 'A chave so vai por HTTPS.'
}

if ($null -eq $Chave) {
    $Chave = Read-Host -Prompt 'Cole a chave da OpenAI (nao aparece na tela)' -AsSecureString
}

function Get-CodigoHttp {
    param($Erro)
    if ($null -ne $Erro.Exception.Response) { return [int]$Erro.Exception.Response.StatusCode }
    return 0
}

$ponteiro = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Chave)
try {
    $cabecalhos = @{ 'Authorization' = ('Bearer ' + [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ponteiro)) }
    $codificado = [System.Uri]::EscapeDataString($ModeloId)

    try {
        $modelo = Invoke-RestMethod -Method Get -Uri ($BaseDaApi + '/models/' + $codificado) -Headers $cabecalhos
        Write-Host ('DISPONIVEL: o modelo ' + $modelo.id + ' existe nesta conta.')
        exit 0
    }
    catch {
        $codigo = Get-CodigoHttp $_
        if ($codigo -eq 401 -or $codigo -eq 403) { Write-Host ('CHAVE RECUSADA (HTTP ' + $codigo + '): confira a chave e as permissoes do projeto.'); exit 1 }
        if ($codigo -ne 404) { Write-Host ('FALHA na consulta (HTTP ' + $codigo + '). Tente de novo mais tarde.'); exit 1 }
    }

    Write-Host ('NAO ENCONTRADO: o modelo ' + $ModeloId + ' nao existe nesta conta. Identificadores que comecam com "' + $Prefixo + '":')
    $lista = Invoke-RestMethod -Method Get -Uri ($BaseDaApi + '/models') -Headers $cabecalhos
    $ids = @($lista.data | ForEach-Object { $_.id } | Where-Object { $_ -like ($Prefixo + '*') } | Sort-Object)
    foreach ($id in $ids) { Write-Host ('  ' + $id) }
    if ($ids.Count -eq 0) { Write-Host '  (nenhum: use -Prefixo para outro filtro)' }
    exit 1
}
finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ponteiro)
    $cabecalhos = $null
}
