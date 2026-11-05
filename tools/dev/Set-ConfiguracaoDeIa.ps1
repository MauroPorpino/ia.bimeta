<#
.SYNOPSIS
    Prepara, nos user-secrets do Api e do Worker (os dois usam o mesmo UserSecretsId), a CHAVE MESTRA que
    cifra as credenciais BYOK e o MODELO de IA do tenant de demonstracao. Roda uma vez, no SEU terminal.

.DESCRIPTION
    Grava somente em "dotnet user-secrets" (fora do repositorio). Nunca imprime segredo.
    - Chave mestra: 32 bytes aleatorios em Base64 (AiGateway:Secrets). Criada so se nao existir; com
      -NovaChaveMestra cria outra e a passa a ser a ativa (a antiga continua conhecida, para decifrar o que ela cifrou).
    - Modelo: entrada no catalogo (AiGateway:Catalog:Models:50) com os precos que VOCE informa (o custo no ledger e
      ESTIMADO por esta tabela; nao e fatura) e o modelo do tenant (Tenancy:Development:Ai:ModelId).
    - O identificador do modelo precisa ser um que a SUA conta da OpenAI realmente tem. Confirme antes com
      Test-ModeloDaOpenAI.ps1 (nao presuma nenhum identificador).

.PARAMETER ModeloId
    O identificador do modelo na OpenAI (o campo "id" de GET /v1/models).

.PARAMETER PrecoEntradaPorMilhao
    Preco por milhao de tokens de entrada, na moeda informada (veja a tabela de precos do modelo).

.PARAMETER PrecoSaidaPorMilhao
    Preco por milhao de tokens de saida (inclui o raciocinio).

.PARAMETER PrecoEntradaEmCachePorMilhao
    Preco por milhao de tokens de entrada lidos do cache. Padrao: o preco da entrada.

.PARAMETER JanelaDeContexto
    Janela de contexto do modelo, em tokens (so valida limites; ajuste se quiser).

.PARAMETER TetoDeSaidaDoModelo
    Maximo de tokens de saida do modelo (o limite real do tenant e menor: 1000 por padrao).

.PARAMETER EsforcoDeRaciocinio
    padrao = nao envia o campo reasoning (o mais seguro para um primeiro teste: serve a qualquer modelo).
    none, minimal, low, medium, high = envia reasoning.effort com esse valor; so use um valor que o modelo aceite.

.EXAMPLE
    .\tools\dev\Set-ConfiguracaoDeIa.ps1 -ModeloId o-id-da-sua-conta -PrecoEntradaPorMilhao 0.40 -PrecoSaidaPorMilhao 1.60
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModeloId,
    [Parameter(Mandatory = $true)][decimal]$PrecoEntradaPorMilhao,
    [Parameter(Mandatory = $true)][decimal]$PrecoSaidaPorMilhao,
    [decimal]$PrecoEntradaEmCachePorMilhao = -1,
    [int]$JanelaDeContexto = 128000,
    [int]$TetoDeSaidaDoModelo = 16000,
    [string]$Moeda = 'USD',
    [ValidateSet('padrao', 'none', 'minimal', 'low', 'medium', 'high')][string]$EsforcoDeRaciocinio = 'padrao',
    [switch]$NovaChaveMestra
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projetoDoApi = Join-Path $raiz 'backend\host\IaBimeta.Api'
$perfil = 'dev-ia'
$indiceNoCatalogo = 50
$invariante = [System.Globalization.CultureInfo]::InvariantCulture

if ($ModeloId -notmatch '^[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}$') {
    throw 'O identificador do modelo tem formato invalido (letras, numeros e ._:/-, ate 100 caracteres).'
}
if ($Moeda -notmatch '^[A-Z]{3}$') {
    throw 'A moeda deve ter tres letras maiusculas, por exemplo USD.'
}
if ($PrecoEntradaPorMilhao -lt 0 -or $PrecoSaidaPorMilhao -lt 0) {
    throw 'Os precos nao podem ser negativos.'
}
if ($TetoDeSaidaDoModelo -gt $JanelaDeContexto -or $TetoDeSaidaDoModelo -lt 1) {
    throw 'O teto de saida do modelo precisa ser positivo e caber na janela de contexto.'
}
if ($PrecoEntradaEmCachePorMilhao -lt 0) {
    $PrecoEntradaEmCachePorMilhao = $PrecoEntradaPorMilhao
}

function Set-Segredo {
    param([string]$Nome, [string]$Valor)
    & dotnet user-secrets set $Nome $Valor --project $projetoDoApi | Out-Null
    if ($LASTEXITCODE -ne 0) { throw ('Falha ao gravar o segredo ' + $Nome + '.') }
}

function Remove-Segredo {
    param([string]$Nome)
    & dotnet user-secrets remove $Nome --project $projetoDoApi | Out-Null
}

$existentes = @(& dotnet user-secrets list --project $projetoDoApi)
if ($LASTEXITCODE -ne 0) { throw 'Nao foi possivel ler os user-secrets (o SDK do .NET esta instalado?).' }
$temChaveAtiva = @($existentes | Where-Object { $_ -like 'AiGateway:Secrets:ActiveKeyId*' }).Count -gt 0

# 1. A chave mestra (nunca impressa).
if ($temChaveAtiva -and -not $NovaChaveMestra) {
    Write-Host 'Chave mestra: ja existe (mantida). Use -NovaChaveMestra para criar outra.'
}
else {
    $idDaChave = 'dev-1'
    if ($temChaveAtiva) { $idDaChave = 'dev-' + (Get-Date).ToString('yyyyMMddHHmmss') }
    $bytes = New-Object byte[] 32
    $gerador = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $gerador.GetBytes($bytes) } finally { $gerador.Dispose() }
    $base64 = [Convert]::ToBase64String($bytes)
    Set-Segredo ('AiGateway:Secrets:Keys:' + $idDaChave) $base64
    Set-Segredo 'AiGateway:Secrets:ActiveKeyId' $idDaChave
    $base64 = $null
    $bytes = $null
    Write-Host ('Chave mestra: criada (id ' + $idDaChave + ') e ativa. O valor nao e exibido.')
}

# 2. O modelo no catalogo (precos pontuados com ponto, sem depender da cultura da maquina).
$prefixo = 'AiGateway:Catalog:Models:' + $indiceNoCatalogo + ':'
Set-Segredo ($prefixo + 'ProviderId') 'openai'
Set-Segredo ($prefixo + 'ModelId') $ModeloId
Set-Segredo ($prefixo + 'Profile') $perfil
Set-Segredo ($prefixo + 'ContextWindowTokens') $JanelaDeContexto.ToString($invariante)
Set-Segredo ($prefixo + 'MaxOutputTokens') $TetoDeSaidaDoModelo.ToString($invariante)
Set-Segredo ($prefixo + 'Currency') $Moeda
Set-Segredo ($prefixo + 'InputPerMillion') $PrecoEntradaPorMilhao.ToString($invariante)
Set-Segredo ($prefixo + 'CachedInputPerMillion') $PrecoEntradaEmCachePorMilhao.ToString($invariante)
Set-Segredo ($prefixo + 'CacheWritePerMillion') $PrecoEntradaPorMilhao.ToString($invariante)
Set-Segredo ($prefixo + 'OutputPerMillion') $PrecoSaidaPorMilhao.ToString($invariante)
Set-Segredo ($prefixo + 'Status') 'Active'

# 3. O esforco de raciocinio do perfil (sem o campo, vale o padrao do modelo).
if ($EsforcoDeRaciocinio -eq 'padrao') {
    Remove-Segredo ('AiGateway:Providers:OpenAi:Profiles:' + $perfil + ':ReasoningEffort')
}
else {
    Set-Segredo ('AiGateway:Providers:OpenAi:Profiles:' + $perfil + ':ReasoningEffort') $EsforcoDeRaciocinio
}

# 4. O tenant de demonstracao usa este modelo.
Set-Segredo 'Tenancy:Development:Ai:ProviderId' 'openai'
Set-Segredo 'Tenancy:Development:Ai:ModelId' $ModeloId

Write-Host ''
Write-Host ('Modelo configurado: openai / ' + $ModeloId + ' (precos por milhao em ' + $Moeda + ': entrada ' + $PrecoEntradaPorMilhao.ToString($invariante) + ', cache ' + $PrecoEntradaEmCachePorMilhao.ToString($invariante) + ', saida ' + $PrecoSaidaPorMilhao.ToString($invariante) + ').')
Write-Host ('Esforco de raciocinio: ' + $EsforcoDeRaciocinio)
Write-Host 'Proximo passo: tools\dev\Start-SimuladorDeIa.ps1 e depois tools\dev\Set-ChaveDaOpenAI.ps1 (veja tools\dev\README.md).'
