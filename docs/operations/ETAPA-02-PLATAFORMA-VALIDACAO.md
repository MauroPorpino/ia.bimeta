# Etapa 02 — Roteiro de validação funcional (administração da plataforma BImeta)

> **Etapa 02 concluída tecnicamente em 27/11/2026 (ADR 0043 Aceito).** Etapa 01 segue concluída. Próxima: Etapa 03 — Adaptador Anthropic (não iniciada). Encerramento técnico não autoriza produção nem exposição pública.

Ambiente **local de desenvolvimento**, dados **sintéticos**. Não toca `demo-ia` nem `demo-usuarios`. **O painel não está autorizado para exposição pública** (falta o 2FA; ver ADR 0043).

## Endereços
| O quê | Endereço |
|---|---|
| Painel da plataforma | http://localhost:5173/plataforma |
| Ambiente das empresas (atendimento) | http://localhost:5173 (campo "Tenant" = identificador da empresa) |
| API | http://localhost:5155 |

## Preparar (uma vez; repetir é inofensivo)
```powershell
.\tools\dev\Novo-AmbienteDePlataformaDeDemonstracao.ps1
```
Cria o tenant técnico, o administrador da plataforma pelo comando `platform-init` (token de uso único, como em produção; o script conclui o primeiro acesso com senha **sintética de desenvolvimento**), os operadores Comercial e Consulta e as empresas `demo-alfa` e `demo-beta`. **Os tokens de primeiro acesso dos administradores das empresas aparecem uma vez na saída**: guarde-os para o passo 6.

Em produção o primeiro administrador nasce assim, sem senha padrão: `IaBimeta.Api platform-init --email <e-mail> --name "<nome>"` e a pessoa define a senha no "Primeiro acesso" do painel.

## Operadores (senhas sintéticas só deste banco de desenvolvimento)
| Perfil | E-mail | Senha |
|---|---|---|
| Administrador BImeta | `admin@bimeta-platform.local` | `DevLocal-Plataforma-Admin-2026!` |
| Operador comercial | `comercial@bimeta-platform.local` | `DevLocal-Plataforma-Comercial-2026!` |
| Consulta | `consulta@bimeta-platform.local` | `DevLocal-Plataforma-Consulta-2026!` |

A sessão fica no `sessionStorage` **da aba**, em chave própria: use abas/janelas diferentes para ver operadores e empresas ao mesmo tempo.

## Roteiro
1. **Login** do Administrador BImeta. Menus: Visão geral · Empresas · Operadores · Auditoria · Minha conta. Entre depois como Consulta: só Visão geral, Empresas e Minha conta.
2. **Cadastrar empresa** (Empresas → Cadastrar empresa): identificador técnico (permanente), limite **obrigatório**, habilitação, nome e e-mail do primeiro administrador. O token de primeiro acesso aparece **uma única vez**.
3. **Limite**: no detalhe, "Alterar limite". Reduzir abaixo do uso mostra o excesso; ninguém é desativado.
4. **Primeiro administrador**: "Reemitir primeiro acesso" (o token anterior deixa de valer). No ambiente das empresas, "Esqueci a senha (tenho um token)" com a empresa, o e-mail do administrador, o token e uma senha nova. Depois do primeiro acesso a reemissão some.
5. **O próprio tenant cadastra usuários** (Configurações → Usuários da empresa) e a BImeta só vê as **contagens** (vagas ativas, reservas, disponíveis).
6. **Desabilitar novos cadastros** ("Desabilitar novos cadastros"): a empresa só consulta; os usuários existentes continuam entrando.
7. **Suspender** (exige motivo e digitar o identificador): login bloqueado, sessão aberta cai, IA, fluxos e envios param; mensagens recebidas ficam guardadas **sem atendimento automático**. Para ver as mensagens: `.\tools\dev\Send-MensagemParaEmpresa.ps1 -Slug <empresa> -WebhookPath <webhook> -Texto 'ola'` em uma empresa provisionada com WhatsApp simulado.
8. **Reativar**: novas sessões são exigidas; **nenhuma** mensagem antiga é reprocessada; limite, habilitação e usuários preservados. O histórico de situação mostra os dois motivos.
9. **Perfis**: Operador comercial cadastra e altera limite, mas não suspende, não vê Operadores nem Auditoria. Em Operadores, o administrador **concede** e **retira** permissões individuais ("Restaurar padrão"); a restrição vence o perfil.
10. **Auditoria**: filtros por operação, recurso (o id do tenant afetado), resultado e período; as negações (403) aparecem como Denied. Nunca há senha, token ou chave.

## Pontos acrescentados pela revisão do Codex (26/11/2026)
11. **Elevação de privilégio**: com dois administradores restringidos em "Suspender empresas", um não consegue tirar a restrição do outro nem "Restaurar padrão" (403, nada muda, negação na auditoria); o administrador sem restrição consegue.
12. **Suspensão e fluxos**: mensagem recebida com a empresa suspensa não gera resposta (nem de fluxo, nem de IA) e continua visível no histórico; ao reativar, nada é respondido sozinho; só uma mensagem **nova** é atendida (a IA usa o histórico como contexto; uma sessão **nova** usa o fluxo).
13. **Sessões**: login iniciado antes da suspensão e concluído depois da reativação não resulta em sessão válida; um login novo funciona.
14. **Catálogo de perfis** (`/platform/roles`): só o Administrador BImeta; sem sessão, 401; Operador comercial e Consulta, 403.
15. Cadastro de uma empresa com o identificador `bimeta-platform` pelo comando antigo de provisionamento é recusado.

16. **Autoridade revogada durante a operação**: o operador que perde a gestão de permissões enquanto espera a trava tem a alteração recusada (403, auditada) e o alvo fica intacto.
17. **Fluxo antigo após a reativação** (regra ratificada): fluxos criados antes da reativação são cancelados; nenhum timeout, `Wake` ou agendamento antigo envia mensagem, transferência ou integração; mensagem nova em sessão antiga é atendida pela IA (se a conversa for da IA; com atendente humano continua humana); sessão nova usa o fluxo normalmente.
18. **Envio e suspensão**: pedido admitido antes da suspensão pode concluir; o que ainda esperava (vazão, gravações) é bloqueado; a tentativa que não ocorreu não conta; o pedido anterior à reativação falha de forma visível e pode ser reenviado pelo atendente. A janela residual entre a admissão e a chamada imediata ao adaptador não é eliminada.
19. **Login recusado**: a sessão gravada por um login atravessado por suspensão/reativação fica revogada no banco e nenhum token é entregue.

## O que **não** faz parte desta etapa
Cobrança, fiscal, financeiro, pagamentos, controle monetário de IA, Anthropic/Ollama, múltiplos agentes, RAG documental, consultas externas autenticadas, recuperação de administradores ativos das empresas, TOTP (proposta no ADR 0043).
