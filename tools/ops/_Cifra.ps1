# Cifra de arquivos de backup (ADR 0026), so com o .NET que o PowerShell 5.1 ja tem: AES-256-CBC + HMAC-SHA256 (cifra e depois autentica), com a
# chave derivada por PBKDF2-SHA256 (600000 iteracoes, sal aleatorio por arquivo) de uma FRASE lida de uma variavel de ambiente. A frase nunca e impressa,
# gravada nem passada em argumento; so o NOME da variavel aparece em mensagens. Perder a frase = perder o backup cifrado: guarde-a fora da maquina do banco.
#
# Formato do arquivo (.enc): "IABK1" + 3 bytes zero | iteracoes (int32 LE) | sal (16) | IV (16) | texto cifrado (multiplo de 16) | HMAC-SHA256 de tudo que vem antes (32).
# So ASCII (PowerShell 5.1 le .ps1 sem BOM como ANSI). Arquivo para "dot-source" (. .\_Cifra.ps1).

Set-StrictMode -Version Latest

$script:CifraMagica = [byte[]]([System.Text.Encoding]::ASCII.GetBytes('IABK1') + [byte[]](0, 0, 0))
$script:CifraIteracoes = 600000
$script:CifraTamanhoDoCabecalho = 44
$script:CifraTamanhoDoMac = 32
$script:CifraBloco = 1048576

function Get-FraseDeCifra {
    param([Parameter(Mandatory)][string]$ChaveEnv)
    $frase = [Environment]::GetEnvironmentVariable($ChaveEnv)
    if ([string]::IsNullOrEmpty($frase)) { throw ('A variavel de ambiente ' + $ChaveEnv + ' (frase de cifra) nao esta definida.') }
    if ($frase.Length -lt 16) { throw ('A frase de cifra da variavel ' + $ChaveEnv + ' deve ter ao menos 16 caracteres.') }
    return $frase
}

function New-ChavesDeCifra {
    param([Parameter(Mandatory)][string]$Frase, [Parameter(Mandatory)][byte[]]$Sal, [Parameter(Mandatory)][int]$Iteracoes)
    $derivador = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($Frase, $Sal, $Iteracoes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $bytes = $derivador.GetBytes(64) } finally { $derivador.Dispose() }
    return [pscustomobject]@{ Cifra = [byte[]]($bytes[0..31]); Mac = [byte[]]($bytes[32..63]) }
}

# Le ate encher o buffer (ou o limite), ate o fim do arquivo: FileStream.Read pode devolver menos que o pedido.
function Read-Bloco {
    param([Parameter(Mandatory)]$Fluxo, [Parameter(Mandatory)][byte[]]$Buffer, [long]$Limite = -1)
    $alvo = $Buffer.Length
    if ($Limite -ge 0 -and $Limite -lt $alvo) { $alvo = [int]$Limite }
    $total = 0
    while ($total -lt $alvo) {
        $lidos = $Fluxo.Read($Buffer, $total, $alvo - $total)
        if ($lidos -le 0) { break }
        $total += $lidos
    }
    return $total
}

function ConvertTo-Hexadecimal {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    return ([System.BitConverter]::ToString($Bytes)).Replace('-', '')
}

# HMAC-SHA256 dos primeiros $Quantos bytes do arquivo.
function Get-MacDoArquivo {
    param([Parameter(Mandatory)][string]$Caminho, [Parameter(Mandatory)][byte[]]$ChaveDoMac, [Parameter(Mandatory)][long]$Quantos)
    $hmac = New-Object System.Security.Cryptography.HMACSHA256 -ArgumentList (,$ChaveDoMac)
    $fluxo = [System.IO.File]::OpenRead($Caminho)
    try {
        $buffer = New-Object byte[] $script:CifraBloco
        $restante = $Quantos
        while ($restante -gt 0) {
            $lidos = Read-Bloco -Fluxo $fluxo -Buffer $buffer -Limite $restante
            if ($lidos -le 0) { throw 'O arquivo cifrado terminou antes do esperado.' }
            $hmac.TransformBlock($buffer, 0, $lidos, $null, 0) | Out-Null
            $restante -= $lidos
        }
        $hmac.TransformFinalBlock((New-Object byte[] 0), 0, 0) | Out-Null
        return , [byte[]]$hmac.Hash
    }
    finally { $fluxo.Dispose(); $hmac.Dispose() }
}

# Comparacao em tempo constante.
function Test-BytesIguais {
    param([Parameter(Mandatory)][byte[]]$A, [Parameter(Mandatory)][byte[]]$B)
    if ($A.Length -ne $B.Length) { return $false }
    $diferenca = 0
    for ($i = 0; $i -lt $A.Length; $i++) { $diferenca = $diferenca -bor ($A[$i] -bxor $B[$i]) }
    return ($diferenca -eq 0)
}

# Cifra Origem em Destino (que NAO pode existir). Devolve o SHA-256 do original, os tamanhos e o SHA-256 do arquivo cifrado.
function Protect-Arquivo {
    param([Parameter(Mandatory)][string]$Origem, [Parameter(Mandatory)][string]$Destino, [Parameter(Mandatory)][string]$ChaveEnv)
    if (-not (Test-Path $Origem)) { throw ('Arquivo a cifrar nao encontrado: ' + $Origem) }
    if (Test-Path $Destino) { throw ('O destino da cifra ja existe e nao sera sobrescrito: ' + $Destino) }
    $frase = Get-FraseDeCifra -ChaveEnv $ChaveEnv

    $sal = New-Object byte[] 16
    $iv = New-Object byte[] 16
    $gerador = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $gerador.GetBytes($sal)
    $gerador.GetBytes($iv)
    $gerador.Dispose()
    $chaves = New-ChavesDeCifra -Frase $frase -Sal $sal -Iteracoes $script:CifraIteracoes

    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.KeySize = 256
    $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
    $aes.Key = $chaves.Cifra
    $aes.IV = $iv
    $cifrador = $aes.CreateEncryptor()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $entrada = [System.IO.File]::OpenRead($Origem)
    $saida = [System.IO.File]::Create($Destino)
    $concluiu = $false
    try {
        $cabecalho = [byte[]]($script:CifraMagica + [System.BitConverter]::GetBytes([int]$script:CifraIteracoes) + $sal + $iv)
        $saida.Write($cabecalho, 0, $cabecalho.Length)
        $buffer = New-Object byte[] $script:CifraBloco
        $cifrado = New-Object byte[] ($script:CifraBloco + 32)
        $ultimo = $false
        $bytesLidos = 0L
        while (-not $ultimo) {
            $lidos = Read-Bloco -Fluxo $entrada -Buffer $buffer
            $bytesLidos += $lidos
            if ($lidos -lt $buffer.Length) { $ultimo = $true }
            $sha.TransformBlock($buffer, 0, $lidos, $null, 0) | Out-Null
            if ($ultimo) {
                $final = $cifrador.TransformFinalBlock($buffer, 0, $lidos)
                $saida.Write($final, 0, $final.Length)
            }
            else {
                $n = $cifrador.TransformBlock($buffer, 0, $lidos, $cifrado, 0)
                $saida.Write($cifrado, 0, $n)
            }
        }
        $sha.TransformFinalBlock((New-Object byte[] 0), 0, 0) | Out-Null
        $saida.Flush()
        $tamanhoSemMac = $saida.Length
        $saida.Dispose()

        $mac = Get-MacDoArquivo -Caminho $Destino -ChaveDoMac $chaves.Mac -Quantos $tamanhoSemMac
        $anexo = [System.IO.File]::Open($Destino, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
        try { $anexo.Write($mac, 0, $mac.Length) } finally { $anexo.Dispose() }
        $concluiu = $true
        return [pscustomobject]@{
            Sha256DoOriginal = (ConvertTo-Hexadecimal -Bytes ([byte[]]$sha.Hash))
            TamanhoDoOriginal = $bytesLidos
            TamanhoCifrado = (Get-Item $Destino).Length
            Sha256DoCifrado = ((Get-FileHash -Algorithm SHA256 -Path $Destino).Hash)
        }
    }
    finally {
        $entrada.Dispose()
        $saida.Dispose()
        $sha.Dispose()
        $cifrador.Dispose()
        $aes.Dispose()
        [Array]::Clear($chaves.Cifra, 0, $chaves.Cifra.Length)
        [Array]::Clear($chaves.Mac, 0, $chaves.Mac.Length)
        # Cifra que nao terminou nao fica no disco (e um arquivo que ESTA execucao criou).
        if (-not $concluiu -and (Test-Path $Destino)) { Remove-Item -Force -Path $Destino -ErrorAction SilentlyContinue }
    }
}

# Autentica (HMAC) e decifra Origem em Destino (que NAO pode existir). Frase errada ou arquivo alterado: a MESMA falha, antes de produzir qualquer saida.
# Devolve o SHA-256 do texto claro. Sem -Destino, so autentica (nao grava nada) e devolve $null.
function Unprotect-Arquivo {
    param([Parameter(Mandatory)][string]$Origem, [string]$Destino, [Parameter(Mandatory)][string]$ChaveEnv)
    if (-not (Test-Path $Origem)) { throw ('Arquivo cifrado nao encontrado: ' + $Origem) }
    if ($Destino -and (Test-Path $Destino)) { throw ('O destino da decifragem ja existe e nao sera sobrescrito: ' + $Destino) }
    $frase = Get-FraseDeCifra -ChaveEnv $ChaveEnv

    $arquivo = [System.IO.File]::OpenRead($Origem)
    $tamanho = $arquivo.Length
    $minimo = $script:CifraTamanhoDoCabecalho + 16 + $script:CifraTamanhoDoMac
    if ($tamanho -lt $minimo) { $arquivo.Dispose(); throw 'O arquivo nao e um backup cifrado valido (pequeno demais).' }
    $cabecalho = New-Object byte[] $script:CifraTamanhoDoCabecalho
    $arquivo.Read($cabecalho, 0, $cabecalho.Length) | Out-Null
    $macGravado = New-Object byte[] $script:CifraTamanhoDoMac
    $arquivo.Position = $tamanho - $script:CifraTamanhoDoMac
    $arquivo.Read($macGravado, 0, $macGravado.Length) | Out-Null
    $arquivo.Dispose()

    $magica = New-Object byte[] 8
    [Array]::Copy($cabecalho, 0, $magica, 0, 8)
    if (-not (Test-BytesIguais -A $magica -B $script:CifraMagica)) { throw 'O arquivo nao e um backup cifrado por esta ferramenta.' }
    $iteracoes = [System.BitConverter]::ToInt32($cabecalho, 8)
    if ($iteracoes -lt 100000 -or $iteracoes -gt 10000000) { throw 'O arquivo cifrado tem parametros invalidos.' }
    $sal = New-Object byte[] 16
    $iv = New-Object byte[] 16
    [Array]::Copy($cabecalho, 12, $sal, 0, 16)
    [Array]::Copy($cabecalho, 28, $iv, 0, 16)

    $chaves = New-ChavesDeCifra -Frase $frase -Sal $sal -Iteracoes $iteracoes
    $aes = $null
    $decifrador = $null
    $sha = $null
    $entrada = $null
    $saida = $null
    $concluiu = $false
    try {
        # Primeiro autentica, depois decifra: nenhum byte de texto claro existe antes de o MAC conferir.
        $macCalculado = Get-MacDoArquivo -Caminho $Origem -ChaveDoMac $chaves.Mac -Quantos ($tamanho - $script:CifraTamanhoDoMac)
        if (-not (Test-BytesIguais -A $macCalculado -B $macGravado)) { throw 'Falha na autenticacao do backup cifrado: a frase esta errada ou o arquivo foi alterado ou corrompido.' }
        if ([string]::IsNullOrEmpty($Destino)) { $concluiu = $true; return $null }

        $textoCifrado = $tamanho - $script:CifraTamanhoDoCabecalho - $script:CifraTamanhoDoMac
        if ($textoCifrado -lt 16 -or ($textoCifrado % 16) -ne 0) { throw 'O arquivo cifrado tem tamanho invalido.' }

        $aes = [System.Security.Cryptography.Aes]::Create()
        $aes.KeySize = 256
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $chaves.Cifra
        $aes.IV = $iv
        $decifrador = $aes.CreateDecryptor()
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $entrada = [System.IO.File]::OpenRead($Origem)
        $entrada.Position = $script:CifraTamanhoDoCabecalho
        $saida = [System.IO.File]::Create($Destino)
        $buffer = New-Object byte[] $script:CifraBloco
        $claro = New-Object byte[] ($script:CifraBloco + 32)
        $restante = $textoCifrado
        while ($restante -gt 0) {
            $lidos = Read-Bloco -Fluxo $entrada -Buffer $buffer -Limite $restante
            if ($lidos -le 0 -or ($lidos % 16) -ne 0) { throw 'Leitura inesperada do arquivo cifrado.' }
            $restante -= $lidos
            if ($restante -eq 0) {
                $final = $decifrador.TransformFinalBlock($buffer, 0, $lidos)
                $saida.Write($final, 0, $final.Length)
                $sha.TransformBlock($final, 0, $final.Length, $null, 0) | Out-Null
            }
            else {
                $n = $decifrador.TransformBlock($buffer, 0, $lidos, $claro, 0)
                $saida.Write($claro, 0, $n)
                $sha.TransformBlock($claro, 0, $n, $null, 0) | Out-Null
            }
        }
        $sha.TransformFinalBlock((New-Object byte[] 0), 0, 0) | Out-Null
        $saida.Flush()
        $concluiu = $true
        return (ConvertTo-Hexadecimal -Bytes ([byte[]]$sha.Hash))
    }
    finally {
        if ($null -ne $entrada) { $entrada.Dispose() }
        if ($null -ne $saida) { $saida.Dispose() }
        if ($null -ne $sha) { $sha.Dispose() }
        if ($null -ne $decifrador) { $decifrador.Dispose() }
        if ($null -ne $aes) { $aes.Dispose() }
        [Array]::Clear($chaves.Cifra, 0, $chaves.Cifra.Length)
        [Array]::Clear($chaves.Mac, 0, $chaves.Mac.Length)
        # Texto claro parcial nao fica no disco quando a decifragem falha.
        if (-not $concluiu -and $Destino -and (Test-Path $Destino)) { Remove-Item -Force -Path $Destino -ErrorAction SilentlyContinue }
    }
}
