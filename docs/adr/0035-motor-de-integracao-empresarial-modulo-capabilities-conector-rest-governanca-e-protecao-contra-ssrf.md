# ADR 0035 — Motor de Integração Empresarial: módulo `Capabilities`, conector REST/JSON genérico, governança de escrita e proteção contra SSRF

- **Status:** **Aceito** (ratificado pelo proprietário em 12/11/2026, depois de conferir que a implementação corresponde ao decidido; ver "Conferência da ratificação" ao final). Atualização do ADR 0036: o Worker também compõe o módulo.
- **Data:** 11/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0005 e `BOUNDARIES.md` (o módulo `Capabilities` e a porta `ICapabilityConnector` já previstos, ARQ-21, EFX-4, LIM-1), 0007 e 0017 (Agents), 0034 (Knowledge híbrido), `CLAUDE.md` (LLM interpreta e sugere, não executa; o Capability Execution Engine valida e executa).
- **Preserva:** matriz de dependências (`Capabilities` depende só de Identity, Tenancy e Audit; nenhuma aresta nova), o comportamento do ContactCenter, do Conversations e do Knowledge, e o contrato aprovado do Agents (nenhum código do Agents muda).

## Contexto

A plataforma precisa consultar e, com governança, alterar dados em sistemas empresariais (ERPs e legados) por APIs. O `CLAUDE.md` já exige que o modelo de IA **não execute** operação externa diretamente: a execução passa por um motor que valida autorização e parâmetros, e o `BOUNDARIES.md` reserva o módulo `Capabilities` para isso. Faltava o motor: o catálogo de operações por tenant, a separação entre a definição de uma operação e a credencial de conexão, a execução segura (SSRF, segredos, idempotência, limites) e a administração.

## Decisão

### 1. Módulo `Capabilities` (Contracts, Core, Infrastructure, Presentation)

Esquema `capabilities`, `CapabilitiesDbContext` e migrations próprios. Nenhuma referência a LabNet ou BImeta: o motor é genérico; um sistema entra **por configuração** (conexão + operações) ou, no futuro, por um adaptador que implemente `ICapabilityConnector` (outro tipo de conector).

- **Contracts:** `ICapabilityCatalog` (o que está disponível), `ICapabilityExecutor` (executar, confirmar, cancelar), `CapabilityInvocation`, `CapabilityResult`, `CapabilityError`, `CapabilityDescriptor` e a porta `ICapabilityConnector` (tipo de conector). O chatbot visual, o Agents, o Workflows e o assistente interno usarão só isso.
- **Três conceitos separados:** **conexão** (`IntegrationConnection`: endereço base, autenticação, limites, cabeçalhos permitidos, segredo cifrado), **operação** (`CapabilityDefinition`: método, caminho, parâmetros tipados, mapeamento da resposta, efeito, idempotência, confirmação; versionada) e **execução** (`CapabilityExecution`: a trilha de cada chamada). A operação nunca contém credencial; a conexão nunca contém regra de operação.
- **Versionamento:** cada alteração cria uma versão em rascunho; publicar aposenta a anterior, numa transação; só a versão publicada executa; uma execução registra a versão usada.

### 2. Execução (a ordem é fixa; falha segura: na dúvida, não executa)

1. Tenant e permissões vêm do `RequestContext` (nunca de parâmetro). 2. A operação publicada e a conexão existem e estão habilitadas; o chamador (`Operator`, `Agent`, `Workflow`, `EmployeeAssistant`, `Chatbot`) está entre os permitidos da operação. 3. Permissão: leitura exige `IntegrationExecute`; escrita exige `IntegrationExecuteWrite`; um contexto de sistema só inicia operação para chamadores não humanos. 4. Os parâmetros são validados contra o esquema declarado (tipo, tamanho, padrão, faixa, enumeração; nome desconhecido é recusado). 5. Limites por tenant e por conexão. 6. A execução é registrada **antes** de a chamada sair. 7. O conector chama e o resultado é mapeado em campos tipados e sanitizados.

O texto livre do cliente **nunca** vira URL, cabeçalho ou operação: o chamador escolhe uma chave de operação do catálogo e fornece **valores** de parâmetros declarados; o caminho, a consulta, o corpo e os cabeçalhos são montados a partir do molde da operação, com escape. A resposta de um sistema externo é conteúdo não confiável: só os campos mapeados saem, com limite de tamanho e sem caracteres de controle, e o resultado é marcado `UntrustedContent`.

### 3. Escrita

- Toda operação com efeito colateral declara `IdempotencySupport` (`Proven`, `Reconcilable`, `None`; ARQ-21). **Retentativa automática só para leitura ou escrita `Proven`.** `None` e `Reconcilable` nunca são reenviadas automaticamente; timeout ou 5xx depois de enviar vira **`Unknown`** (resultado ambíguo), nunca reenvio.
- Escrita exige **chave de idempotência** do chamador (`(tenant, chave)` único): a mesma chave com os mesmos parâmetros devolve o resultado registrado (sem nova chamada); com parâmetros diferentes é recusada; duas chamadas simultâneas disputam um índice único (uma executa, a outra vê "em andamento").
- Escrita pede **confirmação humana** por padrão: a primeira chamada grava a execução como `AwaitingConfirmation` (parâmetros cifrados, validade curta) e uma pessoa com `IntegrationExecuteWrite` a confirma ou cancela. Dispensar a confirmação só é aceito para escrita `Proven`.
- Um contexto de sistema (Agents, Workflows) **nunca confirma**: confirmar exige usuário.

### 4. Segurança do conector REST/JSON

- Métodos GET, POST, PUT, PATCH, DELETE; autenticação por API Key (cabeçalho ou consulta), Basic e OAuth 2.0 Client Credentials (token em cache em memória, nunca persistido).
- **SSRF:** só `https` (http e portas extras só por configuração do **operador da plataforma**, nunca do tenant); sem credencial na URL; nomes internos (`localhost`, `.local`, `.internal`, rótulo único) recusados; a resolução de DNS e a verificação do endereço acontecem **no momento da conexão** (`ConnectCallback`), então não há janela para DNS rebinding; loopback, redes privadas, CGNAT, link-local, multicast, reservados, NAT64, 6to4 e IPv4 mapeado em IPv6 são recusados; metadados de nuvem e link-local **nunca** são liberáveis; um ERP em rede privada exige que o operador libere o CIDR na configuração (`Capabilities:Options:Outbound:AllowedPrivateCidrs`). **Redirecionamentos nunca são seguidos** (3xx vira erro), proxy do sistema e cookies são ignorados.
- Tempo limite por chamada, cancelamento cooperativo, limite de bytes da resposta contado no fluxo (inclusive após descompressão), JSON com profundidade limitada, `Content-Type` JSON obrigatório.
- **Segredos:** AES-256-GCM com chaves próprias do módulo (`Capabilities:Secrets`, 32 bytes em Base64, rotação por identificador), tenant, conexão e tipo de autenticação como dado autenticado; o segredo nunca volta em API, log, auditoria nem mensagem de erro; sem chave configurada a gravação e a execução falham fechadas (503 / `secret-unavailable`).
- Mensagens de erro são **códigos curtos fixos** (nunca texto do sistema externo nem valor de parâmetro).

### 5. Governança

- **Permissões novas:** `IntegrationRead`, `IntegrationManage` (conexões, segredos, operações, habilitar), `IntegrationTest` (testar conexão e operações de leitura), `IntegrationExecute`, `IntegrationExecuteWrite`. O administrador da organização as recebe; o gerente só lê. Escopo por organização da conexão (a conexão sem organização é do tenant e exige concessão de tenant).
- **Auditoria:** toda mudança de configuração, teste, execução, confirmação, cancelamento e negativa vai à trilha do módulo Audit (sem valores); a tabela de execuções guarda quem pediu, de qual origem, o atendimento correlacionado, a versão, o resultado, o código de erro e a duração, **sem parâmetros nem resposta** (os parâmetros de uma confirmação pendente ficam cifrados e são apagados ao concluir).
- **Limites (LIM-1 do módulo):** chamadas por minuto por tenant e por conexão, simultâneas por conexão, escritas por minuto por tenant, testes por minuto; contados no banco (valem entre instâncias) e configuráveis.

### 6. Fora de escopo (deliberado)

Construtor visual de chatbot; adaptadores LabNet e BImeta; SOAP, GraphQL e filas; disjuntor persistido; armazenamento do corpo das respostas; retenção própria das execuções (não há dado pessoal nelas, só metadados); chamadas reais a qualquer sistema externo (a homologação com um ERP real é etapa futura).

## Consequências

- O Agents e o chatbot visual passam a ter um único ponto de entrada para ações externas, com as proteções acima. Nenhum código existente muda de comportamento.
- Um novo ERP é **configuração** (conexão e operações), não código, enquanto falar REST/JSON.
- Custo: a administração de operações é técnica (método, caminho, parâmetros); um assistente de criação e modelos prontos por sistema ficam para depois.
- Riscos conhecidos: a liberação de CIDR privado é decisão do operador e precisa de revisão; o cache de token OAuth é por processo; a idempotência de ponta a ponta depende de o sistema externo honrar a chave (por isso `Proven` é uma declaração do administrador, auditada).

## Registro da implementação (11/11/2026)

- Entregue em cinco pacotes: contratos e núcleo, persistência e conector, governança (embutida nos serviços e provada nos testes), administração em React e testes e documentação (`docs/operations/MOTOR-DE-INTEGRACAO.md`).
- **Portão de função:** um usuário sem nenhuma permissão de integração recebe 403 antes de qualquer consulta (descoberto ao escrever o teste de rotas: um 404 ou uma lista vazia permitiria sondar chaves e ids).
- **Índice único da versão publicada:** o teste de invariantes do banco mostrou que o EF fundia os dois índices parciais sobre as mesmas colunas e a regra "uma versão publicada por chave" só valia na aplicação; a migration `CapabilitiesPublishedUniqueIndex` a restabelece no banco (o índice do rascunho passou a incluir o status nas colunas).
- **Pendente de decisão (fora do escopo desta etapa):** a aresta do Agents (e do Workflows e do assistente interno) para `Capabilities` exige ADR e alteração da matriz; nenhum código do Agents foi tocado.

## Conferência da ratificação (12/11/2026)

Conferido contra a implementação: módulo `Capabilities` sem aresta nova na matriz (só `Audit`); três conceitos separados (conexão, operação versionada, execução sem parâmetros nem resposta); segredos AES-256-GCM com chaves próprias; SSRF com conferência de cada endereço na conexão e sem seguir redirecionamento; escrita com idempotência declarada, confirmação por pessoa e resultado ambíguo nunca reenviado; permissões `IntegrationRead/Manage/Test/Execute/ExecuteWrite`; portão de função (403); índices únicos por banco; auditoria sem valores; limites no banco. Divergências já registradas: o Worker também compõe o módulo (ADR 0036) e o `Agents` passou a usá-lo (ADR 0036).
