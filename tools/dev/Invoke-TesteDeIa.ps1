<#
.SYNOPSIS
    O teste de IA pelo simulador: envia uma mensagem de cliente simulado, espera o fluxo Agents -> modelo ->
    resposta (ou transferencia para o atendente humano) e mostra a resposta, o consumo (tokens), o custo ESTIMADO
    e o resultado registrados no ledger. Com -VerificarHumanoAssume, prova que a IA para de responder depois que
    o humano assume.

.DESCRIPTION
    ATENCAO: com a chave real cadastrada, cada execucao faz UMA chamada PAGA a OpenAI (pequena: o teto de saida e
    de 600 tokens). Rode so depois de autorizado. O Api e o Worker precisam estar no ar (Start-SimuladorDeIa.ps1)
    e a chave cadastrada (Set-ChaveDaOpenAI.ps1). Este script nunca le nem imprime a chave.

.PARAMETER VerificarHumanoAssume
    Depois da primeira resposta da IA, o humano assume a conversa (transicao do simulador), o cliente escreve de
    novo e o script confere que NAO houve resposta da IA nem nova chamada ao modelo. Faz so a chamada da primeira
    mensagem.

.EXAMPLE
    .\tools\dev\Invoke-TesteDeIa.ps1 -Mensagem 'Ola, tudo bem?'
#>
[CmdletBinding()]
param(
    [string]$Tenant = 'demo-ia',
    [string]$Url = 'http://localhost:5155',
    [string]$Mensagem = 'Ola! Voce pode me explicar como funciona este atendimento?',
    [string]$Contato = '',
    [int]$EsperaMaximaEmSegundos = 90,
    [switch]$VerificarHumanoAssume
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$cabecalhos = @{ 'X-Dev-Tenant' = $Tenant }

function Invoke-Simulador {
    param([string]$Metodo, [string]$Caminho, $Corpo = $null)
    $parametros = @{ Method = $Metodo; Uri = ($Url + $Caminho); Headers = $cabecalhos }
    if ($null -ne $Corpo) {
        $json = ConvertTo-Json -InputObject $Corpo -Compress -Depth 5
        $parametros['Body'] = [System.Text.Encoding]::UTF8.GetBytes($json)
        $parametros['ContentType'] = 'application/json; charset=utf-8'
    }
    return Invoke-RestMethod @parametros
}

function Send-MensagemDoCliente {
    param([string]$Texto)
    $resposta = Invoke-Simulador 'Post' '/simulator/inbound' @{ contactRef = $Contato; contactName = $Contato; organizationId = $status.organizationId; text = $Texto }
    return $resposta.sessionId
}

function Get-RespostasDaIA {
    param($Sessao)
    return @($Sessao.messages | Where-Object { $_.author -eq 'Ai' })
}

function Write-Ledger {
    $ledger = Invoke-Simulador 'Get' '/simulator/ai/requests?take=10'
    $linhas = @($ledger.items)
    Write-Host ('Ledger (ultimas ' + $linhas.Count + ' chamadas ao modelo deste tenant):')
    foreach ($linha in $linhas) {
        Write-Host ('  ' + $linha.providerId + '/' + $linha.modelId + ' | estado ' + $linha.state + ' | resultado ' + $linha.outcome + ' | certeza ' + $linha.certainty +
            ' | entrada ' + $linha.inputTokens + ' (cache ' + $linha.cachedInputTokens + ') | saida ' + $linha.outputTokens + ' | custo estimado ' + $linha.estimatedCost + ' ' + $linha.currency +
            ' | latencia ' + $linha.latencyMs + ' ms | erro ' + $linha.errorKind + ' ' + $linha.errorCode + ' | id no provedor ' + $linha.providerRequestId)
    }
    Write-Host ('  Totais confirmados: entrada ' + $ledger.totals.inputTokens + ' tokens, saida ' + $ledger.totals.outputTokens + ' tokens, custo estimado ' + $ledger.totals.estimatedCost)
    return $ledger
}

# 1. A IA do tenant esta pronta?
try {
    $status = Invoke-Simulador 'Get' '/simulator/ai/status'
}
catch {
    Write-Host 'FALHA: o Api nao respondeu em /simulator/ai/status. Ele esta no ar com o perfil simulador-ia? (tools\dev\Start-SimuladorDeIa.ps1)'
    exit 1
}

$pronto = $true
if (-not $status.ai.configured) { Write-Host 'FALTA: o tenant nao tem modelo de IA (rode Set-ConfiguracaoDeIa.ps1 e reinicie o Api).'; $pronto = $false }
if (-not $status.ai.credentialPresent) { Write-Host 'FALTA: o tenant nao tem a chave da OpenAI (rode Set-ChaveDaOpenAI.ps1).'; $pronto = $false }
if ($null -eq $status.organizationId -or $null -eq $status.queueId) { Write-Host 'FALTA: organizacao ou fila de demonstracao (o Api foi iniciado com o perfil simulador-ia?).'; $pronto = $false }
if ($null -eq $status.agent) { Write-Host 'FALTA: definicao de agente publicada.'; $pronto = $false }
if (-not $pronto) { exit 1 }
Write-Host ('IA do tenant ' + $Tenant + ': ' + $status.ai.providerId + ' / ' + $status.ai.modelId + ' (' + $status.ai.status + '), chave presente (impressao ' + $status.ai.credentialFingerprint + '), agente v' + $status.agent.publishedVersion + '.')

# 2. A mensagem do cliente simulado.
if ([string]::IsNullOrEmpty($Contato)) { $Contato = 'dev-' + (Get-Date).ToString('yyyyMMddHHmmss') }
Write-Host ''
Write-Host ('Cliente (' + $Contato + '): ' + $Mensagem)
$sessaoId = Send-MensagemDoCliente $Mensagem

# 3. Espera a resposta da IA ou a transferencia para o humano.
$limite = (Get-Date).AddSeconds($EsperaMaximaEmSegundos)
$resultado = 'TEMPO ESGOTADO'
$sessao = $null
while ((Get-Date) -lt $limite) {
    Start-Sleep -Seconds 1
    $sessao = Invoke-Simulador 'Get' ('/simulator/sessions/' + $sessaoId)
    $respostas = @(Get-RespostasDaIA $sessao)
    if ($sessao.session.handlingMode -eq 'HumanAssisted') { $resultado = 'TRANSFERIDO PARA O ATENDENTE HUMANO'; break }
    if ($respostas.Count -ge 1) { $resultado = 'RESPONDIDO PELA IA'; break }
}

Write-Host ''
Write-Host ('Resultado: ' + $resultado + ' (modo da conversa: ' + $sessao.session.handlingMode + ')')
foreach ($m in @($sessao.messages)) {
    Write-Host ('  [' + $m.messageSeq + '] ' + $m.author + ' (' + $m.direction + '): ' + $m.text)
}
Write-Host ''
$ledger = Write-Ledger
Write-Host ''
Write-Host 'Turnos do agente:'
$turnos = Invoke-Simulador 'Get' '/simulator/agents/turns?take=5'
foreach ($turno in @($turnos.items)) {
    Write-Host ('  ' + $turno.id + ' | ' + $turno.state + ' | tentativas ' + $turno.attempts + ' | transferencia ' + $turno.handoffReason + ' | fim ' + $turno.endReason)
}

if ($resultado -eq 'TEMPO ESGOTADO') {
    Write-Host 'O Worker esta no ar? Sem ele nada processa (veja a janela do Worker).'
    exit 1
}

# 4. Depois que o humano assume, a IA nao pode responder.
if ($VerificarHumanoAssume) {
    if ($resultado -ne 'RESPONDIDO PELA IA') {
        Write-Host 'A verificacao do humano precisa de uma conversa ainda com a IA: a primeira mensagem ja foi transferida. Rode de novo com outra mensagem.'
        exit 1
    }

    $chamadasAntes = @($ledger.items).Count
    $respostasAntes = @(Get-RespostasDaIA $sessao).Count
    Invoke-Simulador 'Post' '/simulator/handling/transition' @{ sessionId = $sessaoId; to = 'HumanAssisted'; responderRef = $null; expectedStateVersion = $sessao.session.stateVersion; transitionId = [guid]::NewGuid().ToString(); reason = 'teste-humano-assume' } | Out-Null
    Write-Host ''
    Write-Host 'O humano assumiu a conversa. O cliente escreve de novo...'
    $null = Send-MensagemDoCliente 'Tem alguem ai?'
    Start-Sleep -Seconds 8

    $depois = Invoke-Simulador 'Get' ('/simulator/sessions/' + $sessaoId)
    $ledgerDepois = Invoke-Simulador 'Get' '/simulator/ai/requests?take=10'
    $ok = (@(Get-RespostasDaIA $depois).Count -eq $respostasAntes) -and (@($ledgerDepois.items).Count -eq $chamadasAntes)
    if ($ok) { Write-Host 'APROVADO: depois que o humano assumiu, a IA nao respondeu e nenhuma chamada nova foi feita ao modelo.' }
    else { Write-Host 'REPROVADO: houve resposta da IA ou nova chamada ao modelo depois que o humano assumiu.'; exit 1 }
}
