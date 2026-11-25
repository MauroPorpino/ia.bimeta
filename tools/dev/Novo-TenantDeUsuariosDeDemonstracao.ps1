<#
.SYNOPSIS
    Cria a empresa SINTETICA "demo-usuarios" para voce validar a Etapa 01 (usuarios, vagas, habilitacao e permissoes)
    pela interface: 1 administrador, 1 supervisor, 1 atendente, limite de 5 usuarios e cadastro habilitado.

.DESCRIPTION
    Nao toca em nenhuma empresa existente (nem na demo-ia). Repetir e seguro: tudo que ja existe e mantido.

      1. Coloca a empresa em funcionamento com o comando "provision" (tools\ops\Novo-Tenant.ps1 + tools\dev\empresa-usuarios-demo.json):
         tenant com limite de 5 usuarios, administrador, organizacao e fila.
      2. Com o Api no ar (perfil simulador-ia, porta 5155), entra como o administrador e cria o supervisor (perfil Supervisor)
         e o atendente (perfil Atendente) PELO MESMO CAMINHO DA INTERFACE: admissao com token de primeiro acesso, e
         o primeiro acesso de cada um (ele mesmo define a senha). Poe o atendente na fila de atendimento humano.

    As senhas abaixo sao SINTETICAS e so valem neste banco de desenvolvimento.

.EXAMPLE
    .\tools\dev\Novo-TenantDeUsuariosDeDemonstracao.ps1
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://localhost:5155',
    [string]$Tenant = 'demo-usuarios',
    [string]$SenhaDoAdministrador = 'DevLocal-Admin-2026!',
    [string]$SenhaDoSupervisor = 'DevLocal-Supervisor-2026!',
    [string]$SenhaDoAtendente = 'DevLocal-Atendente-2026!'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$emailAdmin = 'admin@demo-usuarios.local'
$emailSupervisor = 'supervisor@demo-usuarios.local'
$emailAtendente = 'atendente@demo-usuarios.local'

# 1. A empresa (tenant, administrador, organizacao e fila) pelo comando provision.
$env:DEMO_USUARIOS_SENHA_ADMIN = $SenhaDoAdministrador
& (Join-Path $raiz 'tools\ops\Novo-Tenant.ps1') -Arquivo (Join-Path $raiz 'tools\dev\empresa-usuarios-demo.json')
if ($LASTEXITCODE -ne 0) { Write-Host 'FALHA: o provisionamento da empresa nao terminou. Veja a saida acima.'; exit 1 }

# 2. Supervisor e atendente pela API, como a interface faz.
$corpoLogin = ConvertTo-Json -InputObject @{ tenant = $Tenant; email = $emailAdmin; password = $SenhaDoAdministrador } -Compress
try { $login = Invoke-RestMethod -Method Post -Uri ($Url + '/auth/login') -ContentType 'application/json' -Body $corpoLogin }
catch { Write-Host 'FALHA: nao consegui entrar como o administrador. O Api esta no ar em' $Url '?'; exit 1 }
$auth = @{ Authorization = ('Bearer ' + $login.token) }

$organizacoes = @(Invoke-RestMethod -Method Get -Uri ($Url + '/organizations') -Headers $auth | ForEach-Object { $_ })
$organizacao = $organizacoes[0].id
$filas = @(Invoke-RestMethod -Method Get -Uri ($Url + '/contact-center/queues') -Headers $auth | ForEach-Object { $_ })
$fila = $filas[0].id

function Garantir-Usuario([string]$email, [string]$nome, [string]$papel, [string]$senha) {
    $pagina = Invoke-RestMethod -Method Get -Uri ($Url + '/users?search=' + [uri]::EscapeDataString($email)) -Headers $auth
    $existente = @($pagina.items | Where-Object { $_.email -eq $email })
    if ($existente.Count -gt 0) { Write-Host ('Ja existia: ' + $email); return $existente[0].id }

    $corpo = ConvertTo-Json -InputObject @{ email = $email; displayName = $nome; role = $papel } -Compress
    $admitido = Invoke-RestMethod -Method Post -Uri ($Url + '/organizations/' + $organizacao + '/users/admissions') -Headers $auth -ContentType 'application/json' -Body $corpo
    # O primeiro acesso: a propria pessoa define a senha com o token (o token nunca e impresso aqui).
    $resgate = ConvertTo-Json -InputObject @{ tenant = $Tenant; email = $email; token = $admitido.token; newPassword = $senha } -Compress
    Invoke-RestMethod -Method Post -Uri ($Url + '/auth/password-reset/redeem') -ContentType 'application/json' -Body $resgate | Out-Null
    Write-Host ('Criado e com primeiro acesso concluido: ' + $email + ' (' + $papel + ')')
    return $admitido.user.id
}

$idSupervisor = Garantir-Usuario $emailSupervisor 'Sergio Supervisor' 'Manager' $SenhaDoSupervisor
$idAtendente = Garantir-Usuario $emailAtendente 'Aline Atendente' 'Attendant' $SenhaDoAtendente

$membro = ConvertTo-Json -InputObject @{ role = 'Agent' } -Compress
Invoke-RestMethod -Method Put -Uri ($Url + '/contact-center/queues/' + $fila + '/members/' + $idAtendente) -Headers $auth -ContentType 'application/json' -Body $membro | Out-Null

$uso = Invoke-RestMethod -Method Get -Uri ($Url + '/users/usage') -Headers $auth
Write-Host ''
Write-Host 'Pronto. Empresa demo-usuarios (limite' $uso.limit ', vagas usadas' $uso.seatsUsed ', disponiveis' $uso.available ', cadastro habilitado:' $uso.registrationEnabled ')'
Write-Host ('  Administrador: ' + $emailAdmin + ' / ' + $SenhaDoAdministrador)
Write-Host ('  Supervisor:    ' + $emailSupervisor + ' / ' + $SenhaDoSupervisor)
Write-Host ('  Atendente:     ' + $emailAtendente + ' / ' + $SenhaDoAtendente)
Write-Host 'Interface: http://localhost:5173 (empresa demo-usuarios). Veja docs\operations\ETAPA-01-USUARIOS-VALIDACAO.md.'
