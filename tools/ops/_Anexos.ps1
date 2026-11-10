# Funcoes do backup dos ANEXOS PRIVADOS (arquivos de midia do WhatsApp; ADR 0028 e 0031): copiar a pasta de anexos junto com o backup do banco, conferir e restaurar.
# Nao e executado sozinho: os scripts fazem "dot-source" (. .\_Anexos.ps1), que ja carrega _Comum.ps1 e _Cifra.ps1. So ASCII (PowerShell 5.1 le .ps1 sem BOM como ANSI).
# Nenhum segredo: este arquivo nunca ve a chave de cifra dos anexos (ela e do Api/Worker); so le os 4 primeiros bytes de cada objeto para saber se esta cifrado.
#
# O FORMATO DO ARMAZENAMENTO (fonte da verdade e o Api; aqui so se le):
#   <raiz>\<tenant: 32 hex minusculos>\<arquivo: 32 hex minusculos>.bin   (so estes arquivos sao objetos; todo o resto, como *.parcial, e IGNORADO e contado)
#   Os objetos sao IMUTAVEIS (gravados uma vez) e so sao apagados pela rotina de retencao. Por isso copiar de novo um objeto que ja existe no destino com o mesmo
#   SHA-256 e seguro. Objetos cifrados pelo Api (AEAD) comecam com os 4 bytes ASCII "IABA"; os antigos (legados) ficam em texto claro, sem esse cabecalho.
#
# O BACKUP (pasta da versao):  anexos\<tenant>\<arquivo>.bin   +   anexos-manifesto.json  (versao 1, arquivos ordenados por chave: deterministico).

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '_Comum.ps1')

# A chave do manifesto usa barra normal ("tenant/arquivo.bin"); o caminho no disco, barra invertida. O \z (e nao $) recusa um fim de linha escondido.
$script:AnexosPadraoDoCaminho = '^[0-9a-f]{32}\\[0-9a-f]{32}\.bin\z'
$script:AnexosPadraoDaChave = '^[0-9a-f]{32}/[0-9a-f]{32}\.bin\z'
$script:AnexosCabecalhoCifrado = [byte[]](0x49, 0x41, 0x42, 0x41)
$script:AnexosBloco = 1048576
# Leitura que nao atrapalha o Api: quem escreve ou apaga (retencao) nao e bloqueado enquanto lemos.
$script:AnexosCompartilhamento = [IO.FileShare]'ReadWrite, Delete'
$script:AnexosLimiteDeErros = 100

function Resolve-CaminhoCompleto {
    param([Parameter(Mandatory)][string]$Caminho)
    $completo = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Caminho)
    if ($completo.Length -gt 3) { $completo = $completo.TrimEnd('\') }
    return $completo
}

function Test-PropriedadeDoObjeto {
    param($Objeto, [string]$Nome)
    return ($null -ne $Objeto) -and (@($Objeto.PSObject.Properties.Name) -contains $Nome)
}

# Acumula erros ate um limite (um backup muito danificado nao enche a tela).
function Add-ErroLimitado {
    param([Parameter(Mandatory)]$Lista, [Parameter(Mandatory)][string]$Texto)
    if ($Lista.Count -lt $script:AnexosLimiteDeErros) { $Lista.Add($Texto) }
    elseif ($Lista.Count -eq $script:AnexosLimiteDeErros) { $Lista.Add('(mais erros omitidos)') }
}

# Uma chave vinda de um manifesto pode ser hostil: nada de caractere de controle nem texto longo na mensagem.
function Format-ChaveParaMensagem {
    param($Chave)
    $texto = [regex]::Replace([string]$Chave, '[^\x20-\x7E]', '?')
    if ($texto.Length -gt 80) { $texto = $texto.Substring(0, 80) + '...' }
    return $texto
}

function Test-ChaveDeAnexo {
    param($Chave)
    return (($Chave -is [string]) -and ($Chave -cmatch $script:AnexosPadraoDaChave))
}

function Get-MensagemDeAnexosSemCifra {
    param([Parameter(Mandatory)][long]$Quantidade)
    return ('ha ' + $Quantidade + " anexo(s) sem cifra em repouso; rode 'attachments encrypt-existing --confirm' no Api antes de fazer backup cifrado ou com destino externo")
}

# SHA-256 (hexadecimal maiusculo, como o Get-FileHash) lendo sem bloquear quem escreve ou apaga o arquivo.
function Get-Sha256DoArquivo {
    param([Parameter(Mandatory)][string]$Caminho)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fluxo = [IO.File]::Open($Caminho, [IO.FileMode]::Open, [IO.FileAccess]::Read, $script:AnexosCompartilhamento)
    try { return (ConvertTo-Hexadecimal -Bytes ([byte[]]$sha.ComputeHash($fluxo))) }
    finally { $fluxo.Dispose(); $sha.Dispose() }
}

# Copia Origem para Destino calculando o SHA-256 do que foi LIDO da origem. O destino e criado (ou truncado): so use com um arquivo ".parcial" proprio.
function Copy-ArquivoComHash {
    param([Parameter(Mandatory)][string]$Origem, [Parameter(Mandatory)][string]$Destino)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $entrada = $null
    $saida = $null
    try {
        $entrada = [IO.File]::Open($Origem, [IO.FileMode]::Open, [IO.FileAccess]::Read, $script:AnexosCompartilhamento)
        $saida = [IO.File]::Open($Destino, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $buffer = New-Object byte[] $script:AnexosBloco
        $total = 0L
        while ($true) {
            $lidos = $entrada.Read($buffer, 0, $buffer.Length)
            if ($lidos -le 0) { break }
            [void]$sha.TransformBlock($buffer, 0, $lidos, $null, 0)
            $saida.Write($buffer, 0, $lidos)
            $total += $lidos
        }
        [void]$sha.TransformFinalBlock((New-Object byte[] 0), 0, 0)
        $saida.Flush($true)
        return [pscustomobject]@{ Sha256 = (ConvertTo-Hexadecimal -Bytes ([byte[]]$sha.Hash)); Tamanho = $total }
    }
    finally {
        if ($null -ne $entrada) { $entrada.Dispose() }
        if ($null -ne $saida) { $saida.Dispose() }
        $sha.Dispose()
    }
}

# O objeto esta cifrado na origem? So le os 4 primeiros bytes (cabecalho "IABA"); nunca decifra nada.
function Test-AnexoCifrado {
    param([Parameter(Mandatory)][string]$Caminho)
    $fluxo = [IO.File]::Open($Caminho, [IO.FileMode]::Open, [IO.FileAccess]::Read, $script:AnexosCompartilhamento)
    try {
        $cabecalho = New-Object byte[] 4
        $lidos = Read-Bloco -Fluxo $fluxo -Buffer $cabecalho
        if ($lidos -lt 4) { return $false }
        return (Test-BytesIguais -A $cabecalho -B $script:AnexosCabecalhoCifrado)
    }
    finally { $fluxo.Dispose() }
}

# Lista os objetos de anexos de uma raiz. Devolve Objetos (Chave 'tenant/arquivo.bin', Caminho, Tamanho, Cifrado), ordenados por chave (comparacao ordinal),
# e Ignorados (arquivos que nao casam com o formato, como *.parcial de gravacao em andamento). Somente leitura.
function Get-ObjetosDeAnexos {
    param([Parameter(Mandatory)][string]$Raiz)
    if (-not (Test-Path -PathType Container -LiteralPath $Raiz)) { throw ('A pasta de anexos nao existe (ou nao e uma pasta): ' + $Raiz) }
    $raizCompleta = Resolve-CaminhoCompleto -Caminho $Raiz
    $prefixo = $raizCompleta + '\'
    $porChave = @{}
    $ignorados = 0
    foreach ($caminho in [IO.Directory]::EnumerateFiles($raizCompleta, '*', [IO.SearchOption]::AllDirectories)) {
        $relativo = $caminho.Substring($prefixo.Length)
        if ($relativo -cnotmatch $script:AnexosPadraoDoCaminho) { $ignorados++; continue }
        try {
            $tamanho = (New-Object IO.FileInfo $caminho).Length
            $cifrado = Test-AnexoCifrado -Caminho $caminho
        }
        catch {
            # Sumiu entre a listagem e a leitura (a retencao apaga objetos): nao e objeto nem ignorado.
            if (-not (Test-Path -LiteralPath $caminho)) { continue }
            throw
        }
        $chave = $relativo.Replace('\', '/')
        $porChave[$chave] = [pscustomobject]@{ Chave = $chave; Caminho = $caminho; Tamanho = [long]$tamanho; Cifrado = [bool]$cifrado }
    }
    $chaves = [string[]]@($porChave.Keys)
    [Array]::Sort($chaves, [StringComparer]::Ordinal)
    $ordenados = New-Object System.Collections.Generic.List[object]
    foreach ($chave in $chaves) { $ordenados.Add($porChave[$chave]) }
    return [pscustomobject]@{ Objetos = $ordenados.ToArray(); Ignorados = $ignorados }
}

# Copia cada objeto para <Destino>\anexos\<tenant>\<arquivo>.bin (.parcial + conferencia do SHA-256 + renomeacao) e grava <Destino>\anexos-manifesto.json (por
# arquivo temporario e troca). Um objeto que ja existe no destino com o MESMO SHA-256 e pulado (os objetos sao imutaveis); com SHA-256 DIFERENTE a copia FALHA:
# nunca sobrescreve. Um objeto que a retencao apagou durante a copia nao e erro: e contado em Sumiram. Devolve o resumo (inclui o SHA-256 do manifesto).
function Backup-Anexos {
    param([Parameter(Mandatory)][string]$Raiz, [Parameter(Mandatory)][string]$Destino)
    $achados = Get-ObjetosDeAnexos -Raiz $Raiz
    $pastaDeAnexos = Join-Path $Destino 'anexos'
    New-Item -ItemType Directory -Force -Path $pastaDeAnexos | Out-Null

    $entradas = New-Object System.Collections.Generic.List[object]
    $copiados = 0
    $pulados = 0
    $sumiram = 0
    foreach ($objeto in $achados.Objetos) {
        $pastaDoTenant = Join-Path $pastaDeAnexos $objeto.Chave.Substring(0, 32)
        $final = Join-Path $pastaDoTenant $objeto.Chave.Substring(33)
        $sha = $null
        $tamanho = 0L
        try {
            if (Test-Path -LiteralPath $final) {
                $shaDaOrigem = Get-Sha256DoArquivo -Caminho $objeto.Caminho
                $shaDoDestino = Get-Sha256DoArquivo -Caminho $final
                if ($shaDaOrigem -ne $shaDoDestino) { throw ('O anexo ' + $objeto.Chave + ' ja existe no destino com conteudo DIFERENTE (SHA-256); nao sobrescrevo.') }
                $sha = $shaDaOrigem
                $tamanho = (Get-Item -LiteralPath $final).Length
                $pulados++
            }
            else {
                New-Item -ItemType Directory -Force -Path $pastaDoTenant | Out-Null
                $parcial = $final + '.parcial'
                try {
                    $copia = Copy-ArquivoComHash -Origem $objeto.Caminho -Destino $parcial
                    if ((Get-Sha256DoArquivo -Caminho $parcial) -ne $copia.Sha256) { throw ('A copia do anexo ' + $objeto.Chave + ' difere da origem (SHA-256).') }
                    Move-Item -LiteralPath $parcial -Destination $final
                }
                catch {
                    Remove-Item -Force -LiteralPath $parcial -ErrorAction SilentlyContinue
                    throw
                }
                $sha = $copia.Sha256
                $tamanho = $copia.Tamanho
                $copiados++
            }
        }
        catch {
            if (-not (Test-Path -LiteralPath $objeto.Caminho)) { $sumiram++; continue }
            throw
        }
        $entradas.Add([pscustomobject]@{ Chave = $objeto.Chave; Tamanho = $tamanho; Sha256 = $sha; Cifrado = $objeto.Cifrado })
    }

    $total = 0L
    $cifrados = 0
    $tenants = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($e in $entradas) {
        $total += $e.Tamanho
        if ($e.Cifrado) { $cifrados++ }
        [void]$tenants.Add($e.Chave.Substring(0, 32))
    }

    # O JSON e montado a mao (so ha hexadecimais, numeros e booleanos): formato estavel, uma linha por arquivo, rapido para dezenas de milhares de objetos.
    $texto = New-Object System.Text.StringBuilder
    [void]$texto.Append("{`n")
    [void]$texto.Append('  "versaoDoManifesto": 1,' + "`n")
    [void]$texto.Append('  "criadoEmUtc": "' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') + '",' + "`n")
    [void]$texto.Append('  "objetos": ' + $entradas.Count + ',' + "`n")
    [void]$texto.Append('  "tamanhoTotalEmBytes": ' + $total + ',' + "`n")
    [void]$texto.Append('  "tenants": ' + $tenants.Count + ',' + "`n")
    [void]$texto.Append('  "ignorados": ' + $achados.Ignorados + ',' + "`n")
    [void]$texto.Append('  "cifradosNaOrigem": ' + $cifrados + ',' + "`n")
    [void]$texto.Append('  "legadosSemCifra": ' + ($entradas.Count - $cifrados) + ',' + "`n")
    if ($entradas.Count -eq 0) { [void]$texto.Append('  "arquivos": []' + "`n") }
    else {
        [void]$texto.Append('  "arquivos": [' + "`n")
        for ($i = 0; $i -lt $entradas.Count; $i++) {
            $e = $entradas[$i]
            $linha = '    { "chave": "' + $e.Chave + '", "tamanhoEmBytes": ' + $e.Tamanho + ', "sha256": "' + $e.Sha256 + '", "cifradoNaOrigem": ' + $(if ($e.Cifrado) { 'true' } else { 'false' }) + ' }'
            if ($i -lt $entradas.Count - 1) { $linha += ',' }
            [void]$texto.Append($linha + "`n")
        }
        [void]$texto.Append("  ]`n")
    }
    [void]$texto.Append("}`n")

    $caminhoDoManifesto = Join-Path $Destino 'anexos-manifesto.json'
    $temporario = $caminhoDoManifesto + '.tmp'
    Write-ArquivoUtf8 -Caminho $temporario -Texto $texto.ToString()
    Move-Item -Force -LiteralPath $temporario -Destination $caminhoDoManifesto

    return [pscustomobject]@{
        Objetos             = $entradas.Count
        TamanhoTotalEmBytes = $total
        Tenants             = $tenants.Count
        Ignorados           = $achados.Ignorados
        CifradosNaOrigem    = $cifrados
        LegadosSemCifra     = ($entradas.Count - $cifrados)
        Copiados            = $copiados
        Pulados             = $pulados
        Sumiram             = $sumiram
        Manifesto           = 'anexos-manifesto.json'
        Sha256DoManifesto   = (Get-Sha256DoArquivo -Caminho $caminhoDoManifesto)
    }
}

# Le e valida a ESTRUTURA do anexos-manifesto.json (sem abrir os arquivos de anexos): campos, chaves (so o formato exato; nenhuma com "..", barra invertida ou caminho
# absoluto), tamanhos, SHA-256, duplicadas e a coerencia das contagens e dos totais. Devolve Manifesto e Erros; nao lanca.
function Read-ManifestoDeAnexos {
    param([Parameter(Mandatory)][string]$Pasta)
    $erros = New-Object System.Collections.Generic.List[string]
    $caminho = Join-Path $Pasta 'anexos-manifesto.json'
    $manifesto = $null
    if (-not (Test-Path -PathType Leaf -LiteralPath $caminho)) { $erros.Add('anexos-manifesto.json nao encontrado no backup.') }
    else {
        try { $manifesto = Get-Content -Raw -LiteralPath $caminho | ConvertFrom-Json }
        catch { $erros.Add('anexos-manifesto.json nao e um JSON valido.') }
        if ($null -eq $manifesto -and $erros.Count -eq 0) { $erros.Add('anexos-manifesto.json esta vazio.') }
    }
    if ($null -ne $manifesto) {
        $completo = $true
        foreach ($nome in 'versaoDoManifesto', 'objetos', 'tamanhoTotalEmBytes', 'tenants', 'ignorados', 'cifradosNaOrigem', 'legadosSemCifra', 'arquivos') {
            if (-not (Test-PropriedadeDoObjeto -Objeto $manifesto -Nome $nome)) { $erros.Add("anexos-manifesto.json: falta o campo '" + $nome + "'."); $completo = $false }
        }
        if ($completo) {
            try { Test-EstruturaDoManifestoDeAnexos -Manifesto $manifesto -Erros $erros }
            catch { Add-ErroLimitado -Lista $erros -Texto 'anexos-manifesto.json mal formado (campo com tipo ou valor inesperado).' }
        }
    }
    return [pscustomobject]@{ Manifesto = $manifesto; Erros = $erros.ToArray() }
}

# A parte estrutural de Read-ManifestoDeAnexos: acrescenta a Erros o que estiver errado nas entradas e nos totais.
function Test-EstruturaDoManifestoDeAnexos {
    param([Parameter(Mandatory)]$Manifesto, [Parameter(Mandatory)]$Erros)
    if ($Manifesto.versaoDoManifesto -ne 1) { $Erros.Add('anexos-manifesto.json: versao desconhecida (' + (Format-ChaveParaMensagem $Manifesto.versaoDoManifesto) + ').') }
    $vistas = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $tenants = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $total = 0L
    $cifrados = 0
    $quantidade = 0
    foreach ($a in @($Manifesto.arquivos)) {
        $quantidade++
        if (-not (Test-PropriedadeDoObjeto -Objeto $a -Nome 'chave')) { Add-ErroLimitado -Lista $Erros -Texto ('entrada ' + $quantidade + ' do manifesto sem chave.'); continue }
        $chave = $a.chave
        if (-not (Test-ChaveDeAnexo -Chave $chave)) {
            $motivo = 'fora do formato tenant/arquivo.bin'
            if (([string]$chave) -match '\.\.|\\|:|^/') { $motivo = 'aponta para fora da raiz dos anexos' }
            Add-ErroLimitado -Lista $Erros -Texto ("chave recusada ('" + (Format-ChaveParaMensagem $chave) + "'): " + $motivo + '.')
            continue
        }
        if (-not $vistas.Add($chave)) { Add-ErroLimitado -Lista $Erros -Texto ('chave duplicada no manifesto: ' + $chave); continue }
        [void]$tenants.Add($chave.Substring(0, 32))
        $tamanho = -1L
        if ((Test-PropriedadeDoObjeto -Objeto $a -Nome 'tamanhoEmBytes') -and ($a.tamanhoEmBytes -is [int] -or $a.tamanhoEmBytes -is [long])) { $tamanho = [long]$a.tamanhoEmBytes }
        if ($tamanho -lt 0) { Add-ErroLimitado -Lista $Erros -Texto ('tamanho invalido no manifesto para ' + $chave + '.') } else { $total += $tamanho }
        if (-not ((Test-PropriedadeDoObjeto -Objeto $a -Nome 'sha256') -and ($a.sha256 -is [string]) -and ($a.sha256 -cmatch '^[0-9A-Fa-f]{64}\z'))) { Add-ErroLimitado -Lista $Erros -Texto ('SHA-256 invalido no manifesto para ' + $chave + '.') }
        if (-not ((Test-PropriedadeDoObjeto -Objeto $a -Nome 'cifradoNaOrigem') -and ($a.cifradoNaOrigem -is [bool]))) { Add-ErroLimitado -Lista $Erros -Texto ('cifradoNaOrigem invalido no manifesto para ' + $chave + '.') }
        elseif ($a.cifradoNaOrigem) { $cifrados++ }
    }
    if ($Erros.Count -eq 0) {
        if ([long]$Manifesto.objetos -ne $quantidade) { $Erros.Add('o manifesto declara ' + $Manifesto.objetos + ' objeto(s) e lista ' + $quantidade + '.') }
        if ([long]$Manifesto.tamanhoTotalEmBytes -ne $total) { $Erros.Add('o tamanho total do manifesto (' + $Manifesto.tamanhoTotalEmBytes + ') nao bate com a soma dos arquivos (' + $total + ').') }
        if ([long]$Manifesto.tenants -ne $tenants.Count) { $Erros.Add('o manifesto declara ' + $Manifesto.tenants + ' tenant(s) e as chaves tem ' + $tenants.Count + '.') }
        if ([long]$Manifesto.cifradosNaOrigem -ne $cifrados) { $Erros.Add('o manifesto declara ' + $Manifesto.cifradosNaOrigem + ' cifrado(s) e os arquivos indicam ' + $cifrados + '.') }
        if ([long]$Manifesto.cifradosNaOrigem + [long]$Manifesto.legadosSemCifra -ne $quantidade) { $Erros.Add('cifradosNaOrigem + legadosSemCifra nao somam o numero de objetos.') }
    }
}

# Confere <Pasta>\anexos-manifesto.json contra <Pasta>\anexos: cada arquivo presente com o tamanho e o SHA-256 do manifesto, nenhum arquivo EXTRA, chaves validas e
# contagens e total coerentes. Com -SemHash confere so a estrutura e os tamanhos (para quem ja conferiu o SHA-256 de cada arquivo). NAO LANCA: devolve Ok e Erros.
function Test-Anexos {
    param([Parameter(Mandatory)][string]$Pasta, [switch]$SemHash)
    $erros = New-Object System.Collections.Generic.List[string]
    $manifesto = $null
    try {
        $leitura = Read-ManifestoDeAnexos -Pasta $Pasta
        foreach ($erro in $leitura.Erros) { Add-ErroLimitado -Lista $erros -Texto $erro }
        $manifesto = $leitura.Manifesto
        if ($leitura.Erros.Count -eq 0) {
            $pastaDeAnexos = Join-Path $Pasta 'anexos'
            if (-not (Test-Path -PathType Container -LiteralPath $pastaDeAnexos)) { $erros.Add('a pasta anexos nao existe no backup.') }
            else {
                $conhecidas = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
                foreach ($a in @($manifesto.arquivos)) {
                    $chave = [string]$a.chave
                    [void]$conhecidas.Add($chave)
                    $arquivo = Join-Path $pastaDeAnexos $chave.Replace('/', '\')
                    if (-not (Test-Path -PathType Leaf -LiteralPath $arquivo)) { Add-ErroLimitado -Lista $erros -Texto ('anexo ausente no backup: ' + $chave); continue }
                    if ((Get-Item -LiteralPath $arquivo).Length -ne [long]$a.tamanhoEmBytes) { Add-ErroLimitado -Lista $erros -Texto ('o tamanho de ' + $chave + ' difere do manifesto (arquivo truncado ou trocado).'); continue }
                    if (-not $SemHash) {
                        if ((Get-Sha256DoArquivo -Caminho $arquivo) -ne [string]$a.sha256) { Add-ErroLimitado -Lista $erros -Texto ('o SHA-256 de ' + $chave + ' difere do manifesto (arquivo corrompido ou alterado).') }
                    }
                }
                $baseCompleta = Resolve-CaminhoCompleto -Caminho $pastaDeAnexos
                foreach ($caminho in [IO.Directory]::EnumerateFiles($baseCompleta, '*', [IO.SearchOption]::AllDirectories)) {
                    $relativo = $caminho.Substring($baseCompleta.Length + 1).Replace('\', '/')
                    if (-not $conhecidas.Contains($relativo)) { Add-ErroLimitado -Lista $erros -Texto ('arquivo extra no backup (nao consta do manifesto): ' + (Format-ChaveParaMensagem $relativo)) }
                }
            }
        }
    }
    catch { Add-ErroLimitado -Lista $erros -Texto ('falha ao verificar os anexos: ' + $_.Exception.Message) }
    $objetos = 0
    $bytes = 0L
    if ($null -ne $manifesto -and (Test-PropriedadeDoObjeto -Objeto $manifesto -Nome 'objetos') -and (Test-PropriedadeDoObjeto -Objeto $manifesto -Nome 'tamanhoTotalEmBytes')) {
        $objetos = [long]$manifesto.objetos
        $bytes = [long]$manifesto.tamanhoTotalEmBytes
    }
    return [pscustomobject]@{ Ok = ($erros.Count -eq 0); Erros = $erros.ToArray(); Objetos = $objetos; TamanhoTotalEmBytes = $bytes; Manifesto = $manifesto }
}

# O bloco "anexos" do manifesto.json principal (ou $null se o backup nao leva anexos).
function Get-BlocoDeAnexos {
    param([Parameter(Mandatory)]$Manifesto)
    if (Test-PropriedadeDoObjeto -Objeto $Manifesto -Nome 'anexos') { return $Manifesto.anexos }
    return $null
}

# Verificacao completa dos anexos de um backup, a partir do manifesto.json principal: o SHA-256 do anexos-manifesto.json contra o bloco "anexos" e, depois, Test-Anexos.
# Backup sem anexos: Ok e TemAnexos = $false. Com -SemHash, nao le o conteudo dos arquivos (so estrutura e tamanhos; para quem vai conferir o SHA-256 ao copiar). Nao lanca.
function Test-AnexosDoBackup {
    param([Parameter(Mandatory)][string]$Pasta, [Parameter(Mandatory)]$Manifesto, [switch]$SemHash)
    $bloco = Get-BlocoDeAnexos -Manifesto $Manifesto
    if ($null -eq $bloco) { return [pscustomobject]@{ TemAnexos = $false; Ok = $true; Erros = @(); Objetos = 0; TamanhoTotalEmBytes = 0L; Manifesto = $null } }
    $erros = New-Object System.Collections.Generic.List[string]
    $anexos = $null
    try {
        foreach ($nome in 'manifesto', 'objetos', 'tamanhoTotalEmBytes', 'sha256DoManifesto') {
            if (-not (Test-PropriedadeDoObjeto -Objeto $bloco -Nome $nome)) { $erros.Add("o bloco 'anexos' do manifesto.json nao tem o campo '" + $nome + "'.") }
        }
        if ($erros.Count -eq 0) {
            # O nome do arquivo e fixo: o manifesto principal nao escolhe caminho nenhum.
            if ([string]$bloco.manifesto -cne 'anexos-manifesto.json') { $erros.Add("o bloco 'anexos' aponta para um arquivo inesperado.") }
            else {
                $caminho = Join-Path $Pasta 'anexos-manifesto.json'
                if (-not (Test-Path -PathType Leaf -LiteralPath $caminho)) { $erros.Add('anexos-manifesto.json nao encontrado no backup.') }
                elseif ((Get-Sha256DoArquivo -Caminho $caminho) -ne [string]$bloco.sha256DoManifesto) { $erros.Add('o SHA-256 do anexos-manifesto.json difere do registrado no manifesto.json (manifesto de anexos alterado).') }
            }
        }
        if ($erros.Count -eq 0) {
            $teste = Test-Anexos -Pasta $Pasta -SemHash:$SemHash
            foreach ($erro in $teste.Erros) { Add-ErroLimitado -Lista $erros -Texto $erro }
            $anexos = $teste.Manifesto
            if ($teste.Ok) {
                if ([long]$bloco.objetos -ne $teste.Objetos) { $erros.Add('o numero de objetos do manifesto.json (' + $bloco.objetos + ') difere do anexos-manifesto.json (' + $teste.Objetos + ').') }
                if ([long]$bloco.tamanhoTotalEmBytes -ne $teste.TamanhoTotalEmBytes) { $erros.Add('o tamanho total do manifesto.json difere do anexos-manifesto.json.') }
            }
        }
    }
    catch { Add-ErroLimitado -Lista $erros -Texto ('falha ao verificar os anexos: ' + $_.Exception.Message) }
    $objetos = 0
    $bytes = 0L
    if ($null -ne $anexos -and $erros.Count -eq 0) { $objetos = [long]$anexos.objetos; $bytes = [long]$anexos.tamanhoTotalEmBytes }
    return [pscustomobject]@{ TemAnexos = $true; Ok = ($erros.Count -eq 0); Erros = $erros.ToArray(); Objetos = $objetos; TamanhoTotalEmBytes = $bytes; Manifesto = $anexos }
}

# Junta os erros numa frase curta (os primeiros) para mensagens e alertas.
function Join-ErrosDeAnexos {
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Erros, [int]$Maximo = 3)
    $primeiros = @($Erros | Select-Object -First $Maximo)
    $texto = $primeiros -join '; '
    if ($Erros.Count -gt $Maximo) { $texto += ' (e mais ' + ($Erros.Count - $Maximo) + ' problema(s))' }
    return $texto
}

# O destino dos anexos restaurados: nunca sobrescreve e nunca apaga. Aceita uma pasta que nao existe ou uma pasta existente VAZIA; recusa arquivo, pasta com conteudo
# e uma pasta dentro do proprio backup. Lanca com o motivo.
function Confirm-DestinoDeAnexosLivre {
    param([Parameter(Mandatory)][string]$Destino, [string]$PastaDoBackup)
    $completo = Resolve-CaminhoCompleto -Caminho $Destino
    if (Test-Path -PathType Leaf -LiteralPath $completo) { throw 'O destino dos anexos ja existe e e um arquivo: escolha uma pasta nova.' }
    if (Test-Path -PathType Container -LiteralPath $completo) {
        $primeiro = [IO.Directory]::EnumerateFileSystemEntries($completo) | Select-Object -First 1
        if ($null -ne $primeiro) { throw 'O destino dos anexos ja existe e NAO esta vazio. Esta ferramenta nunca sobrescreve nem apaga: escolha uma pasta nova (veja docs\operations\BACKUP-E-RESTAURACAO.md).' }
    }
    if ($PastaDoBackup) {
        $backup = (Resolve-CaminhoCompleto -Caminho $PastaDoBackup) + '\'
        if (($completo + '\').StartsWith($backup, [StringComparison]::OrdinalIgnoreCase)) { throw 'O destino dos anexos nao pode ficar dentro da pasta do proprio backup.' }
    }
    return $completo
}

# Restaura os anexos de um backup em uma pasta NOVA: <Destino>\<tenant>\<arquivo>.bin. Verifica primeiro (Test-Anexos), recusa destino existente e nao vazio, recusa
# qualquer chave fora do formato ANTES de copiar (defesa contra manifesto adulterado), copia com ".parcial" e SHA-256 do que foi lido e, no fim, confere que o numero
# de objetos e o SHA-256 de cada um, lidos de novo do disco, batem com o manifesto. Nunca sobrescreve nem apaga. -JaVerificado: o chamador acabou de rodar a verificacao
# completa (Test-AnexosDoBackup); so se pula a primeira leitura dos arquivos (a estrutura e as chaves sao sempre conferidas e o SHA-256 e conferido de novo ao copiar).
function Restore-Anexos {
    param([Parameter(Mandatory)][string]$Pasta, [Parameter(Mandatory)][string]$Destino, [switch]$JaVerificado)
    if ($JaVerificado) {
        $leitura = Read-ManifestoDeAnexos -Pasta $Pasta
        if ($leitura.Erros.Count -gt 0) { throw ('Os anexos do backup foram recusados: ' + (Join-ErrosDeAnexos -Erros $leitura.Erros)) }
        $manifesto = $leitura.Manifesto
    }
    else {
        $teste = Test-Anexos -Pasta $Pasta
        if (-not $teste.Ok) { throw ('Os anexos do backup nao passaram na verificacao: ' + (Join-ErrosDeAnexos -Erros $teste.Erros)) }
        $manifesto = $teste.Manifesto
    }
    $destinoCompleto = Confirm-DestinoDeAnexosLivre -Destino $Destino -PastaDoBackup $Pasta
    $origemBase = Join-Path $Pasta 'anexos'
    $base = $destinoCompleto + '\'
    New-Item -ItemType Directory -Force -Path $destinoCompleto | Out-Null

    $arquivos = @($manifesto.arquivos)
    foreach ($a in $arquivos) {
        $relativo = ([string]$a.chave).Replace('/', '\')
        $final = Join-Path $destinoCompleto $relativo
        # Defesa em profundidade: mesmo com a chave validada, o caminho final tem de ficar dentro do destino.
        if (-not ([IO.Path]::GetFullPath($final)).StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw ('Recuso restaurar ' + (Format-ChaveParaMensagem $a.chave) + ': o caminho sai do destino.') }
        $pastaDoTenant = Split-Path -Parent $final
        if (-not (Test-Path -PathType Container -LiteralPath $pastaDoTenant)) { New-Item -ItemType Directory -Path $pastaDoTenant | Out-Null }
        $parcial = $final + '.parcial'
        try {
            $copia = Copy-ArquivoComHash -Origem (Join-Path $origemBase $relativo) -Destino $parcial
            if ($copia.Sha256 -ne [string]$a.sha256 -or $copia.Tamanho -ne [long]$a.tamanhoEmBytes) { throw ('O anexo ' + $a.chave + ' lido do backup difere do manifesto (SHA-256 ou tamanho).') }
            Move-Item -LiteralPath $parcial -Destination $final
        }
        catch {
            Remove-Item -Force -LiteralPath $parcial -ErrorAction SilentlyContinue
            throw
        }
    }

    # Conferencia final, lendo de novo o que ficou no disco: nenhum objeto a mais ou a menos e o SHA-256 de cada um igual ao do manifesto.
    $esperado = @{}
    foreach ($a in $arquivos) { $esperado[[string]$a.chave] = [string]$a.sha256 }
    $restaurados = Get-ObjetosDeAnexos -Raiz $destinoCompleto
    if ($restaurados.Ignorados -gt 0) { throw ('Conferencia final: ' + $restaurados.Ignorados + ' arquivo(s) estranho(s) no destino dos anexos restaurados.') }
    if ($restaurados.Objetos.Count -ne $arquivos.Count) { throw ('Conferencia final: ' + $restaurados.Objetos.Count + ' objeto(s) restaurado(s) e o manifesto tem ' + $arquivos.Count + '.') }
    foreach ($objeto in $restaurados.Objetos) {
        if (-not $esperado.ContainsKey($objeto.Chave)) { throw ('Conferencia final: o objeto ' + $objeto.Chave + ' nao consta do manifesto.') }
        if ((Get-Sha256DoArquivo -Caminho $objeto.Caminho) -ne $esperado[$objeto.Chave]) { throw ('Conferencia final: o SHA-256 de ' + $objeto.Chave + ' restaurado difere do manifesto.') }
    }
    return [pscustomobject]@{ Destino = $destinoCompleto; Objetos = $arquivos.Count; TamanhoTotalEmBytes = [long]$manifesto.tamanhoTotalEmBytes }
}

# Compara os anexos restaurados com os da ORIGEM (SHA-256 de cada objeto). Diferentes = o objeto existe nos dois e o conteudo difere (grave). AusentesNaOrigem = restaurado
# que a origem nao tem mais (a retencao apagou depois do backup); NovosNaOrigem = a origem ganhou objetos depois do backup (orfaos inofensivos).
function Compare-AnexosComOrigem {
    param([Parameter(Mandatory)][string]$Restaurados, [Parameter(Mandatory)][string]$Origem)
    # Atencao: o PowerShell nao distingue maiusculas de minusculas em variaveis, e $Restaurados (parametro) e uma string: nao reutilize o nome.
    $listaRestaurada = Get-ObjetosDeAnexos -Raiz $Restaurados
    $listaDaOrigem = Get-ObjetosDeAnexos -Raiz $Origem
    $porChave = @{}
    foreach ($o in $listaDaOrigem.Objetos) { $porChave[$o.Chave] = $o }
    $iguais = 0
    $ausentes = 0
    $diferentes = New-Object System.Collections.Generic.List[string]
    foreach ($r in $listaRestaurada.Objetos) {
        if (-not $porChave.ContainsKey($r.Chave)) { $ausentes++; continue }
        if ((Get-Sha256DoArquivo -Caminho $r.Caminho) -eq (Get-Sha256DoArquivo -Caminho $porChave[$r.Chave].Caminho)) { $iguais++ } else { $diferentes.Add($r.Chave) }
    }
    return [pscustomobject]@{ Iguais = $iguais; Diferentes = $diferentes.ToArray(); AusentesNaOrigem = $ausentes; NovosNaOrigem = ($listaDaOrigem.Objetos.Count - ($listaRestaurada.Objetos.Count - $ausentes)) }
}

# Simula a PERDA da pasta de anexos de origem para o ensaio: RENOMEIA (nunca apaga) <pasta> para <pasta>.perdida-ensaio-<data>. Quem restaura so pode ler o backup:
# qualquer leitura da origem falharia. Devolve o objeto que Resume-PastaDeAnexosDeOrigem usa para devolver o nome. Use com o Api e o Worker PARADOS.
function Suspend-PastaDeAnexosDeOrigem {
    param([Parameter(Mandatory)][string]$Pasta)
    $completa = Resolve-CaminhoCompleto -Caminho $Pasta
    if (-not (Test-Path -PathType Container -LiteralPath $completa)) { throw ('A pasta de anexos de origem nao existe: ' + $completa) }
    $nome = Split-Path -Leaf $completa
    $pai = Split-Path -Parent $completa
    if ([string]::IsNullOrEmpty($nome) -or [string]::IsNullOrEmpty($pai)) { throw 'Nao simulo a perda de uma raiz de disco.' }
    $novoNome = $nome + '.perdida-ensaio-' + (Get-Date -Format 'yyyyMMddHHmmss')
    $renomeada = Join-Path $pai $novoNome
    if (Test-Path -LiteralPath $renomeada) { throw ('Ja existe ' + $renomeada + ': nao simulo a perda por cima.') }
    try { Rename-Item -LiteralPath $completa -NewName $novoNome }
    catch { throw ('Nao consegui renomear a pasta de anexos de origem para simular a perda (algum processo a esta usando? pare o Api e o Worker): ' + $_.Exception.Message) }
    return [pscustomobject]@{ Original = $completa; Renomeada = $renomeada; Devolvida = $false }
}

# Devolve o nome original da pasta renomeada por Suspend-PastaDeAnexosDeOrigem. Nunca apaga nada; se ja existe algo no nome original, lanca com o caminho da pasta renomeada.
function Resume-PastaDeAnexosDeOrigem {
    param([Parameter(Mandatory)]$Perda)
    if ($Perda.Devolvida) { return }
    if (Test-Path -LiteralPath $Perda.Original) { throw ('NAO devolvi a pasta de anexos de origem: ja existe algo em ' + $Perda.Original + '. A pasta original esta em ' + $Perda.Renomeada + ': devolva o nome dela manualmente.') }
    try { Rename-Item -LiteralPath $Perda.Renomeada -NewName (Split-Path -Leaf $Perda.Original) }
    catch { throw ('NAO consegui devolver a pasta de anexos de origem. Ela esta em ' + $Perda.Renomeada + ': renomeie-a de volta para ' + $Perda.Original + ' manualmente. ' + $_.Exception.Message) }
    $Perda.Devolvida = $true
}

# Copia os anexos de um backup (anexos\ e anexos-manifesto.json) para a pasta de OUTRA versao (a copia externa, ainda como pasta ".parcial-*"): cada arquivo e lido,
# gravado e lido de novo, sempre conferido contra o SHA-256 do manifesto; no fim confere a estrutura da copia. Lanca em qualquer divergencia.
function Copy-AnexosDoBackup {
    param([Parameter(Mandatory)][string]$Pasta, [Parameter(Mandatory)][string]$DestinoDaVersao)
    $leitura = Read-ManifestoDeAnexos -Pasta $Pasta
    if ($leitura.Erros.Count -gt 0) { throw ('Os anexos do backup foram recusados: ' + (Join-ErrosDeAnexos -Erros $leitura.Erros)) }
    $origemBase = Join-Path $Pasta 'anexos'
    $destinoBase = Join-Path $DestinoDaVersao 'anexos'
    New-Item -ItemType Directory -Force -Path $destinoBase | Out-Null
    foreach ($a in @($leitura.Manifesto.arquivos)) {
        $relativo = ([string]$a.chave).Replace('/', '\')
        $final = Join-Path $destinoBase $relativo
        $pastaDoTenant = Split-Path -Parent $final
        if (-not (Test-Path -PathType Container -LiteralPath $pastaDoTenant)) { New-Item -ItemType Directory -Path $pastaDoTenant | Out-Null }
        $copia = Copy-ArquivoComHash -Origem (Join-Path $origemBase $relativo) -Destino $final
        if ($copia.Sha256 -ne [string]$a.sha256) { throw ('O anexo ' + $a.chave + ' lido do backup difere do manifesto (SHA-256).') }
        if ((Get-Sha256DoArquivo -Caminho $final) -ne [string]$a.sha256) { throw ('O SHA-256 da copia externa do anexo ' + $a.chave + ' difere do manifesto.') }
    }
    Copy-Item -LiteralPath (Join-Path $Pasta 'anexos-manifesto.json') -Destination $DestinoDaVersao
    $teste = Test-Anexos -Pasta $DestinoDaVersao -SemHash
    if (-not $teste.Ok) { throw ('A copia externa dos anexos nao confere: ' + (Join-ErrosDeAnexos -Erros $teste.Erros)) }
    return [pscustomobject]@{ Objetos = $teste.Objetos; TamanhoTotalEmBytes = $teste.TamanhoTotalEmBytes }
}
