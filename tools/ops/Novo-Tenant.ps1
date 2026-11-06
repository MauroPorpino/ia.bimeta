<#
.SYNOPSIS
    Coloca uma EMPRESA (tenant) em funcionamento a partir de um arquivo JSON: tenant, administrador, organizacao,
    fila de atendimento humano, agente de IA (instrucoes e perguntas frequentes), IA do tenant (modelo e chave),
    conta de WhatsApp (simulada ou real) e atendentes. Repetir e seguro: completa o que falta e nunca duplica.

.DESCRIPTION
    Chama o comando "provision" do Api (backend\host\IaBimeta.Api): roda na SUA maquina, com acesso ao banco, e nao
    abre porta nem rota HTTP. Nenhum segredo vai no arquivo: o JSON traz so os NOMES das variaveis de ambiente
    (passwordEnv, apiKeyEnv, appSecretEnv, accessTokenEnv, verifyTokenEnv). Com -PedirSegredos, o script pergunta o
    valor de cada variavel ainda nao definida SEM eco (Read-Host -AsSecureString) e a define so para este processo;
    Enter em branco pula (senha de pessoa e token de verificacao sao GERADOS e mostrados uma unica vez no relatorio).

    Pre-requisitos (uma vez): PostgreSQL de desenvolvimento no ar (porta 5433) com a string de conexao nos
    user-secrets do Api, e as chaves mestras (tools\dev\Set-ChaveMestraDosCanais.ps1 e tools\dev\Set-ConfiguracaoDeIa.ps1,
    esta ultima inclui o modelo em AiGateway:Catalog:Models). Passo a passo completo: docs\operations\NOVA-EMPRESA.md.

    O comando le a configuracao do ambiente Development (user-secrets e banco de desenvolvimento). Para outro banco,
    defina ConnectionStrings__Default no terminal antes de rodar.

.PARAMETER Arquivo
    O JSON da empresa (veja tools\ops\empresa-exemplo.json).

.PARAMETER PedirSegredos
    Pergunta, sem eco, as variaveis de ambiente que o arquivo cita e ainda nao estao definidas.

.PARAMETER SemMigrations
    Nao aplica as migrations antes (o padrao aplica: so cria o que falta).

.EXAMPLE
    .\tools\ops\Novo-Tenant.ps1 -Arquivo .\tools\ops\empresa-exemplo.json -PedirSegredos
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Arquivo,
    [switch]$PedirSegredos,
    [switch]$SemMigrations,

    # So confere o arquivo (formato, campos obrigatorios, nomes de variaveis) e mostra quais variaveis faltam. Nao abre o banco e nao altera nada.
    [switch]$SoValidar
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path $Arquivo)) { Write-Host ('FALHA: arquivo nao encontrado: ' + $Arquivo); exit 2 }
$caminho = (Resolve-Path $Arquivo).Path

# Os NOMES das variaveis de ambiente que o arquivo cita (qualquer campo que termine em "Env").
function Get-NomesDeVariaveis {
    param($No)
    $nomes = @()
    if ($null -eq $No) { return $nomes }
    if ($No -is [System.Management.Automation.PSCustomObject]) {
        foreach ($p in $No.PSObject.Properties) {
            if ($p.Name -like '*Env' -and $p.Value -is [string] -and -not [string]::IsNullOrWhiteSpace($p.Value)) { $nomes += $p.Value.Trim() }
            else { $nomes += @(Get-NomesDeVariaveis $p.Value) }
        }
    }
    elseif ($No -is [System.Collections.IEnumerable] -and $No -isnot [string]) {
        foreach ($item in $No) { $nomes += @(Get-NomesDeVariaveis $item) }
    }
    return $nomes
}

# O arquivo aceita comentarios (//), como o comando. O ConvertFrom-Json do PowerShell 5.1 nao aceita: para ler a lista de
# nomes aqui, tira o que vem depois de "//" quando o "//" esta fora de um texto entre aspas.
function Remove-Comentarios {
    param([string]$Linha)
    $dentro = $false
    for ($i = 0; $i -lt $Linha.Length; $i++) {
        $c = $Linha[$i]
        if ($c -eq '\' -and $dentro) { $i++; continue }
        if ($c -eq '"') { $dentro = -not $dentro; continue }
        if (-not $dentro -and $c -eq '/' -and ($i + 1) -lt $Linha.Length -and $Linha[$i + 1] -eq '/') { return $Linha.Substring(0, $i) }
    }
    return $Linha
}
$texto = (Get-Content -Raw -Path $caminho) -split "`n" | ForEach-Object { Remove-Comentarios $_ }
try { $json = ($texto -join "`n") | ConvertFrom-Json } catch { Write-Host ('FALHA: o arquivo nao e um JSON valido: ' + $_.Exception.Message); exit 2 }
$variaveis = @(Get-NomesDeVariaveis $json | Sort-Object -Unique)

$naoDefinidas = @($variaveis | Where-Object { [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($_)) })
if ($PedirSegredos) {
    foreach ($nome in $naoDefinidas) {
        $valor = Read-Host -Prompt ('Valor de ' + $nome + ' (sem eco; Enter pula)') -AsSecureString
        $ponteiro = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($valor)
        try {
            $claro = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ponteiro)
            if (-not [string]::IsNullOrEmpty($claro)) { [Environment]::SetEnvironmentVariable($nome, $claro, 'Process') }
        }
        finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ponteiro) }
    }
    $naoDefinidas = @($variaveis | Where-Object { [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($_)) })
}

if ($naoDefinidas.Count -gt 0) {
    Write-Host 'Variaveis citadas no arquivo e NAO definidas (so os nomes):'
    foreach ($nome in $naoDefinidas) { Write-Host ('  - ' + $nome) }
    Write-Host 'Senha de pessoa e token de verificacao serao GERADOS e mostrados uma vez; chave da IA e credenciais reais da Meta, nao: a etapa avisa ou falha com o nome da variavel.'
    Write-Host ''
}

if ([string]::IsNullOrEmpty($env:ASPNETCORE_ENVIRONMENT)) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }

$argumentos = @('run', '--project', (Join-Path $raiz 'backend\host\IaBimeta.Api'), '--no-launch-profile', '--', 'provision', '--file', $caminho)
if ($SemMigrations) { $argumentos += '--no-migrations' }
if ($SoValidar) { $argumentos += '--validate-only' }

& dotnet @argumentos
$codigo = $LASTEXITCODE

Write-Host ''
switch ($codigo) {
    0 {
        if ($SoValidar) { Write-Host 'Arquivo conferido. Para colocar a empresa em funcionamento, rode de novo sem -SoValidar.' }
        else { Write-Host 'Proximo passo: suba o Api e o Worker (docs\operations\NOVA-EMPRESA.md, secao 4) e entre na interface com o administrador acima.' }
    }
    1 { Write-Host 'Houve falha numa etapa. Corrija o que ela aponta e rode este script de novo: ele completa o que falta.' }
    default { Write-Host 'Uso ou arquivo invalido. Veja as mensagens acima.' }
}
exit $codigo
