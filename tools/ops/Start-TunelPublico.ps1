<#
.SYNOPSIS
    Abre um TUNEL HTTPS PUBLICO para a porta do webhook do Api (Cloudflare "quick tunnel": sem conta, sem instalador,
    so saida de rede), imprime a URL publica e mantem o tunel ate voce apertar Ctrl-C.

.DESCRIPTION
    SEGURANCA: o tunel aponta para a porta PUBLICA do Api (padrao 5156), nunca para a 5155. O Api, com o perfil
    homologacao-meta, serve nessa porta SO o caminho /webhooks/whatsapp/...; o resto (login, simulador, cabecalho
    X-Dev-Tenant) responde 404. Mesmo assim o webhook continua exigindo a assinatura do corpo (HMAC com o App Secret).

    A URL muda a CADA execucao (e some quando o tunel para): depois de abrir o tunel, cadastre a URL de callback de novo
    no painel da Meta (e defina Channels__Webhook__PublicBaseUrl no terminal do Api, antes de subi-lo).
    O quick tunnel nao tem garantia de disponibilidade e nao e para producao.

    Obter o cloudflared.exe (binario unico, sem instalador): use -Baixar (baixa do GitHub oficial da Cloudflare para
    C:\Projetos\ferramentas\cloudflared\ e mostra o SHA-256 para voce conferir na pagina da release) ou baixe voce mesmo
    em https://developers.cloudflare.com/tunnel/downloads/ e passe -Cloudflared <caminho>.

.EXAMPLE
    .\tools\ops\Start-TunelPublico.ps1 -Baixar      # primeira vez
    .\tools\ops\Start-TunelPublico.ps1              # nas proximas
#>
[CmdletBinding()]
param(
    [int]$Porta = 5156,
    [string]$Cloudflared = '',
    [switch]$Baixar,

    # Baixa (se faltar) o cloudflared e SAI, sem abrir o tunel.
    [switch]$SomenteBaixar,
    [int]$EsperaEmSegundos = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pastaPadrao = 'C:\Projetos\ferramentas\cloudflared'
$exePadrao = Join-Path $pastaPadrao 'cloudflared.exe'

if ($Porta -eq 5155) { throw 'Nao aponte o tunel para a porta 5155 (a do uso local, com login e simulador). Use a porta publica (5156).' }

$exe = $Cloudflared
if ([string]::IsNullOrEmpty($exe)) {
    if (Test-Path $exePadrao) { $exe = $exePadrao }
    else {
        $achado = Get-Command cloudflared -ErrorAction SilentlyContinue
        if ($null -ne $achado) { $exe = $achado.Source }
    }
}

if ([string]::IsNullOrEmpty($exe) -or -not (Test-Path $exe)) {
    if (-not $Baixar) {
        Write-Host 'cloudflared.exe nao encontrado.'
        Write-Host ('Rode de novo com -Baixar (baixa o binario oficial para ' + $pastaPadrao + '), ou baixe em')
        Write-Host '  https://developers.cloudflare.com/tunnel/downloads/  e passe -Cloudflared <caminho>.'
        exit 1
    }
    New-Item -ItemType Directory -Force -Path $pastaPadrao | Out-Null
    $origem = 'https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe'
    Write-Host ('Baixando ' + $origem)
    Invoke-WebRequest -Uri $origem -OutFile $exePadrao -UseBasicParsing
    $exe = $exePadrao
    $hash = (Get-FileHash -Algorithm SHA256 -Path $exe).Hash
    Write-Host ('SHA-256 do arquivo baixado: ' + $hash)
    Write-Host '(confira com o valor da release em https://github.com/cloudflare/cloudflared/releases/latest antes de confiar no binario)'
}

if ($SomenteBaixar) {
    Write-Host ('cloudflared pronto em ' + $exe + '. Para abrir o tunel, rode: .\tools\ops\Start-TunelPublico.ps1')
    exit 0
}

$saida = Join-Path ([System.IO.Path]::GetTempPath()) ('tunel-out-' + [guid]::NewGuid().ToString('N') + '.log')
$erro = Join-Path ([System.IO.Path]::GetTempPath()) ('tunel-err-' + [guid]::NewGuid().ToString('N') + '.log')
$processo = $null
try {
    # O cloudflared escreve o log (e a URL) em STDERR.
    $argumentos = @('tunnel', '--no-autoupdate', '--url', ('http://localhost:' + $Porta))
    $processo = Start-Process -FilePath $exe -ArgumentList $argumentos -PassThru -WindowStyle Hidden -RedirectStandardOutput $saida -RedirectStandardError $erro

    $url = $null
    $limite = (Get-Date).AddSeconds($EsperaEmSegundos)
    while ((Get-Date) -lt $limite -and $null -eq $url) {
        Start-Sleep -Milliseconds 500
        if ($processo.HasExited) { throw ('O cloudflared terminou sozinho (codigo ' + $processo.ExitCode + '). Veja: ' + $erro) }
        foreach ($arquivo in @($erro, $saida)) {
            if (Test-Path $arquivo) {
                $achado = Select-String -Path $arquivo -Pattern 'https://[a-z0-9-]+\.trycloudflare\.com' -AllMatches -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($null -ne $achado) { $url = $achado.Matches[0].Value; break }
            }
        }
    }
    if ($null -eq $url) { throw 'O tunel nao informou a URL publica a tempo (rede bloqueada?).' }

    Write-Host ''
    Write-Host '=== TUNEL PUBLICO NO AR ==='
    Write-Host ('  URL publica (base)     : ' + $url)
    Write-Host ('  Aponta para            : http://localhost:' + $Porta + '  (so o webhook; o resto responde 404)')
    Write-Host ('  Callback para a Meta   : ' + $url + '/webhooks/whatsapp/<id-opaco-da-conta>')
    Write-Host ''
    Write-Host 'No terminal do Api, ANTES de subi-lo:'
    Write-Host ('  $env:Channels__Webhook__PublicBaseUrl = ''' + $url + '''')
    Write-Host ''
    Write-Host 'Esta URL muda a cada execucao: cadastre o callback de novo na Meta sempre que reabrir o tunel.'
    Write-Host 'Deixe esta janela aberta. Ctrl-C encerra o tunel (a URL deixa de funcionar).'
    Write-Host ''

    while (-not $processo.HasExited) { Start-Sleep -Seconds 2 }
    Write-Host ('O cloudflared terminou (codigo ' + $processo.ExitCode + ').')
}
finally {
    if ($null -ne $processo -and -not $processo.HasExited) { & taskkill /PID $processo.Id /T /F 2>&1 | Out-Null }
    Remove-Item -Path $saida, $erro -ErrorAction SilentlyContinue
}
