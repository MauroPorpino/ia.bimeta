# Knowledge Engine: backlog de otimização de custos de IA

Origem: auditoria independente do Codex sobre o Knowledge Engine (resultado informado pelo proprietário em 18/11/2026): **PARCIALMENTE ADEQUADO** para otimização de custos de IA. **O relatório integral do Codex não está neste repositório e esta lista não foi reconferida no código nesta sessão**; cada item deve ser verificado no código antes de virar tarefa. Documento vivo. Referências existentes: ADR 0029 (FAQ direto), ADR 0030 (lacunas), ADR 0034 (Knowledge híbrido), `docs/operations/CONHECIMENTO-DO-FAQ.md`, `CONHECIMENTO-HIBRIDO.md`, `HOMOLOGACAO-DO-CONHECIMENTO.md`.

**Nada aqui é bloqueador da E2 do Embedded.** Nenhuma melhoria do Knowledge Engine é implementada na E2 nem nesta etapa de planejamento.

## 1. O que o Codex confirmou como implementado

- FAQ determinístico que responde sem LLM.
- Aliases e respostas aprovadas.
- Busca semântica condicional com pgvector.
- Controle de tokens e consumo por tenant.
- Identificação condicional de lacunas.
- Reutilização de conhecimento entre Agents e Workflows.

## 2. Parcial ou pendente

| Id | Item | Observação |
|---|---|---|
| KN-1 | Cache de respostas generativas | não existe |
| KN-2 | RAG documental completo | parcial |
| KN-3 | Seleção automática de modelos por custo | não existe |
| KN-4 | Contabilização integral de embeddings e de geração local | parcial |
| KN-5 | Redução do FAQ inteiro enviado ao fallback | o contexto do fallback leva o FAQ inteiro |
| KN-6 | Ressincronização confiável do índice semântico | pendente |
| KN-7 | Correção do denominador no relatório de participação do FAQ | métrica econômica incorreta |

## 3. Prioridade para a V1 (nesta ordem)

1. **Limites de consumo e preços dos modelos:** configuração por tenant, conferida na documentação oficial dos provedores antes de usar valores.
2. **Curadoria de FAQ e aliases:** processo e responsáveis (operação; ADR 0030 já cobre a revisão das lacunas).
3. **Medição mínima de chamadas, tokens e custos por tenant.**
4. **Correção das métricas econômicas** (KN-7 e demais denominadores).
5. **Homologação da busca semântica antes de ativá-la** (`HOMOLOGACAO-DO-CONHECIMENTO.md`; segue desligada por padrão, ADR 0034).
6. **Diagnóstico e ressincronização do índice** (KN-6), **se** a semântica for usada.

Cada item da V1 entra numa etapa própria, com plano e autorização; nenhum é pré-requisito do Embedded.

## 4. Evoluções posteriores (salvo justificativa concreta)

Cache generativo (KN-1); roteamento automático entre modelos (KN-3); RAG documental com ACL (KN-2); cache de embeddings; otimização avançada do contexto do fallback (KN-5); contabilização integral de embeddings e geração local (KN-4), exceto o mínimo do item 3 acima.

## 5. Diretrizes

- **Reuso:** novos agentes e canais **reutilizam** os mecanismos centrais de conhecimento e de controle de consumo quando aplicáveis; não criam caminho paralelo de FAQ, medição ou limite.
- **Operações transacionais não consultam FAQ:** preço, agenda, cobertura, cadastro e demais dados atuais continuam vindo de **integrações autorizadas com o sistema de origem** (CLAUDE.md, "Knowledge Engine" e "Agentes, workflow e integração"); o conhecimento aprovado só cobre orientações, nunca fatos transacionais.
- **Sem novos bloqueadores:** oportunidades de otimização econômica não bloqueiam etapas de outros módulos (inclusive a E2 do Embedded); entram aqui com prioridade e evidência.
- **Correções humanas** continuam gerando sugestões para revisão (ADR 0030), nunca atualização automática de orientação clínica.
