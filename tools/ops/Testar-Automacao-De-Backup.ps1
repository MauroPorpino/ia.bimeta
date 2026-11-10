<#
.SYNOPSIS
    TESTE de ponta a ponta da automacao de backup (ADR 0026), numa instancia PostgreSQL DESCARTAVEL e pequena: nenhum banco real e tocado.

.DESCRIPTION
    Sobe uma instancia descartavel com um banco de brinquedo e prova, rodando os scripts de verdade: backup agendado simples e cifrado; verificacao (parcial sem
    a frase, completa com ela, recusa com a frase errada); restauracao do backup cifrado em OUTRA instancia com contagens identicas; copia externa (cifrada, sem
    sobrescrever, recusa de copia sem cifra); politica de versoes (so as do agendamento, nunca as recentes, nada sem politica explicita); ensaio periodico de
    restauracao; configuracao por arquivo; e FALHA com ALERTA (arquivo e webhook local) que some no sucesso seguinte, sem nenhum segredo em log, estado ou alerta.

    ANEXOS PRIVADOS no backup (sempre com anexos SINTETICOS numa pasta temporaria): backup do banco e da pasta de anexos juntos (dump primeiro, depois os arquivos),
    manifesto deterministico, ignorados contados, idempotencia (nunca sobrescreve), verificacao que reprova byte alterado, arquivo ausente, extra, truncado e
    manifesto adulterado (chave "..\x"), restauracao dos anexos em pasta nova (recusa destino nao vazio), recuperacao DEPOIS DA PERDA da pasta original (so do backup),
    backup agendado cifrado que FALHA com anexo em texto claro e que, com anexos ja cifrados, copia para fora sem texto claro e sem cifra dupla, ensaio de restauracao
    com anexos, politica de versoes levando os anexos junto, e o backup SEM -PastaDeAnexos identico ao de sempre.

    Sai com 0 se tudo passou. Leva alguns minutos. Apaga so o que criou (a pasta temporaria dele e as instancias).

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
$externoCifrado = Join-Path $raiz 'externo-cifrado'
New-Item -ItemType Directory -Force -Path $pastaDeBackups, $externo, $externoCifrado | Out-Null
$falhas = 0
$verificacoes = 0
$cronometroDoTeste = [Diagnostics.Stopwatch]::StartNew()
function Conferir {
    param([string]$Nome, [bool]$Ok)
    $script:verificacoes++
    if ($Ok) { Write-Host ('PASSOU  ' + $Nome) } else { Write-Host ('FALHOU  ' + $Nome); $script:falhas++ }
}

function Write-Secao {
    param([string]$Titulo)
    Write-Host ('--- ' + $Titulo + '   [t = ' + [int]$cronometroDoTeste.Elapsed.TotalSeconds + ' s]')
}

# ---- Ajudantes dos anexos sinteticos (nenhum dado real; o "cifrado" e so o cabecalho IABA seguido de bytes aleatorios) ----
$marcadorDeTextoClaro = 'TEXTO-CLARO-SINTETICO-DO-ANEXO'
$gerador = [System.Security.Cryptography.RandomNumberGenerator]::Create()
function Get-Hex32 { return [guid]::NewGuid().ToString('N') }

function New-AnexoSintetico {
    param([Parameter(Mandatory)][string]$Raiz, [Parameter(Mandatory)][string]$Tenant, [Parameter(Mandatory)][int]$Tamanho, [Parameter(Mandatory)][bool]$Cifrado)
    $pasta = Join-Path $Raiz $Tenant
    New-Item -ItemType Directory -Force -Path $pasta | Out-Null
    $caminho = Join-Path $pasta ((Get-Hex32) + '.bin')
    if ($Cifrado) {
        $bytes = New-Object byte[] $Tamanho
        $gerador.GetBytes($bytes)
        [Array]::Copy([byte[]](0x49, 0x41, 0x42, 0x41), 0, $bytes, 0, [Math]::Min(4, $Tamanho))
    }
    else {
        $texto = ''
        while ($texto.Length -lt $Tamanho) { $texto += ($marcadorDeTextoClaro + '-' + (Get-Hex32) + ' ') }
        $bytes = [Text.Encoding]::ASCII.GetBytes($texto.Substring(0, $Tamanho))
    }
    [IO.File]::WriteAllBytes($caminho, $bytes)
    return $caminho
}

# chave 'tenant/arquivo.bin' -> SHA-256, de uma raiz de anexos (a origem, a pasta anexos de um backup ou um destino restaurado).
function Get-MapaDeHashes {
    param([Parameter(Mandatory)][string]$Raiz)
    $mapa = @{}
    foreach ($objeto in (Get-ObjetosDeAnexos -Raiz $Raiz).Objetos) { $mapa[$objeto.Chave] = Get-Sha256DoArquivo -Caminho $objeto.Caminho }
    return $mapa
}

function Test-MapasIguais {
    param($A, $B)
    if ($A.Count -ne $B.Count) { return $false }
    foreach ($chave in $A.Keys) { if (-not $B.ContainsKey($chave) -or $A[$chave] -ne $B[$chave]) { return $false } }
    return $true
}

# A pasta da versao que um script (Backup-Banco, Ensaiar) devolve: a ultima linha da saida.
function Get-PastaDaSaida {
    param([string]$Saida)
    return (($Saida -split "`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -Last 1).Trim())
}

# O texto do manifesto de anexos sem a linha do instante de criacao (a unica parte que muda entre duas copias do mesmo conteudo).
function Get-ManifestoDeAnexosSemData {
    param([Parameter(Mandatory)][string]$Pasta)
    return ((Get-Content -Raw -Path (Join-Path $Pasta 'anexos-manifesto.json')) -replace '"criadoEmUtc": "[^"]*",', '')
}

function Set-ByteAlterado {
    param([Parameter(Mandatory)][string]$Caminho, [int]$Posicao = 50)
    $bytes = [IO.File]::ReadAllBytes($Caminho)
    $bytes[$Posicao] = $bytes[$Posicao] -bxor 1
    [IO.File]::WriteAllBytes($Caminho, $bytes)
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

    # 8. Anexos privados no backup: o dump e depois os arquivos; manifesto; ignorados; idempotencia
    Write-Secao '8. Anexos privados no backup: dump e depois os arquivos, manifesto deterministico, ignorados, idempotencia'
    $origemDosAnexos = Join-Path $raiz 'anexos-origem'
    $tenantA = Get-Hex32
    $tenantB = Get-Hex32
    [void](New-AnexoSintetico -Raiz $origemDosAnexos -Tenant $tenantA -Tamanho 5000 -Cifrado $true)
    [void](New-AnexoSintetico -Raiz $origemDosAnexos -Tenant $tenantA -Tamanho 2600000 -Cifrado $true)   # atravessa o bloco de 1 MB da copia
    [void](New-AnexoSintetico -Raiz $origemDosAnexos -Tenant $tenantA -Tamanho 70 -Cifrado $true)
    [void](New-AnexoSintetico -Raiz $origemDosAnexos -Tenant $tenantB -Tamanho 3000 -Cifrado $false)    # legado em texto claro
    [void](New-AnexoSintetico -Raiz $origemDosAnexos -Tenant $tenantB -Tamanho 17 -Cifrado $false)
    # Cinco arquivos que NAO sao objetos: gravacao em andamento, arquivo estranho na raiz, nome em maiusculas, fundo demais e objeto sem tenant.
    [IO.File]::WriteAllText((Join-Path (Join-Path $origemDosAnexos $tenantA) ((Get-Hex32) + '.bin.parcial')), 'gravacao em andamento')
    [IO.File]::WriteAllText((Join-Path $origemDosAnexos 'leiame.txt'), 'estranho')
    [IO.File]::WriteAllText((Join-Path (Join-Path $origemDosAnexos $tenantA) ((Get-Hex32).ToUpper() + '.bin')), 'nome em maiusculas')
    New-Item -ItemType Directory -Force -Path (Join-Path (Join-Path $origemDosAnexos $tenantA) 'sub') | Out-Null
    [IO.File]::WriteAllText((Join-Path (Join-Path (Join-Path $origemDosAnexos $tenantA) 'sub') ((Get-Hex32) + '.bin')), 'fundo demais')
    [IO.File]::WriteAllText((Join-Path $origemDosAnexos ((Get-Hex32) + '.bin')), 'sem tenant')
    $hashesDaOrigem = Get-MapaDeHashes -Raiz $origemDosAnexos
    $listagem = Get-ObjetosDeAnexos -Raiz $origemDosAnexos
    Conferir 'a listagem acha 5 objetos (3 cifrados pelo cabecalho IABA e 2 legados) e conta 5 ignorados' (($listagem.Objetos.Count -eq 5) -and ($listagem.Ignorados -eq 5) -and (@($listagem.Objetos | Where-Object { $_.Cifrado }).Count -eq 3))
    $totalEsperado = 5000 + 2600000 + 70 + 3000 + 17

    $r = Invoke-Script 'Backup-Banco.ps1' ($base + @('-Rotulo', 'anexos1', '-PastaDeAnexos', $origemDosAnexos))
    $pastaComAnexos = Get-PastaDaSaida $r.Saida
    Conferir 'Backup-Banco com -PastaDeAnexos termina com sucesso e devolve a pasta da versao' (($r.Codigo -eq 0) -and (Test-Path (Join-Path $pastaComAnexos 'manifesto.json')))
    $manifestoPrincipal = Get-Content -Raw (Join-Path $pastaComAnexos 'manifesto.json') | ConvertFrom-Json
    $manifestoDeAnexos = Get-Content -Raw (Join-Path $pastaComAnexos 'anexos-manifesto.json') | ConvertFrom-Json
    Conferir 'o manifesto.json ganha o bloco anexos (manifesto, objetos, tamanho total e SHA-256 do manifesto de anexos)' ([bool](($manifestoPrincipal.anexos.manifesto -eq 'anexos-manifesto.json') -and ($manifestoPrincipal.anexos.objetos -eq 5) -and ($manifestoPrincipal.anexos.tamanhoTotalEmBytes -eq $totalEsperado) -and ($manifestoPrincipal.anexos.sha256DoManifesto -eq (Get-Sha256DoArquivo -Caminho (Join-Path $pastaComAnexos 'anexos-manifesto.json')))))
    Conferir 'o anexos-manifesto.json traz objetos, tenants, ignorados, cifrados, legados e tamanho total corretos' ([bool](($manifestoDeAnexos.versaoDoManifesto -eq 1) -and ($manifestoDeAnexos.objetos -eq 5) -and ($manifestoDeAnexos.tenants -eq 2) -and ($manifestoDeAnexos.ignorados -eq 5) -and ($manifestoDeAnexos.cifradosNaOrigem -eq 3) -and ($manifestoDeAnexos.legadosSemCifra -eq 2) -and ($manifestoDeAnexos.tamanhoTotalEmBytes -eq $totalEsperado) -and (@($manifestoDeAnexos.arquivos).Count -eq 5)))
    $chaves = @($manifestoDeAnexos.arquivos | ForEach-Object { $_.chave })
    $ordenadas = [string[]]$chaves.Clone()
    [Array]::Sort($ordenadas, [StringComparer]::Ordinal)
    $hashesBatem = $true
    foreach ($entrada in @($manifestoDeAnexos.arquivos)) { if ($hashesDaOrigem[$entrada.chave] -ne $entrada.sha256) { $hashesBatem = $false } }
    Conferir 'os arquivos do manifesto estao ordenados por chave e cada SHA-256 e o do arquivo de origem' ((($chaves -join ',') -ceq ($ordenadas -join ',')) -and $hashesBatem)
    $textoDosManifestos = (Get-Content -Raw (Join-Path $pastaComAnexos 'anexos-manifesto.json')) + (Get-Content -Raw (Join-Path $pastaComAnexos 'manifesto.json'))
    Conferir 'nenhum caminho absoluto da origem nos manifestos (nem escapado em JSON)' ((-not $textoDosManifestos.Contains($raiz)) -and (-not $textoDosManifestos.Contains($raiz.Replace('\', '\\'))) -and (-not $textoDosManifestos.Contains($origemDosAnexos)) -and ($textoDosManifestos -notmatch '[A-Za-z]:\\'))
    $objetosCopiados = @(Get-ChildItem (Join-Path $pastaComAnexos 'anexos') -Recurse -File)
    Conferir 'a copia tem exatamente os 5 objetos, byte a byte (SHA-256) iguais aos da origem, sem os ignorados' (($objetosCopiados.Count -eq 5) -and (Test-MapasIguais (Get-MapaDeHashes -Raiz (Join-Path $pastaComAnexos 'anexos')) $hashesDaOrigem) -and (@($objetosCopiados | Where-Object { $_.Name -notmatch '^[0-9a-f]{32}\.bin$' }).Count -eq 0))
    $dump = Get-ChildItem $pastaComAnexos -Filter '*.dump' | Select-Object -First 1
    $primeiraCopia = ($objetosCopiados | ForEach-Object { $_.CreationTimeUtc } | Sort-Object | Select-Object -First 1)
    Conferir 'ORDEM: o pg_dump terminou antes de o primeiro arquivo de anexo ser copiado (e o manifesto.json registra o instante do dump)' (($dump.LastWriteTimeUtc -le $primeiraCopia) -and ($manifestoPrincipal.criadoEmUtc -le $manifestoDeAnexos.criadoEmUtc))
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $pastaComAnexos, '-PostgresBin', $PostgresBin)
    Conferir 'Verificar-Backup confere o banco e os anexos e relata objetos e bytes' (($v.Codigo -eq 0) -and ($v.Saida -like '*Backup integro*') -and ($v.Saida -like '*Anexos integros: 5 objeto(s)*'))

    # Determinismo e idempotencia (os objetos sao imutaveis: copiar de novo o que ja existe, igual, e seguro)
    $det1 = Join-Path $raiz 'det1'
    $det2 = Join-Path $raiz 'det2'
    $d1 = Backup-Anexos -Raiz $origemDosAnexos -Destino $det1
    Start-Sleep -Seconds 1
    $d2 = Backup-Anexos -Raiz $origemDosAnexos -Destino $det2
    Conferir 'o manifesto de anexos e deterministico: o mesmo conteudo gera o mesmo texto (salvo o instante de criacao)' (((Get-ManifestoDeAnexosSemData $det1) -ceq (Get-ManifestoDeAnexosSemData $det2)) -and ((Get-ManifestoDeAnexosSemData $det1) -ceq (Get-ManifestoDeAnexosSemData $pastaComAnexos)))
    $d1b = Backup-Anexos -Raiz $origemDosAnexos -Destino $det1
    Conferir 'copiar de novo para o mesmo destino pula os objetos iguais (idempotente) e o manifesto nao muda' (($d1.Copiados -eq 5) -and ($d1b.Copiados -eq 0) -and ($d1b.Pulados -eq 5) -and ((Get-ManifestoDeAnexosSemData $det1) -ceq (Get-ManifestoDeAnexosSemData $det2)))
    $alvoDoDestino = (Get-ChildItem (Join-Path $det1 'anexos') -Recurse -File | Where-Object { $_.Length -ge 100 } | Select-Object -First 1).FullName
    Set-ByteAlterado -Caminho $alvoDoDestino
    $hashAlterado = Get-Sha256DoArquivo -Caminho $alvoDoDestino
    $recusa = $null
    try { Backup-Anexos -Raiz $origemDosAnexos -Destino $det1 | Out-Null } catch { $recusa = $_.Exception.Message }
    Conferir 'objeto que ja existe no destino com SHA-256 DIFERENTE: a copia FALHA e nunca sobrescreve' (($recusa -like '*DIFERENTE*') -and ((Get-Sha256DoArquivo -Caminho $alvoDoDestino) -eq $hashAlterado) -and (@(Get-ChildItem $det1 -Recurse -File -Filter '*.parcial').Count -eq 0))
    $recusa = $null
    try { Get-ObjetosDeAnexos -Raiz (Join-Path $raiz 'nao-existe') | Out-Null } catch { $recusa = $_.Exception.Message }
    Conferir 'pasta de anexos inexistente e rejeitada com mensagem clara' ($recusa -like '*nao existe*')
    $versoesAntes = @(Get-ChildItem $pastaDeBackups -Directory | Where-Object { $_.Name -notlike '_instancia*' }).Count
    $r = Invoke-Script 'Backup-Banco.ps1' ($base + @('-Rotulo', 'anexosruim', '-PastaDeAnexos', (Join-Path $raiz 'nao-existe')))
    Conferir 'Backup-Banco com pasta de anexos inexistente falha ANTES do dump (nenhuma pasta de versao nova)' (($r.Codigo -ne 0) -and (@(Get-ChildItem $pastaDeBackups -Directory | Where-Object { $_.Name -notlike '_instancia*' }).Count -eq $versoesAntes))

    # Sem -PastaDeAnexos o backup e o manifesto continuam IDENTICOS aos de sempre
    $r = Invoke-Script 'Backup-Banco.ps1' ($base + @('-Rotulo', 'semanexos'))
    $pastaSemAnexos = Get-PastaDaSaida $r.Saida
    $manifestoSem = Get-Content -Raw (Join-Path $pastaSemAnexos 'manifesto.json') | ConvertFrom-Json
    Conferir 'sem -PastaDeAnexos: o manifesto.json tem exatamente os campos de sempre, sem o bloco anexos' (($r.Codigo -eq 0) -and ((@($manifestoSem.PSObject.Properties.Name) -join ',') -ceq 'versaoDoManifesto,rotulo,criadoEmUtc,banco,servidor,arquivo,tamanhoEmBytes,sha256,pgDump,entradasDoDump,contagens'))
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $pastaSemAnexos, '-PostgresBin', $PostgresBin)
    Conferir 'sem -PastaDeAnexos: nao ha pasta anexos, nem anexos-manifesto.json, e a verificacao nao fala de anexos' ((-not (Test-Path (Join-Path $pastaSemAnexos 'anexos'))) -and (-not (Test-Path (Join-Path $pastaSemAnexos 'anexos-manifesto.json'))) -and ($v.Codigo -eq 0) -and ($v.Saida -notlike '*nexos*'))

    # 9. Adulteracao dos anexos da copia
    Write-Secao '9. Adulteracao dos anexos da copia: a verificacao reprova'
    $alvo = @($manifestoDeAnexos.arquivos | Where-Object { $_.tamanhoEmBytes -ge 100 })[0]
    $caminhoDoAlvo = 'anexos\' + $alvo.chave.Replace('/', '\')
    $c = Join-Path $raiz 'adulterada-byte'
    Copy-Item -Recurse -Path $pastaComAnexos -Destination $c
    Set-ByteAlterado -Caminho (Join-Path $c $caminhoDoAlvo)
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $c, '-PostgresBin', $PostgresBin)
    Conferir '1 byte alterado em um anexo da copia: Verificar-Backup reprova (codigo 1, aponta o SHA-256)' (($v.Codigo -eq 1) -and ($v.Saida -like '*FALHA nos anexos*') -and ($v.Saida -like '*SHA-256*'))
    $c = Join-Path $raiz 'adulterada-ausente'
    Copy-Item -Recurse -Path $pastaComAnexos -Destination $c
    Remove-Item -Force -Path (Join-Path $c $caminhoDoAlvo)
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $c, '-PostgresBin', $PostgresBin)
    Conferir 'um anexo apagado da copia: Verificar-Backup reprova (codigo 1, diz que esta ausente)' (($v.Codigo -eq 1) -and ($v.Saida -like '*ausente*'))
    $c = Join-Path $raiz 'adulterada-extra'
    Copy-Item -Recurse -Path $pastaComAnexos -Destination $c
    [IO.File]::WriteAllText((Join-Path $c 'anexos\estranho.txt'), 'arquivo que o manifesto nao conhece')
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $c, '-PostgresBin', $PostgresBin)
    Conferir 'um arquivo EXTRA na pasta anexos da copia: Verificar-Backup reprova (codigo 1, diz que e extra)' (($v.Codigo -eq 1) -and ($v.Saida -like '*extra*'))
    $c = Join-Path $raiz 'adulterada-truncada'
    Copy-Item -Recurse -Path $pastaComAnexos -Destination $c
    $bytes = [IO.File]::ReadAllBytes((Join-Path $c $caminhoDoAlvo))
    [IO.File]::WriteAllBytes((Join-Path $c $caminhoDoAlvo), [byte[]]($bytes[0..($bytes.Length - 2)]))
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $c, '-PostgresBin', $PostgresBin)
    Conferir 'um anexo truncado na copia: Verificar-Backup reprova (codigo 1, aponta o tamanho)' (($v.Codigo -eq 1) -and ($v.Saida -like '*tamanho*'))
    $c = Join-Path $raiz 'adulterada-manifesto'
    Copy-Item -Recurse -Path $pastaComAnexos -Destination $c
    $textoDoManifesto = Get-Content -Raw (Join-Path $c 'anexos-manifesto.json')
    Write-ArquivoUtf8 -Caminho (Join-Path $c 'anexos-manifesto.json') -Texto $textoDoManifesto.Replace('"ignorados": 5,', '"ignorados": 6,')
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $c, '-PostgresBin', $PostgresBin)
    Conferir 'o anexos-manifesto.json alterado: Verificar-Backup reprova (o SHA-256 do manifesto de anexos nao bate com o manifesto.json)' (($v.Codigo -eq 1) -and ($v.Saida -like '*manifesto de anexos alterado*'))

    # Manifesto adulterado com chave de fuga (path traversal): recusada ANTES de copiar qualquer coisa
    $textoOriginal = Get-Content -Raw (Join-Path $pastaComAnexos 'anexos-manifesto.json')
    $primeiraChave = $manifestoDeAnexos.arquivos[0].chave
    $todasRecusadas = $true
    $nadaFoiCriado = $true
    foreach ($ruim in @('..\\x', '../../x.bin', 'C:\\Windows\\x.bin', '/etc/x.bin')) {
        $pastaRuim = Join-Path $raiz 'adulterada-chave'
        if (Test-Path $pastaRuim) { Remove-Item -Recurse -Force -Path $pastaRuim }
        Copy-Item -Recurse -Path $pastaComAnexos -Destination $pastaRuim
        Write-ArquivoUtf8 -Caminho (Join-Path $pastaRuim 'anexos-manifesto.json') -Texto $textoOriginal.Replace('"' + $primeiraChave + '"', '"' + $ruim + '"')
        $teste = Test-Anexos -Pasta $pastaRuim
        $destinoRuim = Join-Path $raiz 'restauracao-recusada'
        $recusouRestore = $false
        $recusouRestoreJaVerificado = $false
        try { Restore-Anexos -Pasta $pastaRuim -Destino $destinoRuim | Out-Null } catch { $recusouRestore = $true }
        try { Restore-Anexos -Pasta $pastaRuim -Destino $destinoRuim -JaVerificado | Out-Null } catch { $recusouRestoreJaVerificado = $true }
        if ($teste.Ok -or (($teste.Erros -join ' ') -notlike '*chave recusada*') -or -not $recusouRestore -or -not $recusouRestoreJaVerificado) { $todasRecusadas = $false }
        if ((Test-Path $destinoRuim) -or (Test-Path (Join-Path $raiz 'x')) -or (Test-Path (Join-Path $raiz 'x.bin')) -or (Test-Path (Join-Path $pastaRuim 'x'))) { $nadaFoiCriado = $false }
    }
    Conferir 'chaves de fuga no manifesto (..\x, ../../x.bin, C:\...\x.bin, /etc/x.bin): Test-Anexos reprova e Restore-Anexos RECUSA (com e sem -JaVerificado)' $todasRecusadas
    Conferir 'a recusa vem ANTES de copiar: nem o destino nem nenhum arquivo fora dele foi criado' $nadaFoiCriado

    # 10. Restauracao dos anexos em pasta nova
    Write-Secao '10. Restauracao dos anexos em pasta nova (Restaurar-Backup e Restaurar-Anexos)'
    $destinoDosAnexos = Join-Path $raiz 'anexos-restaurados'
    $rs = Invoke-Script 'Restaurar-Backup.ps1' @('-Pasta', $pastaComAnexos, '-BancoDeDestino', 'restaurado_com_anexos', '-Origem', 'Ambiente', '-VariavelDeConexao', 'IABIMETA_T_DESTINO', '-PastaDeAnexosDestino', $destinoDosAnexos, '-PostgresBin', $PostgresBin)
    Conferir 'Restaurar-Backup com -PastaDeAnexosDestino restaura o banco e os anexos (codigo 0, contagens identicas)' (($rs.Codigo -eq 0) -and ($rs.Saida -like '*Anexos restaurados em*') -and ($rs.Saida -like '*Contagens identicas*'))
    Conferir 'os anexos restaurados em pasta nova tem exatamente os SHA-256 da origem (5 objetos, nenhum .parcial)' ((Test-MapasIguais (Get-MapaDeHashes -Raiz $destinoDosAnexos) $hashesDaOrigem) -and (@(Get-ChildItem $destinoDosAnexos -Recurse -File).Count -eq 5))
    $contagem = @(Invoke-Psql -Conexao $outra.Conexao -Banco 'restaurado_com_anexos' -Sql 'SELECT count(*) FROM identity.users;')
    Conferir 'o banco restaurado junto tem os 200 usuarios' ([int]$contagem[0] -eq 200)
    $arquivosAntes = @(Get-ChildItem $destinoDosAnexos -Recurse -File).Count
    $rs = Invoke-Script 'Restaurar-Backup.ps1' @('-Pasta', $pastaComAnexos, '-BancoDeDestino', 'restaurado_recusado', '-Origem', 'Ambiente', '-VariavelDeConexao', 'IABIMETA_T_DESTINO', '-PastaDeAnexosDestino', $destinoDosAnexos, '-PostgresBin', $PostgresBin)
    $existe = @(Invoke-Psql -Conexao $outra.Conexao -Banco 'postgres' -Sql "SELECT count(*) FROM pg_database WHERE datname = 'restaurado_recusado';")
    Conferir 'destino de anexos existente e nao vazio: RECUSADO (codigo 1) antes de criar o banco, sem alterar nem apagar nada' (($rs.Codigo -eq 1) -and ($rs.Saida -like '*NAO esta vazio*') -and ([int]$existe[0] -eq 0) -and (@(Get-ChildItem $destinoDosAnexos -Recurse -File).Count -eq $arquivosAntes))
    $rs = Invoke-Script 'Restaurar-Backup.ps1' @('-Pasta', $pastaComAnexos, '-BancoDeDestino', 'restaurado_so_banco', '-Origem', 'Ambiente', '-VariavelDeConexao', 'IABIMETA_T_DESTINO', '-PostgresBin', $PostgresBin)
    Conferir 'sem -PastaDeAnexosDestino: restaura so o banco (codigo 0) e AVISA claramente que os anexos NAO foram restaurados e como fazer' (($rs.Codigo -eq 0) -and ($rs.Saida -like '*AVISO*NAO foram restaurados*') -and ($rs.Saida -like '*Restaurar-Anexos.ps1*'))
    $soArquivos = Join-Path $raiz 'anexos-so-arquivos'
    $rs = Invoke-Script 'Restaurar-Anexos.ps1' @('-Pasta', $pastaComAnexos, '-Destino', $soArquivos, '-PostgresBin', $PostgresBin)
    Conferir 'Restaurar-Anexos restaura so os arquivos em pasta nova, com os SHA-256 da origem' (($rs.Codigo -eq 0) -and (Test-MapasIguais (Get-MapaDeHashes -Raiz $soArquivos) $hashesDaOrigem))
    $rs = Invoke-Script 'Restaurar-Anexos.ps1' @('-Pasta', $pastaComAnexos, '-Destino', $soArquivos, '-PostgresBin', $PostgresBin)
    Conferir 'Restaurar-Anexos recusa destino existente e nao vazio (codigo 1)' (($rs.Codigo -eq 1) -and ($rs.Saida -like '*NAO esta vazio*'))
    $rs = Invoke-Script 'Restaurar-Anexos.ps1' @('-Pasta', $pastaSemAnexos, '-Destino', (Join-Path $raiz 'sem-anexos-nenhum'), '-PostgresBin', $PostgresBin)
    Conferir 'Restaurar-Anexos de um backup sem anexos e recusado (codigo 1)' (($rs.Codigo -eq 1) -and ($rs.Saida -like '*nao leva anexos*'))
    $vazio = Join-Path $raiz 'destino-ja-vazio'
    New-Item -ItemType Directory -Path $vazio | Out-Null
    $ok = Restore-Anexos -Pasta $pastaComAnexos -Destino $vazio
    Conferir 'um destino que ja existe mas esta VAZIO e aceito' (($ok.Objetos -eq 5) -and (Test-MapasIguais (Get-MapaDeHashes -Raiz $vazio) $hashesDaOrigem))
    $recusa = $null
    try { Restore-Anexos -Pasta $pastaComAnexos -Destino (Join-Path $pastaComAnexos 'dentro') | Out-Null } catch { $recusa = $_.Exception.Message }
    Conferir 'um destino dentro da propria pasta do backup e recusado' ($recusa -like '*dentro da pasta do proprio backup*')

    # 11. Perda da pasta original: restaurar SO do backup
    Write-Secao '11. Perda da pasta original: os anexos voltam so do backup'
    $perda = Suspend-PastaDeAnexosDeOrigem -Pasta $origemDosAnexos
    try {
        $origemSumiu = -not (Test-Path $origemDosAnexos)
        $semOrigem = Join-Path $raiz 'restaurado-sem-origem'
        $restaurado = Restore-Anexos -Pasta $pastaComAnexos -Destino $semOrigem
        $hashesSemOrigem = Get-MapaDeHashes -Raiz $semOrigem
    }
    finally { Resume-PastaDeAnexosDeOrigem -Perda $perda }
    Conferir 'com a pasta original ausente (renomeada), os 5 anexos sao restaurados so do backup, com os SHA-256 registrados antes da perda' ($origemSumiu -and ($restaurado.Objetos -eq 5) -and (Test-MapasIguais $hashesSemOrigem $hashesDaOrigem))
    Conferir 'a pasta original foi devolvida intacta (nada foi apagado)' ((Test-Path $origemDosAnexos) -and (Test-MapasIguais (Get-MapaDeHashes -Raiz $origemDosAnexos) $hashesDaOrigem))
    $r = Invoke-Script 'Ensaiar-Restauracao.ps1' @('-Origem', 'Ambiente', '-VariavelDeConexao', 'IABIMETA_T_CONEXAO', '-PastaDeBackups', $pastaDeBackups, '-PostgresBin', $PostgresBin, '-PastaDeAnexos', $origemDosAnexos, '-SimularPerdaDaOrigem')
    Conferir 'o ensaio com -SimularPerdaDaOrigem APROVA (codigo 0): banco e anexos restaurados so do backup e conferidos contra o manifesto e a origem' (($r.Codigo -eq 0) -and ($r.Saida -like '*ENSAIO APROVADO*') -and ($r.Saida -like '*Anexos: 5 objeto(s)*') -and ($r.Saida -like '*com a pasta de origem ausente durante a restauracao*'))
    Conferir 'depois do ensaio a origem esta de volta, nada ficou renomeado e a pasta temporaria dos anexos foi apagada' ((Test-Path $origemDosAnexos) -and (Test-MapasIguais (Get-MapaDeHashes -Raiz $origemDosAnexos) $hashesDaOrigem) -and (@(Get-ChildItem $raiz -Directory | Where-Object { $_.Name -like '*.perdida-ensaio-*' }).Count -eq 0) -and (@(Get-ChildItem $pastaDeBackups -Directory | Where-Object { $_.Name -like '_ensaio-anexos-*' -or $_.Name -match '^_instancia-ensaio-\d{14}$' }).Count -eq 0))
    $r = Invoke-Script 'Ensaiar-Restauracao.ps1' @('-Origem', 'Ambiente', '-VariavelDeConexao', 'IABIMETA_T_CONEXAO', '-PastaDeBackups', $pastaDeBackups, '-PostgresBin', $PostgresBin, '-SimularPerdaDaOrigem')
    Conferir '-SimularPerdaDaOrigem sem -PastaDeAnexos e recusado (codigo 2)' (($r.Codigo -eq 2) -and ($r.Saida -like '*exige -PastaDeAnexos*'))

    # 12. Backup agendado: cifra, anexo em texto claro e copia externa
    Write-Secao '12. Backup agendado com anexos: cifra, texto claro, copia externa, ensaio, versoes'
    $versoesAntes = @(Get-ChildItem $pastaDeBackups -Directory -Filter 'agendado-*').Count
    $mensagemSemCifra = Get-MensagemDeAnexosSemCifra -Quantidade 2
    $r = Invoke-Script 'Backup-Agendado.ps1' ($base + @('-ChaveEnv', 'IABIMETA_T_CHAVE', '-PastaDeAnexos', $origemDosAnexos))
    $estado = Get-EstadoAtual
    $alerta = Join-Path $pastaDeBackups 'ALERTA-BACKUP.txt'
    Conferir 'backup cifrado COM anexo legado em texto claro FALHA (codigo 1) com a mensagem do attachments encrypt-existing' (($r.Codigo -eq 1) -and $r.Saida.Contains($mensagemSemCifra) -and $mensagemSemCifra.Contains("'attachments encrypt-existing --confirm'"))
    Conferir 'o alerta (arquivo) traz a mensagem e o estado registra a falha na etapa anexos-sem-cifra' ((Test-Path $alerta) -and (Get-Content -Raw $alerta).Contains($mensagemSemCifra) -and ($estado.resultado -eq 'falha') -and $estado.mensagemDaFalha.StartsWith('[anexos-sem-cifra]'))
    Conferir 'a recusa vem antes do dump: nenhuma versao nova foi criada' (@(Get-ChildItem $pastaDeBackups -Directory -Filter 'agendado-*').Count -eq $versoesAntes)
    $antesDoExterno = @(Get-ChildItem $externoCifrado).Count
    $r = Invoke-Script 'Backup-Agendado.ps1' ($base + @('-DestinoExterno', $externoCifrado, '-PastaDeAnexos', $origemDosAnexos))
    Conferir 'destino externo com anexo em texto claro (sem -PermitirExternoSemCriptografia) FALHA com a mesma mensagem e nada e copiado para fora' (($r.Codigo -eq 1) -and $r.Saida.Contains($mensagemSemCifra) -and (@(Get-ChildItem $externoCifrado).Count -eq $antesDoExterno))
    Start-Sleep -Seconds 1
    $r = Invoke-Script 'Backup-Agendado.ps1' ($base + @('-DestinoExterno', $externo, '-PermitirExternoSemCriptografia', '-PastaDeAnexos', $origemDosAnexos))
    $estado = Get-EstadoAtual
    Conferir 'com -PermitirExternoSemCriptografia (decisao explicita) o backup com anexo legado segue, com AVISO de texto claro, e o estado registra os anexos' (($r.Codigo -eq 0) -and ($r.Saida -like '*AVISO*texto claro*') -and ($estado.anexosObjetos -eq 5) -and ($estado.anexosLegadosSemCifra -eq 2) -and ($estado.anexosCifradosNaOrigem -eq $false) -and ($estado.cifrado -eq $false))
    Conferir 'o sucesso apaga o alerta' (-not (Test-Path $alerta))

    # So anexos cifrados na origem: sucesso, sem cifra dupla, copia externa sem nenhum texto claro de anexo, ensaio que restaura os anexos
    $origemCifrada = Join-Path $raiz 'anexos-origem-cifrados'
    $tenantC = Get-Hex32
    $tenantD = Get-Hex32
    [void](New-AnexoSintetico -Raiz $origemCifrada -Tenant $tenantC -Tamanho 8000 -Cifrado $true)
    [void](New-AnexoSintetico -Raiz $origemCifrada -Tenant $tenantC -Tamanho 1500000 -Cifrado $true)
    [void](New-AnexoSintetico -Raiz $origemCifrada -Tenant $tenantC -Tamanho 90 -Cifrado $true)
    [void](New-AnexoSintetico -Raiz $origemCifrada -Tenant $tenantD -Tamanho 4096 -Cifrado $true)
    [IO.File]::WriteAllText((Join-Path (Join-Path $origemCifrada $tenantC) ((Get-Hex32) + '.bin.parcial')), 'gravacao em andamento')
    $hashesCifrados = Get-MapaDeHashes -Raiz $origemCifrada
    $totalCifrado = 8000 + 1500000 + 90 + 4096
    # O ensaio so roda se estiver "devido": envelhece o ultimo ensaio registrado no estado (o arquivo de estado e do proprio teste).
    $arquivoDeEstado = Join-Path $pastaDeBackups 'estado-backup.json'
    $estadoEditavel = Get-Content -Raw $arquivoDeEstado | ConvertFrom-Json
    $estadoEditavel.ultimoEnsaioUtc = (Get-Date).ToUniversalTime().AddDays(-30).ToString('yyyy-MM-ddTHH:mm:ssZ')
    Write-ArquivoUtf8 -Caminho $arquivoDeEstado -Texto (($estadoEditavel | ConvertTo-Json -Depth 6) + "`n")
    $arquivoDeConfiguracaoComAnexos = Join-Path $raiz 'config-anexos.json'
    $json = '{ "origem": "Ambiente", "variavelDeConexao": "IABIMETA_T_CONEXAO", "pastaDeBackups": "' + $pastaDeBackups.Replace('\', '\\') + '", "pastaDeAnexos": "' + $origemCifrada.Replace('\', '\\') + '", "chaveEnv": "IABIMETA_T_CHAVE", "destinoExterno": "' + $externoCifrado.Replace('\', '\\') + '", "ensaiarRestauracaoACadaDias": 1 }'
    Write-ArquivoUtf8 -Caminho $arquivoDeConfiguracaoComAnexos -Texto $json
    Start-Sleep -Seconds 1
    $r = Invoke-Script 'Backup-Agendado.ps1' @('-ArquivoDeConfiguracao', $arquivoDeConfiguracaoComAnexos, '-PostgresBin', $PostgresBin)
    $estado = Get-EstadoAtual
    Conferir 'so com anexos cifrados na origem + chave + destino externo + ensaio (configuracao por arquivo com pastaDeAnexos): SUCESSO (codigo 0)' (($r.Codigo -eq 0) -and ($estado.resultado -eq 'sucesso') -and ($estado.cifrado -eq $true))
    Conferir 'o estado registra objetos e bytes dos anexos e que estao cifrados na origem (sem segredo)' (($estado.anexosObjetos -eq 4) -and ($estado.anexosTamanhoEmBytes -eq $totalCifrado) -and ($estado.anexosCifradosNaOrigem -eq $true) -and ($estado.anexosLegadosSemCifra -eq 0))
    Conferir 'o ensaio de restauracao rodou, restaurou os 4 anexos numa pasta nova e aprovou' (($r.Saida -like '*Ensaio de restauracao aprovado*4 anexo(s) restaurados*') -and ($estado.ultimoEnsaioAprovado -eq $true))
    $versaoLocal = Join-Path $pastaDeBackups $estado.pastaDoUltimoBackup
    $versaoExterna = Join-Path $externoCifrado $estado.pastaDoUltimoBackup
    $mapaLocal = Get-MapaDeHashes -Raiz (Join-Path $versaoLocal 'anexos')
    Conferir 'NAO ha cifra dupla: os anexos do backup local sao byte a byte os da origem (ja cifrados, cabecalho IABA)' ((Test-MapasIguais $mapaLocal $hashesCifrados) -and (@((Get-ObjetosDeAnexos -Raiz (Join-Path $versaoLocal 'anexos')).Objetos | Where-Object { -not $_.Cifrado }).Count -eq 0))
    $manifestoLocal = Get-Content -Raw (Join-Path $versaoLocal 'manifesto.json') | ConvertFrom-Json
    Conferir 'o bloco anexos sobrevive a cifra do dump (o manifesto.json reescrito continua registrando os anexos e o backup e cifrado)' ((Test-ManifestoCifrado -Manifesto $manifestoLocal) -and ($manifestoLocal.anexos.objetos -eq 4) -and (@(Get-ChildItem $versaoLocal -Filter '*.dump').Count -eq 0))
    Conferir 'a copia externa tem anexos\ e anexos-manifesto.json, com os mesmos bytes da origem' ((Test-Path (Join-Path $versaoExterna 'anexos-manifesto.json')) -and (Test-MapasIguais (Get-MapaDeHashes -Raiz (Join-Path $versaoExterna 'anexos')) $hashesCifrados))
    $comTextoClaro = 0
    $semCabecalho = 0
    foreach ($arquivo in (Get-ChildItem $externoCifrado -Recurse -File)) {
        if ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($arquivo.FullName)).Contains($marcadorDeTextoClaro)) { $comTextoClaro++ }
        if ($arquivo.FullName -like '*\anexos\*.bin' -and -not (Test-AnexoCifrado -Caminho $arquivo.FullName)) { $semCabecalho++ }
    }
    Conferir 'SEM texto claro de anexo na copia externa: nenhum arquivo com o marcador de texto claro e todo objeto copiado comeca com IABA' (($comTextoClaro -eq 0) -and ($semCabecalho -eq 0))
    $v = Invoke-Script 'Verificar-Backup.ps1' @('-Pasta', $versaoExterna, '-ChaveEnv', 'IABIMETA_T_CHAVE', '-PostgresBin', $PostgresBin)
    Conferir 'a copia externa verifica por completo, anexos inclusive' (($v.Codigo -eq 0) -and ($v.Saida -like '*Anexos integros: 4 objeto(s)*'))
    Conferir 'nao sobrou pasta temporaria de anexos do ensaio nem de decifragem' ((@(Get-ChildItem $pastaDeBackups -Directory | Where-Object { $_.Name -like '_ensaio-anexos-*' }).Count -eq 0) -and (@(Get-ChildItem $pastaDeBackups -Recurse -Directory -Filter '_tmp-*').Count -eq 0))

    # Defesa em profundidade: mesmo chamada direto, a copia externa recusa anexo em texto claro quando a cifra e exigida
    $mistura = Join-Path $raiz 'mistura'
    Copy-Item -Recurse -Path $versao -Destination $mistura
    $resumoMistura = Backup-Anexos -Raiz $origemDosAnexos -Destino $mistura
    $manifestoMistura = Get-Content -Raw (Join-Path $mistura 'manifesto.json') | ConvertFrom-Json
    Add-Member -InputObject $manifestoMistura -NotePropertyName anexos -NotePropertyValue ([pscustomobject]@{ manifesto = 'anexos-manifesto.json'; objetos = $resumoMistura.Objetos; tamanhoTotalEmBytes = $resumoMistura.TamanhoTotalEmBytes; sha256DoManifesto = $resumoMistura.Sha256DoManifesto })
    Write-ArquivoUtf8 -Caminho (Join-Path $mistura 'manifesto.json') -Texto (($manifestoMistura | ConvertTo-Json -Depth 6) + "`n")
    $recusa = $null
    try { Copy-ParaDestinoExterno -Pasta $mistura -DestinoExterno $externoCifrado | Out-Null } catch { $recusa = $_.Exception.Message }
    Conferir 'Copy-ParaDestinoExterno recusa anexo em texto claro mesmo com o dump cifrado, e nada e copiado' (($recusa -like '*sem cifra em repouso*') -and (-not (Test-Path (Join-Path $externoCifrado 'mistura'))) -and (@(Get-ChildItem $externoCifrado | Where-Object { $_.Name -like 'mistura*' }).Count -eq 0))
    $liberada = Copy-ParaDestinoExterno -Pasta $mistura -DestinoExterno $externo -ExigirCriptografia $false
    Conferir 'com a exigencia desligada de forma explicita, a copia de anexos em texto claro segue (e e conferida)' ($liberada.Copiado -and (Test-Path (Join-Path (Join-Path $externo 'mistura') 'anexos-manifesto.json')) -and ((Test-AnexosDoBackup -Pasta (Join-Path $externo 'mistura') -Manifesto $manifestoMistura).Ok))

    # Politica de versoes: a pasta inteira da versao (anexos incluidos) sai junto, e nunca uma versao com menos de DiasMinimos
    $oficinaComAnexos = Join-Path $raiz 'versoes-com-anexos'
    foreach ($dias in 90, 80, 70, 60, 50, 1) {
        $nome = 'agendado-' + (Get-Date).AddDays(-$dias).ToString('yyyyMMdd-HHmmss')
        $pastaDoTenantDaVersao = Join-Path (Join-Path (Join-Path $oficinaComAnexos $nome) 'anexos') $tenantA
        New-Item -ItemType Directory -Force -Path $pastaDoTenantDaVersao | Out-Null
        Write-ArquivoUtf8 -Caminho (Join-Path $pastaDoTenantDaVersao ((Get-Hex32) + '.bin')) -Texto 'objeto'
        Write-ArquivoUtf8 -Caminho (Join-Path (Join-Path $oficinaComAnexos $nome) 'manifesto.json') -Texto '{}'
    }
    $removidas = @(Remove-VersoesAntigas -Base $oficinaComAnexos -ManterUltimos 3 -DiasMinimos 7)
    $restantes = @(Get-ChildItem $oficinaComAnexos -Directory)
    Conferir 'a politica remove a pasta inteira das 3 versoes antigas (os anexos vao junto) e preserva as 3 recentes com os anexos' (($removidas.Count -eq 3) -and ($restantes.Count -eq 3) -and (@($restantes | Where-Object { Test-Path (Join-Path $_.FullName 'anexos') }).Count -eq 3) -and (@(Get-ChildItem $oficinaComAnexos -Recurse -File -Filter '*.bin').Count -eq 3))
    $oficinaRecente = Join-Path $raiz 'versoes-recentes'
    foreach ($dias in 6, 5, 4, 3, 2, 1) {
        $nome = 'agendado-' + (Get-Date).AddDays(-$dias).ToString('yyyyMMdd-HHmmss')
        $pastaDoTenantDaVersao = Join-Path (Join-Path (Join-Path $oficinaRecente $nome) 'anexos') $tenantA
        New-Item -ItemType Directory -Force -Path $pastaDoTenantDaVersao | Out-Null
        Write-ArquivoUtf8 -Caminho (Join-Path $pastaDoTenantDaVersao ((Get-Hex32) + '.bin')) -Texto 'objeto'
        Write-ArquivoUtf8 -Caminho (Join-Path (Join-Path $oficinaRecente $nome) 'manifesto.json') -Texto '{}'
    }
    $removidasRecentes = @(Remove-VersoesAntigas -Base $oficinaRecente -ManterUltimos 3 -DiasMinimos 7)
    Conferir 'nunca apaga versao com menos de DiasMinimos dias (6 versoes de 1 a 6 dias, manter 3, minimo 7: nenhuma sai, anexos intactos)' (($removidasRecentes.Count -eq 0) -and (@(Get-ChildItem $oficinaRecente -Recurse -File -Filter '*.bin').Count -eq 6))

    # 13. Nenhum segredo em lugar nenhum
    Write-Secao '13. Nenhum segredo em log, estado, alerta, manifestos ou saida (anexos inclusive)'
    $texto = ''
    foreach ($pastaVarrida in $pastaDeBackups, $externo, $externoCifrado) {
        foreach ($arquivo in (Get-ChildItem $pastaVarrida -Recurse -File | Where-Object { $_.Extension -in '.log', '.json', '.txt' })) { $texto += (Get-Content -Raw $arquivo.FullName) }
    }
    $texto += $recebido
    Conferir 'a frase de cifra nao aparece' (-not $texto.Contains($fraseSecreta))
    Conferir 'a senha do banco nao aparece' (-not $texto.Contains($senhaDoBanco))
    Conferir 'a URL do webhook nao aparece' (-not $texto.Contains($urlSecreta))
    Conferir 'a string de conexao nao aparece' (-not $texto.Contains('Password='))
    $manifestosDeTodos = ''
    foreach ($pastaVarrida in $pastaDeBackups, $externo, $externoCifrado) {
        foreach ($arquivo in (Get-ChildItem $pastaVarrida -Recurse -File | Where-Object { $_.Name -in 'manifesto.json', 'anexos-manifesto.json' })) { $manifestosDeTodos += (Get-Content -Raw $arquivo.FullName) }
    }
    Conferir 'nenhum manifesto (principal ou de anexos) revela caminho de origem, string de conexao ou segredo' ((-not $manifestosDeTodos.Contains($raiz)) -and (-not $manifestosDeTodos.Contains($raiz.Replace('\', '\\'))) -and (-not $manifestosDeTodos.Contains($origemDosAnexos)) -and (-not $manifestosDeTodos.Contains($origemCifrada)) -and (-not $manifestosDeTodos.Contains($fraseSecreta)) -and (-not $manifestosDeTodos.Contains('Password=')))
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
Write-Host ('Verificacoes executadas: ' + $verificacoes + '; falharam: ' + $falhas + '; tempo total: ' + [int]$cronometroDoTeste.Elapsed.TotalSeconds + ' s.')
if ($falhas -eq 0) { Write-Host 'AUTOMACAO DE BACKUP: TODOS OS TESTES PASSARAM.' } else { Write-Host ('AUTOMACAO DE BACKUP: ' + $falhas + ' TESTE(S) FALHARAM.') }
exit $(if ($falhas -eq 0) { 0 } else { 1 })
