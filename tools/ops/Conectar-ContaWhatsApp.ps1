<#
.SYNOPSIS
    Cadastra (ou completa) uma conta WhatsApp REAL de um tenant pela API do ia.bimeta, sem enviar nenhuma mensagem:
    conta (WABA ID), numero (Phone Number ID), unidade autorizada, as tres credenciais CIFRADAS e o endereco do
    webhook para colar na Meta. O numero fica INATIVO: so o ative depois do teste de prontidao (veja abaixo).

.DESCRIPTION
    As credenciais sao pedidas sem eco (Read-Host -AsSecureString) e vao so no corpo de um PUT para o Api: nunca
    ficam em arquivo, variavel de ambiente persistente, log ou commit. O token de verificacao do webhook e GERADO
    aqui (aleatorio, 32 caracteres) e mostrado UMA vez, para voce colar no campo "Verify token" do painel da Meta.
    O script pode ser repetido: reaproveita a conta e o numero que ja existem e MANTEM as credenciais ja cadastradas
    (por isso repetir com -Ativar nao troca o token de verificacao que a Meta ja conhece). Para TROCAR credenciais,
    use -RegravarCredenciais.

    Ordem recomendada (docs\operations\WHATSAPP-CONEXAO-REAL.md):
      1. Este script (cadastro + credenciais + endereco do webhook).
      2. Cadastrar o webhook no painel da Meta (URL e Verify token impressos aqui) e assinar o campo "messages".
      3. tools\ops\Test-ProntidaoWhatsApp.ps1 -Live  (so leitura no provedor; nenhuma mensagem).
      4. Ativar o numero (este script com -Ativar, ou POST /channels/instances/{id}/activate).
      5. Mandar UMA mensagem de um celular para o numero e acompanhar na interface do atendente.

.PARAMETER Url
    O Api (padrao http://localhost:5155). Em producao, o endereco interno ou o publico do Api.

.PARAMETER UrlPublica
    O endereco PUBLICO HTTPS do Api, o prefixo da URL do webhook que a Meta vai chamar (por exemplo
    https://atendimento.exemplo.com.br). So usado para imprimir a URL completa.

.EXAMPLE
    .\tools\ops\Conectar-ContaWhatsApp.ps1 -Tenant meu-tenant -Email admin@meu.com `
        -WabaId 123456789012345 -PhoneNumberId 109876543210987 -NomeDoNumero 'Atendimento Matriz' `
        -UrlPublica https://atendimento.exemplo.com.br
    (sem -OrganizacaoId/-UnidadeId: usa a unica organizacao do tenant e a matriz dela, criando a matriz se faltar)
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://localhost:5155',
    [Parameter(Mandatory = $true)][string]$Tenant,
    [Parameter(Mandatory = $true)][string]$Email,
    # Opcionais: se omitidos, usa a unica organizacao do tenant e a matriz dela (e cria a matriz "Matriz" se o tenant ainda nao tem unidade).
    [string]$OrganizacaoId = '',
    [string]$UnidadeId = '',
    [Parameter(Mandatory = $true)][string]$WabaId,
    [Parameter(Mandatory = $true)][string]$PhoneNumberId,
    [Parameter(Mandatory = $true)][string]$NomeDoNumero,
    [string]$NomeDaConta = '',
    [string]$NumeroDeExibicao = '',
    [string]$UrlPublica = '',
    [switch]$Ativar,

    # Por padrao, credencial que JA esta cadastrada e mantida (repetir o script para ativar NAO troca o token de verificacao
    # que a Meta ja conhece). Use esta chave so para TROCAR as credenciais (rotacao): o token de verificacao novo
    # precisara ser cadastrado de novo no painel da Meta.
    [switch]$RegravarCredenciais,

    # So o TOKEN DE ACESSO: troca o token (por exemplo, o temporario da Meta que expirou) e MANTEM o App Secret e o token de verificacao (nada muda no painel da Meta).
    [switch]$RegravarTokenDeAcesso,

    # Opcionais: se omitidos, sao pedidos sem eco. So use em automacao/ensaio, nunca digitando o valor na linha de comando.
    [System.Security.SecureString]$Senha,
    [System.Security.SecureString]$TokenDeAcesso,
    [System.Security.SecureString]$AppSecret
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($WabaId -notmatch '^[0-9]{1,30}$') { throw 'WabaId deve ter so digitos (e o identificador da conta WhatsApp Business na Meta).' }
if ($PhoneNumberId -notmatch '^[0-9]{1,30}$') { throw 'PhoneNumberId deve ter so digitos (e o ID do numero na Meta, NAO o telefone).' }
if ([string]::IsNullOrWhiteSpace($NomeDaConta)) { $NomeDaConta = 'WhatsApp ' + $NomeDoNumero }

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
        $detalhe = ''
        if ($null -ne $_.ErrorDetails -and -not [string]::IsNullOrEmpty($_.ErrorDetails.Message)) { $detalhe = $_.ErrorDetails.Message }
        throw ('Falha em ' + $Metodo + ' ' + $Caminho + ': HTTP ' + $codigo + ' ' + $detalhe)
    }
}

# 1. Login (a senha e pedida sem eco e descartada em seguida).
$senhaSegura = $Senha
if ($null -eq $senhaSegura) { $senhaSegura = Read-Host -Prompt ('Senha de ' + $Email) -AsSecureString }
$login = Invoke-Api -Metodo Post -Caminho '/auth/login' -Corpo @{ tenant = $Tenant; email = $Email; password = (ConvertTo-TextoPlano $senhaSegura) }
$senhaSegura = $null
$token = [string]$login.token

# 1b. Organizacao e unidade: as informadas, ou a unica organizacao do tenant e a matriz dela (criada se nao existir).
if ($OrganizacaoId -eq '') {
    $organizacoes = @(Invoke-Api -Metodo Get -Caminho '/organizations' -Token $token | ForEach-Object { $_ })
    if ($organizacoes.Count -ne 1) { throw ('O tenant tem ' + $organizacoes.Count + ' organizacoes: informe -OrganizacaoId.') }
    $OrganizacaoId = [string]$organizacoes[0].id
}
if ($UnidadeId -eq '') {
    $unidades = @(Invoke-Api -Metodo Get -Caminho ('/organizations/' + $OrganizacaoId + '/business-units') -Token $token | ForEach-Object { $_ })
    $matriz = $unidades | Where-Object { [string]$_.kind -eq 'Headquarters' } | Select-Object -First 1
    if ($null -eq $matriz) { $matriz = $unidades | Select-Object -First 1 }
    if ($null -eq $matriz) {
        $matriz = Invoke-Api -Metodo Post -Caminho ('/organizations/' + $OrganizacaoId + '/business-units') -Token $token -Corpo @{ name = 'Matriz'; kind = 'Headquarters' }
        Write-Host ('Unidade "Matriz" criada: ' + $matriz.id)
    }
    $UnidadeId = [string]$matriz.id
}

# 2. Conta e numero: reaproveita o que ja existe (o script pode ser repetido).
# No PowerShell 5.1 a lista JSON chega como UM objeto (um array dentro do pipeline): o ForEach-Object a desagrega, para o filtro olhar cada conta.
$contas = @(Invoke-Api -Metodo Get -Caminho '/channels/accounts' -Token $token | ForEach-Object { $_ })
$conta = $contas | Where-Object { $_.providerAccountId -eq $WabaId } | Select-Object -First 1
if ($null -eq $conta) {
    $conta = Invoke-Api -Metodo Post -Caminho '/channels/accounts' -Token $token -Corpo @{ organizationId = $OrganizacaoId; provider = 'WhatsAppCloud'; providerAccountId = $WabaId; name = $NomeDaConta }
    Write-Host ('Conta cadastrada: ' + $conta.id)
}
else { Write-Host ('Conta ja existia: ' + $conta.id) }

$instancias = @(Invoke-Api -Metodo Get -Caminho '/channels/instances' -Token $token | ForEach-Object { $_ })
$instancia = $instancias | Where-Object { $_.phoneNumberId -eq $PhoneNumberId } | Select-Object -First 1
if ($null -eq $instancia) {
    $corpoDoNumero = @{ accountId = $conta.id; name = $NomeDoNumero; phoneNumberId = $PhoneNumberId; displayPhoneNumber = $null; defaultLocale = $null }
    if ($NumeroDeExibicao -ne '') { $corpoDoNumero['displayPhoneNumber'] = $NumeroDeExibicao }
    $instancia = Invoke-Api -Metodo Post -Caminho '/channels/instances' -Token $token -Corpo $corpoDoNumero
    Write-Host ('Numero cadastrado (INATIVO): ' + $instancia.id)
}
else { Write-Host ('Numero ja existia: ' + $instancia.id + ' (situacao ' + $instancia.status + ')') }

# 3. Unidade autorizada e unidade padrao (idempotentes).
$instancia = Invoke-Api -Metodo Put -Caminho ('/channels/instances/' + $instancia.id + '/authorized-units/' + $UnidadeId) -Token $token
$instancia = Invoke-Api -Metodo Put -Caminho ('/channels/instances/' + $instancia.id + '/defaults') -Token $token -Corpo @{ defaultBusinessUnitId = $UnidadeId; defaultDepartmentId = $null }
Write-Host 'Unidade autorizada e definida como padrao do numero.'

# 4. As credenciais: token de acesso e App Secret (digitados, sem eco) e o token de verificacao (gerado aqui).
function Set-Credencial {
    param([string]$Papel, [string]$Valor)
    $resposta = Invoke-Api -Metodo Put -Caminho ('/channels/accounts/' + $conta.id + '/secrets/' + $Papel) -Token $token -Corpo @{ value = $Valor }
    Write-Host ('Credencial ' + $Papel + ' gravada, CIFRADA (versao ' + $resposta.version + ').')
}

$jaCadastradas = @($conta.secrets | ForEach-Object { [string]$_.kind })
function Test-Mantida {
    param([string]$Papel)
    if ($Papel -eq 'AccessToken' -and $RegravarTokenDeAcesso) { return $false }
    return (($jaCadastradas -contains $Papel) -and -not $RegravarCredenciais)
}

if (Test-Mantida 'AccessToken') { Write-Host 'Credencial AccessToken: ja cadastrada, MANTIDA (use -RegravarCredenciais para trocar).' }
else {
    $tokenDeAcessoSeguro = $TokenDeAcesso
    if ($null -eq $tokenDeAcessoSeguro) { $tokenDeAcessoSeguro = Read-Host -Prompt 'Token de acesso permanente do usuario do sistema da Meta (nao aparece)' -AsSecureString }
    Set-Credencial -Papel 'AccessToken' -Valor (ConvertTo-TextoPlano $tokenDeAcessoSeguro)
    $tokenDeAcessoSeguro = $null
}

if (Test-Mantida 'AppSecret') { Write-Host 'Credencial AppSecret: ja cadastrada, MANTIDA (use -RegravarCredenciais para trocar).' }
else {
    $appSecretSeguro = $AppSecret
    if ($null -eq $appSecretSeguro) { $appSecretSeguro = Read-Host -Prompt 'App Secret do aplicativo da Meta (Configuracoes > Basico; nao aparece)' -AsSecureString }
    Set-Credencial -Papel 'AppSecret' -Valor (ConvertTo-TextoPlano $appSecretSeguro)
    $appSecretSeguro = $null
}

$tokenDeVerificacao = $null
if (Test-Mantida 'WebhookVerifyToken') { Write-Host 'Credencial WebhookVerifyToken: ja cadastrada, MANTIDA (o valor nao e recuperavel; para um novo, use -RegravarCredenciais e cadastre-o de novo na Meta).' }
else {
    $bytes = New-Object byte[] 24
    $gerador = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $gerador.GetBytes($bytes) } finally { $gerador.Dispose() }
    $tokenDeVerificacao = (($bytes | ForEach-Object { $_.ToString('x2') }) -join '')
    Set-Credencial -Papel 'WebhookVerifyToken' -Valor $tokenDeVerificacao
}

# 5. O endereco do webhook (identificador opaco, criado se ainda nao existe).
$webhook = Invoke-Api -Metodo Post -Caminho ('/channels/accounts/' + $conta.id + '/webhook') -Token $token
Write-Host ''
Write-Host '=== COLE NO PAINEL DA META (Aplicativo > WhatsApp > Configuracao > Webhook) ==='
if ($UrlPublica -ne '') { Write-Host ('  URL de callback : ' + $UrlPublica.TrimEnd('/') + $webhook.path) }
else { Write-Host ('  URL de callback : <endereco publico HTTPS do Api>' + $webhook.path) }
if ($null -ne $tokenDeVerificacao) { Write-Host ('  Verify token    : ' + $tokenDeVerificacao) }
else { Write-Host '  Verify token    : (o ja cadastrado na Meta continua valendo: nada foi trocado)' }
Write-Host '  Campo a assinar : messages'
Write-Host '(o endereco do webhook e um segredo operacional: nao o publique nem o coloque em log; o Verify token aparece so quando e gerado)'
Write-Host ''
$tokenDeVerificacao = $null

# 6. Ativacao (so com -Ativar, e so depois do teste de prontidao).
if ($Ativar) {
    $instancia = Invoke-Api -Metodo Post -Caminho ('/channels/instances/' + $instancia.id + '/activate') -Token $token
    Write-Host ('Numero ATIVO (situacao ' + $instancia.status + '): ele passa a receber e enviar.')
}
elseif ([string]$instancia.status -eq 'Active') {
    Write-Host 'O numero ja esta ATIVO (nada foi alterado na situacao dele).'
}
else {
    Write-Host 'O numero continua INATIVO. Proximo passo: tools\ops\Test-ProntidaoWhatsApp.ps1 -Live, e so entao ativar (-Ativar).'
}

Write-Host ('Conta: ' + $conta.id + '  Numero: ' + $instancia.id)
