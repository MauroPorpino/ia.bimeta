<#
.SYNOPSIS
    BACKUP AUTOMATICO do PostgreSQL do projeto (ADR 0026): faz o backup, verifica a integridade, cifra (opcional), copia para um destino externo (opcional),
    aplica a politica de versoes (opcional), ensaia a restauracao de tempos em tempos (opcional) e ALERTA se algo falhar. Feito para o Agendador de Tarefas
    (veja Registrar-Agendamento.ps1), mas roda a mao do mesmo jeito. Reaproveita Backup-Banco.ps1, Verificar-Backup e Restaurar-Backup.

.DESCRIPTION
    Cada execucao cria uma versao "agendado-AAAAMMDD-HHMMSS" na pasta de backups, com o dump (ou o dump CIFRADO, .enc) e o manifesto (SHA-256, tamanho,
    contagens). Fluxo e o que falha onde:
      1 backup       pg_dump -Fc, somente leitura no banco.
      2 verificacao  manifesto, tamanho, SHA-256 e leitura do dump (pg_restore --list).
      3 cifra        so com -ChaveEnv: AES-256 + HMAC (frase lida da variavel de ambiente, nunca impressa), confere a ida e volta ANTES de apagar o dump em
                     texto claro; depois da cifra so o .enc fica no disco.
      4 copia        so com -DestinoExterno: pasta de outro disco, compartilhamento UNC ou pasta de nuvem sincronizada. Sem credencial no script (quem autentica
                     e o Windows). Nunca sobrescreve, confere o SHA-256 e, por padrao, recusa copiar backup sem cifra.
      5 ensaio       so com -EnsaiarRestauracaoACadaDias: restaura numa instancia PostgreSQL descartavel e confere as contagens.
      6 versoes      so com -ManterUltimos (>= 3): mantem as N mais recentes (e nunca apaga uma com menos de -ManterDiasMinimos dias). Sem essa politica
                     explicita NADA e apagado. Backups manuais (outros rotulos) nunca entram. Na copia externa so com -ExternoManterUltimos.
    ANEXOS (opcional, -PastaDeAnexos ou "pastaDeAnexos" no JSON): a pasta privada de anexos (arquivos de midia do WhatsApp) entra no MESMO backup, DEPOIS do
    pg_dump (anexos\<tenant>\<arquivo>.bin + anexos-manifesto.json), e a verificacao, a copia externa, o ensaio de restauracao (que restaura os anexos numa pasta
    temporaria nova e confere o SHA-256 de cada objeto) e a politica de versoes passam a cobri-los. CIFRA: com -ChaveEnv (ou com destino externo, salvo
    -PermitirExternoSemCriptografia) TODO objeto precisa estar cifrado na ORIGEM (cabecalho IABA, feito pelo Api); havendo anexo em texto claro o backup FALHA com
    alerta e nada e copiado para fora. Objetos ja cifrados na origem sao copiados como estao (sem cifra dupla). A chave de cifra dos anexos e do Api/Worker:
    guarde-a fora da maquina junto da frase de cifra do backup; sem ela os anexos restaurados nao abrem.
    Resultado: estado-backup.json (sem segredo) e backup-agendado.log na pasta de backups. FALHA: sai com codigo 1 (o Agendador mostra), cria
    ALERTA-BACKUP.txt na pasta de backups (some no proximo sucesso) e, se -AlertaWebhookEnv apontar para uma variavel de ambiente com uma URL, faz um POST JSON
    {"text": ...}. Nenhuma senha, frase de cifra, string de conexao ou URL e gravada em log, estado, alerta ou saida: so os NOMES das variaveis.

    Tudo pode vir de um arquivo JSON (-ArquivoDeConfiguracao; veja backup-agendado.exemplo.json): o que for passado na linha de comando vale mais.
    Para a comparacao exata de contagens o banco deve estar em repouso durante o backup.

.EXAMPLE
    .\tools\ops\Backup-Agendado.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -ChaveEnv IABIMETA_BACKUP_KEY -DestinoExterno \\servidor\backups -ManterUltimos 14 -PastaDeAnexos D:\ia.bimeta\anexos
    .\tools\ops\Backup-Agendado.ps1 -ArquivoDeConfiguracao D:\config\backup-agendado.json
#>
[CmdletBinding()]
param(
    [string]$ArquivoDeConfiguracao,
    [ValidateSet('Desenvolvimento', 'Teste', 'Ambiente')][string]$Origem,
    [string]$VariavelDeConexao,
    [string]$PastaDeBackups = 'C:\Projetos\ferramentas\backups\ia.bimeta',

    # A pasta privada de anexos (Conversations:Attachments:RootPath) a copiar no mesmo backup. Opcional; o operador a informa.
    [string]$PastaDeAnexos,

    # O NOME da variavel de ambiente com a frase de cifra (>= 16 caracteres). Sem ela o backup NAO e cifrado.
    [string]$ChaveEnv,

    [string]$DestinoExterno,
    [switch]$PermitirExternoSemCriptografia,

    # A politica de versoes. Sem ela nada e apagado. 0 = sem politica.
    [int]$ManterUltimos = 0,
    [int]$ManterDiasMinimos = 7,
    [int]$ExternoManterUltimos = 0,

    # Ensaia a restauracao numa instancia descartavel a cada N dias (0 = nunca).
    [int]$EnsaiarRestauracaoACadaDias = 0,

    # O NOME da variavel de ambiente com a URL do webhook de alerta.
    [string]$AlertaWebhookEnv,

    # So mostra o que faria na politica de versoes (nenhuma pasta e apagada), alem do backup normal.
    [switch]$SoSimularVersoes,
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Backup.ps1')

# O arquivo de configuracao preenche o que NAO veio na linha de comando.
if ($ArquivoDeConfiguracao) {
    if (-not (Test-Path $ArquivoDeConfiguracao)) { Write-Host ('FALHA: arquivo de configuracao nao encontrado: ' + $ArquivoDeConfiguracao); exit 2 }
    try { $config = Get-Content -Raw -Path $ArquivoDeConfiguracao | ConvertFrom-Json } catch { Write-Host 'FALHA: o arquivo de configuracao nao e um JSON valido.'; exit 2 }
    $mapa = @{
        origem = 'Origem'; variavelDeConexao = 'VariavelDeConexao'; pastaDeBackups = 'PastaDeBackups'; chaveEnv = 'ChaveEnv'; destinoExterno = 'DestinoExterno'
        permitirExternoSemCriptografia = 'PermitirExternoSemCriptografia'; manterUltimos = 'ManterUltimos'; manterDiasMinimos = 'ManterDiasMinimos'
        externoManterUltimos = 'ExternoManterUltimos'; ensaiarRestauracaoACadaDias = 'EnsaiarRestauracaoACadaDias'; alertaWebhookEnv = 'AlertaWebhookEnv'
        pastaDeAnexos = 'PastaDeAnexos'
    }
    foreach ($propriedade in $config.PSObject.Properties) {
        if (-not $mapa.ContainsKey($propriedade.Name)) { Write-Host ('FALHA: a configuracao tem um campo desconhecido: ' + $propriedade.Name); exit 2 }
        $parametro = $mapa[$propriedade.Name]
        if (-not $PSBoundParameters.ContainsKey($parametro)) { Set-Variable -Name $parametro -Value $propriedade.Value }
    }
}
if (-not $Origem) { Write-Host 'FALHA: informe -Origem (Desenvolvimento, Teste ou Ambiente).'; exit 2 }
if ($ManterUltimos -ne 0 -and $ManterUltimos -lt 3) { Write-Host 'FALHA: ManterUltimos deve ser 0 (sem politica) ou pelo menos 3.'; exit 2 }
if ($ExternoManterUltimos -ne 0 -and $ExternoManterUltimos -lt 3) { Write-Host 'FALHA: ExternoManterUltimos deve ser 0 (sem politica) ou pelo menos 3.'; exit 2 }

$segredos = @($ChaveEnv, $AlertaWebhookEnv, $VariavelDeConexao)
$arquivoDeEstado = Join-Path $PastaDeBackups 'estado-backup.json'
$arquivoDeLog = Join-Path $PastaDeBackups 'backup-agendado.log'
New-Item -ItemType Directory -Force -Path $PastaDeBackups | Out-Null

function Write-Log {
    param([string]$Mensagem)
    $linha = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z ' + $Mensagem
    Write-Host $linha
    [IO.File]::AppendAllText($arquivoDeLog, $linha + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

# O log nao cresce sem fim: passando de 1 MB, fica so com as ultimas 500 linhas.
if ((Test-Path $arquivoDeLog) -and (Get-Item $arquivoDeLog).Length -gt 1MB) {
    $ultimas = @(Get-Content -Path $arquivoDeLog -Tail 500)
    Write-ArquivoUtf8 -Caminho $arquivoDeLog -Texto (($ultimas -join "`n") + "`n")
}

$anterior = Read-EstadoDoBackup -Arquivo $arquivoDeEstado
function Get-DoEstado {
    param([string]$Nome, $Padrao = $null)
    if ($null -ne $anterior -and $anterior.PSObject.Properties.Name -contains $Nome -and $null -ne $anterior.$Nome) { return $anterior.$Nome }
    return $Padrao
}

$inicio = Get-Date
$etapa = 'inicio'
$pasta = $null
$estado = [ordered]@{
    ultimaExecucaoUtc = $inicio.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    resultado = 'em-andamento'
    ultimoSucessoUtc = (Get-DoEstado 'ultimoSucessoUtc')
    ultimaFalhaUtc = (Get-DoEstado 'ultimaFalhaUtc')
    mensagemDaFalha = $null
    pastaDoUltimoBackup = (Get-DoEstado 'pastaDoUltimoBackup')
    tamanhoEmBytes = (Get-DoEstado 'tamanhoEmBytes')
    cifrado = (Get-DoEstado 'cifrado' $false)
    verificadoCompletamente = (Get-DoEstado 'verificadoCompletamente' $false)
    anexosObjetos = (Get-DoEstado 'anexosObjetos')
    anexosTamanhoEmBytes = (Get-DoEstado 'anexosTamanhoEmBytes')
    anexosCifradosNaOrigem = (Get-DoEstado 'anexosCifradosNaOrigem')
    anexosLegadosSemCifra = (Get-DoEstado 'anexosLegadosSemCifra')
    copiaExterna = (Get-DoEstado 'copiaExterna')
    ultimoEnsaioUtc = (Get-DoEstado 'ultimoEnsaioUtc')
    ultimoEnsaioAprovado = (Get-DoEstado 'ultimoEnsaioAprovado')
    versoesLocais = (Get-DoEstado 'versoesLocais' 0)
    duracaoEmSegundos = $null
}

try {
    Write-Log ('Backup agendado iniciado (origem ' + $Origem + ', cifra ' + $(if ($ChaveEnv) { 'ligada' } else { 'desligada' }) + ', copia externa ' + $(if ($DestinoExterno) { 'ligada' } else { 'desligada' }) + ', anexos ' + $(if ($PastaDeAnexos) { 'incluidos' } else { 'fora do backup' }) + ').')

    # Com cifra do backup, ou com destino externo (salvo a permissao explicita), nenhum anexo pode estar em texto claro: nem o local nem a copia externa carregam texto claro.
    $exigirCifraNosAnexos = [bool]($ChaveEnv -or ($DestinoExterno -and -not $PermitirExternoSemCriptografia))

    # Os anexos primeiro "de olho" (antes do dump, que e o mais demorado): a pasta existe e, se a cifra e exigida, todos os objetos estao cifrados na origem.
    if ($PastaDeAnexos) {
        $etapa = 'anexos-sem-cifra'
        if (-not (Test-Path -PathType Container -LiteralPath $PastaDeAnexos)) { throw 'A pasta de anexos (-PastaDeAnexos ou pastaDeAnexos) nao existe ou nao e uma pasta.' }
        if ($exigirCifraNosAnexos) {
            $achados = Get-ObjetosDeAnexos -Raiz $PastaDeAnexos
            $semCifra = @($achados.Objetos | Where-Object { -not $_.Cifrado }).Count
            if ($semCifra -gt 0) { throw (Get-MensagemDeAnexosSemCifra -Quantidade $semCifra) }
            Write-Log ('Anexos conferidos na origem: ' + @($achados.Objetos).Count + ' objeto(s), todos cifrados na origem.')
        }
    }

    # Espaco: o backup, a cifra (um segundo arquivo) e a verificacao (um terceiro, temporario) cabem? Os anexos entram uma vez (copia sem cifra nova);
    # o ensaio de restauracao, quando devido, restaura os anexos numa pasta temporaria (mais uma vez).
    $etapa = 'espaco-em-disco'
    $ultimoTamanho = [long](Get-DoEstado 'tamanhoEmBytes' 0)
    if ($ultimoTamanho -gt 0) {
        $livre = $null
        $raiz = [IO.Path]::GetPathRoot((Resolve-Path -Path $PastaDeBackups).ProviderPath)
        if ($raiz -match '^[A-Za-z]:\\') { $livre = (New-Object System.IO.DriveInfo($raiz)).AvailableFreeSpace }
        $necessario = [long]($ultimoTamanho * $(if ($ChaveEnv) { 3.5 } else { 1.5 }))
        if ($PastaDeAnexos) { $necessario += [long](Get-DoEstado 'anexosTamanhoEmBytes' 0) * $(if ($EnsaiarRestauracaoACadaDias -gt 0) { 2 } else { 1 }) }
        if ($null -ne $livre -and $livre -lt $necessario) { throw ('Espaco livre insuficiente na pasta de backups: ' + [Math]::Round($livre / 1MB) + ' MB livres, ' + [Math]::Round($necessario / 1MB) + ' MB necessarios.') }
    }

    $etapa = 'backup'
    $argumentos = @{ Origem = $Origem; Rotulo = 'agendado'; PastaDeBackups = $PastaDeBackups; PostgresBin = $PostgresBin }
    if ($VariavelDeConexao) { $argumentos['VariavelDeConexao'] = $VariavelDeConexao }
    if ($PastaDeAnexos) { $argumentos['PastaDeAnexos'] = $PastaDeAnexos }
    $pasta = & (Join-Path $PSScriptRoot 'Backup-Banco.ps1') @argumentos | Select-Object -Last 1
    if ([string]::IsNullOrEmpty($pasta) -or -not (Test-Path $pasta)) { throw 'O backup nao devolveu a pasta da nova versao.' }
    Write-Log ('Backup feito em ' + (Split-Path -Leaf $pasta) + '.')

    $etapa = 'verificacao'
    $manifesto = Test-IntegridadeDoBackup -Pasta $pasta
    Write-Log ('Integridade conferida (SHA-256, tamanho e leitura do dump; ' + $manifesto.entradasDoDump + ' entradas, ' + @($manifesto.contagens).Count + ' tabelas).')
    $anexos = $null
    if ($PastaDeAnexos) {
        $etapa = 'verificacao-dos-anexos'
        $anexos = Test-AnexosDoBackup -Pasta $pasta -Manifesto $manifesto
        if (-not $anexos.TemAnexos) { throw 'O backup nao registrou os anexos no manifesto.json.' }
        if (-not $anexos.Ok) { throw ('Os anexos do backup nao passaram na verificacao: ' + (Join-ErrosDeAnexos -Erros $anexos.Erros)) }
        Write-Log ('Anexos conferidos (SHA-256 e tamanho de cada arquivo; ' + $anexos.Objetos + ' objeto(s), ' + [Math]::Round($anexos.TamanhoTotalEmBytes / 1MB, 2) + ' MB; ' + $anexos.Manifesto.cifradosNaOrigem + ' cifrado(s) na origem, ' + $anexos.Manifesto.legadosSemCifra + ' em texto claro).')
        # A decisao final e sobre o que FOI copiado (a origem pode ter mudado desde a conferencia de antes do dump): texto claro nunca fica num backup cifrado nem sai da maquina.
        if ($exigirCifraNosAnexos -and [long]$anexos.Manifesto.legadosSemCifra -gt 0) { $etapa = 'anexos-sem-cifra'; throw (Get-MensagemDeAnexosSemCifra -Quantidade ([long]$anexos.Manifesto.legadosSemCifra)) }
        if ([long]$anexos.Manifesto.legadosSemCifra -gt 0) { Write-Log ('AVISO: ' + $anexos.Manifesto.legadosSemCifra + ' anexo(s) em texto claro no backup local (sem cifra em repouso na origem); o backup local nao sai da maquina.') }
        if ($anexos.Objetos -eq 0) { Write-Log 'AVISO: a pasta de anexos nao tem nenhum objeto. Confira se -PastaDeAnexos e a pasta certa.' }
    }

    if ($ChaveEnv) {
        $etapa = 'cifra'
        $manifesto = Protect-Backup -Pasta $pasta -ChaveEnv $ChaveEnv
        Write-Log ('Backup cifrado e verificado por completo (autenticacao, decifragem e leitura do dump, numa unica ida e volta); o dump em texto claro foi removido.' + $(if ($PastaDeAnexos) { ' Os anexos ja cifrados na origem foram copiados como estao (sem cifra dupla).' } else { '' }))
    }

    $estado.pastaDoUltimoBackup = (Split-Path -Leaf $pasta)
    $estado.tamanhoEmBytes = [long]$manifesto.tamanhoEmBytes
    if ($ChaveEnv) { $estado.cifrado = $true } else { $estado.cifrado = $false }
    $estado.verificadoCompletamente = [bool]$manifesto.VerificacaoCompleta
    if ($null -ne $anexos) {
        $estado.anexosObjetos = [long]$anexos.Objetos
        $estado.anexosTamanhoEmBytes = [long]$anexos.TamanhoTotalEmBytes
        $estado.anexosLegadosSemCifra = [long]$anexos.Manifesto.legadosSemCifra
        $estado.anexosCifradosNaOrigem = ([long]$anexos.Manifesto.legadosSemCifra -eq 0)
    }
    else {
        $estado.anexosObjetos = $null
        $estado.anexosTamanhoEmBytes = $null
        $estado.anexosLegadosSemCifra = $null
        $estado.anexosCifradosNaOrigem = $null
    }

    if ($DestinoExterno) {
        $etapa = 'copia-externa'
        $copia = Copy-ParaDestinoExterno -Pasta $pasta -DestinoExterno $DestinoExterno -ExigirCriptografia (-not $PermitirExternoSemCriptografia)
        Write-Log ('Copia externa: ' + $copia.Motivo + '.')
        $estado.copiaExterna = [ordered]@{ ultimaCopiaUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'); versao = (Split-Path -Leaf $pasta); copiada = $copia.Copiado }
    }

    if ($EnsaiarRestauracaoACadaDias -gt 0) {
        $etapa = 'ensaio-de-restauracao'
        $ultimoEnsaio = Get-DoEstado 'ultimoEnsaioUtc'
        $devido = $true
        if ($ultimoEnsaio) { $devido = (((Get-Date).ToUniversalTime() - ([datetime]::Parse($ultimoEnsaio, [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime())).TotalDays -ge $EnsaiarRestauracaoACadaDias) }
        if ($devido) {
            Write-Log 'Ensaiando a restauracao numa instancia descartavel ...'
            $ensaio = Test-RestauracaoEmInstancia -Pasta $pasta -ChaveEnv $ChaveEnv -PastaDeBackups $PastaDeBackups
            $estado.ultimoEnsaioUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
            $estado.ultimoEnsaioAprovado = $ensaio.Aprovado
            if (-not $ensaio.Aprovado) { throw ('O ensaio de restauracao REPROVOU: ' + $ensaio.Mensagem + (($ensaio.Diferencas | Select-Object -First 3) -join '; ') + '.') }
            Write-Log ('Ensaio de restauracao aprovado (' + $ensaio.SegundosDoRestore + ' s, ' + $ensaio.Tabelas + ' tabelas com as contagens do manifesto' + $(if ($ensaio.TemAnexos) { '; ' + $ensaio.Anexos + ' anexo(s) restaurados numa pasta nova com o SHA-256 do manifesto' } else { '' }) + ').')
        }
    }

    $etapa = 'politica-de-versoes'
    if ($ManterUltimos -ge 3) {
        $removidas = @(Remove-VersoesAntigas -Base $PastaDeBackups -ManterUltimos $ManterUltimos -DiasMinimos $ManterDiasMinimos -SoSimular:$SoSimularVersoes)
        Write-Log ('Politica de versoes locais (manter ' + $ManterUltimos + ', nunca menos de ' + $ManterDiasMinimos + ' dias): ' + $(if ($SoSimularVersoes) { 'removeria ' } else { 'removidas ' }) + $removidas.Count + $(if ($removidas.Count -gt 0) { ' (' + ($removidas -join ', ') + ')' } else { '' }) + '.')
    }
    else { Write-Log 'Sem politica de versoes configurada: nenhuma versao antiga foi apagada.' }
    if ($DestinoExterno -and $ExternoManterUltimos -ge 3) {
        $etapa = 'politica-de-versoes-externa'
        $removidasExternas = @(Remove-VersoesAntigas -Base $DestinoExterno -ManterUltimos $ExternoManterUltimos -DiasMinimos $ManterDiasMinimos -SoSimular:$SoSimularVersoes)
        Write-Log ('Politica de versoes externas (manter ' + $ExternoManterUltimos + '): ' + $(if ($SoSimularVersoes) { 'removeria ' } else { 'removidas ' }) + $removidasExternas.Count + '.')
    }
    $estado.versoesLocais = @(Get-VersoesAgendadas -Base $PastaDeBackups).Count

    $estado.resultado = 'sucesso'
    $estado.ultimoSucessoUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $estado.duracaoEmSegundos = [Math]::Round(((Get-Date) - $inicio).TotalSeconds, 1)
    Write-EstadoDoBackup -Arquivo $arquivoDeEstado -Estado $estado
    Remove-AlertaDeFalha -PastaDeBackups $PastaDeBackups
    Write-Log ('Backup agendado concluido em ' + $estado.duracaoEmSegundos + ' s: ' + (Split-Path -Leaf $pasta) + ', ' + [Math]::Round($estado.tamanhoEmBytes / 1MB, 1) + ' MB' + $(if ($estado.cifrado) { ', cifrado' } else { ', SEM cifra' }) + $(if ($null -ne $anexos) { '; anexos: ' + $estado.anexosObjetos + ' objeto(s), ' + [Math]::Round($estado.anexosTamanhoEmBytes / 1MB, 1) + ' MB' } else { '' }) + '.')
    exit 0
}
catch {
    $mensagem = Hide-Segredos -Texto $_.Exception.Message -NomesDeVariaveis $segredos
    $estado.resultado = 'falha'
    $estado.ultimaFalhaUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $estado.mensagemDaFalha = ('[' + $etapa + '] ' + $mensagem)
    $estado.duracaoEmSegundos = [Math]::Round(((Get-Date) - $inicio).TotalSeconds, 1)
    try { Write-EstadoDoBackup -Arquivo $arquivoDeEstado -Estado $estado } catch { Write-Host 'ATENCAO: nao foi possivel gravar o estado do backup.' }
    Write-Log ('FALHA na etapa ' + $etapa + ': ' + $mensagem)

    # A versao que nasceu e falhou fica marcada como INVALIDA (nunca conta como backup bom e nunca e apagada sozinha: quem opera a examina e remove).
    if ($pasta -and (Test-Path $pasta)) {
        try { Write-ArquivoUtf8 -Caminho (Join-Path $pasta 'INVALIDO.txt') -Texto ('Versao marcada como INVALIDA: falhou na etapa ' + $etapa + '. ' + $mensagem + "`n") } catch { }
    }
    try {
        $alerta = Send-AlertaDeFalha -PastaDeBackups $PastaDeBackups -Etapa $etapa -Mensagem $mensagem -WebhookEnv $AlertaWebhookEnv
        Write-Log ('Alerta gravado em ' + $alerta.Arquivo + '; webhook: ' + $alerta.Webhook + '.')
    }
    catch { Write-Host 'ATENCAO: nao foi possivel gravar o alerta.' }
    exit 1
}
