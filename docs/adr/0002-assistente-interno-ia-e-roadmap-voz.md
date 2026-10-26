# ADR 0002 — Assistente interno de IA e roadmap de voz

- **Status:** Aceito
- **Data:** 26/10/2026
- **Decisor:** Mauro Porpino
- **Referências:** `ARCHITECTURE.md` (seções 4, 10, 11 e 15) e `CLAUDE.md` (atualização do Assistente interno de IA)

## Contexto

A plataforma ia.bimeta nasceu para atendimento a clientes por IA, fluxos determinísticos e operadores humanos. Colaboradores do tenant, atendentes ou não, também precisam consultar informações da empresa e dos sistemas integrados (por exemplo LabNet e BImeta) sem abrir um atendimento de cliente. Em paralelo, havia dúvida sobre a ordem de entrega da voz em relação ao MVP e à primeira versão comercial.

## Decisão

1. **Assistente IA para qualquer colaborador autorizado do tenant**, não apenas atendentes, em aba ou painel próprio chamado *Assistente IA* (`Employee AI Workspace`). Funciona sem atendimento de cliente aberto.
2. **Mesma aplicação e mesma interface** da Central de Atendimento, com opção de painel lateral durante o atendimento. Sessões e históricos **separados**: `EmployeeAssistantSession` (privada do colaborador) é distinta de `ServiceSession` e `Conversation`. Nenhuma resposta do assistente interno é enviada automaticamente ao cliente; contexto de atendimento só entra por ação explícita e autorizada do colaborador.
3. **Dados dos sistemas integrados por capacidades tipadas, autorizadas e auditadas** (Capability Execution Engine). O assistente nunca executa SQL arbitrário gerado por LLM. A conta técnica da integração não amplia os direitos do colaborador. Fatos operacionais vêm do sistema de origem, com fonte e horário da consulta quando pertinente. Orientações e normas vêm do Knowledge Engine aprovado.
4. **Autorização efetiva em cada consulta:** identidade, tenant, organização, unidade, setor, cargo e concessões vigentes.
5. **O Assistente interno integra o MVP, inicialmente somente leitura**, com um catálogo pequeno de capacidades LabNet homologadas. Operações com efeito colateral ficam para fase posterior, com confirmação, autorização, idempotência e auditoria.
6. **A voz não integra o MVP nem a primeira versão comercial e será implementada após ela.** A primeira versão comercial prioriza WhatsApp, IA e atendimento humano; seu escopo detalhado permanece pendente de definição. As abstrações de voz (Voice Gateway, `VoiceCall`) permanecem na arquitetura, sem implementação antes desse marco.
7. **Somente a voz é adiada.** A continuidade entre canais de texto (sessão multicanal) **não está adiada**: poderá ser implementada conforme as necessidades do MVP, com verificação de identidade e isolamento de contexto. Apenas a associação de ligações telefônicas a sessões multicanal fica junto da voz.

## Justificativas

- Reutiliza AI Agent Runtime, Provider Gateway/BYOK, Knowledge Engine, Capability Execution Engine, autenticação e auditoria, sem criar um segundo produto.
- Separar sessões evita o risco central do sistema: contexto ou resposta chegando à pessoa errada.
- Capacidades tipadas e autorização por consulta impedem que a IA vire um atalho para dados fora do direito do usuário.
- Somente leitura no MVP reduz a superfície de risco enquanto o catálogo de consultas é homologado.
- A voz depende de APIs, custos e elegibilidade ainda não validados (`ARCHITECTURE.md` seção 4.3). Adiá-la reduz risco e antecipa a entrega do núcleo comercial.
- A continuidade entre canais de texto não depende de voz e atende a necessidade do MVP; adiá-la apenas por causa da voz seria desnecessário.

## Alternativas descartadas

- **Assistente só para atendentes:** exclui colaboradores de outras áreas que também precisam das consultas.
- **Aplicação separada para o assistente:** duplicaria autenticação, auditoria, BYOK e infraestrutura.
- **Consulta direta a bancos de ERPs por SQL gerado por IA:** sem controle de autorização, auditoria e segurança.
- **Voz no MVP ou na primeira versão comercial:** aumenta risco técnico e comercial antes de validar o núcleo.
- **Adiar toda a sessão multicanal junto com a voz:** descartado; apenas a voz é posterior.

## Consequências

- Novo componente conceitual `Employee AI Workspace` e nova entidade `EmployeeAssistantSession`, sem ligação automática a atendimentos de clientes.
- O catálogo inicial de capacidades LabNet e a matriz de papéis passam a ser pré-requisitos do módulo.
- Aumento de consumo de IA (BYOK) pelos colaboradores; limites e custos precisam de observabilidade.
- O histórico interno exige política de retenção, minimização e LGPD próprias.
- A seção 11 de `ARCHITECTURE.md` posiciona o Employee AI Workspace na etapa 9, depois dos componentes de IA, Knowledge Engine, Capability Execution Engine e do conector LabNet, e antes da homologação Linux. A voz fica na etapa 11, depois da homologação e da produção.
- Arquitetura e contratos devem manter portas (interfaces) para voz, sem implementação, sem bloquear nenhuma entrega.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Resposta interna enviada a um cliente | Separação rígida de sessões; envio ao cliente só por ação explícita e validação do destino; testes de acesso. |
| Escalonamento de privilégio pela conta técnica | Autorização efetiva por consulta, com o usuário final como referência; conta técnica nunca amplia direitos. |
| Vazamento de dados sensíveis entre colaboradores, unidades ou tenants | Isolamento por colaborador, tenant e unidade; paginação e limites; logs minimizados. |
| Prompt injection via dados de ERP, documentos ou mensagens | Conteúdo externo tratado como não confiável; ferramentas tipadas; sem SQL livre. |
| Custo de IA crescendo sem controle | Limites por tenant e colaborador; métricas de tokens e custo estimado. |
| Escopo crescente (relatórios, escritas) | MVP somente leitura; novas capacidades por homologação e aprovação. |
| Associação indevida de pessoas entre canais de texto | Verificação de identidade e isolamento de contexto; nunca associar pessoas só por semelhança de números. |
| Expectativa comercial de voz | Comunicação explícita de que a voz vem após a primeira versão comercial. |
| Indisponibilidade do sistema de origem | Falha segura: informar a indisponibilidade, sem inventar dados. |

## Critérios de aceitação

1. Um colaborador sem permissão para um dado recebe negativa explícita, mesmo se a conta técnica da integração tiver acesso.
2. Dois colaboradores, de tenants ou unidades diferentes, nunca veem contexto ou histórico um do outro (teste automatizado de isolamento).
3. Nenhuma mensagem do assistente interno chega a um cliente sem ação explícita e autorizada.
4. Nenhuma capacidade do MVP realiza escrita no sistema de origem (teste que confirma a recusa).
5. Nenhuma consulta executa SQL gerado por LLM; apenas capacidades do catálogo.
6. Respostas com dados operacionais indicam fonte e horário da consulta.
7. Falha, timeout ou indisponibilidade da origem gera resposta segura, sem dados inventados.
8. Conteúdo externo com instruções maliciosas não altera permissões nem ferramentas disponíveis.
9. Toda consulta é auditada (usuário, tenant, capacidade, parâmetros minimizados, resultado e horário).
10. Revisão de arquitetura confirma que o Voice Gateway existe apenas como porta, sem implementação, e que sua ausência não bloqueia nenhuma entrega do MVP nem da primeira versão comercial.
11. A continuidade entre canais de texto, quando implementada, exige verificação de identidade e isola o contexto de cada pessoa (o cenário Mauro/Pedro continua valendo entre canais); nenhuma associação ocorre apenas por semelhança de números.

## Pendências

Matriz de papéis, catálogo inicial de consultas LabNet, retenção do histórico interno, escopo de relatórios, critérios de autenticação reforçada e definição formal do escopo detalhado da primeira versão comercial.

## Revisão

Reavaliar esta decisão se o catálogo de capacidades exigir escritas antes do previsto, se a voz for antecipada, se houver incidente de isolamento ou se o modelo de permissões mudar.
