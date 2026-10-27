<#
.SYNOPSIS
    Confere o grafo de ProjectReference dos .csproj rastreados com a matriz module-dependencies.json.
    Somente leitura. Versao provisoria em PowerShell das regras ARQ-04, ARQ-05, ARQ-06 e DEP-4/6;
    os testes C# do ArchitectureTests (etapa M3) a substituem.

.PARAMETER Legado
    Nomes de projetos legados, fora das regras, que devem desaparecer na migracao.

.PARAMETER ExigirModulos
    Falha se nenhum projeto de modulo existir (guarda contra regra que passa em conjunto vazio).
#>
[CmdletBinding()]
param(
    [string]$Raiz = '',
    [string[]]$Legado = @(),
    [switch]$ExigirModulos
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Comum.ps1"

if (-not $Raiz) { $Raiz = Get-RaizDoRepositorio }

function Get-ClasseDoProjeto {
    param([string]$Nome, [string[]]$ListaLegado, [string[]]$Modulos)
    if ($ListaLegado -contains $Nome) { return [pscustomobject]@{ Tipo = 'Legado'; Modulo = ''; Camada = '' } }
    if (@('IaBimeta.Api', 'IaBimeta.Worker') -contains $Nome) { return [pscustomobject]@{ Tipo = 'Host'; Modulo = ''; Camada = '' } }
    if (@('IaBimeta.SharedKernel', 'IaBimeta.Messaging') -contains $Nome) { return [pscustomobject]@{ Tipo = 'Bloco'; Modulo = ''; Camada = '' } }
    if ($Nome -like 'IaBimeta.Adapters.*') { return [pscustomobject]@{ Tipo = 'Adaptador'; Modulo = ''; Camada = '' } }
    if ($Nome -like '*Tests') { return [pscustomobject]@{ Tipo = 'Teste'; Modulo = ''; Camada = '' } }
    $m = [regex]::Match($Nome, '^IaBimeta\.([A-Za-z]+)\.(Contracts|Core|Infrastructure|Presentation)$')
    if ($m.Success -and ($Modulos -contains $m.Groups[1].Value)) {
        return [pscustomobject]@{ Tipo = 'Modulo'; Modulo = $m.Groups[1].Value; Camada = $m.Groups[2].Value }
    }
    return [pscustomobject]@{ Tipo = 'NaoClassificado'; Modulo = ''; Camada = '' }
}

function Find-CicloDeProjetos {
    param([string]$No, [hashtable]$PorNome, [hashtable]$Estado, [object]$Pilha)
    $Estado[$No] = 1
    $Pilha.Add($No)
    foreach ($r in $PorNome[$No].Referencias) {
        if (-not $PorNome.ContainsKey($r)) { continue }
        if ($Estado.ContainsKey($r) -and ($Estado[$r] -eq 1)) { return ((@($Pilha) + $r) -join ' -> ') }
        if (-not $Estado.ContainsKey($r)) {
            $achado = Find-CicloDeProjetos -No $r -PorNome $PorNome -Estado $Estado -Pilha $Pilha
            if ($achado) { return $achado }
        }
    }
    $Pilha.RemoveAt($Pilha.Count - 1)
    $Estado[$No] = 2
    return $null
}

$grafo = @(Get-GrafoDeProjetos -Raiz $Raiz)
$matriz = Get-MatrizDeModulos -Raiz $Raiz
$modulos = @($matriz.Keys)

$porNome = @{}
$classe = @{}
foreach ($p in $grafo) {
    $porNome[$p.Nome] = $p
    $classe[$p.Nome] = Get-ClasseDoProjeto -Nome $p.Nome -ListaLegado $Legado -Modulos $modulos
}

Write-Host ('Projetos rastreados: ' + $grafo.Count)
foreach ($p in ($grafo | Sort-Object Nome)) {
    $refs = ($p.Referencias -join ', ')
    Write-Host ('  {0} [{1}] -> {2}' -f $p.Nome, $classe[$p.Nome].Tipo, $(if ($refs) { $refs } else { '(nenhuma)' }))
}

$violacoes = New-Object 'System.Collections.Generic.List[string]'
$inexistentes = New-Object 'System.Collections.Generic.List[string]'
foreach ($p in $grafo) {
    $de = $classe[$p.Nome]
    foreach ($refNome in $p.Referencias) {
        if (-not $porNome.ContainsKey($refNome)) { $inexistentes.Add($p.Nome + ' -> ' + $refNome); continue }
        $para = $classe[$refNome]
        if (($de.Tipo -eq 'Legado') -or ($para.Tipo -eq 'Legado')) { continue }
        $aresta = $p.Nome + ' -> ' + $refNome
        # ARQ-04: so hosts (e testes) referenciam Infrastructure, Presentation e adaptadores.
        $alvoRestrito = ($para.Tipo -eq 'Adaptador') -or (($para.Tipo -eq 'Modulo') -and (@('Infrastructure', 'Presentation') -contains $para.Camada))
        if ($alvoRestrito -and (@('Host', 'Teste') -notcontains $de.Tipo)) { $violacoes.Add('ARQ-04|' + $aresta) }
        # ARQ-05 e ARQ-06: entre modulos, so Contracts, e so as arestas da matriz.
        if (($de.Tipo -eq 'Modulo') -and ($para.Tipo -eq 'Modulo') -and ($de.Modulo -ne $para.Modulo)) {
            if ($para.Camada -ne 'Contracts') { $violacoes.Add('ARQ-05|' + $aresta) }
            if ($matriz[$de.Modulo] -notcontains $para.Modulo) { $violacoes.Add('ARQ-06|' + $aresta) }
        }
        # Contracts so referenciam Contracts e blocos de construcao.
        if (($de.Tipo -eq 'Modulo') -and ($de.Camada -eq 'Contracts') -and ($para.Tipo -eq 'Modulo') -and ($para.Camada -ne 'Contracts')) { $violacoes.Add('CTR|' + $aresta) }
        # Adaptadores referenciam so o Contracts do dono.
        if (($de.Tipo -eq 'Adaptador') -and ($para.Tipo -eq 'Modulo') -and ($para.Camada -ne 'Contracts')) { $violacoes.Add('DEP-2|' + $aresta) }
        # DEP-4 e DEP-6: blocos de construcao nao referenciam modulos, hosts nem adaptadores.
        if (($de.Tipo -eq 'Bloco') -and (@('Modulo', 'Host', 'Adaptador') -contains $para.Tipo)) { $violacoes.Add('DEP-4|' + $aresta) }
        if (($p.Nome -eq 'IaBimeta.SharedKernel') -and ($para.Tipo -ne 'Teste')) { $violacoes.Add('DEP-4|' + $aresta + ' (SharedKernel nao referencia projeto algum)') }
        # Ninguem referencia um host, exceto testes.
        if (($para.Tipo -eq 'Host') -and ($de.Tipo -ne 'Teste')) { $violacoes.Add('HOST|' + $aresta) }
    }
}

$resultados = @()
$naoClassificados = @($grafo | Where-Object { $classe[$_.Nome].Tipo -eq 'NaoClassificado' } | ForEach-Object { $_.Nome })
$resultados += New-Resultado -Nome 'Todo projeto esta classificado (modulo, host, bloco, adaptador, teste ou legado declarado)' -Ok ($naoClassificados.Count -eq 0) -Detalhe ($naoClassificados -join "`n")
$resultados += New-Resultado -Nome 'Toda referencia de projeto aponta para projeto rastreado' -Ok ($inexistentes.Count -eq 0) -Detalhe ($inexistentes -join "`n")

$ciclo = $null
$estado = @{}
foreach ($p in $grafo) {
    if (-not $estado.ContainsKey($p.Nome)) {
        $pilha = New-Object 'System.Collections.Generic.List[string]'
        $achado = Find-CicloDeProjetos -No $p.Nome -PorNome $porNome -Estado $estado -Pilha $pilha
        if ($achado) { $ciclo = $achado; break }
    }
}
$resultados += New-Resultado -Nome 'Grafo de projetos sem ciclo' -Ok ($null -eq $ciclo) -Detalhe ([string]$ciclo)

$regras = @(
    @('ARQ-04', 'ARQ-04: so hosts e testes referenciam Infrastructure, Presentation e adaptadores'),
    @('ARQ-05', 'ARQ-05: referencia entre modulos so para .Contracts'),
    @('ARQ-06', 'ARQ-06: arestas entre modulos iguais as da matriz module-dependencies.json'),
    @('CTR', 'Contracts so referenciam Contracts e blocos de construcao'),
    @('DEP-2', 'DEP-2: adaptadores referenciam so o .Contracts do dono'),
    @('DEP-4', 'DEP-4/6: blocos de construcao nao referenciam modulos, hosts nem adaptadores'),
    @('HOST', 'Nenhum projeto (exceto testes) referencia um host')
)
foreach ($r in $regras) {
    $dessaRegra = @($violacoes | Where-Object { $_.StartsWith($r[0] + '|') } | ForEach-Object { $_.Substring($r[0].Length + 1) })
    $resultados += New-Resultado -Nome $r[1] -Ok ($dessaRegra.Count -eq 0) -Detalhe ($dessaRegra -join "`n")
}

$qtdModulos = @($grafo | Where-Object { $classe[$_.Nome].Tipo -eq 'Modulo' }).Count
Write-Host ('Projetos de modulo: {0}; legados declarados: {1}' -f $qtdModulos, @($grafo | Where-Object { $classe[$_.Nome].Tipo -eq 'Legado' }).Count)
if ($ExigirModulos) {
    $resultados += New-Resultado -Nome 'Existe ao menos um projeto de modulo (guarda contra conjunto vazio)' -Ok ($qtdModulos -gt 0)
}

$falhas = Write-Resultados -Resultados $resultados
if ($falhas -gt 0) { exit 1 }
exit 0
