# Funcoes da automacao de backup (ADR 0026): cifrar um backup, estado e alerta de falha, copia para destino externo e retencao de versoes.
# Nao e executado sozinho: os scripts fazem "dot-source" (. .\_Backup.ps1), que ja carrega _Comum.ps1 e _Cifra.ps1.
# So ASCII (PowerShell 5.1 le .ps1 sem BOM como ANSI). Nenhuma senha, frase de cifra, string de conexao ou URL de webhook e impressa ou gravada:
# so os NOMES das variaveis de ambiente aparecem em mensagens, e o texto de erro passa por Hide-Segredos antes de ir ao alerta ou ao estado.

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '_Comum.ps1')

$script:PadraoDaVersaoAgendada = '^agendado-\d{8}-\d{6}$'

function Write-ArquivoUtf8 {
    param([Parameter(Mandatory)][string]$Caminho, [Parameter(Mandatory)][string]$Texto)
    [IO.File]::WriteAllText($Caminho, $Texto, (New-Object System.Text.UTF8Encoding($false)))
}

# Tira do texto qualquer valor das variaveis de ambiente citadas (frase de cifra, URL do webhook, conexao) e senhas de string de conexao.
function Hide-Segredos {
    param([string]$Texto, [string[]]$NomesDeVariaveis = @())
    if ([string]::IsNullOrEmpty($Texto)) { return $Texto }
    $limpo = $Texto
    foreach ($nome in @($NomesDeVariaveis | Where-Object { $_ })) {
        $valor = [Environment]::GetEnvironmentVariable($nome)
        if (-not [string]::IsNullOrEmpty($valor) -and $valor.Length -ge 6) { $limpo = $limpo.Replace($valor, '***') }
    }
    $limpo = [regex]::Replace($limpo, '(?i)(password|pwd|senha)\s*=\s*[^;\s]+', '$1=***')
    $limpo = [regex]::Replace($limpo, '(?i)https?://[^\s''"]+', '[url]')
    return $limpo
}

# Cifra o dump de um backup: AES-256 + HMAC, confere a IDA E VOLTA (decifra para uma pasta temporaria e compara o SHA-256) ANTES de apagar o dump em
# texto claro, e reescreve o manifesto (arquivo, sha256 e tamanho passam a ser os do .enc; o dump original fica em "dump"). Devolve o manifesto novo.
function Protect-Backup {
    param([Parameter(Mandatory)][string]$Pasta, [Parameter(Mandatory)][string]$ChaveEnv)
    $caminhoDoManifesto = Join-Path $Pasta 'manifesto.json'
    $manifesto = Get-Content -Raw -Path $caminhoDoManifesto | ConvertFrom-Json
    if (Test-ManifestoCifrado -Manifesto $manifesto) { throw 'O backup ja esta cifrado.' }
    $original = Join-Path $Pasta $manifesto.arquivo
    $cifrado = $original + '.enc'

    $resultado = Protect-Arquivo -Origem $original -Destino $cifrado -ChaveEnv $ChaveEnv
    if ($resultado.Sha256DoOriginal -ne $manifesto.sha256) {
        Remove-Item -Force -Path $cifrado -ErrorAction SilentlyContinue
        throw 'O SHA-256 lido durante a cifra difere do manifesto: o dump mudou depois do backup.'
    }

    $temporaria = Join-Path $Pasta ('_tmp-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
    New-Item -ItemType Directory -Path $temporaria | Out-Null
    try {
        $volta = Unprotect-Arquivo -Origem $cifrado -Destino (Join-Path $temporaria 'volta.bin') -ChaveEnv $ChaveEnv
        if ($volta -ne $manifesto.sha256) { throw 'A decifragem de teste nao reproduziu o dump original.' }
    }
    catch {
        Remove-Item -Force -Path $cifrado -ErrorAction SilentlyContinue
        throw
    }
    finally { Remove-Item -Recurse -Force -Path $temporaria -ErrorAction SilentlyContinue }

    $dump = [pscustomobject]@{ arquivo = $manifesto.arquivo; sha256 = $manifesto.sha256; tamanhoEmBytes = $manifesto.tamanhoEmBytes }
    $manifesto.arquivo = $manifesto.arquivo + '.enc'
    $manifesto.sha256 = $resultado.Sha256DoCifrado
    $manifesto.tamanhoEmBytes = $resultado.TamanhoCifrado
    Add-Member -InputObject $manifesto -NotePropertyName dump -NotePropertyValue $dump -Force
    Add-Member -InputObject $manifesto -NotePropertyName cifrado -NotePropertyValue ([pscustomobject]@{ algoritmo = 'AES-256-CBC+HMAC-SHA256'; kdf = 'PBKDF2-SHA256'; iteracoes = 600000; formato = 'IABK1' }) -Force
    Write-ArquivoUtf8 -Caminho $caminhoDoManifesto -Texto (($manifesto | ConvertTo-Json -Depth 6) + "`n")

    # So agora, com a ida e volta provada e o manifesto gravado, o texto claro local sai.
    Remove-Item -Force -Path $original
    return $manifesto
}

# O estado do backup agendado (sem segredo): lido pelo Status-Ambiente.ps1. Gravado por um arquivo temporario e troca, para nunca ficar pela metade.
function Write-EstadoDoBackup {
    param([Parameter(Mandatory)][string]$Arquivo, [Parameter(Mandatory)]$Estado)
    $temporario = $Arquivo + '.tmp'
    Write-ArquivoUtf8 -Caminho $temporario -Texto (($Estado | ConvertTo-Json -Depth 6) + "`n")
    Move-Item -Force -Path $temporario -Destination $Arquivo
}

function Read-EstadoDoBackup {
    param([Parameter(Mandatory)][string]$Arquivo)
    if (-not (Test-Path $Arquivo)) { return $null }
    try { return (Get-Content -Raw -Path $Arquivo | ConvertFrom-Json) } catch { return $null }
}

# O alerta de falha: um arquivo ALERTA-BACKUP.txt na pasta de backups (some no proximo sucesso) e, se a variavel de ambiente do webhook estiver definida, um
# POST JSON {"text": ...} (Slack, Teams, ntfy e semelhantes). A URL vem SO da variavel de ambiente e nunca aparece em saida, log ou estado.
function Send-AlertaDeFalha {
    param([Parameter(Mandatory)][string]$PastaDeBackups, [Parameter(Mandatory)][string]$Etapa, [Parameter(Mandatory)][string]$Mensagem, [string]$WebhookEnv)
    $quando = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $texto = 'ALERTA ia.bimeta: o backup agendado FALHOU na etapa ' + $Etapa + ' em ' + $quando + ' (UTC), maquina ' + $env:COMPUTERNAME + '. ' + $Mensagem + ' Veja tools\ops\Status-Ambiente.ps1 e docs\operations\BACKUP-E-RESTAURACAO.md.'
    New-Item -ItemType Directory -Force -Path $PastaDeBackups | Out-Null
    Write-ArquivoUtf8 -Caminho (Join-Path $PastaDeBackups 'ALERTA-BACKUP.txt') -Texto ($texto + "`n")

    $webhook = 'nao configurado'
    if ($WebhookEnv) {
        $url = [Environment]::GetEnvironmentVariable($WebhookEnv)
        if ([string]::IsNullOrEmpty($url)) { $webhook = 'a variavel ' + $WebhookEnv + ' nao esta definida' }
        else {
            try {
                $corpo = [Text.Encoding]::UTF8.GetBytes(('{"text":' + (ConvertTo-Json -InputObject $texto) + '}'))
                Invoke-RestMethod -Method Post -Uri $url -ContentType 'application/json; charset=utf-8' -Body $corpo -TimeoutSec 15 | Out-Null
                $webhook = 'enviado'
            }
            catch { $webhook = 'FALHOU (' + $_.Exception.GetType().Name + ')' }
        }
    }
    return [pscustomobject]@{ Arquivo = (Join-Path $PastaDeBackups 'ALERTA-BACKUP.txt'); Webhook = $webhook }
}

function Remove-AlertaDeFalha {
    param([Parameter(Mandatory)][string]$PastaDeBackups)
    $arquivo = Join-Path $PastaDeBackups 'ALERTA-BACKUP.txt'
    if (Test-Path $arquivo) { Remove-Item -Force -Path $arquivo }
}

# Copia a pasta do backup (dump, ou dump cifrado, e manifesto) para uma pasta EXTERNA (outro disco, compartilhamento UNC, pasta sincronizada de nuvem), sem
# credencial alguma no script: quem autentica e o sistema operacional. Nunca sobrescreve (se a versao ja existe no destino, nao faz nada), copia para uma
# pasta .parcial e so renomeia depois de conferir o SHA-256, nunca cria a raiz do destino e, por padrao, recusa copiar backup SEM criptografia.
function Copy-ParaDestinoExterno {
    param([Parameter(Mandatory)][string]$Pasta, [Parameter(Mandatory)][string]$DestinoExterno, [bool]$ExigirCriptografia = $true)
    $manifesto = Get-Content -Raw -Path (Join-Path $Pasta 'manifesto.json') | ConvertFrom-Json
    if ($ExigirCriptografia -and -not (Test-ManifestoCifrado -Manifesto $manifesto)) {
        throw 'Recuso copiar para o destino externo um backup SEM criptografia (configure a chave de cifra ou use -PermitirExternoSemCriptografia).'
    }
    if (-not (Test-Path -PathType Container -Path $DestinoExterno)) { throw 'O destino externo nao existe ou nao e uma pasta (a raiz do destino nunca e criada pela ferramenta).' }

    $nome = Split-Path -Leaf $Pasta
    $final = Join-Path $DestinoExterno $nome
    if (Test-Path $final) { return [pscustomobject]@{ Copiado = $false; Motivo = 'a versao ja existe no destino (nao sobrescrevo)' } }

    $parcial = $final + '.parcial-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    New-Item -ItemType Directory -Path $parcial | Out-Null
    try {
        Copy-Item -Path (Join-Path $Pasta $manifesto.arquivo) -Destination $parcial
        Copy-Item -Path (Join-Path $Pasta 'manifesto.json') -Destination $parcial
        $copiado = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $parcial $manifesto.arquivo)).Hash
        if ($copiado -ne $manifesto.sha256) { throw 'O SHA-256 da copia externa difere do manifesto.' }
        Rename-Item -Path $parcial -NewName $nome
    }
    catch {
        # So a pasta parcial que ESTA execucao criou.
        if ((Split-Path -Leaf $parcial) -like ($nome + '.parcial-*')) { Remove-Item -Recurse -Force -Path $parcial -ErrorAction SilentlyContinue }
        throw
    }
    return [pscustomobject]@{ Copiado = $true; Motivo = 'copiado e conferido (SHA-256)' }
}

function Get-VersoesAgendadas {
    param([Parameter(Mandatory)][string]$Base)
    if (-not (Test-Path -PathType Container -Path $Base)) { return @() }
    # Versao marcada como INVALIDA (falhou no meio) nao conta como backup bom: nao entra na contagem nem na politica.
    return @(Get-ChildItem -Directory -Path $Base | Where-Object { $_.Name -match $script:PadraoDaVersaoAgendada -and (Test-Path (Join-Path $_.FullName 'manifesto.json')) -and -not (Test-Path (Join-Path $_.FullName 'INVALIDO.txt')) } | Sort-Object Name -Descending)
}

function Get-VersoesInvalidas {
    param([Parameter(Mandatory)][string]$Base)
    if (-not (Test-Path -PathType Container -Path $Base)) { return @() }
    return @(Get-ChildItem -Directory -Path $Base | Where-Object { $_.Name -match $script:PadraoDaVersaoAgendada -and (Test-Path (Join-Path $_.FullName 'INVALIDO.txt')) })
}

# A POLITICA de versoes: mantem as N mais recentes e NUNCA apaga uma versao com menos de DiasMinimos dias. So age em pastas "agendado-AAAAMMDD-HHMMSS" com
# manifesto, dentro de Base (as criadas pelo backup agendado), nunca em backups manuais. Sem N explicito (>= 3) nao ha politica e nada e apagado: quem chama
# so a executa quando a politica foi configurada, e so depois de um backup novo verificado. Devolve os nomes removidos (ou que seriam, com -SoSimular).
function Remove-VersoesAntigas {
    param([Parameter(Mandatory)][string]$Base, [Parameter(Mandatory)][int]$ManterUltimos, [int]$DiasMinimos = 7, [switch]$SoSimular)
    if ($ManterUltimos -lt 3) { throw 'A politica de versoes exige manter pelo menos 3 (ManterUltimos >= 3).' }
    $baseCompleta = [IO.Path]::GetFullPath($Base).TrimEnd('\')
    $todas = @(Get-VersoesAgendadas -Base $Base)
    $removidas = @()
    for ($i = $ManterUltimos; $i -lt $todas.Count; $i++) {
        $versao = $todas[$i]
        $criada = [datetime]::ParseExact($versao.Name.Substring(9), 'yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
        if (((Get-Date) - $criada).TotalDays -lt $DiasMinimos) { continue }
        $completo = [IO.Path]::GetFullPath($versao.FullName)
        if (-not $completo.StartsWith($baseCompleta + '\', [StringComparison]::OrdinalIgnoreCase) -or $versao.Name -notmatch $script:PadraoDaVersaoAgendada) { continue }
        if (-not $SoSimular) { Remove-Item -Recurse -Force -Path $completo }
        $removidas += $versao.Name
    }
    return $removidas
}

# Ensaia a restauracao de UM backup (cifrado ou nao) numa instancia PostgreSQL descartavel, sem tocar em banco algum. Devolve Aprovado, tempos e diferencas.
function Test-RestauracaoEmInstancia {
    param([Parameter(Mandatory)][string]$Pasta, [string]$ChaveEnv, [Parameter(Mandatory)][string]$PastaDeBackups)
    $marca = Get-Date -Format 'yyyyMMddHHmmss'
    $instancia = $null
    $dump = $null
    try {
        $manifesto = Test-IntegridadeDoBackup -Pasta $Pasta -ChaveEnv $ChaveEnv
        $dump = Get-DumpParaRestaurar -Pasta $Pasta -Manifesto $manifesto -ChaveEnv $ChaveEnv
        $instancia = New-InstanciaDescartavel -Pasta (Join-Path $PastaDeBackups ('_instancia-ensaio-' + $marca))
        $resultado = Restore-EmBancoNovo -Conexao $instancia.Conexao -ArquivoDoDump $dump.Arquivo -Manifesto $manifesto -BancoDeDestino ('iabimeta_ensaio_' + $marca)
        $aprovado = ($resultado.Diferencas.Count -eq 0) -and ($resultado.RestricoesNaoValidadas -eq 0)
        return [pscustomobject]@{ Aprovado = $aprovado; SegundosDoRestore = $resultado.SegundosDoRestore; Tabelas = $resultado.Tabelas; Diferencas = @($resultado.Diferencas); Mensagem = $(if ($aprovado) { 'restauracao conferida' } else { 'contagens ou restricoes diferentes do manifesto' }) }
    }
    finally {
        Remove-DumpTemporario -Dump $dump
        if ($null -ne $instancia) { try { Remove-InstanciaDescartavel -Instancia $instancia -PastaDeBackups $PastaDeBackups } catch { } }
    }
}
