<#
.SYNOPSIS
    TESTE de ponta a ponta da automacao de backup (ADR 0026), numa instancia PostgreSQL DESCARTAVEL e pequena: nenhum banco real e tocado.

.DESCRIPTION
    Sobe uma instancia descartavel com um banco de brinquedo e prova, rodando os scripts de verdade: backup agendado simples e cifrado; verificacao (parcial sem
    a frase, completa com ela, recusa com a frase errada); restauracao do backup cifrado em OUTRA instancia com contagens identicas; copia externa (cifrada, sem
    sobrescrever, recusa de copia sem cifra); politica de versoes (so as do agendamento, nunca as recentes, nada sem politica explicita); ensaio periodico de
    restauracao; configuracao por arquivo; e FALHA com ALERTA (arquivo e webhook local) que some no sucesso seguinte, sem nenhum segredo em log, estado ou alerta.
    Sai com 0 se tudo passou. Leva 1 a 2 minutos. Apaga so o que criou (a pasta temporaria dele e as instancias).

.EXAMPLE
    .\tools\ops\Testar-Automacao-De-Backup.ps1
#>
[CmdletBinding()]
param(
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Backup.ps1')

$raiz = Join-Path ([IO.Path]::GetTempPath()) ('iabimeta-teste-backup-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
$pastaDeBackups = Join-Path $raiz 'backups'
$externo = Join-Path $raiz 'externo'
New-Item -ItemType Directory -Force -Path $pastaDeBackups, $externo | Out-Null
$falhas = 0
function Conferir {
    param([string]$Nome, [bool]$Ok)
    if ($Ok) { Write-Host ('PASSOU  ' + $Nome) } else { Write-Host ('FALHOU  ' + $Nome); $script:falhas++ }
}

function Invoke-Script {
    param([string]$Script, [string[]]$Argumentos)
    $saida = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $Script) @Argumentos
    return [pscustomobject]@{ Codigo = $LASTEXITCODE; Saida = (@($saida) -join "`n") }
}

function Get-EstadoAtual { return (Get-Content -Raw -Path (Join-Path $pastaDeBackups 'estado-backup.json') | ConvertFrom-Json) }

$origem = $null
$outra = $null
$webhook = $null
try {
    Write-Host 'Subindo a instancia de origem (banco de brinquedo) ...'
    $origem = New-InstanciaDescartavel -Pasta (Join-Path $pastaDeBackups '_instancia-ensaio-origem')
    Invoke-Psql -Conexao $origem.Conexao -Banco 'postgres' -Sql 'CREATE DATABASE appteste;' | Out-Null
    $sql = 'CREATE SCHEMA identity; CREATE TABLE identity.users (id int primary key, nome text); INSERT INTO identity.users SELECT g, ''usuario '' || g FROM generate_series(1, 200) g; CREATE TABLE identity."__EFMigrationsHistory" (id int); INSERT INTO identity."__EFMigrationsHistory" VALUES (1);'
    Invoke-Psql -Conexao $origem.Conexao -Banco 'appteste' -Sql $sql | Out-Null
    $senhaDoBanco = $origem.Conexao.Senha
    $env:IABIMETA_T_CONEXAO = 'Host=127.0.0.1;Port=' + $origem.Conexao.Porta + ';Database=appteste;Username=ensaio;Password=' + $senhaDoBanco
    $env:IABIMETA_T_CHAVE = 'frase-secreta-de-teste-do-backup-123456'
    $env:IABIMETA_T_CHAVE_ERRADA = 'outra-frase-secreta-errada-do-teste-9876'
    $fraseSecreta = $env:IABIMETA_T_CHAVE
    $base = @('-Origem', 'Ambiente', '-VariavelDeConexao', 'IABIMETA_T_CONEXAO', '-PastaDeBackups', $pastaDeBackups, '-PostgresBin', $PostgresBin)

    # 1. Backup simples
    Write-Host '--- 1. Backup agendado simples'
    $r = Invoke-Script 'Backup-Agendado.ps1' $base
    $estado = Get-EstadoAtual
    Conferir 'backup simples termina com sucesso (codigo 0)' ($r.Codigo -eq 0)
    Conferir 'estado registra sucesso e a versao' (($estado.resultado -eq 'sucesso') -and ($estado.pastaDoUltimoBackup -like 'agendado-*') -and ($estado.cifrado -eq $false))
    Conferir 'sem falha nao ha alerta' (-not (Test-Path (Join-Path $pastaDeBackups 'ALERTA-BACKUP.txt')))
    Conferir 'sem politica explicita nada e apagado' ($r.Saida -like '*Sem politica de versoes configurada*')

    # 2. Backup cifrado
    Write-Host '--- 2. Backup cifrado'
    Start-Sleep -Seconds 1
    $r = Invoke-Script 'Backup-Agendado.ps1' ($base + @('-ChaveEnv', 'IABIMETA_T_CHAVE'))
    $estado = Get-EstadoAtual
    $versao = Join-Path $pastaDeBackups $estado.pastaDoUltimoBackup
    Conferir 'backup cifrado termina com sucesso' ($r.Codigo -eq 0)
    Conferir 'so o arquivo cifrado fica no disco (o dump em texto claro foi removido)' ((@(Get-ChildItem $versao -Filter '*.dump.enc').Count -eq 1) -and (@(Get-ChildItem $versao -Filter '*.dump').Count -eq 0))
    Conferir 'estado marca cifrado e verificado por completo' (($estado.cifrado -eq $true) -and ($estado.verificadoCompletamente -eq $true))
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $versao, '-PostgresBin', $PostgresBin)
    Conferir 'verificacao sem a frase e PARCIAL e avisa' (($v.Codigo -eq 0) -and ($v.Saida -like '*ATENCAO*so pelo arquivo*'))
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $versao, '-ChaveEnv', 'IABIMETA_T_CHAVE', '-PostgresBin', $PostgresBin)
    Conferir 'verificacao com a frase e COMPLETA' (($v.Codigo -eq 0) -and ($v.Saida -like '*Backup integro*') -and ($v.Saida -notlike '*ATENCAO*'))
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $versao, '-ChaveEnv', 'IABIMETA_T_CHAVE_ERRADA', '-PostgresBin', $PostgresBin)
    Conferir 'frase errada reprova a verificacao' (($v.Codigo -eq 1) -and ($v.Saida -like '*Falha na autenticacao*'))
    Conferir 'a pasta nao guarda nada decifrado depois da verificacao' (@(Get-ChildItem $versao -Directory -Filter '_tmp-*').Count -eq 0)

    # 3. Restaurar o backup cifrado em OUTRA instancia
    Write-Host '--- 3. Restauracao do backup cifrado em outra instancia'
    $outra = New-InstanciaDescartavel -Pasta (Join-Path $pastaDeBackups '_instancia-ensaio-destino')
    $env:IABIMETA_T_DESTINO = 'Host=127.0.0.1;Port=' + $outra.Conexao.Porta + ';Database=postgres;Username=ensaio;Password=' + $outra.Conexao.Senha
    $rs = Invoke-Script 'Restaurar-Backup.ps1' @('-Pasta', $versao, '-BancoDeDestino', 'restaurado_cifrado', '-Origem', 'Ambiente', '-VariavelDeConexao', 'IABIMETA_T_DESTINO', '-ChaveEnv', 'IABIMETA_T_CHAVE', '-PostgresBin', $PostgresBin)
    Conferir 'restauracao do cifrado com a frase certa passa' (($rs.Codigo -eq 0) -and ($rs.Saida -like '*Contagens identicas*'))
    $contagem = @(Invoke-Psql -Conexao $outra.Conexao -Banco 'restaurado_cifrado' -Sql 'SELECT count(*) FROM identity.users;')
    Conferir 'os 200 usuarios voltaram' ([int]$contagem[0] -eq 200)
    $rs = Invoke-Script 'Restaurar-Backup.ps1' @('-Pasta', $versao, '-BancoDeDestino', 'restaurado_cifrado2', '-Origem', 'Ambiente', '-VariavelDeConexao', 'IABIMETA_T_DESTINO', '-PostgresBin', $PostgresBin)
    Conferir 'restaurar o cifrado SEM a frase e recusado' (($rs.Codigo -eq 1) -and ($rs.Saida -like '*CIFRADO*'))
    $rs = Invoke-Script 'Restaurar-Backup.ps1' @('-Pasta', $versao, '-BancoDeDestino', 'restaurado_cifrado', '-Origem', 'Ambiente', '-VariavelDeConexao', 'IABIMETA_T_DESTINO', '-ChaveEnv', 'IABIMETA_T_CHAVE', '-PostgresBin', $PostgresBin)
    Conferir 'restaurar sobre banco existente e recusado' (($rs.Codigo -eq 1) -and ($rs.Saida -like '*JA EXISTE*'))

    # 4. Copia externa
    Write-Host '--- 4. Copia externa'
    Start-Sleep -Seconds 1
    $r = Invoke-Script 'Backup-Agendado.ps1' ($base + @('-ChaveEnv', 'IABIMETA_T_CHAVE', '-DestinoExterno', $externo))
    $estado = Get-EstadoAtual
    $copiada = Join-Path $externo $estado.pastaDoUltimoBackup
    Conferir 'copia externa cifrada concluida' (($r.Codigo -eq 0) -and (Test-Path (Join-Path $copiada 'manifesto.json')))
    $manifestoCopiado = Get-Content -Raw (Join-Path $copiada 'manifesto.json') | ConvertFrom-Json
    Conferir 'o SHA-256 da copia confere com o manifesto' ((Get-FileHash -Algorithm SHA256 (Join-Path $copiada $manifestoCopiado.arquivo)).Hash -eq $manifestoCopiado.sha256)
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $copiada, '-ChaveEnv', 'IABIMETA_T_CHAVE', '-PostgresBin', $PostgresBin)
    Conferir 'a copia externa tambem verifica por completo' ($v.Codigo -eq 0)
    $segunda = Copy-ParaDestinoExterno -Pasta (Join-Path $pastaDeBackups $estado.pastaDoUltimoBackup) -DestinoExterno $externo
    Conferir 'copiar de novo a mesma versao nao sobrescreve' (-not $segunda.Copiado)
    $antesDoExterno = @(Get-ChildItem $externo).Count
    Start-Sleep -Seconds 1
    $r = Invoke-Script 'Backup-Agendado.ps1' ($base + @('-DestinoExterno', $externo))
    Conferir 'backup SEM cifra com destino externo e recusado e alerta' (($r.Codigo -eq 1) -and ($r.Saida -like '*SEM criptografia*') -and (Test-Path (Join-Path $pastaDeBackups 'ALERTA-BACKUP.txt')))
    Conferir 'nada foi copiado para o destino externo na recusa' (@(Get-ChildItem $externo).Count -eq $antesDoExterno)
    $invalidas = @(Get-VersoesInvalidas -Base $pastaDeBackups)
    Conferir 'a versao que falhou ficou marcada como INVALIDA e fora da contagem de versoes boas' (($invalidas.Count -ge 1) -and (@(Get-VersoesAgendadas -Base $pastaDeBackups | Where-Object { $_.Name -eq $invalidas[0].Name }).Count -eq 0))
    $r = Invoke-Script 'Backup-Agendado.ps1' ($base + @('-DestinoExterno', $externo, '-PermitirExternoSemCriptografia'))
    Conferir 'com -PermitirExternoSemCriptografia (explicito) a copia sem cifra e aceita' ($r.Codigo -eq 0)
    Conferir 'o sucesso seguinte apaga o alerta' (-not (Test-Path (Join-Path $pastaDeBackups 'ALERTA-BACKUP.txt')))

    # 5. Politica de versoes
    Write-Host '--- 5. Politica de versoes'
    $oficina = Join-Path $raiz 'versoes'
    New-Item -ItemType Directory -Force -Path $oficina | Out-Null
    foreach ($dias in 90, 80, 70, 60, 50, 1) {
        $nome = 'agendado-' + (Get-Date).AddDays(-$dias).ToString('yyyyMMdd-HHmmss')
        New-Item -ItemType Directory -Path (Join-Path $oficina $nome) | Out-Null
        Write-ArquivoUtf8 -Caminho (Join-Path (Join-Path $oficina $nome) 'manifesto.json') -Texto '{}'
    }
    New-Item -ItemType Directory -Path (Join-Path $oficina 'manual-pre-migracao-20200101-000000') | Out-Null
    Write-ArquivoUtf8 -Caminho (Join-Path (Join-Path $oficina 'manual-pre-migracao-20200101-000000') 'manifesto.json') -Texto '{}'
    $simulada = @(Remove-VersoesAntigas -Base $oficina -ManterUltimos 3 -DiasMinimos 7 -SoSimular)
    Conferir 'simulacao lista as 3 mais antigas e nao apaga nada' (($simulada.Count -eq 3) -and (@(Get-VersoesAgendadas -Base $oficina).Count -eq 6))
    $removidas = @(Remove-VersoesAntigas -Base $oficina -ManterUltimos 3 -DiasMinimos 7)
    $restantes = @(Get-VersoesAgendadas -Base $oficina)
    Conferir 'mantem as 3 mais recentes e remove as 3 mais antigas' (($removidas.Count -eq 3) -and ($restantes.Count -eq 3))
    Conferir 'o backup manual (outro rotulo) nunca e tocado' (Test-Path (Join-Path $oficina 'manual-pre-migracao-20200101-000000'))
    $recente = @(Remove-VersoesAntigas -Base $oficina -ManterUltimos 3 -DiasMinimos 9999)
    Conferir 'nunca apaga versao com menos que DiasMinimos' (($recente.Count -eq 0) -and (@(Get-VersoesAgendadas -Base $oficina).Count -eq 3))
    $recusa = $null
    try { Remove-VersoesAntigas -Base $oficina -ManterUltimos 2 | Out-Null } catch { $recusa = $_.Exception.Message }
    Conferir 'politica com menos de 3 e recusada' ($recusa -like '*pelo menos 3*')

    # 6. Configuracao por arquivo e ensaio periodico
    Write-Host '--- 6. Configuracao por arquivo e ensaio periodico de restauracao'
    $arquivoDeConfiguracao = Join-Path $raiz 'config.json'
    $json = '{ "origem": "Ambiente", "variavelDeConexao": "IABIMETA_T_CONEXAO", "pastaDeBackups": "' + $pastaDeBackups.Replace('\', '\\') + '", "chaveEnv": "IABIMETA_T_CHAVE", "ensaiarRestauracaoACadaDias": 1 }'
    Write-ArquivoUtf8 -Caminho $arquivoDeConfiguracao -Texto $json
    Start-Sleep -Seconds 1
    $r = Invoke-Script 'Backup-Agendado.ps1' @('-ArquivoDeConfiguracao', $arquivoDeConfiguracao, '-PostgresBin', $PostgresBin)
    $estado = Get-EstadoAtual
    Conferir 'a configuracao por arquivo funciona' (($r.Codigo -eq 0) -and ($estado.cifrado -eq $true))
    Conferir 'o ensaio de restauracao rodou e aprovou' (($r.Saida -like '*Ensaio de restauracao aprovado*') -and ($estado.ultimoEnsaioAprovado -eq $true) -and $estado.ultimoEnsaioUtc)
    $primeiroEnsaio = $estado.ultimoEnsaioUtc
    Start-Sleep -Seconds 1
    $r = Invoke-Script 'Backup-Agendado.ps1' @('-ArquivoDeConfiguracao', $arquivoDeConfiguracao, '-PostgresBin', $PostgresBin)
    $estado = Get-EstadoAtual
    Conferir 'dentro do intervalo o ensaio NAO roda de novo' (($r.Codigo -eq 0) -and ($estado.ultimoEnsaioUtc -eq $primeiroEnsaio) -and ($r.Saida -notlike '*Ensaiando a restauracao*'))
    Write-ArquivoUtf8 -Caminho (Join-Path $raiz 'ruim.json') -Texto '{ "campoInexistente": 1 }'
    $r = Invoke-Script 'Backup-Agendado.ps1' @('-ArquivoDeConfiguracao', (Join-Path $raiz 'ruim.json'))
    Conferir 'campo desconhecido na configuracao e recusado (codigo 2)' (($r.Codigo -eq 2) -and ($r.Saida -like '*campo desconhecido*'))
    Conferir 'nao sobraram pastas temporarias de decifragem' (@(Get-ChildItem $pastaDeBackups -Recurse -Directory -Filter '_tmp-*').Count -eq 0)

    # 7. Falha, alerta (arquivo e webhook local) e recuperacao
    Write-Host '--- 7. Falha, alerta e recuperacao'
    $portaDoWebhook = Get-PortaLivre
    $env:IABIMETA_T_WEBHOOK = 'http://127.0.0.1:' + $portaDoWebhook + '/alerta-secreto-' + [guid]::NewGuid().ToString('N')
    $urlSecreta = $env:IABIMETA_T_WEBHOOK
    $arquivoDoWebhook = Join-Path $raiz 'webhook-recebido.txt'
    $webhook = Start-Job -ArgumentList $portaDoWebhook, $arquivoDoWebhook -ScriptBlock {
        param($porta, $arquivo)
        $ouvinte = New-Object System.Net.HttpListener
        $ouvinte.Prefixes.Add('http://127.0.0.1:' + $porta + '/')
        $ouvinte.Start()
        $tarefa = $ouvinte.GetContextAsync()
        if ($tarefa.Wait(60000)) {
            $contexto = $tarefa.Result
            $leitor = New-Object System.IO.StreamReader($contexto.Request.InputStream, [Text.Encoding]::UTF8)
            $texto = $leitor.ReadToEnd()
            [IO.File]::WriteAllText($arquivo, $texto)
            $contexto.Response.StatusCode = 200
            $ok = [Text.Encoding]::UTF8.GetBytes('ok')
            $contexto.Response.OutputStream.Write($ok, 0, $ok.Length)
            $contexto.Response.Close()
        }
        Start-Sleep -Milliseconds 500
        $ouvinte.Stop()
    }
    Start-Sleep -Seconds 2
    $ruim = @('-Origem', 'Ambiente', '-VariavelDeConexao', 'IABIMETA_T_VARIAVEL_QUE_NAO_EXISTE', '-PastaDeBackups', $pastaDeBackups, '-PostgresBin', $PostgresBin, '-AlertaWebhookEnv', 'IABIMETA_T_WEBHOOK')
    $r = Invoke-Script 'Backup-Agendado.ps1' $ruim
    $estado = Get-EstadoAtual
    Conferir 'a falha sai com codigo 1 (o Agendador mostra)' ($r.Codigo -eq 1)
    Conferir 'o estado registra a falha e a etapa' (($estado.resultado -eq 'falha') -and ($estado.mensagemDaFalha.StartsWith('[backup]')))
    Conferir 'o estado guarda o ultimo sucesso anterior' ([bool]$estado.ultimoSucessoUtc)
    $alerta = Join-Path $pastaDeBackups 'ALERTA-BACKUP.txt'
    Conferir 'o alerta em arquivo existe e diz a etapa' ((Test-Path $alerta) -and ((Get-Content -Raw $alerta) -like '*etapa backup*'))
    [void](Wait-Job -Job $webhook -Timeout 30)
    $recebido = if (Test-Path $arquivoDoWebhook) { Get-Content -Raw $arquivoDoWebhook } else { '' }
    Conferir 'o webhook local recebeu o alerta em JSON com o campo text' (($recebido -like '*"text"*') -and ($recebido -like '*FALHOU*'))
    if ($r.Saida -notlike '*webhook: enviado*') { Write-Host ('DIAGNOSTICO: ' + ($r.Saida -split "`n" | Select-Object -Last 4 | Out-String)) }
    Conferir 'a saida do script nao traz a URL do webhook e diz que o webhook foi enviado' ($r.Saida -notlike ('*' + $urlSecreta + '*') -and $r.Saida -like '*webhook: enviado*')
    $r = Invoke-Script 'Backup-Agendado.ps1' $base
    $estado = Get-EstadoAtual
    Conferir 'depois da correcao o backup volta ao normal e o alerta some' (($r.Codigo -eq 0) -and ($estado.resultado -eq 'sucesso') -and -not (Test-Path $alerta))

    # 8. Nenhum segredo em lugar nenhum
    Write-Host '--- 8. Nenhum segredo em log, estado, alerta, manifestos ou saida'
    $texto = ''
    foreach ($arquivo in (Get-ChildItem $pastaDeBackups -Recurse -File | Where-Object { $_.Extension -in '.log', '.json', '.txt' })) { $texto += (Get-Content -Raw $arquivo.FullName) }
    $texto += $recebido
    Conferir 'a frase de cifra nao aparece' (-not $texto.Contains($fraseSecreta))
    Conferir 'a senha do banco nao aparece' (-not $texto.Contains($senhaDoBanco))
    Conferir 'a URL do webhook nao aparece' (-not $texto.Contains($urlSecreta))
    Conferir 'a string de conexao nao aparece' (-not $texto.Contains('Password='))
}
finally {
    if ($null -ne $webhook) { Remove-Job -Job $webhook -Force -ErrorAction SilentlyContinue }
    foreach ($instancia in $origem, $outra) {
        if ($null -ne $instancia) { try { Remove-InstanciaDescartavel -Instancia $instancia -PastaDeBackups $pastaDeBackups } catch { Write-Host ('ATENCAO: ' + $_.Exception.Message) } }
    }
    foreach ($nome in 'IABIMETA_T_CONEXAO', 'IABIMETA_T_CHAVE', 'IABIMETA_T_CHAVE_ERRADA', 'IABIMETA_T_DESTINO', 'IABIMETA_T_WEBHOOK') { Remove-Item "Env:$nome" -ErrorAction SilentlyContinue }
    # So a pasta temporaria que ESTE teste criou.
    if ((Split-Path -Leaf $raiz).StartsWith('iabimeta-teste-backup-') -and (Test-Path $raiz)) { Remove-Item -Recurse -Force -Path $raiz -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($falhas -eq 0) { Write-Host 'AUTOMACAO DE BACKUP: TODOS OS TESTES PASSARAM.' } else { Write-Host ('AUTOMACAO DE BACKUP: ' + $falhas + ' TESTE(S) FALHARAM.') }
exit $(if ($falhas -eq 0) { 0 } else { 1 })
