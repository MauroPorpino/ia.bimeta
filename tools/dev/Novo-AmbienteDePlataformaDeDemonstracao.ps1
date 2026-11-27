<#
.SYNOPSIS
    Prepara, no banco de DESENVOLVIMENTO, o ambiente de demonstracao da administracao da plataforma BImeta (Etapa 02): o tenant tecnico da plataforma, tres operadores ficticios
    (um por perfil) e duas empresas demonstrativas. Nao toca demo-ia nem demo-usuarios. Repetir e inofensivo.

.DESCRIPTION
    Pre-requisitos: PostgreSQL de desenvolvimento no ar e a API rodando em http://localhost:5155 (perfil simulador-ia ou Development) com a Etapa 02 aplicada.
    O primeiro administrador nasce pelo comando `platform-init` (token de uso unico), exatamente como em producao: aqui o proprio script conclui o primeiro acesso com uma senha
    SINTETICA de desenvolvimento (abaixo). Os tokens de primeiro acesso das empresas demonstrativas sao mostrados UMA vez na saida (para o teste do primeiro acesso do administrador do cliente).
    Nada vai a servico externo.

.EXAMPLE
    .\tools\dev\Novo-AmbienteDePlataformaDeDemonstracao.ps1
#>
[CmdletBinding()]
param(
    [string]$Api = 'http://localhost:5155'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $raiz 'backend\host\IaBimeta.Api\bin\Debug\net10.0\IaBimeta.Api.exe'
if (-not (Test-Path $exe)) { throw "Compile a API antes (dotnet build): $exe nao existe." }

$slug = 'bimeta-platform'
$operadores = @(
    @{ Email = 'admin@bimeta-platform.local'; Nome = 'Administrador BImeta (demo)'; Perfil = 'PlatformAdministrator'; Senha = 'DevLocal-Plataforma-Admin-2026!' },
    @{ Email = 'comercial@bimeta-platform.local'; Nome = 'Operador Comercial (demo)'; Perfil = 'PlatformCommercial'; Senha = 'DevLocal-Plataforma-Comercial-2026!' },
    @{ Email = 'consulta@bimeta-platform.local'; Nome = 'Consulta (demo)'; Perfil = 'PlatformViewer'; Senha = 'DevLocal-Plataforma-Consulta-2026!' }
)

function Invoke-Api([string]$Metodo, [string]$Caminho, $Corpo = $null, [string]$Token = $null, [switch]$SemFalha) {
    $cabecalhos = @{}
    if ($Token) { $cabecalhos['Authorization'] = "Bearer $Token" }
    $parametros = @{ Method = $Metodo; Uri = "$Api$Caminho"; Headers = $cabecalhos; ContentType = 'application/json' }
    if ($null -ne $Corpo) { $parametros['Body'] = ($Corpo | ConvertTo-Json -Depth 6) }
    try { return Invoke-RestMethod @parametros } catch {
        if ($SemFalha) { return $null }
        $detalhe = ''
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $detalhe = $_.ErrorDetails.Message }
        throw "Falha em $Metodo ${Caminho}: $detalhe"
    }
}

# 1) O primeiro administrador da plataforma (comando operacional; repetido, reemite o token enquanto o primeiro acesso nao foi concluido).
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$saida = & $exe platform-init --email $operadores[0].Email --name $operadores[0].Nome 2>&1 | Out-String
$token = [regex]::Match($saida, 'ibr_[A-Za-z0-9_\-]+').Value
if ($token) {
    Invoke-Api POST '/auth/password-reset/redeem' @{ tenant = $slug; email = $operadores[0].Email; token = $token; newPassword = $operadores[0].Senha } | Out-Null
    Write-Host "Primeiro acesso do administrador da plataforma concluido ($($operadores[0].Email))."
}
else {
    Write-Host 'Administrador da plataforma ja existe (primeiro acesso concluido antes).'
}

# 2) Login do administrador e cadastro dos demais operadores.
$login = Invoke-Api POST '/platform/auth/login' @{ email = $operadores[0].Email; password = $operadores[0].Senha }
$adm = $login.token
foreach ($op in $operadores | Select-Object -Skip 1) {
    $existente = (Invoke-Api GET "/platform/operators?search=$([uri]::EscapeDataString($op.Email))" -Token $adm).items | Where-Object { $_.email -eq $op.Email }
    if ($existente) { Write-Host "Operador $($op.Email) ja existe."; continue }
    $novo = Invoke-Api POST '/platform/operators' @{ email = $op.Email; displayName = $op.Nome; role = $op.Perfil } -Token $adm
    Invoke-Api POST '/auth/password-reset/redeem' @{ tenant = $slug; email = $op.Email; token = $novo.token; newPassword = $op.Senha } | Out-Null
    Write-Host "Operador $($op.Email) ($($op.Perfil)) cadastrado e com primeiro acesso concluido."
}

# 3) Empresas demonstrativas (nao tocam demo-ia nem demo-usuarios). O token do administrador do cliente aparece UMA vez.
$empresas = @(
    @{ slug = 'demo-alfa'; name = 'Laboratorio Demo Alfa'; legalName = 'Laboratorio Demo Alfa Ltda'; document = '11222333000181'; contactName = 'Contato Alfa'; contactEmail = 'contato@demo-alfa.local'; maxUsers = 3; registrationEnabled = $true; adminEmail = 'admin@demo-alfa.local'; adminName = 'Admin Alfa' },
    @{ slug = 'demo-beta'; name = 'Clinica Demo Beta'; legalName = 'Clinica Demo Beta S/A'; document = ''; contactName = 'Contato Beta'; contactEmail = 'contato@demo-beta.local'; maxUsers = 2; registrationEnabled = $true; adminEmail = 'admin@demo-beta.local'; adminName = 'Admin Beta' }
)
foreach ($e in $empresas) {
    $existente = Invoke-Api GET "/platform/companies/$($e.slug)" -Token $adm -SemFalha
    if ($existente) { Write-Host "Empresa $($e.slug) ja existe (situacao: $($existente.company.status))."; continue }
    $r = Invoke-Api POST '/platform/companies' $e -Token $adm
    Write-Host "Empresa $($e.slug) cadastrada. Token de primeiro acesso do administrador $($r.firstAccess.email) (aparece UMA vez, valido ate $($r.firstAccess.expiresAt)):"
    Write-Host "   $($r.firstAccess.token)"
}

Write-Host ''
Write-Host 'Pronto. Entre em http://localhost:5173/plataforma com um dos operadores (senhas sinteticas deste banco de desenvolvimento):'
foreach ($op in $operadores) { Write-Host "  $($op.Perfil): $($op.Email)  /  $($op.Senha)" }
