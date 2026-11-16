# Plano e andamento do Embedded na V1 (ADR 0039 e 0040)

Documento vivo: atualizado ao fim de cada etapa com o que realmente aconteceu. Branch `feature/v1-embedded` (a partir da tag local `v1-rc1` = `32cf06d`, candidata histórica preservada). Sem push, sem alterar a `main`, sem chamada real à Meta, OpenAI, Anthropic ou ERP, sem instalar infraestrutura no Windows.

## Regras de cada etapa

1. Commits pequenos; ao fim de cada etapa roda o **gate completo** (backend `dotnet test backend/IaBimeta.slnx`, frontend `vitest run` e `npm run build`, testes de arquitetura), além dos testes da etapa. Etapa com item falhando não avança.
2. **Auditoria independente (Codex)** nas etapas de segurança: E1 (Identity federado), E2 (consentimento e isolamento), E3 (início ativo) e E7 (endurecimento). O pacote de entrega é preparado ao fim da etapa; a consolidação segue o procedimento já usado (worktree próprio, sem merge automático de sobreposição).
3. O relatório de fim de etapa traz: ADRs ou alterações, decisões, testes executados, pendências. A etapa seguinte só começa com a aprovação do decisor quando ele pedir.
4. Este plano e o `HOMOLOGACAO-V1.md` (Fase 8) são atualizados junto.

## Etapas, dependências e critérios de aceite

| Etapa | Entrega | Depende de | Aceite resumido | Estado |
|---|---|---|---|---|
| E0 | ADR 0039 e 0040, matriz 0.3, BOUNDARIES §13, spikes documentais, `playwright-core` com o Edge, documentos de homologação | — | ADRs aprovados; Edge dirigível por script; gate verde | **concluída (16/11/2026); aguarda ratificação dos ADRs** |
| E1 | Identity federado: conexões, chaves HMAC, tickets com verifier, identidades externas, sessão embutida, revogação, guarda de rotas, retenção, auditoria, administração por API | E0 | ticket reutilizado, expirado, sem verifier, de outra origem ou nonce repetido recusado; tenant A não toca B; federado sem login por senha; lista de rotas não vazia e sem rota administrativa | **reprovada na auditoria independente (Codex) em 16/11/2026; corrigida na E1-R; aguarda nova auditoria** |
| E1-R | Correção da auditoria da E1: organização da conexão como limite duro do Contact Center, `CustomerOnly` fechado até a E2, revogação sem corrida (marcas, chave, origem), tokens `ibe_` com hash de domínio, ticket de contexto preso à sessão, tetos sob concorrência, taxa sem cota escolhida pelo chamador, corpo e timestamp, rotas públicas exatas, Caddy sem cabeçalhos, corrida preexistente da redefinição de senha | E1 | cada achado com teste que falha sem a correção; gate completo verde | **implementada e commitada (`7a07d91`, `c33f1b9`, `011dac1`); gate verde; aguarda reauditoria** |
| E2 | Vínculo cliente-contato, histórico do cliente, consentimento (registro, revogação, palavras-chave), alias de contato | E1 | histórico só no escopo; 404 fora; vínculo cross-tenant impossível; consentimento sem rastro recusado; PARAR/SAIR/CANCELAR revogam, falsos positivos não | não iniciada |
| E3 | Início ativo (backend): sessão iniciada pela empresa, orquestração idempotente, guardas, limites, falhas, auditoria | E1, E2 | critérios do ADR 0040 (consentimento, duplicidade, retomada por `requestId` em cada passo, `SendUnknown` sem reenvio, permissões, isolamento, loopback) | não iniciada |
| E4 | Frontend: `AtendimentoShell`, `embed.tsx`, layout estreito, `HostBridge`, administração de integrações, `IniciarConversa` (também no portal independente), cartão de consentimento | contratos da E1 (parcial); E3 para o diálogo | app independente sem regressão; painel utilizável a 360 px | não iniciada |
| E5 | SDK, ERP fictício (C#, outra origem), testes de navegador no Edge | E1, E4 | fluxo ERP fictício → painel → conversa; origem não autorizada bloqueada; logout revoga | não iniciada |
| E6 | Capabilities no painel | E1, E4 | só operações liberadas e com o cliente em foco; auditoria com hash do cliente | não iniciada |
| E7 | Endurecimento: CSP, `frame-ancestors`, limites de taxa, revisão de segurança | E1 a E6 | embutir de origem não listada falha; rota administrativa em sessão embutida dá 403; app independente não pode ser embutido | não iniciada |
| E8 | Referência BImeta ERP (exemplos WebForms e guia), `docs/operations/EMBEDDED.md`, plano de homologação final | E1 a E7 | roteiro da Fase 8 executável na VM | não iniciada |

Caminho crítico do backend: E1 → E2 → E3. E4 pode começar com os contratos da E1; E5 e E6 em paralelo depois de E4.

## Cronograma (estimativa, a revisar ao fim da E1)

Calibração: o histórico do projeto mostra fatias pequenas entregues em um a dois dias e fatias sensíveis a segurança com muitas rodadas de auditoria (ContactCenter: dez). Esforço em dias úteis de desenvolvimento assistido: E0 2, E1 8, E2 5, E3 9, E4 9, E5 5, E6 5 (6 previstos), E7 5, E8 4 (depende do ERP) ≈ 53 dias; com paralelismo, caminho crítico de cerca de 42 dias úteis.

| Cenário | Desenvolvimento pronto | Auditoria e consolidação | Homologação final no Ubuntu | Piloto possível |
|---|---|---|---|---|
| Otimista | 18/12/2026 | 25/12/2026 | 08/01/2027 | a partir de 29/01/2027 |
| **Provável** | **16/01/2027** | 27/01/2027 | **12/02/2027** | a partir de 05/03/2027 |
| Conservador | 13/02/2027 | 27/02/2027 | 20/03/2027 | a partir de 10/04/2027 |

Premissas do provável: aprovações rápidas, duas rodadas de auditoria independente, duas semanas de homologação na VM, número de teste e conta de modelos da Meta e acesso ao BImeta ERP em tempo. A preparação do Ubuntu corre em paralelo com a candidata `v1-rc1` e não está no caminho crítico. As datas de 19/11/2026 e 30/11/2026 deixaram de ser compromisso.

## Dependências externas e pendências abertas

- Número de teste e conta de modelos aprovados da Meta (para o primeiro envio real, **autorização expressa**).
- BImeta ERP: quem implementa o lado do ERP, versão do .NET Framework e do TLS, acesso a um ambiente de teste.
- Safari e Chrome em ERP real com HTTPS (só na homologação; no PC há o Edge).
- Valores de limites, códigos de erro e preços da Meta: conferir na documentação oficial antes da E3.
- Política de consentimento e prazos legais: decisão do proprietário e do jurídico (o produto implementa o mecanismo).

## Registro de andamento

| Data | Etapa | O que aconteceu |
|---|---|---|
| 16/11/2026 | E0 | Tag `v1-rc1` em `32cf06d`; branch `feature/v1-embedded` em worktree próprio; ADR 0039 e 0040; matriz 0.3 (sem aresta nova); BOUNDARIES §13 e 0.4.11; spikes documentais (`EMBEDDED-SPIKES.md`); `playwright-core 1.65.1` no `frontend` com o Edge 157 verificado por script; `HOMOLOGACAO-V1.md` com a Fase 8 e o critério de conclusão |
| 16/11/2026 | E0 | Gate: backend sem falha (integração 1752 aprovados e 14 ignorados, como na candidata; arquitetura e demais projetos verdes), frontend 458 testes e build ok. Pendências: ratificação do texto dos ADRs 0039 e 0040; testes de arquitetura novos (lista de rotas) nascem na E1, junto da lista; Safari e nono dígito só na homologação |
| 16/11/2026 | E1 | Commit `8cf9ffd`. Gate: integração 1859 aprovados e 14 ignorados, Identity 237, arquitetura 80, frontend 458 e build. **Auditoria independente (Codex): reprovada** |
| 16/11/2026 | E1-R | Etapa corretiva dos achados da auditoria (ver ADR 0039, "E1-R"). Relatório por achado e pacote de reauditoria em `docs/architecture/EMBEDDED-REAUDITORIA-E1R.md`. A E2 NÃO começa antes da reauditoria sem bloqueadores. O cronograma abaixo pressupunha a E1 aprovada: a data provável de homologação (12/02/2027) fica **suspensa** até a nova auditoria |
| 16/11/2026 | E1-R | Commits `7a07d91`, `c33f1b9`, `011dac1`. Terceiro gate: integração 1919 aprovados/14 ignorados/0 falhas; demais projetos sem falha; frontend 458 e build. Pendência registrada: falha intermitente de concorrência (500 em 2 de 30 resgates de contexto) no 1.º gate, **sem causa comprovada**, a investigar se reaparecer. Próximo passo: reauditoria independente pelo Codex; E2 não iniciada |
