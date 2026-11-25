<#
.SYNOPSIS
    Provisiona o PRIMEIRO administrador de uma empresa (a BImeta cadastra so este): usuario pendente com token de
    primeiro acesso de uso unico (ate 24 horas), mostrado UMA vez na saida. Consome uma vaga, vale mesmo com o cadastro
    desabilitado e nao se repete (recusa se a empresa ja tem administrador).

.DESCRIPTION
    Chama o comando "first-admin" do Api, na sua maquina, sem rota HTTP. Entregue o token por um canal seguro: a pessoa
    escolhe "Esqueci a senha (tenho um token)" na tela de entrada e define a PROPRIA senha. O token nunca fica em
    arquivo, log ou trilha de auditoria. Para uma empresa nova COMPLETA (organizacao, fila, IA) use Novo-Tenant.ps1.

.EXAMPLE
    .\tools\ops\Novo-PrimeiroAdministrador.ps1 -Empresa lab-exemplo -Email dona@exemplo.com -Nome "Maria da Silva"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Empresa,
    [Parameter(Mandatory = $true)][string]$Email,
    [Parameter(Mandatory = $true)][string]$Nome
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrEmpty($env:ASPNETCORE_ENVIRONMENT)) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }

& dotnet run --project (Join-Path $raiz 'backend\host\IaBimeta.Api') --no-launch-profile -- first-admin --tenant $Empresa --email $Email --name $Nome
exit $LASTEXITCODE
