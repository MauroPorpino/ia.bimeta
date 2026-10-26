# ia.bimeta — ARCHITECTURE.md

> **Status:** Documento-base para implementação e discussão.  
> **Versão:** 0.1 (preparação do repositório; consolida decisões discutidas até 26/10/2026).  
> **Natureza:** Decisões confirmadas estão indicadas como **Decidido**; alternativas técnicas como **Proposta a validar**.  
> **Escopo:** Plataforma ia.bimeta independente. LabNet e BImeta são sistemas externos integráveis, não módulos internos.

## 1. Visão do produto

ia.bimeta é uma plataforma SaaS omnichannel, multi-tenant, de atendimento a clientes por agentes de IA, fluxos determinísticos e operadores humanos. Deve operar com ou sem ERP, integrar sistemas diversos por APIs e admitir vários setores econômicos.

O primeiro caso de referência é um laboratório que utiliza o **LabNet**, mas a arquitetura não pode conter dependências de domínio laboratorial no núcleo genérico.

**Decidido:** substituir integralmente, após testes e homologação, a gestão de conversas, chatbot e interface de atendimento humano hoje existentes no módulo WhatsApp do LabNet. O LabNet continua como sistema de gestão laboratorial e fonte oficial de seus dados transacionais.

## 2. Princípios e limites

1. **Independência de ERP:** integrações são adaptadores, não dependências do núcleo.
2. **Isolamento multi-tenant:** dados, contexto, arquivos, credenciais e operações isolados por tenant; isolamento adicional por organização/unidade e permissões.
3. **IA não é fonte oficial de dados transacionais:** consulta endpoints antes de confirmar preços, cobertura, agenda, cadastro ou criação de registros.
4. **Workflow determinístico:** LLM interpreta e sugere; o sistema controla estados, autorização, validação, confirmação e execução.
5. **Intervenção humana reversível:** a IA pode transferir, o humano pode assumir, e o fluxo pode voltar à IA no ponto correto.
6. **Omnichannel real:** sessões podem associar canais distintos após verificação de identidade e autorização.
7. **Observabilidade e auditoria:** rastrear eventos, decisões, chamadas a modelos, ações e custos estimados, sem registrar segredos ou dados sensíveis desnecessários.
8. **Implantação gradual:** primeiro ambiente local no Windows/VS Code; posteriormente homologação Linux e produção isolada.

## 3. Hierarquia de organizações e autorização

Modelo lógico:

```text
Tenant
 ├── Organization (empresa jurídica)
 │    ├── BusinessUnit (matriz, filial, unidade operacional)
 │    │    └── Department
 │    │         └── ServiceQueue
 │    └── equipes/filas compartilhadas autorizadas
 └── canais, agentes, políticas e credenciais, conforme escopo
```

- **Decidido:** considerar matriz, filiais, várias empresas jurídicas, unidades e setores.
- `TenantId`, `OrganizationId` e, quando aplicável, `BusinessUnitId` acompanham entidades e eventos.
- Um operador pode atender várias unidades se possuir concessões explícitas (`UserOrganizationGrant`, `QueueMembership`).
- Fila compartilhada não implica acesso irrestrito aos dados.
- `RoutingPolicy` considera canal, intenção, unidade, região, horário, feriados, fuso, especialidade, prioridade, disponibilidade e carga.
- Um número WhatsApp pode atender uma ou mais unidades, exigindo identificação do destino organizacional.
- Transferências entre organizações/unidades exigem revalidação de autorização.
- **Proposta a validar:** PostgreSQL com Row-Level Security como defesa em profundidade; autorização na aplicação continua obrigatória.

## 4. Componentes do núcleo

| Componente | Responsabilidade |
|---|---|
| Channel Gateway / Channel Registry | Recepção, autenticação, normalização e roteamento de eventos por canal |
| Conversation Engine | Ciclo de vida de atendimentos, mensagens, anexos, histórico e operadores |
| **Conversation Context Manager** | Contexto por atendimento para cada chamada ao LLM; resumos, dados confirmados, correlação e concorrência |
| Workflow Engine | Estados, regras, perguntas, condições, subfluxos, aprovações e retomada |
| AI Agent Runtime | Planeja respostas e usa ferramentas autorizadas, sob regras do workflow |
| AI Provider Gateway | Abstração de OpenAI, Anthropic e outros, BYOK, limites, erros e métricas |
| Knowledge Engine | Conhecimento aprovado, busca, versões, revisão e escopo |
| Capability Execution Engine | Contratos de integração, permissões, validações, idempotência, chamadas e auditoria |
| Routing / Escalation Engine | Filas, distribuição, horários, plantão e transferência humana |
| Voice Gateway | Telefonia, áudio, transcrição/síntese e interrupções, conforme capacidades reais do canal |
| Central de Atendimento | Interface web de operadores, filas, histórico, anexos, IA assistiva e supervisão |
| Workers | Webhooks, filas, sincronização, tarefas assíncronas, reprocessamento e reconciliação |

### 4.1 Isolamento do contexto: exemplo Mauro e Pedro

- Atendimento `#1001` (Mauro): pergunta sobre exame A.
- Atendimento `#1002` (Pedro): pergunta sobre exame B.
- Mauro pergunta depois: “Preciso ficar em jejum para esse exame?”
- O **Conversation Context Manager** recupera **apenas** mensagens e estado autorizados de `#1001`, inclusive assunto atual “exame A”, consulta informação de preparo aprovada/oficial e envia contexto específico ao provedor de IA.
- O retorno é correlacionado à requisição original e enviado apenas ao canal autorizado de Mauro; o histórico de Pedro nunca integra o contexto.

**Regra:** nenhuma “conversa global” compartilhada entre clientes. O provedor recebe requisições isoladas. O estado oficial reside no ia.bimeta, não depende da memória persistente de um provedor.

Identificadores sugeridos:
- `TenantId`
- `OrganizationId`
- `ServiceSessionId` (atendimento lógico, potencialmente multicanal)
- `ConversationId` (conversa de um canal)
- `MessageId`, `ProviderEventId`, `AiRequestId`, `CorrelationId`
- `WorkflowInstanceId`, `Version`

Cada chamada à IA recebe apenas instruções, resumo, mensagens recentes relevantes, dados estruturados confirmados, referências documentais e ferramentas permitidas. Resumos não substituem fatos estruturados. Implementar deduplicação de webhooks, ordenação/controle de concorrência por sessão, idempotência, cancelamento, prevenção de resposta obsoleta e checagem do destino antes do envio.

### 4.2 Central de atendimento e transição IA ↔ humano

**Decidido:** central própria no ia.bimeta, com filas, atendentes, histórico, anexos, supervisão e permissões.

Modos:
1. IA autônoma em ações permitidas;
2. humano assistido por IA;
3. IA supervisionada, com aprovações pontuais.

Ao escalar, registrar motivo, resumo, pendências e estado do fluxo; encaminhar à fila apropriada. O operador pode resolver a pendência e devolver à IA sem reiniciar o atendimento.

### 4.3 Canais e voz

Canais previstos: WhatsApp (Meta Cloud API), Instagram Direct, Messenger, Telegram, web chat, aplicativos, email e telefonia/voz — **sujeitos às APIs, políticas e elegibilidade de cada fornecedor**.

`ServiceSession` pode relacionar uma ligação telefônica e documentos recebidos por WhatsApp. Exigir verificação de identidade e consentimento apropriado; nunca associar pessoas somente por semelhança de números.

Voz em tempo real envolve streaming, STT/TTS ou speech-to-speech, latência, interrupção e handoff. **Não presumir disponibilidade da WhatsApp Business Calling API ou de streaming sem validação técnica e comercial atualizada.**

## 5. BYOK — modelo de IA contratado pelo tenant

**Decidido:** padrão inicial é **Bring Your Own Key**. O tenant contrata e paga diretamente o consumo de APIs de IA. A BImeta cobra seus serviços SaaS e de implantação/suporte.

- Chaves armazenadas em cofre de segredos/armazenamento criptografado, nunca em frontend, Git, logs ou respostas HTTP.
- Configurar provedor e modelo por tenant, organização e tipo de tarefa, quando autorizado.
- Registrar tokens, chamadas, latência, áudio e custo estimado para observabilidade, mesmo sem faturamento de tokens pela BImeta.
- Definir limites, fallback autorizado e comportamento quando a chave ou a cota falhar.
- Manter possibilidade arquitetural futura de serviço de IA gerenciado, **não assumido para o MVP**.
- Assinaturas de aplicativos de chat não equivalem necessariamente a acesso faturável de API.

## 6. Knowledge Engine: conhecimento ≠ dado operacional

**Decidido:** Knowledge Engine próprio, com conteúdo versionado, origem, escopo, validade, aprovação e busca. Não replicar indiscriminadamente bancos de ERPs.

**Conhecimento apropriado:** FAQ, procedimentos, orientações aprovadas, políticas, manuais, sinônimos validados e material de treinamento.

**Fonte oficial externa:** paciente, preço atual, convênio/plano aceito, cobertura, agenda, código ativo de exame, orçamento, requisição, agendamento e resultado laboratorial no LabNet.

Arquitetura híbrida:
1. Buscar conhecimento empresarial aprovado para compreender e orientar.
2. Consultar endpoints oficiais para fatos operacionais atuais.
3. Usar sincronização seletiva somente para índices derivados úteis (por exemplo, catálogo de exames/sinônimos), com versão, origem, invalidação e reconciliação.
4. Antes de operação transacional, revalidar no sistema de origem.
5. Se origem indisponível, registrar solicitação pendente; não afirmar que operação foi concluída.

Alimentação:
- cadastro manual;
- documentos e manuais;
- endpoints/índices seletivos;
- sugestões a partir de dúvidas e correções humanas.

**Decidido:** aprendizado supervisionado. Sugestões de conteúdo passam por revisão humana, especialmente em assuntos clínicos, preparo de exames e convênios. Proibir publicação automática de conteúdo crítico.

**Proposta a validar:** PostgreSQL + `pgvector` para busca semântica, combinada com busca textual, respeitando escopo e autorização.

## 7. Integrações — caso de referência LabNet

O LabNet é um **sistema externo**, acessado por um conector que implementa contratos do Capability Execution Engine.

Casos de uso:
- identificar paciente e consultar/atualizar cadastro com autorização;
- receber carteirinha e identificar convênio/plano com confirmação;
- interpretar pedido médico, inclusive manuscrito, e resolver ambiguidade com revisão especializada;
- correlacionar exames com catálogo oficial do LabNet;
- consultar preços e criar orçamento;
- consultar disponibilidade de coleta domiciliar;
- confirmar com paciente, criar requisição e agendamento;
- escalar para humano quando necessário.

Nunca inventar código de exame, cobertura, preço, disponibilidade ou interpretação ambígua. Registros só são considerados criados após confirmação do LabNet. Ações com efeito colateral exigem validação, autorização, idempotência e trilha de auditoria.

## 8. Diretrizes técnicas iniciais

**Escolhas já discutidas:**
- Editor: **VS Code exclusivamente**, com Claude Code como agente principal de implementação.
- Desenvolvimento inicial: **Windows local**, sem necessidade de servidor Linux ou Docker neste momento.
- Backend: C# / ASP.NET Core (.NET moderno; **.NET 10 proposto**).
- Frontend: React + TypeScript.
- Banco: PostgreSQL (**proposta**, incluindo RLS e `pgvector`).
- Arquitetura de implantação: **monólito modular inicialmente**, com workers e interfaces bem delimitadas; evitar microserviços prematuros.
- Git desde o primeiro commit.
- Linux como alvo futuro de homologação e produção, com ambientes separados.

**Pendente de validação técnica antes da instalação:** suporte de versões de SDK, Node.js, extensões e PostgreSQL ao Windows 10 Pro 22H2; verificar requisitos oficiais e alternativas. Não presumir compatibilidade. Windows 10 está fora do suporte regular e requer avaliação de segurança.

**Proposta a validar:** Redis, broker/filas e object storage conforme demanda real; não exigir todos no primeiro dia. Para desenvolvimento local, minimizar consumo de RAM e disco.

## 9. Segurança e LGPD

- Autorização por tenant, organização, unidade, setor, fila, operador e ferramenta.
- Identidade e autorização do cliente antes de expor dados pessoais ou resultados.
- Princípio do menor privilégio; logs sem segredos ou conteúdo clínico desnecessário.
- Dados médicos são sensíveis: retenção, consentimentos/bases legais, exclusão, auditoria e contratos com provedores devem ser analisados.
- Proteção de webhooks (assinaturas), rate limiting, TLS, rotação de segredos, prevenção de prompt injection e controles contra exfiltração.
- Conteúdo de mensagens, PDFs e resultados de ferramentas é **dado não confiável**, não instrução privilegiada.
- Acesso de agentes de programação restrito a ambientes de desenvolvimento; sem credenciais reais de produção.

## 10. Modelo de entidades inicial (conceitual)

`Tenant`, `Organization`, `BusinessUnit`, `Department`, `User`, `UserOrganizationGrant`, `ServiceQueue`, `QueueMembership`, `RoutingPolicy`, `SchedulePolicy`, `Channel`, `ChannelAccount`, `Contact`, `ServiceSession`, `Conversation`, `Message`, `Attachment`, `VoiceCall`, `WorkflowDefinition`, `WorkflowVersion`, `WorkflowInstance`, `WorkflowState`, `AgentDefinition`, `AiProviderCredentialReference`, `AiRequest`, `KnowledgeSource`, `KnowledgeItem`, `KnowledgeRevision`, `KnowledgeApproval`, `IntegrationDefinition`, `Capability`, `CapabilityExecution`, `AuditEvent`.

**Não gerar o esquema completo sem aprovação dos contratos e invariantes.** Avaliar chaves compostas por tenant, constraints, índices, versionamento e política de retenção antes das migrations.

## 11. Desenvolvimento em etapas (proposta a validar)

1. Preparação do repositório, SDKs, build, testes e execução mínima.
2. Identidade, tenant/organização/unidade, autorização e auditoria.
3. Conversation Engine + Context Manager com testes de isolamento Mauro/Pedro.
4. Central de atendimento, filas, roteamento e handoff IA/humano.
5. WhatsApp e webhooks com testes seguros.
6. AI Provider Gateway BYOK, agente e conhecimento aprovado.
7. Workflow Engine e Capability Execution Engine.
8. Conector LabNet, documentos, orçamento e agendamento com homologação.
9. Voz e sessão multicanal, após validação de APIs/custos.
10. Homologação Linux e implantação de produção em infraestrutura separada.

**MVP:** escopo mínimo operacional a aprovar. Não confundir roadmap completo com escopo contratado do MVP; voz é objetivo importante, mas a ordem e critérios de entrada devem ser decididos explicitamente.

## 12. Critérios mínimos de qualidade

- Testes de isolamento entre tenants, organizações e atendimentos; cenário Mauro/Pedro.
- Concorrência, mensagens fora de ordem, duplicação e idempotência.
- Controle de acesso por filial e fila compartilhada.
- Continuidade após handoff IA ↔ humano.
- Falha de provedor de IA e de endpoints externos.
- Conteúdo clínico sem fonte válida deve resultar em esclarecimento/escalonamento.
- Sem credenciais reais no repositório.
- Compilação e testes reproduzíveis no VS Code e via CLI.
- Migrations revisadas e rollback planejado.

## 13. Decisões ainda abertas

- Escolha final do banco, mecanismo de filas e hospedagem.
- Limites exatos do MVP e prioridade da voz.
- Política de atendimento encerrado versus nova sessão e recuperação de histórico.
- Estratégia de migração dos atendimentos legados do LabNet.
- Contratos dos endpoints LabNet, autenticação e eventos de sincronização.
- Regras de retenção, LGPD e acordos com provedores.
- Disponibilidade e restrições atuais das APIs de voz e canais sociais.
- Modelos de permissão detalhados, SLA e prioridades de fila.
- Compatibilidade de toolchain no Windows 10.

## 14. Referências de trabalho

Este documento é uma **síntese operacional** das decisões debatidas, não substitui o documento Word de arquitetura com diagramas. Divergências ou novas escolhas devem ser registradas como ADRs em `docs/adr/` antes de mudanças estruturais.
