# Aprender com as perguntas dos clientes: lacunas do FAQ (ADR 0030)

Este guia explica como a empresa descobre **o que os clientes perguntam e o FAQ não responde direto**, revisa isso e transforma em entrada de FAQ **sem treinar modelo algum e sem publicar nada sozinho**. A resposta direta, que usa o FAQ sem chamar a IA, está em `NOVA-EMPRESA.md` ("FAQ com resposta direta", ADR 0029). Quem muda o FAQ é sempre uma pessoa, e quem publica é sempre uma pessoa.

## 1. O que acontece

1. O cliente manda uma mensagem de texto. Se ela é **uma única mensagem de texto, no idioma do FAQ**, o sistema procura no FAQ publicado (igualdade normalizada, como no ADR 0029). Se não houve resposta direta, a pergunta vira uma **lacuna** (ou conta mais uma vez numa que já existe) e o atendimento segue como sempre: a IA responde ou transfere.
2. O que é registrado, e **só isto**: um **hash** da pergunta (com um segredo próprio de cada empresa), a **data**, o **tipo** e as **referências** ao atendimento, à conversa e à mensagem. **O texto do cliente não é copiado.** O texto é lido do Conversations, na hora da revisão, com a autorização de quem revisa.
3. O administrador vê a lista das lacunas, das mais frequentes para as menos, abre uma, lê a pergunta original e, **se um atendente respondeu depois**, vê essa resposta como **sugestão**. Ele edita a pergunta, os aliases e a resposta e **envia ao rascunho** da definição do agente, ou **descarta**.
4. Publicar é um passo à parte, também do administrador (`Publicar rascunho`). Enquanto não publica, **nada muda para os clientes**: o rascunho nunca é usado.

### Os tipos de lacuna

| Tipo | Quando | O que fazer |
|---|---|---|
| **Sem correspondência** | nenhuma entrada do FAQ tem aquela pergunta ou alias | é a oportunidade de uma entrada nova |
| **Ambígua** | duas ou mais entradas correspondem à mesma pergunta | há duplicata no FAQ: corrija a existente (o envio ao rascunho recusa uma pergunta que já existe) |
| **Entrada existente sem resposta direta** | uma única entrada corresponde, mas sem `autoReply` | a empresa pode ligar o `autoReply` nela (hoje pelo arquivo da empresa e `provision`) |

**Não viram lacuna:** a pergunta que o FAQ respondeu direto; várias mensagens de uma vez; mídia (imagem, áudio, documento); conversa em outro idioma que o do FAQ; mensagem só de símbolos ou emoji. Uma saudação ("oi", "obrigado") **vira** lacuna como qualquer outro texto: **descarte** o que não vale uma entrada.

## 2. Ligar o registro (uma vez, por ambiente)

O registro **só roda** se o **Worker** tiver a chave mestra do hash. Sem ela fica **desligado**, sem erro (o atendimento não depende dele); a partida do Worker diz no log se está `LIGADO` ou `desligado`.

```powershell
# Gera uma chave aleatória de 32 bytes (Base64). Guarde-a como segredo do ambiente; NUNCA em arquivo versionado.
$b = New-Object byte[] 32; [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

Coloque o valor em `Agents:Options:KnowledgeGapHashKey` na configuração do **Worker** (variável de ambiente `Agents__Options__KnowledgeGapHashKey` ou user-secrets). Outras chaves, todas em `Agents:Options`:

| Chave | Padrão | O que é |
|---|---|---|
| `KnowledgeGapsEnabled` | `true` | desliga o registro sem mexer na chave (nada é gravado) |
| `KnowledgeGapHashKey` | vazia | a chave mestra (Base64, pelo menos 32 bytes). Inválida ou curta: o host não parte |
| `KnowledgeGapMaxPerTenant` | `5000` | o teto de lacunas (perguntas distintas) por empresa; atingido, perguntas **novas** não abrem lacuna e as que existem continuam contando |

**A chave nunca deve ser trocada à toa:** a chave de cada empresa é derivada dela, e trocar separa os grupos de antes dos de depois (a lacuna antiga continua revisável; a mesma pergunta abre outra lacuna). Ela também vale para o Api só no sentido de que o Api **não** precisa dela (a revisão lê por identificador, nunca recalcula o hash).

## 3. Revisar (administrador da empresa)

Quem revisa é quem tem a permissão de **configurar a empresa** (`TenantSettingsManage`: o administrador de escopo do tenant). O atendente não vê a área. Na interface do atendente, o administrador tem a alternância **Atendimento | Conhecimento**.

- **Lista:** filtro por situação (Abertas por padrão), tipo, quantas vezes apareceu e quando foi a última. Nunca traz texto de cliente.
- **Revisão da lacuna:**
  - A **pergunta original** aparece se a mensagem ainda existe e **você tem permissão de ler a conversa**. Se foi removida, ou você não pode vê-la, aparece o aviso e você **escreve a pergunta**.
  - A **resposta de um atendente**, se houver, aparece como **sugestão**: "Resposta de um atendente: confira antes de usar; não é usada automaticamente". Se o cliente mandou outras mensagens antes dela, há um aviso a mais: ela pode ser de outra pergunta. **Nada é copiado sozinho:** o botão "Usar esta resposta" a leva ao campo, onde você a edita.
  - Edite **pergunta**, **aliases** (um por linha, até 10) e **resposta**. A caixa **autoReply vem desmarcada**: só ligue em resposta que se sustenta sozinha (sem preço, agenda, cobertura, orientação clínica nem referência ao que foi dito antes). **Não copie dados pessoais do cliente** (nome, telefone, exame de uma pessoa) para a resposta do FAQ.
  - **Enviar ao rascunho:** cria o rascunho a partir da versão publicada (ou usa o que já existe) e acrescenta a entrada. Recusa uma pergunta ou alias **igual** a qualquer um já existente no FAQ (duas respostas para a mesma pergunta nunca dariam resposta direta). **Descartar:** a decisão vale para aquela pergunta (as próximas vezes só contam).
- **Rascunho da definição:** mostra o rascunho ao lado da versão publicada (entradas novas, alteradas e com resposta direta) e o botão **Publicar rascunho**, com confirmação. Publicar cria uma versão nova e aposenta a anterior; é auditado.

### Por API (mesmas regras)

| Rota | O que faz |
|---|---|
| `GET /agents/knowledge-gaps?status=Open&take=50&skip=0` | lista (sem texto de cliente) |
| `GET /agents/knowledge-gaps/{id}` | detalhe: ocorrências (referências), a pergunta e a resposta humana (sugestão), com a autorização de quem pede |
| `POST /agents/knowledge-gaps/{id}/dismiss` | descarta (repetir não muda nada) |
| `POST /agents/knowledge-gaps/{id}/promote` | envia ao **rascunho**: `{ question, aliases[], answer, autoReply=false }` |
| `GET /agents/definition/draft`, `GET /agents/definition/published` | as versões (404 se não há) |
| `POST /agents/definition/publish` | publica o rascunho (ação explícita) |

Respostas: `401` sem sessão; `403` sem `TenantSettingsManage`; `404` para lacuna de outra empresa ou inexistente; `400` dado inválido; `409` com `code` (`gap-not-open`, `gap-changed`, `no-definition`, `faq-conflict`, `definition-changed`).

## 4. Privacidade, retenção e auditoria

- **Texto do cliente:** não é copiado para as tabelas das lacunas. O hash é um HMAC-SHA256 com chave derivada **por empresa**: o mesmo texto dá hashes diferentes em empresas diferentes, e sem a chave mestra não se confirma um palpite nem se volta ao texto.
- **Referências:** `agents.knowledge_gap_occurrences` guarda só identificadores (atendimento, conversa, mensagem) e a data. Classificação de retenção: **dado pessoal, somente relatório** (nenhum expurgo automático; `RETENCAO-DE-DADOS.md`). Se a mensagem for removida um dia, a revisão mostra "indisponível" e a lacuna continua utilizável (você escreve a pergunta).
- **Auditoria (sem conteúdo):** `agents.knowledge-gap.source-read` (cada leitura da pergunta), `agents.knowledge-gap.dismissed`, `agents.knowledge-gap.promoted`, e as já existentes `agents.definition.draft-saved` e `agents.definition.published`.
- **Isolamento:** toda consulta é do tenant do contexto da sessão; a lacuna de outra empresa responde 404.

## 5. Supervisão

`status` (`OPERACAO-E-SUPERVISAO.md`), seção **FAQ direto**, mostra por empresa quantas **lacunas abertas** esperam revisão (só a contagem) além das chamadas evitadas pela resposta direta. Lacuna aberta não é falha: é trabalho para o administrador.

## 6. Limites desta versão

- Perguntas parecidas **não se agrupam** (a igualdade é do texto normalizado): "qual o horário" e "qual o horário de vocês" são lacunas diferentes. É o mesmo princípio do ADR 0029 (nada de aproximação que acerta "quase").
- **Não há** rota nem tela para criar a **primeira** definição do agente (ela nasce no `provision`), nem para **remover** uma entrada do rascunho ou **reabrir** uma lacuna descartada: corrija pelo arquivo da empresa e `provision`.
- A sugestão é a **primeira resposta de um atendente depois da pergunta** (olhando poucas mensagens); a associação pode errar, por isso é só sugestão e sempre editável.
- O administrador de **uma única organização** não tem `TenantSettingsManage` e, portanto, não vê a área.
