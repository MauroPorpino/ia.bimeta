# Homologação local do Knowledge Engine pela interface (ADR 0030)

Procedimento para repetir, na sua máquina e **sem nenhuma chamada paga**, o ciclo inteiro das lacunas do FAQ **pela interface React**, com **Api, Worker e PostgreSQL reais** e a Meta e a OpenAI **simuladas**: pergunta desconhecida, lacuna, resposta do atendente, revisão pelo administrador, rascunho, publicação explícita e a próxima pergunta respondida direto, com **zero novas chamadas ao provedor**. Foi executado em 09/11/2026 (resultado na seção 6). O guia de uso está em `CONHECIMENTO-DO-FAQ.md`.

## 1. O ambiente (isolado: não toca o banco de desenvolvimento nem o de teste)

Use um **PostgreSQL descartável** (o mesmo binário do projeto, outra pasta de dados e outra porta), para o ambiente começar limpo e nada de outras execuções ser processado pelo Worker:

```powershell
$pg = 'C:\Projetos\ferramentas\postgresql-17.11\bin'; $dados = "$env:TEMP\iabimeta-demo-pg"
& "$pg\initdb.exe" -D $dados -U postgres -A trust -E UTF8 --no-locale
& "$pg\pg_ctl.exe" start -D $dados -o '"-p 5441 -c listen_addresses=127.0.0.1"' -w -l "$dados\..\iabimeta-demo-pg.log"
& "$pg\createdb.exe" -h 127.0.0.1 -p 5441 -U postgres iabimeta_demo
```

(Num terminal comum `pg_ctl start -w` basta; onde o processo filho herda o cano de saída e pendura o terminal, suba-o por `Start-Process -PassThru` + `WaitForExit`, como o `Invoke-PgCtl` de `tools\ops\_Comum.ps1`.) Ao terminar: `pg_ctl stop -D $dados -m fast -w` e apague a pasta.

**Variáveis de ambiente** (valem para o Api, o Worker e o `provision`; **nenhum valor vai ao repositório**; gere cada chave com 32 bytes aleatórios em Base64, como em `CONHECIMENTO-DO-FAQ.md`):

| Variável | Valor |
|---|---|
| `ConnectionStrings__Default` e `ConnectionStrings__Tenancy` | `Host=127.0.0.1;Port=5441;Database=iabimeta_demo;Username=postgres` |
| `Channels__Secrets__ActiveKeyId` / `Channels__Secrets__Keys__demo1` | `demo1` / uma chave gerada (cifra as credenciais da conta de WhatsApp) |
| `AiGateway__Secrets__ActiveKeyId` / `AiGateway__Secrets__Keys__demo1` | `demo1` / outra chave gerada (cifra a chave BYOK do tenant) |
| `AiGateway__Providers__OpenAi__BaseAddress` | `http://127.0.0.1:5199/v1/` (a OpenAI simulada) |
| `Channels__Providers__WhatsApp__BaseAddress` | `http://127.0.0.1:5198/` (a Meta simulada) |
| **`Agents__Options__KnowledgeGapHashKey`** | uma terceira chave gerada: **só o Worker** a recebe (o Api não precisa dela) |
| `Logging__LogLevel__Default` | `Warning` (sem SQL no log) |

Para o `provision`, também as variáveis de pessoas citadas no arquivo da empresa (`DEMO_ADM_SENHA`, `DEMO_ATD_SENHA`, `DEMO_IA_CHAVE`; a chave da "OpenAI" pode ser qualquer texto).

## 2. A empresa de demonstração

Um arquivo `empresa-demo.json` (fora do repositório) como o de `tools\ops\empresa-exemplo.json`, com **modo `simulated`**, o modelo do catálogo (`gpt-6-luna`), um FAQ de **uma** entrada com `autoReply` (o horário) e **uma atendente** (a atendente é a prova de permissão: ela não vê a área). Depois, **uma vez**:

```powershell
dotnet run --project backend\host\IaBimeta.Api --no-launch-profile -- provision --file empresa-demo.json
```

Ele aplica as migrations no banco novo e imprime o **caminho do webhook** (guarde-o).

## 3. Subir os cinco processos (um terminal cada)

```powershell
.\tools\dev\Start-OpenAiSimulada.ps1                      # 5199: transfere se o cliente pede atendente ou fala de preco; senao responde
.\tools\dev\Start-WhatsAppSimulado.ps1                    # 5198: imprime o que o cliente receberia no celular
$env:ASPNETCORE_URLS = 'http://localhost:5155'; dotnet run --project backend\host\IaBimeta.Api --no-launch-profile
dotnet run --project backend\host\IaBimeta.Worker --no-launch-profile   # no log de partida: "Registro de lacunas do FAQ deste Worker: LIGADO"
cd frontend; npm run dev                                  # 5173 (Node: C:\Projetos\ferramentas\node-v24.22.1-win-x64)
```

A linha **LIGADO** do Worker é a prova de que a chave do hash está configurada (sem ela o registro fica desligado, sem erro).

## 4. O roteiro

O cliente é o `Send-MensagemParaEmpresa.ps1` (faz o papel da Meta, com o webhook assinado). Use **texto sem acento** pela linha de comando (a normalização do FAQ ignora acento).

1. **Atendente** em uma aba (`http://localhost:5173`: tenant, `ana@...`) e **administrador** em outra (a sessão fica no `sessionStorage` de cada aba). A atendente **não** vê "Conhecimento"; o administrador vê **Atendimento | Conhecimento**.
2. **Pergunta desconhecida que a OpenAI simulada transfere** (precisa conter, por exemplo, "atendente"):
   `.\tools\dev\Send-MensagemParaEmpresa.ps1 -Slug <slug> -WebhookPath <caminho> -Texto 'Preciso de pedido medico para fazer exame de sangue? Pode me passar para um atendente?' -Nome 'Maria Cliente' -Numero 5511999990501`
   O cliente recebe o aviso de transferência (janela da Meta simulada): **1 requisição** à OpenAI simulada. No banco, `agents.knowledge_gaps` ganha **uma linha** (só `question_hash` e `kind = 1`, sem texto).
3. **A atendente assume e responde** pela interface.
4. **O administrador abre Conhecimento**: a lacuna aparece como "Sem correspondência no FAQ", 1 ocorrência. Ao abri-la: a **pergunta original** (lida do Conversations) e, em "Resposta de um atendente", a **sugestão** com o aviso "é só uma sugestão, não é usada automaticamente". O campo **Resposta está vazio**; "Usar esta resposta" a copia; a caixa **autoReply está desmarcada**.
5. **O administrador edita**: tira "pode me passar para um atendente" da pergunta, escreve dois aliases, ajusta a resposta e **liga o autoReply de propósito**. **Enviar ao rascunho**: "Entrada enviada ao rascunho (versão 2). **Nada foi publicado.**" O painel Rascunho mostra a entrada como "nova" e "resposta direta"; a versão publicada continua a 1.
6. **Ainda sem publicar**, um segundo cliente faz a pergunta já editada: a OpenAI simulada recebe **mais uma** requisição (o rascunho não vale).
7. **Publicar rascunho** → **Cancelar** (nada muda no banco) → **Publicar rascunho** → **Confirmar publicação**: "Versão 2 publicada".
8. **Dois clientes novos** perguntam a pergunta (com outra caixa e pontuação) e um alias: ambos recebem **a resposta revisada** e a contagem de requisições à OpenAI simulada **não sobe**.
9. `status --tenant <slug>` (seção **FAQ DIRETO**): `2 turno(s) respondido(s) direto pelo FAQ, SEM chamar o modelo` e a estimativa rotulada.

## 5. O que conferir além do ciclo

- **Permissões** (`Invoke-WebRequest` com o token de cada usuário): sem token **401**; atendente **403** em toda rota `/agents/...` (lista, detalhe, rascunho, publicada, publicar); administrador **200** (`/agents/definition/draft` dá **404** sem rascunho).
- **Estados de erro na interface:** enviar com a resposta vazia ("Preencha a pergunta e a resposta da entrada."); enviar uma pergunta que já existe no FAQ ("O FAQ já tem uma entrada com essa pergunta ou alias: ajuste a existente em vez de criar outra.", sem perder o que foi digitado); **Descartar** com confirmação; **parar o Api**: faixa de reconexão, "API indisponível ..." nos painéis e "Tentar novamente", que recupera quando o Api volta.
- **Navegação:** alternar Atendimento e Conhecimento mantém o estado; recarregar mantém a sessão; os filtros Abertas, Enviadas ao rascunho, Descartadas e Todas.
- **Nenhum dado pessoal inserido sozinho no FAQ:** o que vai ao FAQ é só o que o administrador digita e envia. Confira no banco que nem nome, nem telefone, nem o texto original do cliente estão nas lacunas, nas ocorrências ou nas definições:

  ```sql
  WITH t AS (SELECT row_to_json(g)::text j FROM agents.knowledge_gaps g
             UNION ALL SELECT row_to_json(o)::text FROM agents.knowledge_gap_occurrences o
             UNION ALL SELECT faq::text || coalesce(instructions, '') FROM agents.agent_definition_versions)
  SELECT count(*) FILTER (WHERE j ~* '(maria|5511999990|passar para um atendente)') AS com_dado_do_cliente FROM t;  -- deve ser 0
  ```

  Atenção: a pergunta **vem pré-preenchida com o texto do cliente** (ponto de partida editável) e **pode conter dado pessoal**; por isso a tela avisa, ao lado do campo, para removê-lo antes de enviar (ver seção 6).

## 6. Resultado da execução de 09/11/2026

Ambiente: PostgreSQL 17.11 descartável (porta 5441), Api e Worker reais (compilados, sem Development), Meta e OpenAI simuladas, Vite em `localhost:5173`, navegador do app. **Nenhuma chamada real à Meta ou à OpenAI.**

| Passo | Resultado |
|---|---|
| Chave do hash | só o Worker; log de partida `Registro de lacunas ... LIGADO (chave do hash configurada: sim ...)`; nada no repositório |
| Pergunta desconhecida | OpenAI simulada transferiu (1 requisição); o cliente recebeu o aviso; **lacuna criada** (`kind = 1`, `status = 1`, 1 ocorrência, só hash) |
| Atendente | assumiu e respondeu pela interface (`enviado`); a atendente **não** viu a área Conhecimento |
| Revisão | pergunta original e sugestão do atendente exibidas; **Resposta vazia** até "Usar esta resposta"; **autoReply desmarcado** |
| Edição e rascunho | pergunta editada, 2 aliases, resposta editada, autoReply ligado: "enviada ao rascunho (versão 2). Nada foi publicado." Banco: v1 publicada (1 entrada) e v2 rascunho (2 entradas) |
| Antes de publicar | 2º cliente: foi à IA simulada (**2 requisições**); resposta "Resposta simulada da IA" |
| Publicação | **Cancelar** não alterou o banco; **Confirmar publicação**: v2 publicada, "Não há rascunho" |
| Depois de publicar | 2 clientes (pergunta em caixa alta com `???` e um alias): ambos receberam a resposta revisada (`reply_kind = Informative`); OpenAI simulada **continuou em 2 requisições** |
| `status` | `2 turno(s) respondido(s) direto pelo FAQ ... 66,7%` e a estimativa rotulada |
| Permissões | 401 sem token; 403 para a atendente em toda rota (inclusive o detalhe de uma lacuna); 200 para o administrador |
| Erros e navegação | todos os da seção 5 se comportaram como descrito |
| Auditoria | `agents.knowledge-gap.source-read`, `agents.knowledge-gap.promoted`, `agents.definition.draft-saved` e `agents.definition.published`, sem conteúdo |
| Dado pessoal | nenhum nome, telefone ou texto original do cliente nas lacunas, ocorrências ou definições |

**Defeito real encontrado e corrigido:** a pergunta vem pré-preenchida com a mensagem do cliente (por desenho: ponto de partida editável) e a tela **não avisava** que ela pode conter nome, telefone ou documento; enviar sem editar levaria o dado ao FAQ. Agora há, junto do campo, o aviso "A pergunta já vem preenchida com o texto do cliente. Antes de enviar, remova nome, telefone, documento ou qualquer outro dado pessoal: o FAQ vale para todos os clientes da empresa." (conferido ao vivo com um cliente que escreveu nome e telefone; teste de componente novo).

## 7. Encerrar

Pare os cinco processos, `pg_ctl stop -D $dados -m fast -w` e apague a pasta de dados e as chaves geradas.
