<#
.SYNOPSIS
    Sondagem SOMENTE LEITURA de um servidor Ollama (real ou simulado): versao, modelos instalados e, para cada modelo
    pedido, se esta instalado e declara a capacidade de conversa (completion). Nao baixa, nao instala e nao altera nada.

.DESCRIPTION
    So aceita destinos de laco local (localhost, 127.0.0.1, ::1) com HTTP; para outro destino exige HTTPS. Nenhum
    redirecionamento e seguido e nenhuma chave e enviada.

.EXAMPLE
    .\tools\dev\Test-ServidorOllama.ps1 -Modelo qwen3:4b
    .\tools\dev\Test-ServidorOllama.ps1 -Url http://127.0.0.1:5201/ -Modelo qwen3:4b
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://127.0.0.1:11434/',
    [string[]]$Modelo = @('qwen3:4b')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$uri = $null
if (-not [System.Uri]::TryCreate($Url, [System.UriKind]::Absolute, [ref]$uri)) { Write-Host 'RECUSADO: a URL e invalida.'; exit 2 }
$ip = $null
$local = $uri.Host -ieq 'localhost' -or ([System.Net.IPAddress]::TryParse($uri.Host.Trim('[', ']'), [ref]$ip) -and [System.Net.IPAddress]::IsLoopback($ip))
if (-not ($uri.Scheme -eq 'https' -or ($uri.Scheme -eq 'http' -and $local))) { Write-Host 'RECUSADO: use HTTP so em laco local; outro destino exige HTTPS.'; exit 2 }
$base = $uri.AbsoluteUri.TrimEnd('/')

try {
    $versao = Invoke-RestMethod -Uri ($base + '/api/version') -TimeoutSec 5 -MaximumRedirection 0
    Write-Host ('Servidor acessivel. Versao: ' + $versao.version)
    $tags = Invoke-RestMethod -Uri ($base + '/api/tags') -TimeoutSec 10 -MaximumRedirection 0
    $instalados = @($tags.models | ForEach-Object { $_.name })
    Write-Host ('Modelos instalados (' + $instalados.Count + '): ' + ($instalados -join ', '))
    foreach ($m in $Modelo) {
        if ($instalados -notcontains $m -and $instalados -notcontains ($m + ':latest')) { Write-Host ('  ' + $m + ': NAO instalado (nada sera baixado por este script)'); continue }
        $corpo = ConvertTo-Json -InputObject @{ model = $m } -Compress
        $show = Invoke-RestMethod -Method Post -Uri ($base + '/api/show') -Body $corpo -ContentType 'application/json' -TimeoutSec 20 -MaximumRedirection 0
        $caps = @($show.capabilities)
        $compativel = $caps -contains 'completion'
        Write-Host ('  ' + $m + ': instalado; capacidades = ' + ($caps -join ', ') + '; compativel com conversa = ' + $compativel)
    }
}
catch {
    Write-Host 'FALHA: o servidor nao respondeu como um Ollama (ou nao esta no ar).'
    exit 1
}
