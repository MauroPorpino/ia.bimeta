<#
.SYNOPSIS
    Verificador estatico (AST) de scripts PowerShell. Somente leitura: nao executa os scripts analisados.

.DESCRIPTION
    Rejeita: erro de sintaxe, caractere nao ASCII, alias, funcao que colide com alias/cmdlet/aplicativo
    ou que se repete, comando fora da lista permitida, comando dinamico, redirecionamento para arquivo,
    metodo .NET de escrita ou exclusao, subcomando de git ou dotnet fora da lista.
    Perfil Leitura (padrao): so leitura. Perfil Preparacao: acrescenta criar pasta, copiar e gravar
    arquivos, git bundle create e git switch -c. Remocao e movimentacao nunca sao permitidas.

.EXAMPLE
    .\Test-SegurancaDeScript.ps1 -Caminho .\tools\verificacao
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Caminho,
    [ValidateSet('Leitura', 'Preparacao')][string]$Perfil = 'Leitura'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$achados = New-Object 'System.Collections.Generic.List[string]'

function Add-Achado {
    param([string]$Arquivo, [int]$Linha, [string]$Texto)
    $achados.Add(('{0}:{1}: {2}' -f $Arquivo, $Linha, $Texto))
}

function Test-ComandoGit {
    param($Comando, [string]$Arquivo)
    $linha = $Comando.Extent.StartLineNumber
    $el = @($Comando.CommandElements)
    $i = 1
    $sub = $null
    while ($i -lt $el.Count) {
        $t = $el[$i].Extent.Text
        if ($t -eq '-C') { $i += 2; continue }
        if ($t.StartsWith('-')) { $i++; continue }
        $sub = $t
        break
    }
    if ($null -eq $sub) {
        $soVersao = $false
        foreach ($e in $el) { if ($e.Extent.Text -eq '--version') { $soVersao = $true } }
        if (-not $soVersao) { Add-Achado $Arquivo $linha 'git sem subcomando literal' }
        return
    }
    $resto = @()
    for ($j = $i + 1; $j -lt $el.Count; $j++) { $resto += $el[$j].Extent.Text }
    $leitura = @('status', 'diff', 'log', 'ls-files', 'ls-tree', 'rev-parse', 'show', 'cat-file', 'describe', 'merge-base', 'rev-list', 'for-each-ref', 'check-ignore', 'diff-tree', 'count-objects')
    $ok = $false
    if ($leitura -contains $sub) { $ok = $true }
    elseif ($sub -eq 'hash-object') { $ok = ($resto -notcontains '-w') }
    elseif ($sub -eq 'branch') { $ok = (($resto -contains '--show-current') -or ($resto -contains '--list')) }
    elseif ($sub -eq 'config') { $ok = (($resto -contains '--get') -or ($resto -contains '--get-all') -or ($resto -contains '--list') -or ($resto -contains '-l') -or ($resto -contains '--get-regexp')) }
    elseif ($sub -eq 'remote') { $ok = (($resto -contains '-v') -or ($resto -contains 'get-url')) }
    elseif ($sub -eq 'bundle') { $ok = (($resto -contains 'verify') -or ($resto -contains 'list-heads') -or (($Perfil -eq 'Preparacao') -and ($resto -contains 'create'))) }
    elseif ($sub -eq 'switch') { $ok = (($Perfil -eq 'Preparacao') -and ($resto -contains '-c')) }
    foreach ($r in $resto) { if ($r -like '--output*') { $ok = $false } }
    if (-not $ok) { Add-Achado $Arquivo $linha ('git ' + $sub + ' fora da lista permitida para o perfil ' + $Perfil) }
}

function Test-ComandoDotnet {
    param($Comando, [string]$Arquivo)
    $linha = $Comando.Extent.StartLineNumber
    $el = @($Comando.CommandElements)
    $textos = @()
    for ($j = 1; $j -lt $el.Count; $j++) { $textos += $el[$j].Extent.Text }
    $ok = $false
    if ($textos.Count -ge 1) {
        $p = $textos[0]
        if (@('--version', '--list-sdks', '--list-runtimes', '--info') -contains $p) { $ok = $true }
        elseif ($p -eq 'test') { $ok = $true }
        elseif (($p -eq 'sln') -and ($textos -contains 'list')) { $ok = $true }
    }
    if (-not $ok) { Add-Achado $Arquivo $linha 'dotnet fora da lista permitida (so: --version, --list-sdks, sln ... list, test)' }
}

$permitidos = @('Get-*', 'Test-Path', 'Select-String', 'Select-Object', 'Where-Object', 'ForEach-Object', 'Sort-Object', 'Group-Object', 'Measure-Object', 'Compare-Object', 'Format-Table', 'Format-List', 'Out-String', 'Out-Null', 'Write-Host', 'Write-Output', 'Write-Warning', 'Write-Error', 'Write-Verbose', 'ConvertFrom-Json', 'ConvertTo-Json', 'Join-Path', 'Split-Path', 'Resolve-Path', 'Set-Location', 'Push-Location', 'Pop-Location', 'Set-StrictMode', 'New-Object')
if ($Perfil -eq 'Preparacao') { $permitidos += @('New-Item', 'Copy-Item', 'Set-Content', 'Add-Content', 'Out-File') }

$membrosProibidos = @('Delete', 'WriteAllText', 'WriteAllLines', 'WriteAllBytes', 'AppendAllText', 'AppendAllLines', 'Move', 'MoveTo', 'Copy', 'CopyTo', 'CreateDirectory', 'Create', 'SetAttributes', 'SetLastWriteTime', 'Kill', 'Start')
if ($Perfil -eq 'Preparacao') { $membrosProibidos = @($membrosProibidos | Where-Object { @('WriteAllText', 'WriteAllLines') -notcontains $_ }) }

$padraoProibido = '^(Remove|Clear|Move|Rename|Start|Stop|Restart|Register|Unregister|Install|Uninstall|Update|Save|Export|Import|Invoke|Set-Item|Set-Content|Add-Content|Out-File|New-Item|Copy-Item|Tee-Object|Add-Type)'

# Resolve os caminhos (arquivo ou pasta com *.ps1).
$arquivos = @()
foreach ($c in $Caminho) {
    if (Test-Path -LiteralPath $c -PathType Container) {
        $arquivos += @(Get-ChildItem -LiteralPath $c -Filter '*.ps1' -File | Select-Object -ExpandProperty FullName)
    }
    else {
        $arquivos += (Resolve-Path -LiteralPath $c).Path
    }
}
if ($arquivos.Count -eq 0) { throw 'Nenhum arquivo .ps1 encontrado em -Caminho.' }

# Primeira passada: ASCII, sintaxe, cabecalho e funcoes definidas.
$analises = @()
$funcoes = @{}
$ehFuncao = { param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }
foreach ($arq in $arquivos) {
    $curto = Split-Path -Leaf $arq
    $bytes = [System.IO.File]::ReadAllBytes($arq)
    $numero = 1
    foreach ($b in $bytes) {
        if ($b -eq 10) { $numero++ }
        elseif ($b -gt 127) {
            Add-Achado $curto $numero 'caractere nao ASCII (o PowerShell 5.1 le script sem BOM como ANSI)'
            break
        }
    }
    $texto = [System.IO.File]::ReadAllText($arq)
    if ($texto -notmatch 'Set-StrictMode\s+-Version\s+Latest') { Add-Achado $curto 1 'falta Set-StrictMode -Version Latest' }
    if ($texto -notmatch "\`$ErrorActionPreference\s*=\s*'Stop'") { Add-Achado $curto 1 "falta `$ErrorActionPreference = 'Stop'" }
    $tokens = $null
    $erros = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($arq, [ref]$tokens, [ref]$erros)
    foreach ($e in $erros) { Add-Achado $curto $e.Extent.StartLineNumber ('erro de sintaxe: ' + $e.Message) }
    foreach ($f in $ast.FindAll($ehFuncao, $true)) {
        if ($funcoes.ContainsKey($f.Name)) { Add-Achado $curto $f.Extent.StartLineNumber ('funcao repetida: ' + $f.Name) }
        else { $funcoes[$f.Name] = $curto }
        $outro = @(Get-Command -Name $f.Name -ErrorAction SilentlyContinue | Where-Object { $_.CommandType -ne 'Function' })
        if ($outro.Count -gt 0) {
            Add-Achado $curto $f.Extent.StartLineNumber ('funcao ' + $f.Name + ' colide com ' + $outro[0].CommandType + ' ' + $outro[0].Definition)
        }
    }
    $analises += [pscustomobject]@{ Curto = $curto; Ast = $ast }
}

# Segunda passada: comandos, membros .NET e redirecionamentos.
$ehComando = { param($n) $n -is [System.Management.Automation.Language.CommandAst] }
$ehMembro = { param($n) $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }
$ehRedirecao = { param($n) $n -is [System.Management.Automation.Language.FileRedirectionAst] }
foreach ($a in $analises) {
    $curto = $a.Curto
    foreach ($c in $a.Ast.FindAll($ehComando, $true)) {
        $linha = $c.Extent.StartLineNumber
        if ($c.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Dot) {
            if ($c.CommandElements[0].Extent.Text -notmatch 'Comum\.ps1') { Add-Achado $curto $linha 'dot-source so e permitido para Comum.ps1' }
            continue
        }
        $nome = $c.GetCommandName()
        if (-not $nome) {
            $primeiro = $c.CommandElements[0].Extent.Text
            $chamaScriptIrmao = ($c.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Ampersand) -and ($primeiro -match '^"\$PSScriptRoot\\[A-Za-z-]+\.ps1"$')
            if (-not $chamaScriptIrmao) { Add-Achado $curto $linha ('comando dinamico: ' + $primeiro) }
            continue
        }
        $resolvido = Get-Command -Name $nome -ErrorAction SilentlyContinue | Select-Object -First 1
        $alvo = $nome
        if ($resolvido -and ($resolvido.CommandType -eq 'Alias')) {
            Add-Achado $curto $linha ('alias ' + $nome + ' (resolve para ' + $resolvido.Definition + ')')
            $alvo = $resolvido.Definition
        }
        if ($alvo -eq 'git') { Test-ComandoGit -Comando $c -Arquivo $curto; continue }
        if ($alvo -eq 'dotnet') { Test-ComandoDotnet -Comando $c -Arquivo $curto; continue }
        $permitido = $funcoes.ContainsKey($alvo)
        # Chamada a outro verificador desta pasta por caminho literal absoluto (ele proprio passa pelo AST).
        if (-not $permitido -and ($alvo -match '^[A-Za-z]:\\.*\\tools\\verificacao\\(Test|Get)-[A-Za-z]+\.ps1$')) { $permitido = $true }
        if (-not $permitido) {
            foreach ($padrao in $permitidos) { if ($alvo -like $padrao) { $permitido = $true; break } }
        }
        if (-not $permitido) {
            if ($alvo -match $padraoProibido) { Add-Achado $curto $linha ('comando de escrita, exclusao ou execucao proibido: ' + $alvo) }
            else { Add-Achado $curto $linha ('comando fora da lista permitida: ' + $alvo) }
            continue
        }
        $textosDoComando = ($c.CommandElements | ForEach-Object { $_.Extent.Text }) -join ' '
        if (($alvo -eq 'New-Object') -and ($textosDoComando -match 'FileStream|StreamWriter|BinaryWriter|IO\.File')) {
            Add-Achado $curto $linha 'New-Object de tipo de escrita em arquivo'
        }
        if (($alvo -eq 'New-Item') -and (($textosDoComando -notmatch '-ItemType') -or ($textosDoComando -notmatch 'Directory'))) {
            Add-Achado $curto $linha 'New-Item so e permitido com -ItemType Directory'
        }
    }
    foreach ($m in $a.Ast.FindAll($ehMembro, $true)) {
        $membro = $m.Member.Extent.Text
        if ($membrosProibidos -contains $membro) { Add-Achado $curto $m.Extent.StartLineNumber ('metodo .NET proibido: ' + $membro) }
    }
    foreach ($r in $a.Ast.FindAll($ehRedirecao, $true)) {
        if ($r.Location.Extent.Text -ne '$null') { Add-Achado $curto $r.Extent.StartLineNumber 'redirecionamento para arquivo proibido (use $null ou o chamador redireciona)' }
    }
}

Write-Host ('Perfil: {0}. Arquivos analisados: {1}' -f $Perfil, $arquivos.Count)
foreach ($a in $analises) { Write-Host ('  ' + $a.Curto) }
if ($achados.Count -eq 0) {
    Write-Host 'Nenhum problema encontrado.'
    exit 0
}
Write-Host ('Problemas encontrados: ' + $achados.Count)
foreach ($t in $achados) { Write-Host ('  ' + $t) }
exit 1
