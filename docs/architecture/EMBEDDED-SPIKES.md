# Spikes da etapa E0 do Embedded (16/11/2026)

Resultado das investigações documentais exigidas pelos ADRs 0039 e 0040. **Nenhum navegador de terceiros, API da Meta ou ERP foi exercitado**; o que depende de execução real está marcado como pendente e entra nos testes das etapas indicadas.

## 1. Número brasileiro e `wa_id` (nono dígito)

- **Confirmado na documentação oficial da Meta** (Cloud API, "Send messages", consultada em 16/11/2026): o `wa_id` da resposta de envio **pode não coincidir** com o número enviado (`input`); com identificadores de usuário por empresa (BSUID) o `wa_id` pode vir omitido.
- **Fonte secundária** (blogs e suporte de fornecedores, sem página oficial da Meta localizada): números brasileiros podem estar registrados no WhatsApp sem o nono dígito, e o `wa_id` devolvido difere do número digitado; a lista de DDDs afetados varia entre as fontes e não deve ser usada como regra.
- **Decisão (ADR 0040, §8):** o `wa_id` devolvido é o identificador canônico; divergência vira alias de contato (`contact_aliases`), sem duplicar contato; o `ExternalRef` é opaco. **Pendente de prova real:** como a entrada (`wa_id` no webhook) se compara ao `input` para os números do piloto. Entra na homologação com número de teste (autorização expressa), nunca antes.

## 2. Iframe, Safari e armazenamento

- **Fonte secundária** (WebKit, bugs e guias; não foi lida a nota oficial de cada versão): no Safari, `localStorage` e IndexedDB de **terceiros** em iframe são particionados por site de topo e efêmeros; cookies de terceiros são bloqueados sem exceção (Storage Access API); CHIPS voltou em versões recentes. A eliminação de storage escrito por script após dias sem visita é referência não oficial.
- **Decisão (ADR 0039, §4 e §10):** token **só em memória**, sem cookie, sem `sessionStorage`; rascunhos continuam em `localStorage` com o fallback existente (o código já trata storage indisponível); nada essencial depende de storage de terceiros.
- **Pendente de prova real:** Safari e Chrome em ERP real com HTTPS. No PC de desenvolvimento só há o Microsoft Edge (testado na E5, com `playwright-core`); Safari entra na homologação.

## 3. Ambiente de testes de navegador

- Edge instalado em `C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`. `playwright-core` (sem baixar navegador) com `channel: 'msedge'` dirige esse Edge. Instalado **só** como devDependency do `frontend` desta branch; não afeta os demais projetos nem o worktree da candidata.
