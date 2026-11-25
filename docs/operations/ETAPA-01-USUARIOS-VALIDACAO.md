# Etapa 01 — Roteiro de validação funcional (usuários, vagas, habilitação e permissões)

Ambiente **local de desenvolvimento**, dados **sintéticos**, empresa própria `demo-usuarios` (não toca na `demo-ia`). Nada vai a serviço externo.

## Endereços e acesso
| O quê | Endereço |
|---|---|
| Interface (React) | http://localhost:5173 |
| API | http://localhost:5155 |
| Empresa (campo "Tenant" da tela de entrada) | `demo-usuarios` |

| Perfil | E-mail | Senha (sintética, só deste banco de desenvolvimento) |
|---|---|---|
| Administrador | `admin@demo-usuarios.local` | `DevLocal-Admin-2026!` |
| Supervisor | `supervisor@demo-usuarios.local` | `DevLocal-Supervisor-2026!` |
| Atendente | `atendente@demo-usuarios.local` | `DevLocal-Atendente-2026!` |

A sessão fica no `sessionStorage` **da aba**: para ver duas pessoas ao mesmo tempo, use uma aba normal e uma janela anônima.
Limite contratado: **5**; cadastro habilitado. O administrador, o supervisor e o atendente ocupam 3 vagas.

## Preparar (uma vez)
```powershell
.\tools\dev\Novo-TenantDeUsuariosDeDemonstracao.ps1
```
Repetir é inofensivo (nada é duplicado nem sobrescrito).

## Como a BImeta altera o limite e a habilitação (só a plataforma; o tenant só consulta)
```powershell
.\tools\ops\Definir-LimiteDeUsuarios.ps1 -Empresa demo-usuarios                 # mostra a situação
.\tools\ops\Definir-LimiteDeUsuarios.ps1 -Empresa demo-usuarios -Maximo 4       # muda o limite
.\tools\ops\Definir-LimiteDeUsuarios.ps1 -Empresa demo-usuarios -Cadastro off   # desabilita o cadastro
.\tools\ops\Definir-LimiteDeUsuarios.ps1 -Empresa demo-usuarios -Cadastro on
```
A tela de usuários mostra o novo limite ao recarregar. O primeiro administrador de uma empresa nova é criado por `Novo-PrimeiroAdministrador.ps1` (token de uso único, mostrado uma vez).

## Roteiro (entrar como Administrador → Configurações → Usuários)
1. **Painel de uso:** limite 5, ativos 3, pendentes 0, vagas disponíveis 2, cadastro habilitado.
2. **Cadastro e perfil:** cadastre "Bruno Atendente" (perfil Atendente). Aparece como **Pendente** (reserva 1 vaga → disponíveis 1). O token aparece **uma única vez**, com validade de 24 h.
3. **Primeiro acesso:** saia, escolha "Esqueci a senha (tenho um token)", informe empresa, e-mail, token e uma senha nova. O usuário passa a **Ativo**; o token não funciona de novo.
4. **Substituir/cancelar token:** cadastre outro usuário, clique em "Novo token" (o anterior deixa de valer) e depois "Cancelar token"/"Cancelar admissão" (a vaga é liberada).
5. **Limite atingido:** complete as 5 vagas e tente cadastrar mais um → mensagem "Limite de usuários atingido… solicite a ampliação à BImeta".
6. **Cadastro desabilitado:** rode `-Cadastro off`; o cadastro é recusado ("desabilitado pela BImeta"), mas os usuários existentes continuam entrando e podem ter vínculos organizacionais e ser reativados.
7. **Ativar/desativar:** desative o atendente: ele não entra mais (e a sessão aberta cai na próxima ação), a vaga é liberada e o histórico fica. Reative (exige vaga).
8. **Redução do limite:** com 4 ativos, rode `-Maximo 2`: o painel mostra **excesso**, ninguém é desativado, novos cadastros são bloqueados; desativar usuários volta ao normal.
9. **Permissões individuais (modelo híbrido):** abra o supervisor (sem personalizações) → "Permissões": a lista traz TODAS as permissões do escopo, com "Herdada do perfil" e "Não concedida pelo perfil". **Conceda** "Gerir usuários" (fora do perfil) e confirme "Personalizada: concedida · Efetiva"; **retire** outra e use **Restaurar padrão** (a permissão adicional volta a "Não concedida pelo perfil"). Entre como o supervisor e veja o cadastro disponível.
10. **Restrições de acesso (supervisor com cadastro delegado):** como supervisor, cadastre um Atendente (permitido); tente cadastrar um Supervisor/Administrador, alterar permissões, redefinir o acesso do administrador ou desativar alguém → **negado**.
11. **Último administrador:** como administrador, tente desativar a si mesmo (negado); crie um segundo administrador e tente desativar/restringir os dois → a **última** operação é recusada ("deixaria a empresa sem administrador ativo").
12. **Isolamento:** o administrador de `demo-usuarios` não vê usuários da `demo-ia` (e vice-versa).

## O que **não** faz parte desta etapa
Interface da administração da plataforma (Etapa 02), TOTP, suspensão completa de tenant, e-mail/SMTP, supervisão completa de atendimentos, outros módulos de configuração.

## Situação
Etapa 01 **concluída e aprovada tecnicamente em 25/11/2026** (validação funcional do proprietário e aprovação independente do Codex, por inspeção). Detalhes, limitações e pendências (BL-26, BL-27) no ADR 0042, seção "Encerramento da Etapa 01". Item adicional de roteiro: o administrador inicial com token vencido recebe novo token repetindo `Novo-PrimeiroAdministrador.ps1` com o mesmo e-mail (só até concluir o primeiro acesso).
