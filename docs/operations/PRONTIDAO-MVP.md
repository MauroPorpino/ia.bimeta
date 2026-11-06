# Prontidão do MVP para homologação (verificada em 06/11/2026)

Verificação objetiva do fluxo **WhatsApp → Channels → Conversations → Agents → ContactCenter → resposta ao cliente**, o que falta para uma empresa homologar o MVP e o que só depende de infraestrutura ou credenciais externas. Tudo abaixo foi exercitado por teste automatizado (Api e Worker reais, PostgreSQL real, Meta e OpenAI simuladas) ou por execução local com os simuladores. Nenhuma chamada real à Meta ou à OpenAI.

## 1. O fluxo, salto a salto

| Salto | O que está provado | Onde |
|---|---|---|
| Cliente → Receiver (webhook da Meta) | assinatura HMAC (recusa ausente, errada, de outro corpo), desafio de verificação, repetição gravada uma vez, número desconhecido descartado, corpo grande recusado, só o webhook alcança o ingresso público | `FluxoWhatsAppTests`, `PublicIngressGuardTests` |
| Receiver → Inbox → Conversations | gravação durável antes do 200, entrega ordenada e idempotente, sessão e histórico por conversa | `FluxoWhatsAppTests`, `Messaging.Tests`, `Conversations.Tests` |
| Conversations → Agents → IA | resposta da IA, instruções e perguntas frequentes da empresa, decisão de transferir; falha transitória (repete), permanente, cota esgotada, tenant sem IA ou sem agente (transfere ao atendente), saída inválida do modelo | `Agents*Tests`, `AgentsOpenAiEndToEndTests` |
| IA → Sender → WhatsApp | envio pelo adaptador real, status entregue/lido que nunca regride, janela de 24 h fechada (falha visível), resultado ambíguo (`SendUnknown`, nunca reenviado), limite de taxa, vazão por conta, pausa e retomada segura | `FluxoWhatsAppTests`, `PausaEVazaoWhatsAppTests`, `ConfiabilidadeDoEnvioTests` |
| Transferência → ContactCenter | conversa entra na fila, corrida por assumir com um único vencedor, responder (uma vez só, mesmo repetindo), devolver à fila, **devolver à IA**, encerrar, histórico em páginas, quem não é o responsável é recusado | `OperacaoDoContactCenterTests`, `FluxoWhatsAppTests` |
| Resposta ao cliente | cada resposta sai só para o cliente da conversa certa; falha de envio vira sinal na fila; conta pausada vira aviso para o atendente | `OperacaoDoContactCenterTests`, `DeliveryFailureSignalTests` |
| Cliente volta | depois de devolvida à IA, ou depois de encerrada, a conversa seguinte é atendida de novo (nada fica sem resposta) | `OperacaoDoContactCenterTests` |
| Empresa nova de ponta a ponta | provisionada por arquivo e comando, webhook assinado, IA, transferência, atendente, resposta; **sem enxergar nem ser vista pela outra empresa** | `ProvisionamentoDeEmpresaTests` |
| Interface do atendente | fila automática, conversa, assumir/responder/devolver/encerrar, dois atendentes, histórico paginado, rascunhos persistentes, queda de conexão, aviso de conta pausada | 182 testes do frontend |

**Defeito encontrado e corrigido nesta verificação:** assumir uma conversa no instante em que ela ainda estava entrando na fila era recusado (`OperationInProgress`) e a interface dizia, errado, que "outro atendente assumiu". Agora o botão fica desabilitado durante a entrada e o texto explica o que acontece.

## 2. Pronto para homologação

- Receber e responder mensagens de texto pelo WhatsApp (simulado e, com credenciais, real), com IA e atendimento humano.
- Fila, atendentes, transferência IA ⇄ humano, histórico completo, rascunhos, avisos de falha de envio e de conta pausada.
- **Colocar uma empresa nova em funcionamento por arquivo e comando** (`docs/operations/NOVA-EMPRESA.md`, ADR 0024): tenant, administrador, fila, agente (instruções e FAQ), IA (modelo e chave BYOK cifrada), conta de WhatsApp e atendentes. Idempotente.
- Multi-tenant isolado (dados, filas, conversas, credenciais cifradas por tenant).
- Segredos fora de arquivo, log e resposta; credenciais de WhatsApp e chave da IA cifradas.
- Diagnóstico do envio, teste de prontidão do WhatsApp (só leitura), pausa de conta, túnel HTTPS de desenvolvimento e roteiro de homologação com a Meta (`HOMOLOGACAO-META.md`).
- Os simuladores (OpenAI e Meta) continuam servindo para demonstrar tudo sem serviço externo.

## 3. O que ainda falta, por prioridade

**P0 (impede a homologação):** nenhum item de código pendente. Os dois impedimentos reais encontrados (não havia como criar uma empresa fora do perfil de desenvolvimento; a tela enganava o atendente ao assumir durante a entrada na fila) foram corrigidos e testados.

**P1 (necessário antes de clientes reais ou de uso contínuo):**

1. **Trocar e redefinir senha.** Não há rota: o atendente fica com a senha que o operador definiu ou gerou, e esquecer a senha exige o operador.
2. **Mensagens de modelo (template) do WhatsApp.** Fora da janela de 24 h o atendente não consegue falar com o cliente (a tentativa falha de forma visível, código `131047`). Depende também de modelos aprovados na Meta.
3. **Mídia.** Imagem, áudio e documento chegam como aviso ("mídia recebida, arquivo indisponível no MVP") e a conversa vai ao atendente; não há envio de mídia nem transcrição de áudio.
4. **Retenção de dados e LGPD** (decisão do negócio, depois implementação) e **backup e restauração** do banco de homologação (só existe o script do banco de desenvolvimento).
5. **Operação contínua:** o Api e o Worker precisam de supervisão (reinício automático, logs persistentes). Hoje sobem à mão.

**P2 (melhorias, depois do primeiro ciclo de homologação):** API e painel administrativos (agente, usuários, filas, horários da fila no arquivo da empresa, suspender empresa); alertas ativos e métricas exportadas; notificações; tempo real (SSE/WebSocket); um único script que suba tudo; Knowledge Engine e integrações com LabNet/BImeta; outros canais.

## 4. Depende exclusivamente de infraestrutura ou credenciais externas

| Item | Quem fornece |
|---|---|
| Conta WhatsApp Business (WABA), número, app da Meta, token de acesso permanente, App Secret, verificação do negócio | você, no painel da Meta |
| URL pública HTTPS para o webhook (hoje: túnel de desenvolvimento, sem garantia) e o cadastro do callback na Meta | hospedagem, domínio e certificado |
| Créditos e chave do provedor de IA (OpenAI; o adaptador da Anthropic não está implementado, por decisão) | você |
| Servidor de homologação (IP fixo, Api e Worker como serviço), PostgreSQL com backup, cofre de segredos para as chaves mestras | infraestrutura |
| Modelos de mensagem aprovados pela Meta (P1.2) | você, na Meta |

## 5. Testes realizados

- **.NET: 3490 testes aprovados, 0 falhas, 0 ignorados** (2160 de unidade e arquitetura + 1330 de integração com PostgreSQL real, Api e Worker reais). Inclui os 7 testes novos de `ProvisionamentoDeEmpresaTests` (criação e repetição, fluxo completo com isolamento, falha e retomada, conta real, validação do arquivo, exemplo entregue, uso do comando), os 4 de `OperacaoDoContactCenterTests` (dois atendentes, cliente que volta, histórico longo, conta pausada) e o teste do Receiver.
- **Frontend: 182 testes aprovados** (Vitest, Testing Library) e build do Vite sem erro.
- **Execução real do comando** contra o banco de teste, com os simuladores: 1ª execução cria tudo (empresa, administrador, fila, agente, IA, conta simulada, 2 atendentes, uma senha gerada mostrada uma vez); 2ª execução só devolve `JA EXISTE`; o cliente recebe a resposta da IA, o aviso de transferência e a resposta da atendente; o atendente de outra empresa vê 0 conversas dela.
- Verificações do repositório: formato dos arquivos, grafo de projetos (11 regras), testes de arquitetura (77) e gate de saída de etapa.
- Nenhuma chamada real à Meta ou à OpenAI; `main` intocada; sem push.

## 6. Colocar uma empresa de teste em funcionamento

Resumo (detalhes em `docs/operations/NOVA-EMPRESA.md`):

1. Uma vez: PostgreSQL no ar, `tools\dev\Set-ChaveMestraDosCanais.ps1`, `tools\dev\Set-ConfiguracaoDeIa.ps1 -ModeloId <modelo> ...`.
2. Copiar `tools\ops\empresa-exemplo.json`, ajustar (tenant, administrador, fila, IA, WhatsApp, atendentes).
3. `.\tools\ops\Novo-Tenant.ps1 -Arquivo .\minha-empresa.json -SoValidar` e depois `... -PedirSegredos`.
4. `.\tools\dev\Start-DemoAtendimento.ps1`, abrir `http://localhost:5173` e entrar com o `slug` e o atendente.
5. `.\tools\dev\Send-MensagemWhatsApp.ps1 -Tenant <slug> -Texto '...' -Nome ... -Numero ...` faz o papel do cliente.
