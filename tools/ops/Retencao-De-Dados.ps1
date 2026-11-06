<#
.SYNOPSIS
    Relatorio e expurgo TECNICO da retencao de dados (ADR 0025): sessoes vencidas, tokens de redefinicao encerrados,
    contadores de login parados, eventos processados do outbox e marcas de idempotencia do Inbox.

.DESCRIPTION
    Chama o comando "retention" do Api (backend\host\IaBimeta.Api): roda na SUA maquina, com acesso ao banco, e nao abre
    porta nem rota HTTP. Sem -Expurgar, so RELATA (nada e alterado): o que cada categoria removeria por empresa, os
    eventos mortos e o inventario de todas as tabelas. Com -Expurgar -Confirmar, remove so o dado tecnico vencido.

    NUNCA toca dado pessoal nem registro de referencia: mensagens, contatos, atendimentos, trilha de auditoria e ledger de
    IA so aparecem no relatorio, porque o expurgo deles depende de decisao juridica e de regras de referencia.

    Os prazos vem da secao Retention da configuracao (padrao e sobrescritas por empresa); veja docs\operations\RETENCAO-DE-DADOS.md.
    Faca BACKUP antes de um expurgo (tools\ops\Backup-Banco.ps1). Le a configuracao do ambiente Development (user-secrets
    e banco de desenvolvimento); para outro banco, defina ConnectionStrings__Default no terminal antes de rodar.

.EXAMPLE
    .\tools\ops\Retencao-De-Dados.ps1
    .\tools\ops\Retencao-De-Dados.ps1 -Expurgar -Confirmar
#>
[CmdletBinding()]
param(
    [switch]$Expurgar,
    [switch]$Confirmar
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Expurgar -and -not $Confirmar) {
    Write-Host 'O expurgo so roda com -Confirmar. Rode antes sem -Expurgar para ver o que sera removido.'
    exit 2
}

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrEmpty($env:ASPNETCORE_ENVIRONMENT)) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }

$argumentos = @('run', '--project', (Join-Path $raiz 'backend\host\IaBimeta.Api'), '--no-launch-profile', '--', 'retention')
if ($Expurgar) { $argumentos += @('purge', '--confirm') } else { $argumentos += 'report' }

& dotnet @argumentos
exit $LASTEXITCODE
