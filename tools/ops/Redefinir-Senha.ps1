<#
.SYNOPSIS
    Emite, na sua maquina, o TOKEN de redefinicao de senha de um usuario (uso unico, validade curta). Para quando nao ha
    servico de e-mail ou quando nenhum administrador da empresa consegue fazer a emissao pela interface.

.DESCRIPTION
    Chama o comando "reset-password" do Api (backend\host\IaBimeta.Api): roda na SUA maquina, com acesso ao banco, e nao
    abre porta nem rota HTTP. O token aparece UMA vez, so no terminal; nao vai a log nem a arquivo, e no banco fica so o
    hash. Entregue-o a pessoa por um canal seguro. Quem usa: POST /auth/password-reset/redeem com empresa, e-mail, token
    e a nova senha (todas as sessoes da pessoa caem). Emitir outro token invalida o anterior.

    Le a configuracao do ambiente Development (user-secrets e banco de desenvolvimento). Para outro banco, defina
    ConnectionStrings__Default no terminal antes de rodar.

.EXAMPLE
    .\tools\ops\Redefinir-Senha.ps1 -Empresa lab-exemplo -Email ana@exemplo.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Empresa,
    [Parameter(Mandatory = $true)][string]$Email
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrEmpty($env:ASPNETCORE_ENVIRONMENT)) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }

& dotnet run --project (Join-Path $raiz 'backend\host\IaBimeta.Api') --no-launch-profile -- reset-password --tenant $Empresa --email $Email
exit $LASTEXITCODE
