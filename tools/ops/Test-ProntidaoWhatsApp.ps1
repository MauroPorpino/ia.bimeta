<#
.SYNOPSIS
    TESTE DE PRONTIDAO de uma conta WhatsApp, antes de apontar a Meta para o webhook: confere a configuracao SEM
    enviar mensagem alguma e sai com codigo 0 (pronta) ou 1 (ha falhas). Com -Live, tambem faz UMA LEITURA do numero
    na Graph API (GET, so leitura) para provar que o token e aceito e que o Phone Number ID existe.

.DESCRIPTION
    Verifica: chave mestra das credenciais, adaptador do provedor, as tres credenciais (cadastradas, legiveis e
    DIFERENTES entre si), numeros (ativos, com unidade e rota de entrada coerente), identificador do webhook,
    endereco publico HTTPS e estado de envio (nao pausado). O relatorio nunca mostra valor de credencial.
    A senha e pedida sem eco. Exige permissao de gerir canais da organizacao da conta.

.PARAMETER ContaId
    O id da conta (a saida de Conectar-ContaWhatsApp.ps1, ou GET /channels/accounts).

.PARAMETER Live
    Faz a leitura do numero no provedor (nenhuma mensagem e enviada).

.EXAMPLE
    .\tools\ops\Test-ProntidaoWhatsApp.ps1 -Tenant meu-tenant -Email admin@meu.com -ContaId <guid> -Live
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://localhost:5155',
    [Parameter(Mandatory = $true)][string]$Tenant,
    [Parameter(Mandatory = $true)][string]$Email,
    [Parameter(Mandatory = $true)][guid]$ContaId,
    [switch]$Live,
    [switch]$MostrarUrlDoWebhook,

    # Opcional: se omitida, a senha e pedida sem eco. So use em automacao/ensaio.
    [System.Security.SecureString]$Senha
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-TextoPlano {
    param([System.Security.SecureString]$Segura)
    $ponteiro = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Segura)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ponteiro) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ponteiro) }
}

function Invoke-Api {
    param([string]$Metodo, [string]$Caminho, $Corpo = $null, [string]$Token = '')
    $cabecalhos = @{}
    if ($Token -ne '') { $cabecalhos['Authorization'] = 'Bearer ' + $Token }
    $parametros = @{ Method = $Metodo; Uri = ($Url.TrimEnd('/') + $Caminho); Headers = $cabecalhos }
    if ($null -ne $Corpo) {
        $parametros['Body'] = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Corpo -Depth 6 -Compress))
        $parametros['ContentType'] = 'application/json; charset=utf-8'
    }
    try { return Invoke-RestMethod @parametros }
    catch {
        $codigo = 0
        if ($null -ne $_.Exception.Response) { $codigo = [int]$_.Exception.Response.StatusCode }
        throw ('Falha em ' + $Metodo + ' ' + $Caminho + ': HTTP ' + $codigo)
    }
}

$senhaSegura = $Senha
if ($null -eq $senhaSegura) { $senhaSegura = Read-Host -Prompt ('Senha de ' + $Email) -AsSecureString }
$login = Invoke-Api -Metodo Post -Caminho '/auth/login' -Corpo @{ tenant = $Tenant; email = $Email; password = (ConvertTo-TextoPlano $senhaSegura) }
$senhaSegura = $null

$consulta = '?live=false'
if ($Live) { $consulta = '?live=true' }
$relatorio = Invoke-Api -Metodo Get -Caminho ('/channels/accounts/' + $ContaId + '/readiness' + $consulta) -Token ([string]$login.token)

Write-Host ''
Write-Host ('Prontidao da conta ' + $ContaId + ($(if ($relatorio.liveProbe) { '  (com leitura no provedor)' } else { '  (so configuracao local; use -Live para conferir no provedor)' })))
Write-Host ''
foreach ($verificacao in @($relatorio.checks)) {
    $marca = switch ([string]$verificacao.status) { 'Pass' { '[ OK  ]' } 'Warn' { '[AVISO]' } 'Fail' { '[FALHA]' } default { '[  -  ]' } }
    Write-Host ($marca + ' ' + $verificacao.code)
    Write-Host ('         ' + $verificacao.detail)
}
Write-Host ''

if ($MostrarUrlDoWebhook -and $null -ne $relatorio.webhookUrl) {
    Write-Host ('URL do webhook (segredo operacional): ' + $relatorio.webhookUrl)
    Write-Host ''
}

if ($relatorio.ready) {
    if ($relatorio.liveProbe) { Write-Host 'PRONTA: a configuracao esta certa e o provedor aceitou o token e o numero.' }
    else { Write-Host 'PRONTA LOCALMENTE: rode de novo com -Live antes de ativar o numero.' }
    exit 0
}

Write-Host 'NAO PRONTA: corrija as falhas acima e rode de novo.'
exit 1
