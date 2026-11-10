<#
.SYNOPSIS
    BACKUP do PostgreSQL do projeto (pg_dump, formato custom) com manifesto de integridade. Somente leitura no banco.

.DESCRIPTION
    Gera, fora do repositorio, uma pasta <rotulo>-<data> com o dump (<banco>.dump) e o manifesto.json: SHA-256 e tamanho do
    arquivo, numero de entradas do dump (pg_restore --list), versao do pg_dump e a contagem exata de linhas por tabela
    no momento do backup. O manifesto nao contem senha nem string de conexao. A senha vai so numa variavel de ambiente
    DESTE processo, durante cada chamada. Use Verificar-Backup.ps1 para conferir o arquivo depois e Restaurar-Backup.ps1
    para restaurar (sempre em banco NOVO).

    Origens: Desenvolvimento (user-secret do Api), Teste (IABIMETA_TEST_CONNECTION ou user-secret dos testes) ou
    Ambiente (variavel de ambiente informada em -VariavelDeConexao, por exemplo a da homologacao).

    As contagens saem logo apos o dump: para comparacao exata na restauracao, o banco de origem deve estar parado ou em
    repouso (sem o Api/Worker gravando) durante o backup.

    -PastaDeAnexos (opcional): a pasta privada de anexos (Conversations:Attachments:RootPath, arquivos de midia do WhatsApp) e copiada para o MESMO backup
    (anexos\<tenant>\<arquivo>.bin + anexos-manifesto.json), e o manifesto.json ganha o bloco "anexos". ORDEM: primeiro o pg_dump, depois os arquivos: assim toda
    linha do dump tem objeto (salvo o que a retencao apagar no meio); objetos criados depois do dump sao orfaos inofensivos. Os objetos sao copiados como estao
    (os cifrados pelo Api continuam cifrados; esta ferramenta nunca ve a chave deles). Sem o parametro, o backup e o manifesto sao os de sempre.

.EXAMPLE
    .\tools\ops\Backup-Banco.ps1 -Origem Teste -Rotulo pre-migracao
    .\tools\ops\Backup-Banco.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -Rotulo homologacao -PastaDeAnexos D:\ia.bimeta\anexos
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Desenvolvimento', 'Teste', 'Ambiente')][string]$Origem,
    [string]$VariavelDeConexao,
    [string]$Rotulo = 'backup',
    [string]$PastaDeBackups = 'C:\Projetos\ferramentas\backups\ia.bimeta',

    # A pasta privada de anexos a copiar junto (opcional). O operador a informa: a ferramenta nao a descobre.
    [string]$PastaDeAnexos,
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Anexos.ps1')

if ($Rotulo -notmatch '^[A-Za-z0-9._-]{1,40}$') { throw 'O rotulo aceita so letras, numeros, ponto, hifen e sublinhado (ate 40).' }
# Falha ANTES do dump (que pode demorar) se a pasta de anexos nao existe: a copia dos anexos so acontece depois do dump, mas o erro de digitacao aparece ja.
if ($PastaDeAnexos -and -not (Test-Path -PathType Container -LiteralPath $PastaDeAnexos)) { throw ('A pasta de anexos nao existe (ou nao e uma pasta): ' + $PastaDeAnexos) }

$conexao = Get-ConexaoDeOrigem -Origem $Origem -VariavelDeConexao $VariavelDeConexao
$pasta = Join-Path $PastaDeBackups ($Rotulo + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $pasta | Out-Null
$arquivo = Join-Path $pasta ($conexao.Banco + '.dump')

$env:PGPASSWORD = $conexao.Senha
try {
    & (Join-Path $PostgresBin 'pg_dump.exe') -h $conexao.Servidor -p $conexao.Porta -U $conexao.Usuario -Fc -f $arquivo $conexao.Banco
    if ($LASTEXITCODE -ne 0) { throw ('pg_dump saiu com ' + $LASTEXITCODE) }
    $versao = (& (Join-Path $PostgresBin 'pg_dump.exe') --version)
}
finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }

$entradas = Get-EntradasDoDump -Arquivo $arquivo
$hash = (Get-FileHash -Algorithm SHA256 -Path $arquivo).Hash
$contagens = @(Get-ContagensDeTabelas -Conexao $conexao -Banco $conexao.Banco)
$criadoEm = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

# ANEXOS, depois do dump (ordem obrigatoria): toda linha do dump encontra o seu objeto, salvo o que a retencao apagar no meio da copia; objetos criados depois do dump
# sao orfaos inofensivos. Se a copia falhar, a pasta fica marcada INVALIDO.txt (sem manifesto.json ela tambem nao conta como backup) e nada e apagado.
$blocoDeAnexos = $null
$resumoDosAnexos = $null
if ($PastaDeAnexos) {
    try { $resumoDosAnexos = Backup-Anexos -Raiz $PastaDeAnexos -Destino $pasta }
    catch {
        $falha = $_
        try { Write-ArquivoUtf8 -Caminho (Join-Path $pasta 'INVALIDO.txt') -Texto ('Backup INCOMPLETO: a copia dos anexos falhou. ' + $falha.Exception.Message + "`n") } catch { }
        throw $falha
    }
    $blocoDeAnexos = [ordered]@{
        manifesto           = $resumoDosAnexos.Manifesto
        objetos             = $resumoDosAnexos.Objetos
        tamanhoTotalEmBytes = $resumoDosAnexos.TamanhoTotalEmBytes
        sha256DoManifesto   = $resumoDosAnexos.Sha256DoManifesto
    }
}

$manifesto = [ordered]@{
    versaoDoManifesto = 1
    rotulo            = $Rotulo
    criadoEmUtc       = $criadoEm
    banco             = $conexao.Banco
    servidor          = ($conexao.Servidor + ':' + $conexao.Porta)
    arquivo           = (Split-Path -Leaf $arquivo)
    tamanhoEmBytes    = (Get-Item $arquivo).Length
    sha256            = $hash
    pgDump            = $versao
    entradasDoDump    = $entradas
    contagens         = $contagens
}
if ($null -ne $blocoDeAnexos) { $manifesto['anexos'] = $blocoDeAnexos }
$json = $manifesto | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $pasta 'manifesto.json'), $json + "`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host ('Backup ok: ' + $arquivo)
Write-Host ('  tamanho ' + [Math]::Round((Get-Item $arquivo).Length / 1MB, 2) + ' MB, ' + $entradas + ' entradas, ' + $contagens.Count + ' tabelas')
Write-Host ('  sha256  ' + $hash)
if ($null -ne $resumoDosAnexos) {
    Write-Host ('  anexos  ' + $resumoDosAnexos.Objetos + ' objeto(s), ' + [Math]::Round($resumoDosAnexos.TamanhoTotalEmBytes / 1MB, 2) + ' MB, ' + $resumoDosAnexos.Tenants + ' tenant(s); ' + $resumoDosAnexos.CifradosNaOrigem + ' cifrado(s) na origem, ' + $resumoDosAnexos.LegadosSemCifra + ' em texto claro; ' + $resumoDosAnexos.Ignorados + ' arquivo(s) ignorado(s) (fora do formato, como *.parcial)')
    if ($resumoDosAnexos.Objetos -eq 0) { Write-Host 'AVISO: nenhum objeto de anexo foi encontrado na pasta informada. Confira se -PastaDeAnexos e a pasta certa (Conversations:Attachments:RootPath).' }
    if ($resumoDosAnexos.LegadosSemCifra -gt 0) { Write-Host ('AVISO: ' + $resumoDosAnexos.LegadosSemCifra + ' anexo(s) estao em TEXTO CLARO no backup (sem cifra em repouso na origem). Proteja a pasta do backup; para cifrar a origem rode ''attachments encrypt-existing --confirm'' no Api.') }
    if ($resumoDosAnexos.Sumiram -gt 0) { Write-Host ('AVISO: ' + $resumoDosAnexos.Sumiram + ' objeto(s) sumiram durante a copia (apagados pela retencao); as linhas deles no banco podem aparecer como disponiveis sem arquivo: rode o comando attachments check do Api na restauracao.') }
}
Write-Host ('  pasta   ' + $pasta)

# A pasta do backup e a saida do script (os outros scripts a capturam); as mensagens acima vao so para o terminal.
$pasta
