# Senhas e sessões (ADR 0025)

Trocar a própria senha, recuperar o acesso e derrubar sessões. É o **mesmo** mecanismo de autenticação do login (sessões opacas revogáveis, PBKDF2, bloqueio por tentativas): não há segundo sistema.

## Quem faz o quê

| Situação | Quem | Como |
|---|---|---|
| Quero trocar minha senha | o próprio usuário, logado | `PUT /auth/me/password` com `currentPassword` e `newPassword` |
| Esqueci a senha | o administrador da organização, ou o operador | gera um **token** de uso único; o usuário usa em `POST /auth/password-reset/redeem` |
| Perdi o aparelho / suspeito de vazamento | o administrador | `POST /organizations/{org}/users/{userId}/sessions/revoke` |
| O único administrador esqueceu a senha | o operador (sua máquina, com acesso ao banco) | `tools\ops\Redefinir-Senha.ps1 -Empresa <slug> -Email <e-mail>` |

## Trocar a própria senha

```
PUT /auth/me/password        (Authorization: Bearer <token da sessão>)
{ "currentPassword": "...", "newPassword": "..." }   ->  204
```

- A senha atual é exigida. Errar conta no **mesmo bloqueio do login** (5 erros, 15 minutos por padrão; `429` com `Retry-After`).
- A nova senha tem de ter de 12 a 128 caracteres e ser **diferente** da atual (erro de política não conta como tentativa).
- Ao trocar, **as outras sessões** do usuário caem; a sessão que trocou continua. Um token de redefinição ainda pendente deixa de valer.

## Redefinir com token (esqueci a senha)

1. **Emitir** (administrador ou operador). O token (`ibr_...`) aparece **uma única vez**, na resposta ou no terminal, e vale **30 minutos** (`Identity:PasswordResetLifetime`). Emitir outro invalida o anterior. Entregue-o à pessoa por um canal seguro (não por chat aberto nem e-mail sem proteção).
   - Administrador: `POST /organizations/{org}/users/{userId}/password-reset` (permissão de gestão de usuários; quem administra só a organização **não** redefine quem administra o tenant inteiro).
   - Operador: `.\tools\ops\Redefinir-Senha.ps1 -Empresa lab-exemplo -Email ana@exemplo.com` (comando do host `reset-password`; não há rota HTTP; lê a configuração do ambiente Development, ou `ConnectionStrings__Default` para outro banco).
2. **Usar** (a pessoa, sem estar logada):
   ```
   POST /auth/password-reset/redeem
   { "tenant": "lab-exemplo", "email": "ana@exemplo.com", "token": "ibr_...", "newPassword": "..." }   ->  204
   ```
   Toda falha (empresa, e-mail ou token inexistente, vencido, já usado ou substituído) tem **a mesma resposta** (`400`, "O token de redefinição é inválido ou expirou."): ninguém descobre quem existe. Tentativas demais bloqueiam **por e-mail** (existindo ou não) com `429`; isso **não** bloqueia o login do dono. Senha fora da política **não gasta** o token. Ao redefinir, **todas as sessões** da pessoa caem e ela entra de novo com a senha nova.

## Revogar sessões

`POST /organizations/{org}/users/{userId}/sessions/revoke` → `{ "revoked": N }`. Derruba todas as sessões ativas do usuário (a senha não muda: ele só precisa entrar de novo). Quem não é administrador recebe `403`; usuário fora da organização, `404`.

## O que fica registrado

A trilha de auditoria registra a troca, a emissão (quem emitiu e para quem), o uso e a revogação, com o resultado; **nunca** o token nem senha alguma. No banco só existe o **SHA-256** do token. O token não vai a log. Para conferir: `GET /audit-events?operation=identity.password.reset.issue`.

## Configuração

| Chave | Padrão | O que é |
|---|---|---|
| `Identity:PasswordResetLifetime` | `00:30:00` | validade do token de redefinição |
| `Identity:MaxFailedLogins` / `FailedLoginWindow` / `LockoutDuration` | 5 / 15 min / 15 min | bloqueio (vale também para senha atual errada e para tentativas de token) |
| `Identity:SessionLifetime` | 8 h | validade da sessão |
| `Identity:LoginRateLimit:PermitLimit` / `WindowSeconds` | 20 / 60 | limite por origem do login e do uso do token |

## Na interface do atendente

- **Trocar senha** (botão no topo, ao lado de "Sair"): senha atual, nova e confirmação; confere a política (12 a 128 e diferente da atual) antes de enviar e mostra o erro do servidor (por exemplo, senha atual errada ou bloqueio). A pessoa continua logada; as outras sessões caem.
- **Esqueci a senha (tenho um token)** (na tela de entrada): empresa, e-mail, token e nova senha. O token não fica na tela depois do uso, e a pessoa volta à entrada com o aviso "Senha redefinida".

## Ainda não existe

Tela para o **administrador** emitir o token e revogar sessões (hoje pelas rotas acima ou pelo operador), entrega do token por e-mail e segundo fator de autenticação.
