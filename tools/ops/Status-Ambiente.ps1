<#
.SYNOPSIS
    ESTADO GERAL do ambiente em um comando (ADR 0026): o que a aplicacao enxerga (PostgreSQL, migrations, Api, Worker, filas, contas, retencao) mais o que e
    do sistema operacional (backup agendado, alerta, copia externa, disco, tarefas agendadas). Para cada falha diz o que fazer. Somente leitura.

.DESCRIPTION
    Parte 1: chama o comando "status" do Api (backend\host\IaBimeta.Api), que roda na SUA maquina com acesso ao banco, sem abrir porta nem rota HTTP.
    Parte 2: confere o backup agendado pelos arquivos que o Backup-Agendado.ps1 grava na pasta de backups (estado-backup.json e ALERTA-BACKUP.txt), o
    espaco em disco, o destino externo e as tarefas "ia.bimeta-*" do Agendador de Tarefas.
    Codigo de saida: 0 tudo em ordem, 1 ha pontos de atencao, 2 ha falha que precisa de intervencao.
    Com -AlertarSeCritico (para o monitor agendado): se houver falha critica grava ALERTA-AMBIENTE.txt na pasta de backups e, com -AlertaWebhookEnv (o NOME de
    uma variavel de ambiente com a URL), faz um POST JSON; o mesmo alerta nao e repetido antes de 6 h; quando tudo volta ao normal o arquivo some.

    A origem do banco segue o resto das ferramentas: sem -Origem, o Api le a propria configuracao (user-secrets do ambiente Development); com Teste ou
    Ambiente (-VariavelDeConexao), a conexao vai so para o processo filho. Nenhuma senha, frase ou URL e impressa.

.EXAMPLE
    .\tools\ops\Status-Ambiente.ps1
    .\tools\ops\Status-Ambiente.ps1 -Empresa lab-exemplo
    .\tools\ops\Status-Ambiente.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -ArquivoDeConfiguracaoDoBackup D:\config\backup-agendado.json -AlertarSeCritico -AlertaWebhookEnv IABIMETA_ALERTA_WEBHOOK
#>
[CmdletBinding()]
param(
    [string]$Empresa,
    [ValidateSet('Desenvolvimento', 'Teste', 'Ambiente')][string]$Origem,
    [string]$VariavelDeConexao,
    [string]$UrlDaApi,
    [string]$PastaDeBackups = 'C:\Projetos\ferramentas\backups\ia.bimeta',

    # O mesmo JSON do Backup-Agendado: de la vem a pasta de backups e o destino externo.
    [string]$ArquivoDeConfiguracaoDoBackup,
    [string]$DestinoExterno,
    [double]$IdadeMaximaDoBackupHoras = 26,
    [double]$IdadeMaximaDoEnsaioDias = 35,
    [switch]$SemAplicacao,
    [switch]$SemSistema,
    [switch]$SemBuild,
    [switch]$AlertarSeCritico,
    [string]$AlertaWebhookEnv,
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Backup.ps1')

$raiz = Get-RaizDoRepositorio
if ($ArquivoDeConfiguracaoDoBackup) {
    if (-not (Test-Path $ArquivoDeConfiguracaoDoBackup)) { Write-Host ('FALHA: configuracao do backup nao encontrada: ' + $ArquivoDeConfiguracaoDoBackup); exit 64 }
    $config = Get-Content -Raw -Path $ArquivoDeConfiguracaoDoBackup | ConvertFrom-Json
    if (-not $PSBoundParameters.ContainsKey('PastaDeBackups') -and $config.PSObject.Properties.Name -contains 'pastaDeBackups') { $PastaDeBackups = $config.pastaDeBackups }
    if (-not $PSBoundParameters.ContainsKey('DestinoExterno') -and $config.PSObject.Properties.Name -contains 'destinoExterno') { $DestinoExterno = $config.destinoExterno }
    if (-not $AlertaWebhookEnv -and $config.PSObject.Properties.Name -contains 'alertaWebhookEnv') { $AlertaWebhookEnv = $config.alertaWebhookEnv }
}

$achados = New-Object System.Collections.Generic.List[object]
function Add-Achado {
    param([string]$Secao, [ValidateSet('Ok', 'Atencao', 'Critico')][string]$Gravidade, [string]$Mensagem, [string]$Acao)
    $achados.Add([pscustomobject]@{ Secao = $Secao; Gravidade = $Gravidade; Mensagem = $Mensagem; Acao = $Acao })
}


# ---------- Parte 1: a aplicacao ----------
$codigoDaAplicacao = 0
$saidaDaAplicacao = $null
if (-not $SemAplicacao) {
    $argumentos = @('run', '--project', (Join-Path $raiz 'backend\host\IaBimeta.Api'), '--no-launch-profile')
    if ($SemBuild) { $argumentos += '--no-build' }
    $argumentos += @('--', 'status')
    if ($Empresa) { $argumentos += @('--tenant', $Empresa) }
    if ($UrlDaApi) { $argumentos += @('--api-url', $UrlDaApi) }
    if ([string]::IsNullOrEmpty($env:ASPNETCORE_ENVIRONMENT)) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }

    # Com Teste ou Ambiente a conexao vai SO para este processo filho, nas duas chaves: o Tenancy le ConnectionStrings:Tenancy ANTES de Default, e o user-secret dele aponta
    # para o banco de desenvolvimento (misturaria dois bancos).
    $padraoAnterior = $env:ConnectionStrings__Default
    $tenancyAnterior = $env:ConnectionStrings__Tenancy
    try {
        if ($Origem -eq 'Teste' -or $Origem -eq 'Ambiente') {
            $c = Get-ConexaoDeOrigem -Origem $Origem -VariavelDeConexao $VariavelDeConexao
            $conexaoDoFilho = 'Host=' + $c.Servidor + ';Port=' + $c.Porta + ';Database=' + $c.Banco + ';Username=' + $c.Usuario + ';Password=' + $c.Senha
            $env:ConnectionStrings__Default = $conexaoDoFilho
            $env:ConnectionStrings__Tenancy = $conexaoDoFilho
        }
        $saidaDaAplicacao = & dotnet @argumentos
        $codigoDaAplicacao = $LASTEXITCODE
    }
    finally {
        $env:ConnectionStrings__Default = $padraoAnterior
        $env:ConnectionStrings__Tenancy = $tenancyAnterior
    }
    foreach ($linha in @($saidaDaAplicacao)) { Write-Host $linha }
}

# ---------- Parte 2: o sistema operacional ----------
if (-not $SemSistema) {
    # Backup agendado
    $arquivoDeEstado = Join-Path $PastaDeBackups 'estado-backup.json'
    $estado = Read-EstadoDoBackup -Arquivo $arquivoDeEstado
    if ($null -eq $estado) {
        Add-Achado 'Backup' 'Atencao' 'nenhum backup agendado registrado (estado-backup.json nao existe).' 'Rode tools\ops\Backup-Agendado.ps1 (e agende com Registrar-Agendamento.ps1); sem backup nao ha recuperacao.'
    }
    else {
        if ($estado.resultado -eq 'falha') {
            Add-Achado 'Backup' 'Critico' ('a ULTIMA execucao do backup agendado FALHOU em ' + $estado.ultimaFalhaUtc + ': ' + $estado.mensagemDaFalha) 'Corrija a causa e rode Backup-Agendado.ps1 de novo; o alerta some no proximo sucesso.'
        }
        if ($estado.ultimoSucessoUtc) {
            $idade = ((Get-Date).ToUniversalTime() - ([datetime]::Parse($estado.ultimoSucessoUtc, [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime())).TotalHours
            if ($idade -gt $IdadeMaximaDoBackupHoras) { Add-Achado 'Backup' 'Critico' ('o ultimo backup bom tem ' + [Math]::Round($idade) + ' h (limite ' + $IdadeMaximaDoBackupHoras + ' h).') 'Confira a tarefa agendada (Registrar-Agendamento.ps1) e o log backup-agendado.log.' }
            else { Add-Achado 'Backup' 'Ok' ('ultimo backup bom ha ' + [Math]::Round($idade, 1) + ' h: ' + $estado.pastaDoUltimoBackup + ', ' + [Math]::Round([long]$estado.tamanhoEmBytes / 1MB, 1) + ' MB.') '' }
        }
        else { Add-Achado 'Backup' 'Critico' 'nenhum backup agendado terminou com sucesso ate agora.' 'Rode Backup-Agendado.ps1 e leia o log.' }
        if (-not $estado.cifrado) { Add-Achado 'Backup' 'Atencao' 'o backup agendado NAO esta cifrado.' 'Defina a frase numa variavel de ambiente e use -ChaveEnv (docs/operations/BACKUP-E-RESTAURACAO.md).' }
        elseif (-not $estado.verificadoCompletamente) { Add-Achado 'Backup' 'Atencao' 'o ultimo backup cifrado nao foi verificado por completo.' 'Confira a frase de cifra e rode Verificar-Backup.ps1 -ChaveEnv.' }
        if ($estado.ultimoEnsaioUtc) {
            $dias = ((Get-Date).ToUniversalTime() - ([datetime]::Parse($estado.ultimoEnsaioUtc, [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime())).TotalDays
            if ($estado.ultimoEnsaioAprovado -eq $false) { Add-Achado 'Backup' 'Critico' 'o ultimo ensaio de restauracao REPROVOU.' 'Rode Ensaiar-Restauracao.ps1 e investigue antes de confiar nos backups.' }
            elseif ($dias -gt $IdadeMaximaDoEnsaioDias) { Add-Achado 'Backup' 'Atencao' ('o ultimo ensaio de restauracao tem ' + [Math]::Round($dias) + ' dias (limite ' + $IdadeMaximaDoEnsaioDias + ').') 'Use -EnsaiarRestauracaoACadaDias no Backup-Agendado ou rode Ensaiar-Restauracao.ps1.' }
            else { Add-Achado 'Backup' 'Ok' ('ultimo ensaio de restauracao aprovado ha ' + [Math]::Round($dias, 1) + ' dias.') '' }
        }
        else { Add-Achado 'Backup' 'Atencao' 'nenhum ensaio de restauracao registrado.' 'Use -EnsaiarRestauracaoACadaDias no Backup-Agendado ou rode Ensaiar-Restauracao.ps1: backup nunca restaurado e esperanca, nao garantia.' }
    }
    if (Test-Path (Join-Path $PastaDeBackups 'ALERTA-BACKUP.txt')) {
        Add-Achado 'Backup' 'Critico' ('existe um ALERTA-BACKUP.txt na pasta de backups: ' + ((Get-Content -Raw (Join-Path $PastaDeBackups 'ALERTA-BACKUP.txt')).Trim())) 'Corrija a causa; o arquivo some no proximo backup bem-sucedido.'
    }
    $invalidas = @(Get-VersoesInvalidas -Base $PastaDeBackups)
    if ($invalidas.Count -gt 0) { Add-Achado 'Backup' 'Atencao' ([string]$invalidas.Count + ' versao(oes) marcada(s) como INVALIDA (falharam no meio): ' + (($invalidas | Select-Object -First 3 | ForEach-Object { $_.Name }) -join ', ') + '.') 'Examine e, se quiser, remova a pasta manualmente: nenhuma politica as apaga.' }

    # Destino externo
    if ($DestinoExterno) {
        if (-not (Test-Path -PathType Container -Path $DestinoExterno)) { Add-Achado 'Copia externa' 'Critico' 'o destino externo nao esta acessivel.' 'Confira o compartilhamento, a unidade e a autenticacao do Windows (cmdkey/mapeamento).' }
        else {
            $copias = @(Get-VersoesAgendadas -Base $DestinoExterno)
            $parciais = @(Get-ChildItem -Directory -Path $DestinoExterno -Filter '*.parcial-*' -ErrorAction SilentlyContinue)
            if ($copias.Count -eq 0) { Add-Achado 'Copia externa' 'Atencao' 'o destino externo esta acessivel mas nao tem nenhuma versao.' 'Rode Backup-Agendado.ps1 com -DestinoExterno.' }
            else {
                $criada = [datetime]::ParseExact($copias[0].Name.Substring(9), 'yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
                $horas = ((Get-Date) - $criada).TotalHours
                if ($horas -gt $IdadeMaximaDoBackupHoras) { Add-Achado 'Copia externa' 'Critico' ('a copia externa mais recente tem ' + [Math]::Round($horas) + ' h.') 'Confira o Backup-Agendado (etapa copia-externa) e o log.' }
                else { Add-Achado 'Copia externa' 'Ok' ([string]$copias.Count + ' versao(oes) no destino; a mais recente ha ' + [Math]::Round($horas, 1) + ' h.') '' }
            }
            if ($parciais.Count -gt 0) { Add-Achado 'Copia externa' 'Atencao' ([string]$parciais.Count + ' copia(s) parcial(is) de execucoes interrompidas no destino.') 'Confira e remova manualmente as pastas *.parcial-*: nada as apaga sozinho.' }
        }
    }

    # Disco
    try {
        $caminhoResolvido = (Resolve-Path -Path $PastaDeBackups -ErrorAction Stop).ProviderPath
        $raizDoDisco = [IO.Path]::GetPathRoot($caminhoResolvido)
        if ($raizDoDisco -match '^[A-Za-z]:\\') {
            $livreGB = (New-Object System.IO.DriveInfo($raizDoDisco)).AvailableFreeSpace / 1GB
            $tamanho = 0
            if ($null -ne $estado -and $estado.tamanhoEmBytes) { $tamanho = [long]$estado.tamanhoEmBytes }
            $necessarioGB = [Math]::Max(1, ($tamanho * 4) / 1GB)
            if ($livreGB -lt 1) { Add-Achado 'Disco' 'Critico' ('menos de 1 GB livre em ' + $raizDoDisco + '.') 'Libere espaco ou aplique a politica de versoes (-ManterUltimos): sem espaco o backup falha.' }
            elseif ($livreGB -lt $necessarioGB) { Add-Achado 'Disco' 'Atencao' ([string][Math]::Round($livreGB, 1) + ' GB livres em ' + $raizDoDisco + ' (o proximo backup cifrado precisa de ate ' + [Math]::Round($necessarioGB, 1) + ' GB).') 'Libere espaco ou aplique a politica de versoes (-ManterUltimos).' }
            else { Add-Achado 'Disco' 'Ok' ([string][Math]::Round($livreGB, 1) + ' GB livres em ' + $raizDoDisco + '.') '' }
        }
    }
    catch { Add-Achado 'Disco' 'Atencao' 'a pasta de backups nao existe ainda.' 'Rode Backup-Agendado.ps1.' }

    # Tarefas agendadas
    $tarefas = @()
    try { $tarefas = @(Get-ScheduledTask -TaskName 'ia.bimeta-*' -ErrorAction Stop) } catch { $tarefas = @() }
    if ($tarefas.Count -eq 0) { Add-Achado 'Agendador' 'Atencao' 'nenhuma tarefa "ia.bimeta-*" no Agendador de Tarefas: o backup e o monitor nao rodam sozinhos.' 'Registre com tools\ops\Registrar-Agendamento.ps1 -Registrar (ela mostra antes o que vai criar).' }
    foreach ($tarefa in $tarefas) {
        $info = Get-ScheduledTaskInfo -TaskName $tarefa.TaskName
        if ($tarefa.State -eq 'Disabled') { Add-Achado 'Agendador' 'Atencao' ('a tarefa ' + $tarefa.TaskName + ' esta DESABILITADA.') 'Habilite no Agendador de Tarefas.' }
        elseif ($info.LastTaskResult -ne 0 -and $info.LastTaskResult -ne 267011) { Add-Achado 'Agendador' 'Critico' ('a tarefa ' + $tarefa.TaskName + ' terminou com codigo ' + $info.LastTaskResult + ' em ' + $info.LastRunTime + '.') 'Veja o log backup-agendado.log e o estado-backup.json.' }
        else { Add-Achado 'Agendador' 'Ok' ('a tarefa ' + $tarefa.TaskName + ' esta ' + $tarefa.State + ' (ultimo resultado ' + $info.LastTaskResult + ', proxima execucao ' + $info.NextRunTime + ').') '' }
    }

    $ordem = 'Backup', 'Copia externa', 'Disco', 'Agendador'
    foreach ($secao in $ordem) {
        $da = @($achados | Where-Object { $_.Secao -eq $secao })
        if ($da.Count -eq 0) { continue }
        Write-Host ''
        Write-Host $secao.ToUpperInvariant()
        foreach ($a in $da) {
            $marca = switch ($a.Gravidade) { 'Critico' { '[CRITICO]' } 'Atencao' { '[ATENCAO]' } default { '[OK]     ' } }
            Write-Host ('  ' + $marca + ' ' + $a.Mensagem)
            if ($a.Acao -and $a.Gravidade -ne 'Ok') { Write-Host ('            -> ' + $a.Acao) }
        }
    }
}

# ---------- Resultado ----------
$criticos = @($achados | Where-Object { $_.Gravidade -eq 'Critico' }).Count
$atencoes = @($achados | Where-Object { $_.Gravidade -eq 'Atencao' }).Count
$gravidade = 0
if ($atencoes -gt 0) { $gravidade = 1 }
if ($criticos -gt 0) { $gravidade = 2 }
if ($codigoDaAplicacao -eq 1 -and $gravidade -lt 1) { $gravidade = 1 }
if ($codigoDaAplicacao -ge 2) { $gravidade = 2 }

Write-Host ''
switch ($gravidade) {
    0 { Write-Host 'RESULTADO GERAL: tudo em ordem (aplicacao e sistema).' }
    1 { Write-Host 'RESULTADO GERAL: sem falha critica; ha pontos de atencao.' }
    default { Write-Host 'RESULTADO GERAL: ha falha que PRECISA de intervencao (veja os itens [CRITICO] acima).' }
}

if ($AlertarSeCritico) {
    $arquivoDoAlerta = 'ALERTA-AMBIENTE.txt'
    if ($gravidade -ge 2) {
        $resumo = @($achados | Where-Object { $_.Gravidade -eq 'Critico' } | ForEach-Object { $_.Secao + ': ' + $_.Mensagem })
        if ($codigoDaAplicacao -ge 2) { $resumo += 'Aplicacao: o comando status apontou falha critica (veja a saida do Status-Ambiente.ps1).' }
        $texto = 'ALERTA ia.bimeta: o ambiente tem falha que precisa de intervencao (' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm') + ' UTC, ' + $env:COMPUTERNAME + '). ' + (Hide-Segredos -Texto ($resumo -join ' | ') -NomesDeVariaveis @($AlertaWebhookEnv, $VariavelDeConexao))
        $resultadoDoAlerta = Send-AlertaGenerico -Pasta $PastaDeBackups -NomeDoArquivo $arquivoDoAlerta -Texto $texto -WebhookEnv $AlertaWebhookEnv -ArquivoDeEstado (Join-Path $PastaDeBackups 'estado-alerta-ambiente.json')
        Write-Host ('Alerta do ambiente gravado em ' + (Join-Path $PastaDeBackups $arquivoDoAlerta) + '; ' + $resultadoDoAlerta + '.')
    }
    else {
        if (Test-Path (Join-Path $PastaDeBackups $arquivoDoAlerta)) { Remove-Item -Force (Join-Path $PastaDeBackups $arquivoDoAlerta); Write-Host 'O ambiente voltou ao normal: o alerta do ambiente foi removido.' }
        $arquivoDeAlertaEnviado = Join-Path $PastaDeBackups 'estado-alerta-ambiente.json'
        if (Test-Path $arquivoDeAlertaEnviado) { Remove-Item -Force $arquivoDeAlertaEnviado }
    }
}

exit $gravidade
