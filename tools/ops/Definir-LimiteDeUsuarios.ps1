<#
.SYNOPSIS
    Administracao da PLATAFORMA (BImeta) do limite contratado de usuarios e da habilitacao do cadastro de uma empresa.
    Sem opcoes, so MOSTRA a situacao (limite, cadastro e vagas). A interface da administracao da plataforma chega na Etapa 02.

.DESCRIPTION
    Chama o comando "tenant-users" do Api: roda na SUA maquina, com acesso ao banco, e nao abre rota HTTP (o tenant so
    CONSULTA a situacao e nunca a altera). A alteracao roda sob a mesma trava do cadastro de usuarios (sem corrida com
    um cadastro em andamento) e fica na trilha de auditoria da empresa. Reduzir o limite abaixo do uso NAO desativa
    ninguem: so bloqueia novos ingressos ate voltar ao limite.

.PARAMETER Empresa
    O identificador (slug) da empresa.

.PARAMETER Maximo
    O novo limite contratado de usuarios (1 ou mais).

.PARAMETER Ilimitado
    Excecao explicita e auditada: sem limite de usuarios (ambientes internos e empresas anteriores a Etapa 01).

.PARAMETER Cadastro
    "on" ou "off": permite ou nao o ingresso de novos usuarios (cadastro, JIT, admissoes). Nao afeta quem ja foi admitido.

.EXAMPLE
    .\tools\ops\Definir-LimiteDeUsuarios.ps1 -Empresa lab-exemplo
    .\tools\ops\Definir-LimiteDeUsuarios.ps1 -Empresa lab-exemplo -Maximo 10 -Cadastro on
    .\tools\ops\Definir-LimiteDeUsuarios.ps1 -Empresa lab-exemplo -Cadastro off
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Empresa,
    [int]$Maximo = 0,
    [switch]$Ilimitado,
    [ValidateSet('', 'on', 'off')][string]$Cadastro = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrEmpty($env:ASPNETCORE_ENVIRONMENT)) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }

$argumentos = @('tenant-users', '--tenant', $Empresa)
if ($Maximo -gt 0) { $argumentos += @('--max', $Maximo) }
if ($Ilimitado) { $argumentos += '--unlimited' }
if ($Cadastro -ne '') { $argumentos += @('--registration', $Cadastro) }

& dotnet run --project (Join-Path $raiz 'backend\host\IaBimeta.Api') --no-launch-profile -- @argumentos
exit $LASTEXITCODE
