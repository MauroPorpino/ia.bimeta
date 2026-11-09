<#
.SYNOPSIS
    Faz o papel da Meta: envia ao Receiver do Api um webhook de mensagem de cliente do WhatsApp, ASSINADO como a
    Meta assina (HMAC-SHA256 do corpo com o App Secret, em X-Hub-Signature-256), para o canal de demonstracao do
    tenant demo-ia. O que acontece depois (IA, transferencia, atendente) e o fluxo de verdade.

.DESCRIPTION
    Le o caminho do webhook e o App Secret SINTETICO de desenvolvimento em GET /simulator/whatsapp/status (so existe em
    Development). O Api precisa estar no ar com o perfil simulador-ia.

    MIDIA: com -Midia (imagem, audio ou documento) o cliente manda um ARQUIVO. O webhook leva so o identificador da
    midia (como a Meta); o arquivo fica na pasta de midia, de onde o WhatsApp simulado (Start-WhatsAppSimulado.ps1,
    que usa a mesma pasta) o entrega ao Worker quando ele o busca. Sem -Arquivo, gera um arquivo sintetico de
    demonstracao (uma imagem PNG, um audio MP3 silencioso de 3 s ou um PDF de uma pagina).

.EXAMPLE
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Ola, tudo bem?'
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Quero falar com um atendente' -Nome 'Pedro' -Numero 5511988887777

    # Uma conversa longa (130 mensagens do mesmo cliente), para ver o historico em paginas na interface:
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'mensagem' -Nome 'Historia' -Numero 5511999990301 -Quantidade 130

    # Uma conversa com a janela de 24 horas FECHADA (a ultima mensagem do cliente tem 26 horas): so modelo aprovado:
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Quero falar com um atendente' -Nome Mauro -Numero 5511999990301 -HorasAtras 26

    # O cliente manda uma imagem, um audio e um documento (arquivos sinteticos de demonstracao):
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Midia imagem -Legenda 'Foto da requisicao do exame'
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Midia audio
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Midia documento -NomeDoArquivo 'laudo-anterior.pdf' -Legenda 'Laudo do ano passado'

    # Um arquivo seu (o tipo vem da extensao: png, jpg, webp, pdf, txt, mp3, m4a, ogg, aac, amr, doc, docx, xls, xlsx...):
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Midia documento -Arquivo C:\temp\resultado.pdf

    # Uma midia que a Meta ja nao tem (expirou): o atendente ve "arquivo indisponivel":
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Midia imagem -Expirada

    # Um arquivo que NAO e o que diz ser (um HTML com extensao .pdf): o atendente ve "arquivo bloqueado":
    .\tools\dev\Send-MensagemWhatsApp.ps1 -Midia documento -Arquivo C:\temp\falso.pdf
#>
[CmdletBinding()]
param(
    [string]$Texto = '',
    [string]$Nome = 'Mauro',
    [string]$Numero = '5511999990001',
    [string]$Url = 'http://localhost:5155',
    [string]$Tenant = 'demo-ia',

    # Quantas mensagens enviar (com mais de uma, cada texto ganha " (i/N)" e o id do provedor e unico).
    [ValidateRange(1, 1000)][int]$Quantidade = 1,

    # A mensagem do cliente tem ESTA idade pelo relogio da Meta (padrao: agora). Com mais de 24 horas, a janela de resposta
    # livre do WhatsApp esta fechada e o atendente so pode responder com um modelo de mensagem aprovado.
    [ValidateRange(0, 720)][double]$HorasAtras = 0,

    # Manda um ARQUIVO em vez de texto (veja a descricao).
    [ValidateSet('imagem', 'audio', 'documento')][string]$Midia = '',

    # O arquivo a enviar (opcional); sem ele, um arquivo sintetico de demonstracao.
    [string]$Arquivo = '',

    # A legenda que o cliente escreve junto (imagem e documento).
    [string]$Legenda = '',

    # O nome do arquivo que o cliente informa (documento). Sem ele, o nome do -Arquivo ou o do arquivo de demonstracao.
    [string]$NomeDoArquivo = '',

    # A pasta onde o arquivo e depositado para o WhatsApp simulado entregar. A mesma de Start-WhatsAppSimulado.ps1.
    [string]$PastaDeMidia = (Join-Path ([System.IO.Path]::GetTempPath()) 'iabimeta-midia-demo'),

    # A midia ja EXPIROU na Meta (o identificador vale 7 dias): o WhatsApp simulado responde 404 e o atendente ve "arquivo indisponivel".
    [switch]$Expirada
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Midia -eq '' -and [string]::IsNullOrWhiteSpace($Texto)) {
    Write-Host 'Informe -Texto (mensagem de texto) ou -Midia imagem|audio|documento (arquivo).'
    exit 1
}

# ---- Arquivos sinteticos de demonstracao (sem biblioteca externa) ----

function New-ImagemDeDemonstracao {
    Add-Type -AssemblyName System.Drawing
    $largura = 640
    $altura = 400
    $imagem = New-Object System.Drawing.Bitmap $largura, $altura
    $g = [System.Drawing.Graphics]::FromImage($imagem)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $retangulo = New-Object System.Drawing.Rectangle 0, 0, $largura, $altura
    $pincel = New-Object System.Drawing.Drawing2D.LinearGradientBrush($retangulo, [System.Drawing.Color]::FromArgb(31, 95, 191), [System.Drawing.Color]::FromArgb(91, 155, 255), 45.0)
    $g.FillRectangle($pincel, $retangulo)
    $g.FillEllipse([System.Drawing.Brushes]::White, 520, 40, 80, 80)
    $titulo = New-Object System.Drawing.Font('Segoe UI', 30, [System.Drawing.FontStyle]::Bold)
    $subtitulo = New-Object System.Drawing.Font('Segoe UI', 16)
    $g.DrawString('Requisicao de exame', $titulo, [System.Drawing.Brushes]::White, 30, 150)
    $g.DrawString('imagem de demonstracao - ia.bimeta', $subtitulo, [System.Drawing.Brushes]::White, 34, 215)
    $g.Dispose()
    $memoria = New-Object System.IO.MemoryStream
    $imagem.Save($memoria, [System.Drawing.Imaging.ImageFormat]::Png)
    $imagem.Dispose()
    return , $memoria.ToArray()
}

function New-AudioDeDemonstracao {
    # Um MP3 valido e SILENCIOSO de ~3 s: quadros MPEG-1 Layer III, 128 kbps, 44,1 kHz (417 bytes por quadro, 26 ms cada). Serve para provar o player;
    # para ouvir algo, use -Arquivo com um audio seu.
    $quadro = New-Object byte[] 417
    $quadro[0] = 0xFF
    $quadro[1] = 0xFB
    $quadro[2] = 0x90
    $quadro[3] = 0x00
    $memoria = New-Object System.IO.MemoryStream
    for ($i = 0; $i -lt 115; $i++) { $memoria.Write($quadro, 0, $quadro.Length) }
    return , $memoria.ToArray()
}

function New-DocumentoDeDemonstracao {
    # Um PDF de uma pagina, minimo e valido (a tabela de referencias com os deslocamentos certos).
    $paginaDoPdf = 'BT /F1 22 Tf 72 740 Td (Laudo de demonstracao - ia.bimeta) Tj 0 -34 Td /F1 12 Tf (Documento sintetico gerado para testar o recebimento de PDF.) Tj ET'
    $objetos = @(
        '<< /Type /Catalog /Pages 2 0 R >>',
        '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
        ('<< /Length ' + $paginaDoPdf.Length + " >>`nstream`n" + $paginaDoPdf + "`nendstream"),
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'
    )
    $texto = New-Object System.Text.StringBuilder
    [void]$texto.Append("%PDF-1.4`n")
    $deslocamentos = @()
    for ($i = 0; $i -lt $objetos.Count; $i++) {
        $deslocamentos += $texto.Length
        [void]$texto.Append(([string]($i + 1)) + " 0 obj`n" + $objetos[$i] + "`nendobj`n")
    }
    $inicioDaTabela = $texto.Length
    [void]$texto.Append("xref`n0 " + ($objetos.Count + 1) + "`n")
    [void]$texto.Append("0000000000 65535 f `n")
    foreach ($d in $deslocamentos) { [void]$texto.Append($d.ToString('D10') + " 00000 n `n") }
    [void]$texto.Append('trailer' + "`n" + '<< /Size ' + ($objetos.Count + 1) + " /Root 1 0 R >>`nstartxref`n" + $inicioDaTabela + "`n%%EOF`n")
    return , [System.Text.Encoding]::ASCII.GetBytes($texto.ToString())
}

$tiposPorExtensao = @{
    '.png' = 'image/png'; '.jpg' = 'image/jpeg'; '.jpeg' = 'image/jpeg'; '.webp' = 'image/webp'
    '.mp3' = 'audio/mpeg'; '.m4a' = 'audio/mp4'; '.ogg' = 'audio/ogg'; '.aac' = 'audio/aac'; '.amr' = 'audio/amr'
    '.pdf' = 'application/pdf'; '.txt' = 'text/plain'; '.doc' = 'application/msword'; '.xls' = 'application/vnd.ms-excel'; '.ppt' = 'application/vnd.ms-powerpoint'
    '.docx' = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
    '.xlsx' = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
    '.pptx' = 'application/vnd.openxmlformats-officedocument.presentationml.presentation'
}

# O que sera enviado: bytes, tipo e nome do arquivo.
$bytesDoArquivo = $null
$tipoDoArquivo = ''
$nomeDoArquivoFinal = ''
$tipoDaMeta = ''
if ($Midia -ne '') {
    $tipoDaMeta = @{ imagem = 'image'; audio = 'audio'; documento = 'document' }[$Midia]
    if ($Arquivo -ne '') {
        if (-not (Test-Path -LiteralPath $Arquivo)) {
            Write-Host ('FALHA: o arquivo ' + $Arquivo + ' nao existe.')
            exit 1
        }
        $extensao = [System.IO.Path]::GetExtension($Arquivo).ToLowerInvariant()
        if (-not $tiposPorExtensao.ContainsKey($extensao)) {
            Write-Host ('FALHA: extensao ' + $extensao + ' desconhecida. Use uma destas: ' + (($tiposPorExtensao.Keys | Sort-Object) -join ', '))
            exit 1
        }
        $bytesDoArquivo = [System.IO.File]::ReadAllBytes($Arquivo)
        $tipoDoArquivo = $tiposPorExtensao[$extensao]
        $nomeDoArquivoFinal = [System.IO.Path]::GetFileName($Arquivo)
    }
    elseif ($Midia -eq 'imagem') {
        $bytesDoArquivo = New-ImagemDeDemonstracao
        $tipoDoArquivo = 'image/png'
        $nomeDoArquivoFinal = 'requisicao-demonstracao.png'
    }
    elseif ($Midia -eq 'audio') {
        $bytesDoArquivo = New-AudioDeDemonstracao
        $tipoDoArquivo = 'audio/mpeg'
        $nomeDoArquivoFinal = 'audio-demonstracao.mp3'
    }
    else {
        $bytesDoArquivo = New-DocumentoDeDemonstracao
        $tipoDoArquivo = 'application/pdf'
        $nomeDoArquivoFinal = 'laudo-demonstracao.pdf'
    }
    if ($NomeDoArquivo -ne '') { $nomeDoArquivoFinal = $NomeDoArquivo }
    New-Item -ItemType Directory -Force -Path $PastaDeMidia | Out-Null
}

try {
    $canal = Invoke-RestMethod -Method Get -Uri ($Url + '/simulator/whatsapp/status') -Headers @{ 'X-Dev-Tenant' = $Tenant }
}
catch {
    Write-Host 'FALHA: o Api nao respondeu em /simulator/whatsapp/status. Ele esta no ar com o perfil simulador-ia?'
    exit 1
}

$textoBase = $Texto
$lote = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
for ($indice = 1; $indice -le $Quantidade; $indice++) {
$Texto = $textoBase
if ($Quantidade -gt 1) { $Texto = $textoBase + ' (' + $indice + '/' + $Quantidade + ')' }
$agora = [DateTimeOffset]::UtcNow.AddHours(-1 * $HorasAtras)
$idDaMensagem = 'wamid.SIM' + $lote + '-' + $indice

if ($Midia -ne '') {
    # O arquivo vai para a pasta de midia (de onde o WhatsApp simulado o entrega); o webhook leva so o identificador e os metadados, como a Meta.
    $idDaMidia = 'demo' + $lote + $indice
    $resumo = ([BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytesDoArquivo)).Replace('-', '').ToLowerInvariant())
    if ($Expirada) {
        # Nada e depositado: o WhatsApp simulado responde 404 para este identificador (a midia que a Meta ja nao tem).
        [System.IO.File]::WriteAllText((Join-Path $PastaDeMidia ($idDaMidia + '.expirada')), 'expirada', (New-Object System.Text.UTF8Encoding($false)))
    }
    else {
        [System.IO.File]::WriteAllBytes((Join-Path $PastaDeMidia ($idDaMidia + '.bin')), $bytesDoArquivo)
        $dadosDaMidia = @{ mime_type = $tipoDoArquivo; sha256 = $resumo; file_size = $bytesDoArquivo.Length }
        [System.IO.File]::WriteAllText((Join-Path $PastaDeMidia ($idDaMidia + '.json')), (ConvertTo-Json -InputObject $dadosDaMidia -Compress), (New-Object System.Text.UTF8Encoding($false)))
    }

    $objetoDaMidia = @{ id = $idDaMidia; mime_type = $tipoDoArquivo; sha256 = $resumo }
    if ($Legenda -ne '' -and $Midia -ne 'audio') { $objetoDaMidia['caption'] = $Legenda }
    if ($Midia -eq 'documento') { $objetoDaMidia['filename'] = $nomeDoArquivoFinal }
    $mensagemDoWebhook = @{ from = $Numero; id = $idDaMensagem; timestamp = [string]$agora.ToUnixTimeSeconds(); type = $tipoDaMeta }
    $mensagemDoWebhook[$tipoDaMeta] = $objetoDaMidia
}
else {
    $mensagemDoWebhook = @{ from = $Numero; id = $idDaMensagem; timestamp = [string]$agora.ToUnixTimeSeconds(); type = 'text'; text = @{ body = $Texto } }
}

$payload = @{
    object = 'whatsapp_business_account'
    entry = @(@{
        id = [string]$canal.wabaId
        changes = @(@{
            field = 'messages'
            value = @{
                messaging_product = 'whatsapp'
                metadata = @{ display_phone_number = '551130000000'; phone_number_id = $canal.phoneNumberId }
                contacts = @(@{ profile = @{ name = $Nome }; wa_id = $Numero })
                messages = @($mensagemDoWebhook)
            }
        })
    })
}

$corpo = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $payload -Depth 10 -Compress))
$hmac = New-Object System.Security.Cryptography.HMACSHA256 (, [System.Text.Encoding]::UTF8.GetBytes([string]$canal.appSecret))
$assinatura = 'sha256=' + ([BitConverter]::ToString($hmac.ComputeHash($corpo)).Replace('-', '').ToLowerInvariant())

try {
    $resposta = Invoke-WebRequest -Method Post -Uri ($Url + $canal.webhookPath) -Body $corpo -ContentType 'application/json' -Headers @{ 'X-Hub-Signature-256' = $assinatura } -UseBasicParsing
    if ($Midia -ne '') {
        Write-Host ('Webhook entregue ao Receiver (HTTP ' + [int]$resposta.StatusCode + '). ' + $Nome + ' (' + $Numero + ') enviou ' + $Midia + ': ' + $nomeDoArquivoFinal + ' (' + $tipoDoArquivo + ', ' + $bytesDoArquivo.Length + ' bytes), midia ' + $idDaMidia)
    }
    else {
        Write-Host ('Webhook entregue ao Receiver (HTTP ' + [int]$resposta.StatusCode + '). Mensagem de ' + $Nome + ' (' + $Numero + '): ' + $Texto)
    }
}
catch {
    $codigo = 0
    if ($null -ne $_.Exception.Response) { $codigo = [int]$_.Exception.Response.StatusCode }
    Write-Host ('FALHA: o Receiver respondeu HTTP ' + $codigo + '.')
    exit 1
}
}
