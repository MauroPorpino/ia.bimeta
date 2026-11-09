# ADR 0029 — Resposta direta do FAQ sem chamar o modelo: igualdade normalizada, aliases, idioma e medição da economia

- **Status:** **Aceito para o MVP (ratificado pelo decisor em 09/11/2026).** Implementado em 09/11/2026 sob a autorização do decisor ("Pacote A" do diagnóstico do Knowledge Engine interno, com as três decisões abaixo), depois da ratificação do ADR 0028. Nenhuma chamada real à Meta ou à OpenAI; a migração só **adiciona uma coluna anulável** (backup do banco de teste feito antes); `main` intocada; sem push.
- **Data:** 09/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0017 (seção F: FAQ no prompt; o Knowledge Engine fica para a etapa 6b), 0007 e `CONVERSATION-LIFECYCLE.md` (turno, cercas de aceitação, resposta defasada sempre recusada), 0018 (fluxo do MVP), 0024 (`provision`), 0026 (`status`), `docs/architecture/BOUNDARIES.md` (módulo Knowledge previsto; nada dele é implementado aqui).
- **Preserva:** nenhuma aresta nova na matriz de dependências (nem edição de `BOUNDARIES.md`, `ARCHITECTURE.md` ou `module-dependencies.json`); o Conversations **não muda** (nenhum contrato, evento ou tabela); tenant e permissões só pelo `RequestContext`; o modelo continua só sugerindo; resposta defasada continua **sempre** recusada (nada de `StaleReplyMode`); **sem módulo Knowledge, sem busca textual ou semântica, sem embeddings, sem aprendizado automático e sem interface administrativa** (o Pacote B não é implementado).

## Contexto

O diagnóstico de 09/11/2026 mostrou que o FAQ cadastrado no provisionamento só entra no **prompt** (`AgentPrompt.BuildSystemInstruction`): toda mensagem, mesmo "Qual o horário de atendimento?", gasta uma chamada ao modelo com o FAQ inteiro (até 30.000 caracteres) como contexto. Não havia resposta determinística, busca local nem medida de quantas chamadas poderiam ser evitadas. O objetivo desta decisão é **reduzir o consumo de tokens de IA comercial** respondendo, sem chamar o modelo, a pergunta que o próprio tenant cadastrou **de propósito** para isso, **sem arriscar uma resposta errada** e **sem cruzar tenants**.

**Fora do escopo (decidido):** o Pacote B (registro de lacunas e promoção supervisionada para o rascunho), o módulo Knowledge, busca textual (`tsvector`) ou semântica, similaridade por percentual de termos, correção ortográfica, sinônimos implícitos, tela administrativa do FAQ, aprendizado automático, cache global de respostas, configuração por tenant da função.

## Decisão

### 1. O que o tenant cadastra (`FaqEntry`, `AgentDefinitionContent`)

- `FaqEntry(Question, Answer, Aliases = null, AutoReply = false)`. **`Aliases`**: até 10 outras formas, cadastradas de propósito, de fazer a **mesma** pergunta (cada uma com letras ou números e até 300 caracteres); servem só à busca e **nunca vão ao prompt**. **`AutoReply`**: liga a resposta direta **naquela entrada**; **desligada por padrão**, inclusive em toda definição que não a conhece.
- `AgentDefinitionContent.FaqLocale`: o idioma em que o FAQ está escrito (BCP 47, validado e guardado na forma canônica). **Vazio é `pt-BR`.**
- A igualdade de `FaqEntry` é **por valor** (aliases nulos e lista vazia são o mesmo; a lista se compara pelos itens, na ordem): sem isso uma definição lida do banco deixaria de ser igual à que foi salva.
- Validação nova (`AgentDefinitionContent.Validate`): no máximo 10 aliases por entrada; alias em branco ou sem letras e números é recusado; uma entrada com `AutoReply` precisa de pergunta com letras ou números; idioma mal formado é recusado. Os aliases **não** entram no limite total do FAQ (que é o do prompt). Regras novas valem só para o que é novo: um FAQ antigo com perguntas repetidas ou só de símbolos continua válido e republicável.
- Armazenamento: o FAQ continua em `agent_definition_versions.faq` (`jsonb`); os campos novos entram no mesmo JSON (`aliases`, `autoReply`) e o JSON **antigo** (só `question` e `answer`) é lido com `AutoReply = false` e sem aliases, **sem migração de dados**. Única mudança de esquema: a coluna **`faq_locale`** (`varchar(35)`, anulável; nula = `pt-BR`), migração `20261109185130_AgentsFaqLocale` (só `ADD COLUMN`; o `Down` a remove). Ao salvar, os aliases são aparados e sem repetição (repetição é a mesma pergunta depois de normalizada), na ordem do cadastro.

### 2. A regra de correspondência (`FaqMatcher`, Agents.Core, puro)

**A única regra é igualdade** do texto normalizado da mensagem com a pergunta da entrada **ou** com um alias dela. Nada de percentual de termos, distância de edição, fuzzy, sinônimo implícito ou embedding.

- **Normalização** (`FaqMatcher.Normalize`): decomposição Unicode canônica (FormD), descarte das marcas combinantes (acentos), caixa baixa invariante, **só letras e números ficam** e qualquer outro caractere (pontuação, símbolo, emoji, espaço, tabulação, quebra de linha) vira **um** espaço entre palavras. Números ficam: "exame 2" e "exame 3" nunca se confundem. Texto vazio, só de símbolos ou Unicode malformado (par substituto solto) normaliza para vazio e **nunca corresponde**. Texto com mais de 1.200 caracteres nem é normalizado.
- **Ambiguidade resolvida a favor da segurança:** se **duas ou mais entradas** correspondem à mesma mensagem (a mesma pergunta, ou um alias de uma igual à pergunta ou ao alias de outra, ou duas entradas idênticas), **não há resposta direta** e o turno vai ao modelo. A contagem vale para **todas** as entradas, ligadas ou não: duas respostas escritas para a mesma pergunta nunca dão resposta direta, mesmo que só uma ligue o `AutoReply`. Uma pergunta igual a um alias **da mesma** entrada conta uma vez.
- Resultado (`FaqMatchKind`): `NoMatch`, `Direct` (única entrada, com `AutoReply`), `NotAutoReply` (única entrada, sem `AutoReply`: vai ao modelo) e `Ambiguous`.

### 3. Onde entra no fluxo (`TurnRunner`)

Depois da política de conteúdo e da leitura da definição **publicada** (e de ela existir) e **antes** de montar o prompt e chamar `IAiGateway.GenerateAsync`. A resposta direta só acontece quando **todas** as condições valem; qualquer dúvida devolve ao fluxo normal, que chama o modelo como antes:

1. a função está ligada (`Agents:Options:FaqDirectAnswerEnabled`, padrão `true`; a chave de desligamento vale para todos os tenants);
2. o grupo pendente é **uma única** mensagem;
3. ela é de **texto**, com conteúdo pronto e texto não vazio (mídia com legenda igual à pergunta **não** responde direto);
4. o **idioma efetivo da conversa** (o do snapshot) é **igual** ao idioma do FAQ (`pt-PT` não é `pt-BR`; uma conversa em `en-US` com FAQ em português vai ao modelo);
5. o resultado do `FaqMatcher` é `Direct`.

Sem definição publicada nada muda: o agente transfere (`ai-not-configured`), como sempre.

### 4. Como a resposta é registrada (reaproveita o que existe)

A resposta sai pelo **mesmo** `RegisterAiReply` do aviso de transferência e da resposta de áudio: `ReplyKind.Informative`, **sem** identificador de chamada ao modelo (`AiRequestIds` vazio, que o Conversations já aceita). **Nada muda no Conversations.** Valem, portanto, todas as proteções existentes: turno ativo, época, `StateVersion`, cerca de entrada (`basedOnInboundSeq`) e idempotência por `TurnId`; resposta defasada é recusada (o turno é descartado e o sucessor cobre as pendentes, **sem** chamar o modelo na recusa); humano que assumiu, turno substituído e forma recusada seguem os mesmos caminhos. O turno termina `Completed` com `end_reason = faq-hit` (constante `AiTurnEndReasons.FaqHit`) e a execução devolve o novo `TurnRunOutcome.FaqReply`. **A resposta direta fica no histórico da conversa:** numa pergunta seguinte que vá ao modelo, ele a vê como a resposta que o cliente recebeu (o contexto continua inteiro, e só o da própria sessão).

**Reinício do Worker:** se o processo cai antes de o Conversations responder, o turno é reexecutado e o registro repete com o **mesmo** `TurnId` (uma única resposta); se cai depois da resposta aceita, o snapshot mostra que não há nada pendente e o turno é descartado sem responder de novo e sem chamar o modelo. Neste segundo caso o turno termina `nothing-pending`, não `faq-hit`: a **contagem de acertos pode subestimar** em uma janela de queda muito curta (nunca superestima).

### 5. Isolamento e segurança

O texto da resposta vem da definição **publicada do tenant do turno** (lida pelo `IAgentDefinitionReader` sob o contexto de sistema daquele tenant) e a mensagem vem do snapshot da **sessão autorizada**: **não há cache global, estado compartilhado nem consulta fora do tenant**. Testados: o mesmo texto em três tenants com respostas diferentes (um deles sem `AutoReply`, que vai ao modelo), o contexto de um tenant não executando o turno de outro, e o teste de aceitação Mauro e Pedro com o FAQ direto ligado (a requisição de Mauro ao modelo tem o contexto do exame A, **inclusive a resposta direta que ele recebeu**, e nunca o do B; cada resposta vai só ao atendimento de quem perguntou). A função **não** vê preço, agenda, cobertura nem orientação clínica: só o que o tenant escreveu e aprovou, entrada por entrada.

### 6. Provisionamento (`provision`, ADR 0024)

`ai.faq[].aliases`, `ai.faq[].autoReply` e `ai.faqLocale` no arquivo da empresa (erro de digitação continua recusado, não ignorado). **`TenantProvisioner.Same`** passou a comparar também os aliases, o `AutoReply` e o idioma (`pt-BR` explícito é o mesmo que ausente): sem isso, reprovisionar com um alias novo ou com `autoReply` ligado seria "sem mudança" e a definição publicada continuaria a antiga. A definição antiga, sem os campos novos, continua igual a um arquivo sem os campos novos (não republica sozinha). O passo "Agente de IA" informa quantas entradas têm resposta direta. `tools/ops/empresa-exemplo.json` e `docs/operations/NOVA-EMPRESA.md` descrevem os campos.

### 7. Medição no diagnóstico operacional (`status`), separando o MEDIDO do ESTIMADO

Nova seção **FAQ direto** (`FaqSavingsReport`, no host, a raiz de composição, para que **nenhum módulo leia as tabelas do outro**: só os `DbContext` públicos de cada módulo, somente leitura, sem texto de conversa). Janela fixa de 7 dias. Por empresa:

- **Medido e exato:** os turnos `faq-hit` (cada um é **uma chamada ao modelo que não foi feita**), os turnos que o modelo respondeu (`replied`) e a taxa de acertos entre os dois.
- **Estimado e rotulado "ESTIMATIVA (não medida)":** tokens e custo evitados = acertos × a **média por chamada confirmada** do mesmo tenant na janela (ledger do AiGateway; o custo vem da tabela de preços configurada, **não é fatura**), com a base à vista ("N chamada(s) na janela"). Sem chamada confirmada na janela não há média e o texto diz que não há base: **nenhum número é inventado**. Sem custo na tabela de preços, só os tokens. A pergunta do FAQ pode ser mais curta ou mais longa que a média, por isso é estimativa.
- A seção é **informativa** (nunca atenção nem falha, o que não derruba o resto do relatório); com `--tenant` a empresa aparece mesmo sem acertos; sem `--tenant`, só as com acerto. Cada empresa vê só os próprios números.

## O que mudou

| Onde | Mudança |
|---|---|
| `Agents.Core/Domain` | `FaqEntry` (aliases, `AutoReply`, igualdade por valor), `AgentDefinitionContent.FaqLocale`/`Validate`, `AgentDefinitionVersion.FaqLocale`, `FaqMatcher`, `AiTurnEndReasons`, `AgentsOptions.FaqDirectAnswerEnabled` |
| `Agents.Core/Application` | `TurnRunner` (resposta direta antes do `AiGateway`) e `TurnRunOutcome.FaqReply` |
| `Agents.Infrastructure` | coluna `faq_locale` e a migração `AgentsFaqLocale` |
| host (`Provisioning`, `Operations`) | `FaqSpec`/`AiSpec`, `TenantProvisioner.Same`, `FaqSavingsReport` e a seção no `status` |
| Conversations, ContactCenter, Channels, AiGateway | **nada** |
| Documentação | este ADR, `NOVA-EMPRESA.md`, `OPERACAO-E-SUPERVISAO.md`, `PRONTIDAO-MVP.md`, o arquivo de exemplo |

## Verificação

- **Gate completo** (`dotnet test backend/IaBimeta.slnx`, 09/11/2026): **3971 testes aprovados, 0 falhas** (2497 de unidade e arquitetura + 1474 de integração com PostgreSQL real); frontend **251** aprovados (sem mudança); verificação de formato dos arquivos sem falha. **+129 testes** sobre os 3842 anteriores: 99 de unidade (`FaqMatcherTests` 41, `AgentDefinitionFaqTests` 25, `TurnRunnerFaqTests` 33) e 30 de integração (`FaqDiretoTests` 16, `FaqDiretoNaOperacaoTests` 6, `EconomiaDoFaqRelatorioTests` 8).
- **O que os testes provam:** o `FaqMatcher` (normalização, acerto por pergunta e por alias, **recusa** de erro de digitação, palavra a mais ou a menos, ordem trocada, duas perguntas, números diferentes, texto enorme, malformado ou só de símbolos, e as quatro formas de ambiguidade); a definição (limites dos aliases, idioma, **JSON antigo lido com `AutoReply` desligado**, definição antiga ainda válida e republicável, aliases fora do prompt); o `TurnRunner` (zero chamadas ao `IAiGateway` em cada acerto; cada condição que recusa; resposta defasada recusada sem chamar o modelo; humano que assume; forma recusada; queda do Worker antes e depois do registro; mesmo `TurnId` nas duas tentativas; Mauro e Pedro; três tenants com a mesma pergunta); PostgreSQL real (nenhuma requisição ao provedor **e nenhuma linha no ledger** no acerto; uma só no que vai ao modelo; FAQ antigo gravado no banco; lease vencido; três executores ao mesmo tempo; seis atendimentos em paralelo; o tenant A nunca executa nem responde o turno do B); o `provision` (arquivo, validação, reprovisionamento só quando algo mudou, definição antiga igual a um arquivo sem campos novos); e o `status` (medido x estimado, sem média não há número, cada empresa só os próprios).
- **Demonstração ponta a ponta** (`FaqDiretoNaOperacaoTests`, Api e Worker reais, Meta e OpenAI simuladas por HTTP, PostgreSQL de teste), na ordem em que aconteceu:

  ```text
  [cliente no WhatsApp] QUAL O HORARIO DE ATENDIMENTO???
  [resposta ao cliente] De segunda a sexta, das 7h às 17h.
  [servidor OpenAI simulado] requisicoes recebidas ate aqui: 0
  [cliente no WhatsApp] Preciso levar documento para a coleta?
  [resposta ao cliente] Resposta gerada pela IA simulada.
  [servidor OpenAI simulado] requisicoes recebidas ate aqui: 1
  [cliente no WhatsApp] que horas vocês abrem          (alias)
  [resposta ao cliente] De segunda a sexta, das 7h às 17h.
  [servidor OpenAI simulado] requisicoes recebidas no total: 1
  ```

  e o `status --tenant` da empresa, rodado depois contra o banco de teste (somente leitura):

  ```text
  FAQ DIRETO
    [OK]  (empresa faq-531fb30366) nos últimos 7 d: 2 turno(s) respondido(s) direto pelo FAQ, SEM chamar o modelo (chamadas evitadas; contagem exata); o modelo respondeu 1 turno(s); 66,7% dos turnos respondidos pela IA saíram do FAQ.
    [OK]  (empresa faq-531fb30366) ESTIMATIVA (não medida): ~2.600 tokens e ~0,0032 USD (custo estimado pela tabela de preços configurada, não é fatura) que as 2 chamada(s) evitada(s) teriam consumido = acertos × a média de 1.300 tokens por chamada confirmada deste tenant (1 chamada(s) na janela). ...
  ```

  Neste ensaio: **2 de 3 perguntas sem chamar a OpenAI** (66,7%), uma só requisição ao provedor. A estimativa usa a única chamada confirmada (1.200 de entrada e 100 de saída, com os preços de teste); em operação ela depende do volume real. **Não** é fatura.
- **Banco de teste:** backup (`tools\ops\Backup-Banco.ps1 -Origem Teste`) antes da migração; a migração só adiciona a coluna anulável `faq_locale`; nenhum dado foi alterado nem apagado; `iabimeta_dev` não foi tocado.

## Limites e riscos aceitos

- **O conteúdo é do tenant.** A resposta direta repete **palavra por palavra** o que o tenant escreveu. Uma resposta escrita para orientar o modelo pode não servir ao cliente tal qual; por isso o `AutoReply` é opt-in por entrada. Resposta com **preço, agenda, cobertura ou orientação clínica** não deve ter `AutoReply` (o dado transacional vem do sistema de origem); o código **não** reconhece esse tipo de conteúdo. Do mesmo modo, uma pergunta que só faz sentido com o contexto da conversa ("preciso de jejum para esse exame?") não deve ser cadastrada com `AutoReply`: a igualdade a responderia sem o contexto. Se o tenant a cadastrou, a escolha é dele; o padrão desligado e a documentação existem para isso.
- **Igualdade exata, de propósito.** "Qual o horário de atendimeto?" (erro de digitação), uma palavra a mais ou a menos e duas perguntas na mesma mensagem **vão ao modelo**. A economia depende de o tenant cadastrar os aliases que seus clientes realmente usam; descobrir quais são (lacunas e sugestões) é o **Pacote B**, não implementado.
- **Atualização do conteúdo.** A definição é versionada: mudar a resposta é publicar uma versão nova (a antiga deixa de valer na próxima mensagem). Não há prazo de validade por entrada.
- **Normalização.** Só acentos, caixa, pontuação e espaços; **não** há equivalência de formas Unicode de compatibilidade (largura total, ligaduras) nem de escritas diferentes. Texto ofuscado não corresponde: cai no modelo.
- **Idioma.** Igualdade exata da tag (`pt-PT` ≠ `pt-BR`). Quando o idioma da conversa é só uma **suposição** do padrão do tenant e coincide com o do FAQ, a resposta direta vale: ela exige que o texto do cliente seja **igual** à pergunta cadastrada, o que um idioma diferente quase nunca será.
- **Medida e estimativa** (seção 7): a contagem de acertos pode subestimar numa queda rara entre o registro e a conclusão; a estimativa de tokens e custo é uma média e depende de o tenant ter chamadas confirmadas na janela; a janela é fixa em 7 dias.
- **A função vale para todos os tenants** (no MVP não há configuração por tenant); desligar é pela configuração do Worker.

## Plano de reversão

1. **Sem implantar nada:** `Agents:Options:FaqDirectAnswerEnabled = false` no Worker (todo turno volta ao modelo; o `FaqEntry` e as definições ficam como estão).
2. **Por tenant ou por entrada:** republicar a definição com `autoReply` desligado (`provision` com o arquivo ajustado) ou sem o alias problemático.
3. **Código:** reverter os commits desta decisão. A migração é reversível (`Down` remove `faq_locale`); a definição que ficou sem `faq_locale` volta ao padrão `pt-BR`, e o JSON com campos a mais é ignorado por um código antigo (campos desconhecidos).

## Pendências e decisões abertas

- Ratificar este ADR.
- **Pacote B** (registro de lacunas com aprovação humana) e o módulo Knowledge, quando a economia medida justificar.
- Janela e rótulos do relatório de economia: calibrar com dados reais de uma operação.
- Revisão do conteúdo cadastrado com `AutoReply` antes da operação real: a lista de respostas diretas de cada empresa é parte da homologação.
