# Comum.ps1 - funcoes compartilhadas dos verificadores de etapa.
# Somente leitura: nenhum comando de escrita ou exclusao. Carregado por dot-source pelos demais scripts.
# O git usa --no-optional-locks para que nem o git status atualize o indice.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RaizDoRepositorio {
    param([string]$Inicio = '')
    if (-not $Inicio) { $Inicio = (Get-Location).Path }
    $saida = git -C $Inicio --no-optional-locks rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0) { throw ('Nao ha repositorio Git em: ' + $Inicio) }
    return ([string]$saida).Trim().Replace('/', '\')
}

function New-Resultado {
    param([string]$Nome, [bool]$Ok, [string]$Detalhe = '')
    return [pscustomobject]@{ Nome = $Nome; Ok = $Ok; Detalhe = $Detalhe }
}

function Write-Resultados {
    param([object[]]$Resultados)
    $falhas = 0
    foreach ($r in $Resultados) {
        if ($r.Ok) {
            Write-Host ('OK     ' + $r.Nome)
        }
        else {
            $falhas++
            Write-Host ('FALHA  ' + $r.Nome)
            if ($r.Detalhe) {
                foreach ($linha in ($r.Detalhe -split "`n")) { Write-Host ('         ' + $linha) }
            }
        }
    }
    Write-Host ('Resumo: {0} verificacao(oes), {1} falha(s).' -f @($Resultados).Count, $falhas)
    return $falhas
}

function Get-StatusDaArvore {
    param([string]$Raiz)
    $linhas = @(git -C $Raiz --no-optional-locks status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'git status falhou.' }
    return @($linhas | Where-Object { $_ -ne '' })
}

function ConvertTo-AlteracaoNormalizada {
    param([string]$LinhaNameStatus)
    $p = $LinhaNameStatus -split "`t"
    $letra = $p[0].Substring(0, 1)
    if (($letra -eq 'R') -or ($letra -eq 'C')) { return ('{0} {1} => {2}' -f $letra, $p[1], $p[2]) }
    return ('{0} {1}' -f $letra, $p[1])
}

# Diferencas da arvore de trabalho em relacao ao HEAD: rastreadas (indice e arquivos) e nao rastreadas.
function Get-AlteracoesDaArvore {
    param([string]$Raiz)
    $conjunto = New-Object 'System.Collections.Generic.SortedSet[string]'
    $diff = @(git -C $Raiz --no-optional-locks diff --name-status -M HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'git diff falhou.' }
    foreach ($l in $diff) {
        if ($l) { [void]$conjunto.Add((ConvertTo-AlteracaoNormalizada -LinhaNameStatus $l)) }
    }
    $novos = @(git -C $Raiz --no-optional-locks ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files falhou.' }
    foreach ($n in $novos) {
        if ($n) { [void]$conjunto.Add('A ' + $n) }
    }
    return @($conjunto)
}

# Diferencas introduzidas por um commit em relacao ao pai.
function Get-AlteracoesDoCommit {
    param([string]$Raiz, [string]$Ref = 'HEAD')
    $conjunto = New-Object 'System.Collections.Generic.SortedSet[string]'
    $diff = @(git -C $Raiz --no-optional-locks diff-tree --no-commit-id --name-status -r -M $Ref)
    if ($LASTEXITCODE -ne 0) { throw 'git diff-tree falhou.' }
    foreach ($l in $diff) {
        if ($l) { [void]$conjunto.Add((ConvertTo-AlteracaoNormalizada -LinhaNameStatus $l)) }
    }
    return @($conjunto)
}

# Arquivo de alteracoes autorizadas: uma por linha ("A caminho", "M caminho", "D caminho",
# "R origem => destino"). Linhas vazias e iniciadas por # sao ignoradas.
function Read-AlteracoesAutorizadas {
    param([string]$Caminho)
    $conjunto = New-Object 'System.Collections.Generic.SortedSet[string]'
    $n = 0
    foreach ($linha in (Get-Content -LiteralPath $Caminho -Encoding UTF8)) {
        $n++
        $t = $linha.Trim()
        if (($t -eq '') -or $t.StartsWith('#')) { continue }
        if (($t -notmatch '^(A|M|D) \S.*$') -and ($t -notmatch '^R \S.* => \S.*$')) {
            throw ('Linha invalida em {0}:{1}: {2}' -f $Caminho, $n, $t)
        }
        [void]$conjunto.Add($t)
    }
    return @($conjunto)
}

function Compare-Alteracoes {
    param([string[]]$Reais, [string[]]$Permitidas, [bool]$ExigirTodas = $true)
    $naoAutorizadas = @($Reais | Where-Object { $Permitidas -notcontains $_ })
    $ausentes = @($Permitidas | Where-Object { $Reais -notcontains $_ })
    $resultados = @()
    $detalhe = ''
    if ($naoAutorizadas.Count -gt 0) { $detalhe = 'Nao autorizadas:' + "`n" + ($naoAutorizadas -join "`n") }
    $resultados += New-Resultado -Nome 'Somente diferencas autorizadas' -Ok ($naoAutorizadas.Count -eq 0) -Detalhe $detalhe
    if ($ExigirTodas) {
        $detalhe = ''
        if ($ausentes.Count -gt 0) { $detalhe = 'Autorizadas e ausentes:' + "`n" + ($ausentes -join "`n") }
        $resultados += New-Resultado -Nome 'Todas as diferencas autorizadas estao presentes' -Ok ($ausentes.Count -eq 0) -Detalhe $detalhe
    }
    return $resultados
}

# Projetos .csproj rastreados pelo git (ignora bin/obj) e suas referencias de projeto.
function Get-GrafoDeProjetos {
    param([string]$Raiz)
    $caminhos = @(git -C $Raiz --no-optional-locks ls-files -- '*.csproj')
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files falhou.' }
    $projetos = @()
    foreach ($rel in $caminhos) {
        if (-not $rel) { continue }
        $completo = Join-Path $Raiz ($rel.Replace('/', '\'))
        $xml = [xml](Get-Content -LiteralPath $completo -Raw -Encoding UTF8)
        $refs = @()
        foreach ($no in $xml.SelectNodes('//ProjectReference')) {
            $alvo = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $completo) $no.GetAttribute('Include')))
            $refs += [System.IO.Path]::GetFileNameWithoutExtension($alvo)
        }
        $projetos += [pscustomobject]@{
            Nome        = [System.IO.Path]::GetFileNameWithoutExtension($completo)
            Caminho     = $rel
            Referencias = $refs
        }
    }
    return $projetos
}

function Get-MatrizDeModulos {
    param([string]$Raiz)
    $caminho = Join-Path $Raiz 'docs\architecture\module-dependencies.json'
    $json = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $caminho -Raw -Encoding UTF8)
    $matriz = @{}
    foreach ($p in $json.modulos.PSObject.Properties) { $matriz[$p.Name] = @($p.Value) }
    return $matriz
}
