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

.EXAMPLE
    .\tools\ops\Backup-Banco.ps1 -Origem Teste -Rotulo pre-migracao
    .\tools\ops\Backup-Banco.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -Rotulo homologacao
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Desenvolvimento', 'Teste', 'Ambiente')][string]$Origem,
    [string]$VariavelDeConexao,
    [string]$Rotulo = 'backup',
    [string]$PastaDeBackups = 'C:\Projetos\ferramentas\backups\ia.bimeta',
    [string]$PostgresBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBin = $PostgresBin
. (Join-Path $PSScriptRoot '_Comum.ps1')

if ($Rotulo -notmatch '^[A-Za-z0-9._-]{1,40}$') { throw 'O rotulo aceita so letras, numeros, ponto, hifen e sublinhado (ate 40).' }

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

$manifesto = [ordered]@{
    versaoDoManifesto = 1
    rotulo            = $Rotulo
    criadoEmUtc       = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    banco             = $conexao.Banco
    servidor          = ($conexao.Servidor + ':' + $conexao.Porta)
    arquivo           = (Split-Path -Leaf $arquivo)
    tamanhoEmBytes    = (Get-Item $arquivo).Length
    sha256            = $hash
    pgDump            = $versao
    entradasDoDump    = $entradas
    contagens         = $contagens
}
$json = $manifesto | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $pasta 'manifesto.json'), $json + "`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host ('Backup ok: ' + $arquivo)
Write-Host ('  tamanho ' + [Math]::Round((Get-Item $arquivo).Length / 1MB, 2) + ' MB, ' + $entradas + ' entradas, ' + $contagens.Count + ' tabelas')
Write-Host ('  sha256  ' + $hash)
Write-Host ('  pasta   ' + $pasta)

# A pasta do backup e a saida do script (os outros scripts a capturam); as mensagens acima vao so para o terminal.
$pasta
