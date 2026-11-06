<#
.SYNOPSIS
    DIAGNOSTICO operacional do envio de mensagens (somente leitura): contas pausadas, mensagens paradas, falhas
    definitivas, envios de resultado incerto e mensagens de ENTRADA mortas, so dos numeros que voce gere.

.DESCRIPTION
    Chama GET /channels/diagnostics/outbound e imprime um resumo. O relatorio nunca traz texto de mensagem nem
    credencial. Codigo de saida: 0 = nada a apontar; 1 = so falhas definitivas ou envios de resultado incerto (alguem
    precisa ver, mas o sistema nao esta doente); 2 = problema ESTRUTURAL (conta pausada, mensagem parada ou sem
    desfecho informado, entrada morta). Exige permissao de gerir canais. A senha e pedida sem eco.

    Como ler os problemas:
      stale-before-sender  o Conversations liberou a mensagem e ela NAO chegou ao Sender (evento pendente ou morto no relay).
      stale-at-sender      chegou ao Sender e nao teve desfecho.
      stuck-dispatching    o despacho esta parado (chamada em curso ou espera longa).
      unnotified           desfecho gravado, ainda nao informado ao Conversations.
      send-unknown         a Meta PODE ter recebido: confira no aparelho do cliente; NUNCA reenvie as cegas.
      failed               falha definitiva (veja o codigo: account-paused, window-closed, relay-dead, expired...).

.PARAMETER ReenfileirarEntradaMorta
    O id de um evento de ENTRADA morto (da lista "entradas mortas") a reenfileirar: volta a Pendente e o relay tenta
    de novo (a deduplicacao por identificador do provedor impede mensagem duplicada). Auditado.

.EXAMPLE
    .\tools\ops\Get-DiagnosticoDeEnvio.ps1 -Tenant meu-tenant -Email admin@meu.com
    .\tools\ops\Get-DiagnosticoDeEnvio.ps1 -Tenant meu-tenant -Email admin@meu.com -Horas 72 -ParadaHaMinutos 10
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://localhost:5155',
    [Parameter(Mandatory = $true)][string]$Tenant,
    [Parameter(Mandatory = $true)][string]$Email,
    [int]$Horas = 24,
    [int]$ParadaHaMinutos = 5,
    [int]$Maximo = 50,
    [guid]$ReenfileirarEntradaMorta,
    [switch]$Json,

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
$token = [string]$login.token

if ($PSBoundParameters.ContainsKey('ReenfileirarEntradaMorta')) {
    Invoke-Api -Metodo Post -Caminho ('/channels/diagnostics/dead-events/' + $ReenfileirarEntradaMorta + '/requeue') -Token $token | Out-Null
    Write-Host ('Evento de entrada ' + $ReenfileirarEntradaMorta + ' reenfileirado (volta a Pendente; o relay tenta de novo).')
}

$consulta = '?hours=' + $Horas + '&staleMinutes=' + $ParadaHaMinutos + '&take=' + $Maximo
$relatorio = Invoke-Api -Metodo Get -Caminho ('/channels/diagnostics/outbound' + $consulta) -Token $token

if ($Json) {
    ConvertTo-Json -InputObject $relatorio -Depth 8
}
else {
    $c = $relatorio.counts
    Write-Host ''
    Write-Host ('Diagnostico do envio em ' + $relatorio.generatedAt + '  (janela de ' + $relatorio.windowHours + ' h; parada = sem desfecho ha mais de ' + $relatorio.staleAfterSeconds + ' s)')
    Write-Host ('Despachos na janela: enviados ' + $c.sent + ' | falhas ' + $c.failed + ' | incertos ' + $c.sendUnknown + ' | em andamento ' + $c.dispatching + ' | aguardando nova tentativa ' + $c.retry)
    Write-Host ''

    $contas = @($relatorio.accounts)
    Write-Host ('Contas pausadas ou esperando a Meta: ' + $contas.Count)
    foreach ($conta in $contas) {
        $estado = 'esperando a Meta ate ' + $conta.cooldownUntil
        if ($conta.paused) { $estado = 'PAUSADA (' + $conta.pauseReason + ') desde ' + $conta.pausedAt }
        Write-Host ('  - ' + $conta.name + ' [' + $conta.accountId + ']: ' + $estado + '; vazao ' + $conta.throughputPerSecond + '/s')
    }
    Write-Host ''

    $mensagens = @($relatorio.messages)
    Write-Host ('Mensagens que pedem atencao: ' + $mensagens.Count)
    foreach ($m in $mensagens) {
        $linha = '  - [' + $m.problem + '] mensagem ' + $m.messageId + ' desde ' + $m.since
        if ($null -ne $m.senderState) { $linha += ' | sender: ' + $m.senderState }
        if ($null -ne $m.errorCode) { $linha += ' | codigo: ' + $m.errorCode }
        if ($null -ne $m.sessionId) { $linha += ' | sessao ' + $m.sessionId }
        Write-Host $linha
    }
    Write-Host ''

    $mortos = @($relatorio.deadInboundEvents)
    Write-Host ('Entradas mortas (mensagem do cliente que nao chegou ao Conversations): ' + $mortos.Count)
    foreach ($d in $mortos) {
        Write-Host ('  - evento ' + $d.eventId + ' (' + $d.eventType + ') de ' + $d.occurredAt + ' | tentativas ' + $d.attempts + ' | motivo ' + $d.deadReason)
    }
    if ($mortos.Count -gt 0) { Write-Host '    Para reenfileirar: -ReenfileirarEntradaMorta <id do evento>' }
    Write-Host ''
}

$incertasOuFalhas = @($relatorio.messages | Where-Object { $_.problem -in @('failed', 'send-unknown') }).Count
if (-not $relatorio.healthy) {
    Write-Host 'PROBLEMA ESTRUTURAL: conta pausada, mensagem parada ou sem desfecho informado, ou entrada morta (veja acima).'
    exit 2
}
if ($incertasOuFalhas -gt 0 -or $relatorio.counts.failed -gt 0 -or $relatorio.counts.sendUnknown -gt 0) {
    Write-Host 'ATENCAO: ha falhas definitivas ou envios de resultado incerto na janela (o sistema esta saudavel).'
    exit 1
}
Write-Host 'OK: nada a apontar.'
exit 0
