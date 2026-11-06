# ADR 0024 — Comando `provision`: colocar uma empresa (tenant) em funcionamento

- **Status:** **Proposto para ratificação.** Implementado em 06/11/2026 sob a autorização do decisor ("consolidação do MVP e preparação para homologação"), depois da ratificação dos ADRs 0022 e 0023. Nenhuma chamada real à Meta ou à OpenAI.
- **Data:** 06/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0017 (IA 6a, BYOK), 0018, 0019 (conta de WhatsApp: credenciais, prontidão), `docs/operations/NOVA-EMPRESA.md`
- **Preserva:** a matriz de módulos (o comando é do host, que já compõe todos os módulos), o contexto de sistema só no código confiável do host, a regra de que segredo não entra em arquivo, log, commit nem resposta, e o número de WhatsApp real inativo até a prova de prontidão.

## Contexto

Criar uma empresa só era possível em **desenvolvimento**: o tenant e o administrador vinham de sementes da configuração (`Tenancy:Development:*`), a IA e a fila, de `DevelopmentAiSetup` (um único tenant por vez), a chave da IA, de uma rota do simulador, e o canal de demonstração tinha identificadores fixos. A conta de WhatsApp real tinha script próprio, mas o resto não tinha procedimento repetível, e não existe API para criar tenant nem para configurar IA e agente (o ADR 0017 deixou a API administrativa para depois). Sem isso, uma empresa de homologação dependia de editar arquivos de perfil e de chamar rotas soltas.

## Decisão

1. **Um comando do host, sem rota HTTP.** `dotnet run --project backend/host/IaBimeta.Api --no-launch-profile -- provision --file empresa.json [--no-migrations] [--validate-only]` (e o script `tools/ops/Novo-Tenant.ps1`, que o envolve) monta a **mesma composição** do Api, mas não abre porta nem inicia serviço hospedado: aplica as migrations (a menos que `--no-migrations`), provisiona a empresa e imprime um relatório. Roda na máquina de quem opera, com acesso ao banco, usando os **casos de uso dos próprios módulos** com o contexto de sistema do tenant (código confiável do host, auditado como os demais). Saída: 0 (tudo certo), 1 (alguma etapa falhou) e 2 (uso ou arquivo inválido). Não há superfície de ataque nova: nenhuma rota, nenhuma credencial de plataforma.
2. **Um arquivo JSON declarativo, sem segredos.** Tenant, administrador, organização e unidade, fila (e a regra que a escolhe), agente (instruções, aviso de transferência e até 50 perguntas frequentes), IA do tenant (provedor, modelo e chave), conta de WhatsApp (`simulated` ou `real`) e atendentes. Campo desconhecido é erro (erro de digitação não vira configuração ignorada). Os campos `*Env` guardam o **nome** da variável de ambiente; um nome que não parece nome de variável é recusado (o operador não cola o segredo por engano).
3. **Idempotente e retomável.** Cada etapa verifica o que já existe e informa `CRIADO`, `JA EXISTE`, `ATUALIZ.` ou `FALHOU`. Uma falha interrompe o que depende dela e diz o que corrigir; repetir completa o resto. Trocar instruções, aviso ou perguntas frequentes publica uma **versão nova** do agente (só quando difere da publicada). Senha de pessoa existente e credenciais de uma conta já cadastrada **nunca** são regravadas pelo comando.
4. **Segredos.** Senhas e chaves vêm de variáveis de ambiente. Senha de pessoa e token de verificação do webhook que **não** vieram do ambiente são gerados (24 e 32 caracteres, `RandomNumberGenerator`) e aparecem **uma única vez** na saída do comando; o relatório impresso nunca traz o que veio do ambiente. As credenciais da conta são cifradas pelo Channels e a chave da IA, pelo AiGateway (chaves mestras do ambiente).
5. **WhatsApp simulado e real.** `simulated`: conta com credenciais sintéticas e identificadores derivados do slug (`waba-sim-<slug>`), número ativo, para a Meta simulada local (`Send-MensagemWhatsApp.ps1 -Tenant`); as rotas `GET /simulator/whatsapp/status` e `/simulator/ai/status` passaram a servir qualquer tenant provisionado. `real`: as três credenciais vêm do ambiente, e o número fica **INATIVO** até `Test-ProntidaoWhatsApp.ps1 -Live` e a ativação (ADR 0019).
6. **Os simuladores continuam intactos:** `DevelopmentAiSetup`, as sementes e o canal de demonstração não mudaram; o provisionamento convive com eles (mesmo banco, tenants diferentes).

## O que fica fora (pendências)

- API administrativa (HTTP) e painel para criar empresa, editar agente e ver a IA: seguem para depois do MVP; o comando cobre a homologação.
- Trocar ou redefinir senha, suspender ou remover empresa, horários da fila no arquivo.
- Cofre de segredos e hospedagem de produção (infraestrutura).

## Consequências

- Código novo só no host (`backend/host/IaBimeta.Api/Provisioning/`) e uma linha de dispatch no `Program`. Nenhuma migração, nenhum contrato de módulo novo, nenhuma aresta nova na matriz.
- Testes de integração (`ProvisionamentoDeEmpresaTests`): empresa criada por completo e repetição sem duplicar, fluxo completo de uma empresa provisionada (webhook assinado, IA, transferência, atendente, resposta ao cliente) com isolamento da outra empresa, falha numa etapa e retomada, senha gerada que vale e sai uma vez, conta real inativa sem vazar segredo, validação do arquivo e do exemplo entregue.
