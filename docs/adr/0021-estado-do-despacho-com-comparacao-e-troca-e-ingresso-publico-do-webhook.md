# ADR 0021 — Estado do despacho com comparação e troca, e ingresso público do webhook (homologação com a Meta)

- **Status:** **Proposto para ratificação.** Implementado em 06/11/2026 sob a autorização do decisor, que pediu a correção pontual de concorrência apontada no ADR 0020 e a preparação prática para a homologação com a Meta. Nenhuma chamada real à Meta ou à OpenAI.
- **Data:** 06/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0011 (E, EFX-3), 0019 e 0020; `docs/operations/HOMOLOGACAO-META.md`
- **Preserva:** a matriz `module-dependencies.json`, a semântica de `SendUnknown` (nunca reenviar sem certeza de que a Meta não recebeu) e o relay (nenhuma mudança nele).

## Decisão

1. **O estado final do despacho nunca é sobrescrito por quem perdeu a vez.** `channels.outbound_messages` passa a gravar com **comparação e troca**: o `state` e as `attempts` lidos são parte da condição do `UPDATE` (`IsConcurrencyToken`, sem coluna nova e sem migração). Quem grava depois de outro processo já ter gravado (um handler cujo lease venceu e continua rodando, ou o tratamento de morto de outro relay) recebe `OutboundStateConflictException`, a gravação é **descartada**, nada é notificado e o handler devolve `Retry` (que o relay ignora se o lease já é de outro). O estado de quem gravou primeiro vale: um `SendUnknown` gravado por um Worker não vira `Sent` nem `Failed` por uma resposta tardia da Meta. O que resolve o incerto é a **evidência do provedor** (o status do webhook, que o Conversations aceita de `SendUnknown` para `Sent`/`Delivered`), não uma gravação tardia. Métrica `channels.outbound.state_conflict`. Fecha a pendência "zumbi com lease vencido" do ADR 0020.
2. **O ingresso público é só o webhook.** Um túnel entrega as requisições da internet ao Api como se viessem do loopback, e o Api de desenvolvimento tem `X-Dev-Tenant` e `/simulator` ligados. `Channels:Webhook:PublicListenerPort` (5156 no perfil `homologacao-meta`) define uma porta que serve **somente** `/webhooks/whatsapp/...` (o resto responde 404), e a requisição com rastro de túnel (`Cf-Ray`, `Cf-Connecting-Ip`, `X-Forwarded-For`) só alcança o webhook em **qualquer** porta (`PublicIngressGuard`, primeiro middleware). O webhook segue protegido pela assinatura do corpo.
3. **Teto do corpo do webhook: 4 MiB** (era 1 MiB). A Meta documenta payloads de **até 3 MB**; um 413 faria a Meta reenviar o mesmo lote por até 7 dias.
4. **Tooling de homologação (sem infraestrutura nova):** `Start-TunelPublico.ps1` (Cloudflare quick tunnel: sem conta, binário único, URL nova a cada execução), `Test-WebhookPublico.ps1` (prova HTTPS, verify token e assinatura chamando só a própria URL pública, sem a Meta), `Backup-BancoDeDesenvolvimento.ps1`, perfil `homologacao-meta` e a unidade "Matriz" criada pelo `Conectar-ContaWhatsApp.ps1` quando faltar.

## O que fica fora

- URL estável de produção (domínio próprio com TLS) e túnel nomeado; app da Meta em modo Live e revisão de requisitos.
- O reenvio de mensagens que falharam e o alerta ativo (ADR 0020).

## Consequências

- Nenhuma migração. `OutboundMessageConfiguration` ganhou dois tokens de concorrência; `IOutboundMessageStore.SaveAsync` traduz o conflito.
- Testes: comparação e troca no banco (oito processos em corrida, ordens inversas, gravações em sequência do mesmo handler); dois Workers simultâneos, com o antigo segurando a chamada e recebendo "aceito" tarde; o guarda do ingresso (porta pública, rastro de túnel, sem configuração) e, no Kestrel real, as duas portas. Ambos os testes de concorrência falham sem os tokens (verificado).
