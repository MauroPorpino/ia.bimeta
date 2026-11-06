# Funcoes comuns dos scripts de backup e restauracao. Nao e executado sozinho: os scripts fazem "dot-source" (. .\_Comum.ps1).
# So ASCII (PowerShell 5.1 le .ps1 sem BOM como ANSI). Nenhuma senha e impressa; PGPASSWORD vive so no processo, durante cada chamada.

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '_Cifra.ps1')

if (-not (Test-Path variable:script:PgBin) -or [string]::IsNullOrEmpty($script:PgBin)) {
    $script:PgBin = 'C:\Projetos\ferramentas\postgresql-17.11\bin'
}

function Get-RaizDoRepositorio {
    return (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
}

function Get-SegredoDeUsuario {
    param([Parameter(Mandatory)][string]$Projeto, [Parameter(Mandatory)][string]$Chave)
    foreach ($linha in @(& dotnet user-secrets list --project $Projeto)) {
        if ($linha.StartsWith($Chave + ' = ')) { return $linha.Substring($Chave.Length + 3) }
    }
    return $null
}

function ConvertFrom-ConexaoNpgsql {
    param([Parameter(Mandatory)][string]$Texto)
    $partes = @{}
    foreach ($par in ($Texto -split ';')) {
        $i = $par.IndexOf('=')
        if ($i -gt 0) { $partes[$par.Substring(0, $i).Trim().ToLowerInvariant()] = $par.Substring($i + 1).Trim() }
    }
    $servidor = $partes['host']; if (-not $servidor) { $servidor = $partes['server'] }
    $porta = $partes['port']; if (-not $porta) { $porta = '5432' }
    $usuario = $partes['username']; if (-not $usuario) { $usuario = $partes['user id'] }
    $banco = $partes['database']
    $senha = $partes['password']
    if ([string]::IsNullOrEmpty($servidor) -or [string]::IsNullOrEmpty($banco) -or [string]::IsNullOrEmpty($usuario)) {
        throw 'A string de conexao nao tem host, database ou usuario.'
    }
    # O PostgreSQL do projeto e o da porta 5433; o da 5432 (se existir na maquina) nao e este. Unica excecao: as instancias descartaveis do ensaio e dos
    # testes destes scripts, que sobem so em 127.0.0.1 numa porta efemera (49152 ou mais), nunca em 5432.
    $descartavel = ($servidor -eq '127.0.0.1') -and ($porta -match '^\d+$') -and ([int]$porta -ge 49152)
    if ($porta -ne '5433' -and -not $descartavel) { throw ('Porta inesperada (' + $porta + '): o PostgreSQL do projeto e o da porta 5433.') }
    return [pscustomobject]@{ Servidor = $servidor; Porta = $porta; Banco = $banco; Usuario = $usuario; Senha = $senha }
}

# Origem: Desenvolvimento (user-secret do Api), Teste (variavel IABIMETA_TEST_CONNECTION ou user-secret dos testes de integracao)
# ou Ambiente (a variavel de ambiente informada em -VariavelDeConexao, por exemplo a de homologacao).
function Get-ConexaoDeOrigem {
    param([Parameter(Mandatory)][string]$Origem, [string]$VariavelDeConexao)
    $raiz = Get-RaizDoRepositorio
    $texto = $null
    switch ($Origem) {
        'Desenvolvimento' { $texto = Get-SegredoDeUsuario -Projeto (Join-Path $raiz 'backend\host\IaBimeta.Api') -Chave 'ConnectionStrings:Tenancy' }
        'Teste' {
            $texto = [Environment]::GetEnvironmentVariable('IABIMETA_TEST_CONNECTION')
            if ([string]::IsNullOrEmpty($texto)) { $texto = Get-SegredoDeUsuario -Projeto (Join-Path $raiz 'tests\IaBimeta.IntegrationTests') -Chave 'ConnectionStrings:TenancyTests' }
        }
        'Ambiente' {
            if ([string]::IsNullOrEmpty($VariavelDeConexao)) { throw 'Informe -VariavelDeConexao com o nome da variavel de ambiente que guarda a string de conexao.' }
            $texto = [Environment]::GetEnvironmentVariable($VariavelDeConexao)
        }
        default { throw ('Origem desconhecida: ' + $Origem) }
    }
    if ([string]::IsNullOrEmpty($texto)) { throw ('String de conexao nao encontrada para a origem ' + $Origem + '.') }
    return (ConvertFrom-ConexaoNpgsql -Texto $texto)
}

# O SQL vai pela entrada padrao (nao por -c): aspas duplas em argumentos de executavel nativo quebram no PowerShell 5.1.
function Invoke-Psql {
    param([Parameter(Mandatory)]$Conexao, [Parameter(Mandatory)][string]$Banco, [Parameter(Mandatory)][string]$Sql)
    $env:PGPASSWORD = $Conexao.Senha
    try {
        $saida = $Sql | & (Join-Path $script:PgBin 'psql.exe') -h $Conexao.Servidor -p $Conexao.Porta -U $Conexao.Usuario -d $Banco -X -q -t -A -F '|' -v ON_ERROR_STOP=1 -f -
        if ($LASTEXITCODE -ne 0) { throw ('psql saiu com ' + $LASTEXITCODE) }
        return @($saida | Where-Object { $_ -ne '' })
    }
    finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

function Test-NomeDeBanco {
    param([Parameter(Mandatory)][string]$Nome)
    return ($Nome -cmatch '^[a-z][a-z0-9_]{2,62}$')
}

# Contagem exata de linhas de todas as tabelas de usuario, numa unica consulta. Serve para comparar o banco restaurado com o de origem.
function Get-ContagensDeTabelas {
    param([Parameter(Mandatory)]$Conexao, [Parameter(Mandatory)][string]$Banco)
    # @(...) em toda chamada de Invoke-Psql: um resultado de uma linha so vira texto solto no PowerShell, e indexar texto devolve caractere.
    $tabelas = @(Invoke-Psql -Conexao $Conexao -Banco $Banco -Sql "SELECT format('%I.%I', table_schema, table_name) FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY 1;")
    if ($tabelas.Count -eq 0) { return @() }
    $uniao = ($tabelas | ForEach-Object { "SELECT '" + $_.Replace("'", "''") + "' AS t, count(*) AS n FROM " + $_ }) -join ' UNION ALL '
    $linhas = @(Invoke-Psql -Conexao $Conexao -Banco $Banco -Sql ($uniao + ' ORDER BY 1;'))
    $resultado = @()
    foreach ($linha in $linhas) {
        $campos = $linha.Split('|')
        $resultado += [pscustomobject]@{ tabela = $campos[0]; linhas = [long]$campos[1] }
    }
    return $resultado
}

function Get-EntradasDoDump {
    param([Parameter(Mandatory)][string]$Arquivo)
    $saida = & (Join-Path $script:PgBin 'pg_restore.exe') --list $Arquivo
    if ($LASTEXITCODE -ne 0) { throw 'pg_restore --list recusou o arquivo.' }
    return @($saida | Where-Object { $_ -match '^\d+; ' }).Count
}

function Invoke-PgCtl {
    param([Parameter(Mandatory)][string[]]$Argumentos, [int]$SegundosDeEspera = 120)
    $processo = Start-Process -FilePath (Join-Path $script:PgBin 'pg_ctl.exe') -ArgumentList $Argumentos -WindowStyle Hidden -PassThru
    if (-not $processo.WaitForExit($SegundosDeEspera * 1000)) {
        try { $processo.Kill() } catch { }
        throw ('pg_ctl ' + $Argumentos[0] + ' nao terminou em ' + $SegundosDeEspera + ' s.')
    }
    return $processo.ExitCode
}

function Get-PortaLivre {
    $ouvinte = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $ouvinte.Start()
    try { return $ouvinte.LocalEndpoint.Port } finally { $ouvinte.Stop() }
}

# Cria e liga uma INSTANCIA descartavel do PostgreSQL (initdb numa pasta temporaria, porta livre, so loopback, senha aleatoria
# que existe so neste processo). E um servidor a parte: nao toca em nenhum banco real e nao exige privilegio algum no servidor
# do projeto. Remova com Remove-InstanciaDescartavel.
function New-InstanciaDescartavel {
    param([Parameter(Mandatory)][string]$Pasta)
    New-Item -ItemType Directory -Force -Path $Pasta | Out-Null
    $dados = Join-Path $Pasta 'dados'
    $log = Join-Path $Pasta 'servidor.log'
    $bytes = New-Object byte[] 24
    $gerador = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $gerador.GetBytes($bytes)
    $gerador.Dispose()
    $senha = [Convert]::ToBase64String($bytes).Replace('+', 'A').Replace('/', 'B').Replace('=', 'C')
    $arquivoDaSenha = Join-Path $Pasta 'senha.tmp'
    [IO.File]::WriteAllText($arquivoDaSenha, $senha, (New-Object System.Text.UTF8Encoding($false)))
    try {
        & (Join-Path $script:PgBin 'initdb.exe') -D $dados -U ensaio -A scram-sha-256 ('--pwfile=' + $arquivoDaSenha) -E UTF8 --locale=C | Out-Null
        if ($LASTEXITCODE -ne 0) { throw ('initdb saiu com ' + $LASTEXITCODE) }
    }
    finally { Remove-Item -Force $arquivoDaSenha -ErrorAction SilentlyContinue }

    $porta = Get-PortaLivre
    Add-Content -Path (Join-Path $dados 'postgresql.conf') -Value @("listen_addresses = '127.0.0.1'", ('port = ' + $porta), 'fsync = off', 'full_page_writes = off', 'synchronous_commit = off', 'max_connections = 20', 'shared_buffers = 128MB')
    # Start-Process + WaitForExit (e nao "& pg_ctl | Out-Null"): o servidor herda o cano de saida e o PowerShell ficaria esperando ele fechar, para sempre.
    $partida = Invoke-PgCtl -Argumentos @('start', '-D', ('"' + $dados + '"'), '-w', '-t', '90', '-l', ('"' + $log + '"')) -SegundosDeEspera 120
    if ($partida -ne 0) { throw ('pg_ctl start saiu com ' + $partida + '; veja ' + $log) }

    return [pscustomobject]@{
        Pasta    = $Pasta
        Dados    = $dados
        Conexao  = [pscustomobject]@{ Servidor = '127.0.0.1'; Porta = [string]$porta; Banco = 'postgres'; Usuario = 'ensaio'; Senha = $senha }
    }
}

# Para e apaga a instancia descartavel. So age em uma pasta "_instancia-ensaio-*" DENTRO da pasta de backups.
function Remove-InstanciaDescartavel {
    param([Parameter(Mandatory)]$Instancia, [Parameter(Mandatory)][string]$PastaDeBackups)
    $pasta = [IO.Path]::GetFullPath($Instancia.Pasta)
    $base = [IO.Path]::GetFullPath($PastaDeBackups)
    if (-not $pasta.StartsWith($base.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Split-Path -Leaf $pasta).StartsWith('_instancia-ensaio-')) {
        throw ('Recuso apagar uma pasta que nao e uma instancia de ensaio: ' + $pasta)
    }
    $parada = Invoke-PgCtl -Argumentos @('stop', '-D', ('"' + $Instancia.Dados + '"'), '-m', 'fast', '-w', '-t', '60') -SegundosDeEspera 90
    if ($parada -ne 0) { Invoke-PgCtl -Argumentos @('stop', '-D', ('"' + $Instancia.Dados + '"'), '-m', 'immediate', '-w', '-t', '60') -SegundosDeEspera 90 | Out-Null }
    for ($i = 0; $i -lt 5; $i++) {
        try { Remove-Item -Recurse -Force -Path $pasta -ErrorAction Stop; return }
        catch { Start-Sleep -Seconds 1 }
    }
    throw ('Nao consegui apagar ' + $pasta + '; remova-a manualmente.')
}

# Confere o backup pela pasta: manifesto, existencia e tamanho do arquivo, SHA-256 e leitura do dump pelo pg_restore --list.
# Devolve o manifesto; lanca excecao com o motivo se qualquer conferencia falhar. Somente leitura.
function Test-ManifestoCifrado {
    param([Parameter(Mandatory)]$Manifesto)
    return ($Manifesto.PSObject.Properties.Name -contains 'cifrado') -and [bool]$Manifesto.cifrado
}

# Decifra o backup para uma pasta temporaria DENTRO da pasta do backup (_tmp-*), confere o SHA-256 do dump original e devolve o caminho do dump e
# a pasta temporaria (quem chama a apaga com Remove-DumpTemporario). Frase errada, arquivo alterado ou SHA-256 diferente do manifesto: falha.
function Get-DumpParaRestaurar {
    param([Parameter(Mandatory)][string]$Pasta, [Parameter(Mandatory)]$Manifesto, [string]$ChaveEnv)
    $armazenado = Join-Path $Pasta $Manifesto.arquivo
    if (-not (Test-ManifestoCifrado -Manifesto $Manifesto)) {
        return [pscustomobject]@{ Arquivo = $armazenado; PastaTemporaria = $null }
    }
    if ([string]::IsNullOrEmpty($ChaveEnv)) { throw 'Este backup e CIFRADO: informe -ChaveEnv com o nome da variavel de ambiente que guarda a frase de cifra.' }
    $temporaria = Join-Path $Pasta ('_tmp-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
    New-Item -ItemType Directory -Path $temporaria | Out-Null
    $destino = Join-Path $temporaria $Manifesto.dump.arquivo
    try {
        $sha = Unprotect-Arquivo -Origem $armazenado -Destino $destino -ChaveEnv $ChaveEnv
        if ($sha -ne $Manifesto.dump.sha256) { throw 'O SHA-256 do dump decifrado difere do manifesto.' }
    }
    catch {
        Remove-DumpTemporario -Dump ([pscustomobject]@{ Arquivo = $destino; PastaTemporaria = $temporaria })
        throw
    }
    return [pscustomobject]@{ Arquivo = $destino; PastaTemporaria = $temporaria }
}

# Apaga a pasta temporaria criada por Get-DumpParaRestaurar (so uma pasta "_tmp-*" que a propria funcao criou).
function Remove-DumpTemporario {
    param($Dump)
    if ($null -eq $Dump -or [string]::IsNullOrEmpty($Dump.PastaTemporaria)) { return }
    if ((Split-Path -Leaf $Dump.PastaTemporaria).StartsWith('_tmp-') -and (Test-Path $Dump.PastaTemporaria)) {
        Remove-Item -Recurse -Force -Path $Dump.PastaTemporaria -ErrorAction SilentlyContinue
    }
}

# Confere o backup pela pasta: manifesto, existencia e tamanho do arquivo, SHA-256 e leitura do dump pelo pg_restore --list. Backup CIFRADO: com -ChaveEnv
# a verificacao e COMPLETA (autentica o MAC, decifra para uma pasta temporaria, confere o SHA-256 do dump original e le o dump); sem ela, so o arquivo
# cifrado (tamanho e SHA-256), e o manifesto devolvido traz VerificacaoCompleta = $false. Somente leitura (a pasta temporaria e apagada).
function Test-IntegridadeDoBackup {
    param([Parameter(Mandatory)][string]$Pasta, [string]$ChaveEnv)
    $arquivoDoManifesto = Join-Path $Pasta 'manifesto.json'
    if (-not (Test-Path $arquivoDoManifesto)) { throw ('manifesto.json nao encontrado em ' + $Pasta) }
    $manifesto = Get-Content -Raw -Path $arquivoDoManifesto | ConvertFrom-Json
    if ($manifesto.versaoDoManifesto -ne 1) { throw ('Versao de manifesto desconhecida: ' + $manifesto.versaoDoManifesto) }
    $arquivo = Join-Path $Pasta $manifesto.arquivo
    if (-not (Test-Path $arquivo)) { throw ('Arquivo do backup nao encontrado: ' + $arquivo) }
    if ((Get-Item $arquivo).Length -ne [long]$manifesto.tamanhoEmBytes) { throw 'O tamanho do arquivo difere do manifesto (arquivo truncado ou trocado).' }
    $hash = (Get-FileHash -Algorithm SHA256 -Path $arquivo).Hash
    if ($hash -ne $manifesto.sha256) { throw 'O SHA-256 do arquivo difere do manifesto (arquivo corrompido ou alterado).' }

    $completa = $true
    if (Test-ManifestoCifrado -Manifesto $manifesto) {
        if ([string]::IsNullOrEmpty($ChaveEnv)) { $completa = $false }
        else {
            $dump = Get-DumpParaRestaurar -Pasta $Pasta -Manifesto $manifesto -ChaveEnv $ChaveEnv
            try {
                $entradas = Get-EntradasDoDump -Arquivo $dump.Arquivo
                if ($entradas -ne [int]$manifesto.entradasDoDump) { throw ('O dump tem ' + $entradas + ' entradas; o manifesto registra ' + $manifesto.entradasDoDump + '.') }
            }
            finally { Remove-DumpTemporario -Dump $dump }
        }
    }
    else {
        $entradas = Get-EntradasDoDump -Arquivo $arquivo
        if ($entradas -ne [int]$manifesto.entradasDoDump) { throw ('O dump tem ' + $entradas + ' entradas; o manifesto registra ' + $manifesto.entradasDoDump + '.') }
    }
    Add-Member -InputObject $manifesto -NotePropertyName VerificacaoCompleta -NotePropertyValue $completa -Force
    return $manifesto
}

# Compara as contagens do manifesto com as do banco restaurado. Devolve a lista de diferencas (vazia = identicas).
function Compare-Contagens {
    param($Manifesto, $Restaurado)
    $esperado = @{}
    foreach ($item in @($Manifesto.contagens)) { $esperado[$item.tabela] = [long]$item.linhas }
    $obtido = @{}
    foreach ($item in @($Restaurado)) { $obtido[$item.tabela] = [long]$item.linhas }
    $diferencas = @()
    foreach ($tabela in ($esperado.Keys + $obtido.Keys | Sort-Object -Unique)) {
        if (-not $esperado.ContainsKey($tabela)) { $diferencas += ($tabela + ': existe so no restaurado'); continue }
        if (-not $obtido.ContainsKey($tabela)) { $diferencas += ($tabela + ': falta no restaurado'); continue }
        if ($esperado[$tabela] -ne $obtido[$tabela]) { $diferencas += ($tabela + ': manifesto ' + $esperado[$tabela] + ', restaurado ' + $obtido[$tabela]) }
    }
    return $diferencas
}

# Restaura o dump num banco NOVO. Recusa se o destino ja existir: nunca restaura sobre banco existente e nunca apaga nada.
# Devolve um objeto com o resultado das conferencias; lanca excecao se o restore falhar (o banco novo fica para inspecao).
function Restore-EmBancoNovo {
    param([Parameter(Mandatory)]$Conexao, [Parameter(Mandatory)][string]$ArquivoDoDump, [Parameter(Mandatory)]$Manifesto, [Parameter(Mandatory)][string]$BancoDeDestino)
    if (-not (Test-Path $ArquivoDoDump)) { throw 'O arquivo do dump a restaurar nao existe.' }
    if (-not (Test-NomeDeBanco -Nome $BancoDeDestino)) { throw 'O nome do banco de destino aceita so minusculas, numeros e sublinhado (3 a 63, comecando por letra).' }
    $existe = @(Invoke-Psql -Conexao $Conexao -Banco 'postgres' -Sql ("SELECT count(*) FROM pg_database WHERE datname = '" + $BancoDeDestino + "';"))
    if ([int]$existe[0] -ne 0) { throw ("O banco '" + $BancoDeDestino + "' JA EXISTE. Esta ferramenta nunca restaura sobre banco existente: escolha outro nome (veja docs\operations\BACKUP-E-RESTAURACAO.md).") }

    Invoke-Psql -Conexao $Conexao -Banco 'postgres' -Sql ('CREATE DATABASE "' + $BancoDeDestino + '" TEMPLATE template0 ENCODING ''UTF8'';') | Out-Null

    # Registra que ESTA execucao criou o banco: quem limpa o ensaio so remove um banco que o proprio script criou.
    $script:BancoCriadoNestaExecucao = $BancoDeDestino
    $arquivo = $ArquivoDoDump
    $relogio = [Diagnostics.Stopwatch]::StartNew()
    $env:PGPASSWORD = $Conexao.Senha
    try {
        & (Join-Path $script:PgBin 'pg_restore.exe') -h $Conexao.Servidor -p $Conexao.Porta -U $Conexao.Usuario -d $BancoDeDestino --no-owner --no-privileges --exit-on-error $arquivo
        if ($LASTEXITCODE -ne 0) { throw ('pg_restore saiu com ' + $LASTEXITCODE + ". O banco '" + $BancoDeDestino + "' ficou incompleto; confira e remova-o manualmente.") }
    }
    finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
    $segundos = [Math]::Round($relogio.Elapsed.TotalSeconds, 1)

    $contagens = @(Get-ContagensDeTabelas -Conexao $Conexao -Banco $BancoDeDestino)
    $diferencas = @(Compare-Contagens -Manifesto $Manifesto -Restaurado $contagens)
    $naoValidadas = @(Invoke-Psql -Conexao $Conexao -Banco $BancoDeDestino -Sql 'SELECT count(*) FROM pg_constraint WHERE NOT convalidated;')
    return [pscustomobject]@{
        Banco                  = $BancoDeDestino
        SegundosDoRestore      = $segundos
        Tabelas                = $contagens.Count
        Diferencas             = $diferencas
        RestricoesNaoValidadas = [int]$naoValidadas[0]
    }
}
