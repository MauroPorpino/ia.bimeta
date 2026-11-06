<#
.SYNOPSIS
    Registra (ou remove) as tarefas "ia.bimeta-backup" e "ia.bimeta-monitor" no Agendador de Tarefas do Windows. SEM -Registrar ou -Remover, so MOSTRA o que
    seria feito e nao altera nada na maquina.

.DESCRIPTION
    ia.bimeta-backup   uma vez por dia, na hora configurada: Backup-Agendado.ps1 -ArquivoDeConfiguracao <json>.
    ia.bimeta-monitor  a cada N minutos (padrao 15): Status-Ambiente.ps1 -AlertarSeCritico (alerta em arquivo e, se configurado, no webhook).
    As tarefas rodam como o USUARIO ATUAL, so com a sessao dele aberta (logon interativo, privilegio limitado): nenhuma senha e guardada pelo script nem pelo
    Agendador. "Iniciar assim que possivel se a execucao agendada for perdida" fica ligado (um computador desligado na hora roda o backup ao ligar).
    A frase de cifra e a URL do webhook NAO vao para a tarefa: ficam em variaveis de ambiente do USUARIO (o JSON so guarda o NOME delas). Defina-as antes,
    por exemplo em Configuracoes > Variaveis de ambiente do usuario, e nao as coloque em arquivo versionado.
    Nao apaga nada alem das duas tarefas que ele mesmo cria (-Remover).

.EXAMPLE
    .\tools\ops\Registrar-Agendamento.ps1 -ArquivoDeConfiguracao D:\config\backup-agendado.json
    .\tools\ops\Registrar-Agendamento.ps1 -ArquivoDeConfiguracao D:\config\backup-agendado.json -HoraDoBackup 02:30 -MonitorACadaMinutos 10 -Registrar
    .\tools\ops\Registrar-Agendamento.ps1 -Remover
#>
[CmdletBinding()]
param(
    [string]$ArquivoDeConfiguracao,
    [string]$HoraDoBackup = '02:30',
    [int]$MonitorACadaMinutos = 15,
    [switch]$SemMonitor,
    [switch]$Registrar,
    [switch]$Remover
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$nomeDoBackup = 'ia.bimeta-backup'
$nomeDoMonitor = 'ia.bimeta-monitor'
$pasta = $PSScriptRoot

if ($Remover) {
    foreach ($nome in $nomeDoBackup, $nomeDoMonitor) {
        $existente = Get-ScheduledTask -TaskName $nome -ErrorAction SilentlyContinue
        if ($null -eq $existente) { Write-Host ('A tarefa ' + $nome + ' nao existe: nada a remover.'); continue }
        Unregister-ScheduledTask -TaskName $nome -Confirm:$false
        Write-Host ('Tarefa ' + $nome + ' removida.')
    }
    exit 0
}

if ([string]::IsNullOrEmpty($ArquivoDeConfiguracao)) { Write-Host 'FALHA: informe -ArquivoDeConfiguracao (o JSON do Backup-Agendado; veja backup-agendado.exemplo.json).'; exit 2 }
if (-not (Test-Path $ArquivoDeConfiguracao)) { Write-Host ('FALHA: arquivo de configuracao nao encontrado: ' + $ArquivoDeConfiguracao); exit 2 }
$configuracao = (Resolve-Path $ArquivoDeConfiguracao).Path
try { $json = Get-Content -Raw -Path $configuracao | ConvertFrom-Json } catch { Write-Host 'FALHA: o arquivo de configuracao nao e um JSON valido.'; exit 2 }
if ($HoraDoBackup -notmatch '^([01]\d|2[0-3]):[0-5]\d$') { Write-Host 'FALHA: -HoraDoBackup deve ser HH:mm (24 h).'; exit 2 }
if ($MonitorACadaMinutos -lt 1 -or $MonitorACadaMinutos -gt 1440) { Write-Host 'FALHA: -MonitorACadaMinutos deve ficar entre 1 e 1440.'; exit 2 }

# As variaveis que a configuracao cita precisam existir (so os NOMES sao mostrados), senao o backup falharia de madrugada.
$usuario = $env:USERDOMAIN + '\' + $env:USERNAME
$variaveis = @()
foreach ($campo in 'variavelDeConexao', 'chaveEnv', 'alertaWebhookEnv') {
    if ($json.PSObject.Properties.Name -contains $campo -and $json.$campo) { $variaveis += $json.$campo }
}
$faltando = @($variaveis | Where-Object { [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($_, 'User')) -and [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($_, 'Machine')) })

$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$argumentoDoBackup = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $pasta 'Backup-Agendado.ps1') + '" -ArquivoDeConfiguracao "' + $configuracao + '"'
$argumentoDoMonitor = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $pasta 'Status-Ambiente.ps1') + '" -ArquivoDeConfiguracaoDoBackup "' + $configuracao + '" -AlertarSeCritico -SemBuild'
if ($json.PSObject.Properties.Name -contains 'alertaWebhookEnv' -and $json.alertaWebhookEnv) { $argumentoDoMonitor += ' -AlertaWebhookEnv ' + $json.alertaWebhookEnv }

Write-Host ('O que sera registrado (usuario ' + $usuario + ', so com a sessao aberta, sem guardar senha):')
Write-Host ''
Write-Host ('  ' + $nomeDoBackup + '   todo dia as ' + $HoraDoBackup)
Write-Host ('    ' + $powershell + ' ' + $argumentoDoBackup)
if (-not $SemMonitor) {
    Write-Host ('  ' + $nomeDoMonitor + '  a cada ' + $MonitorACadaMinutos + ' min')
    Write-Host ('    ' + $powershell + ' ' + $argumentoDoMonitor)
    Write-Host '    (o monitor usa -SemBuild: compile o Api antes, por exemplo com dotnet build backend\host\IaBimeta.Api)'
}
Write-Host ''
if ($faltando.Count -gt 0) {
    Write-Host ('ATENCAO: estas variaveis de ambiente citadas na configuracao NAO estao definidas (usuario ou maquina): ' + ($faltando -join ', '))
    Write-Host '         Defina-as antes de registrar: o backup agendado falharia (e alertaria) na primeira execucao.'
}

if (-not $Registrar) {
    Write-Host 'Nada foi alterado. Para registrar de verdade, rode de novo com -Registrar.'
    exit 0
}
if ($faltando.Count -gt 0) { Write-Host 'FALHA: nao registro com variaveis faltando.'; exit 2 }

$principal = New-ScheduledTaskPrincipal -UserId $usuario -LogonType Interactive -RunLevel Limited
$ajustes = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 6) -MultipleInstances IgnoreNew

$acaoDoBackup = New-ScheduledTaskAction -Execute $powershell -Argument $argumentoDoBackup
$gatilhoDoBackup = New-ScheduledTaskTrigger -Daily -At $HoraDoBackup
Register-ScheduledTask -TaskName $nomeDoBackup -Action $acaoDoBackup -Trigger $gatilhoDoBackup -Principal $principal -Settings $ajustes -Description 'ia.bimeta: backup agendado (ADR 0026). Criada por tools\ops\Registrar-Agendamento.ps1.' -Force | Out-Null
Write-Host ('Tarefa ' + $nomeDoBackup + ' registrada.')

if (-not $SemMonitor) {
    $acaoDoMonitor = New-ScheduledTaskAction -Execute $powershell -Argument $argumentoDoMonitor
    $gatilhoDoMonitor = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes $MonitorACadaMinutos)
    Register-ScheduledTask -TaskName $nomeDoMonitor -Action $acaoDoMonitor -Trigger $gatilhoDoMonitor -Principal $principal -Settings $ajustes -Description 'ia.bimeta: monitor do ambiente (ADR 0026). Criada por tools\ops\Registrar-Agendamento.ps1.' -Force | Out-Null
    Write-Host ('Tarefa ' + $nomeDoMonitor + ' registrada.')
}
Write-Host 'Pronto. Para conferir: Get-ScheduledTask -TaskName ia.bimeta-*   |   para remover: .\tools\ops\Registrar-Agendamento.ps1 -Remover'
exit 0
