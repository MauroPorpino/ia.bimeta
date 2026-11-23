# Plano curto de homologação operacional (após o encerramento técnico da E2)

**Estado:** PLANO, **não executado** (20/11/2026). Nenhum deploy, nenhuma conexão de cliente real e nenhum envio real sem autorização expressa do proprietário. Base: commit de consolidação da E2 em `feature/v1-embedded`; documentos que este plano reutiliza (não duplica): `IMPLANTACAO-LINUX.md`, `HOMOLOGACAO-META.md`, `HOMOLOGACAO-V1.md` (Fase 8), `BACKUP-E-RESTAURACAO.md`, `OPERACAO-E-SUPERVISAO.md`, `PILOTO-PRIMEIRA-EMPRESA.md`, `RETENCAO-DE-DADOS.md`, `EMBEDDED-BACKLOG.md` §3 (pendências obrigatórias antes da produção).

**Objetivo:** colocar o ia.bimeta em operação controlada, comprovar o funcionamento e validar o potencial comercial com UM cliente piloto. Não é fase de novas funcionalidades.

## Etapas (em ordem; cada uma só começa com a anterior aprovada)

| # | Etapa | O que fazer | Evidência de aceite | Depende de |
|---|---|---|---|---|
| H1 | **Ambiente separado do desenvolvimento** | VM própria (Ubuntu 24.04), Compose, Caddy com HTTPS, PostgreSQL próprio, ClamAV real, variáveis e segredos fora do repositório (`deploy/`, `IMPLANTACAO-LINUX.md`); mesma versão (commit) da E2; nenhum dado do PC de desenvolvimento | `aceitacao.sh` verde; `status` sem aviso crítico; Api e Worker no ar | VM, DNS, NAT/porta pública (proprietário) |
| H2 | **Migrations controladas** | Backup verificado antes; aplicar o comando `migrate` em banco NOVO e em cópia de banco com dados; conferir as três migrations da E2 (`ContactLinksAndConsents`, `OptOutReviews`, `MessageOptOutCommandMarker`); ensaiar restauração em ambiente isolado (`Ensaiar-Restauracao.ps1`/equivalente); **não executar `Down` dos consentimentos** (BL-12) | Migrations aplicadas e listadas; restauração ensaiada com sucesso; plano de recuperação escrito com responsável e tempo | H1 |
| H3 | **Autenticação Standalone e Embedded** | Login Standalone por papéis; conexão de integração, chave HMAC, ticket, troca e sessão embutida a partir de um sistema externo de teste em outra origem; logout que revoga; origem não autorizada bloqueada; Edge no PC e Safari/Chrome em HTTPS real | Roteiro do §3 da homologação V1 cumprido; 401 x 503 distinguíveis no registro | H1 |
| H4 | **WhatsApp real** | Número de teste e conta de modelos aprovados; parâmetros da Meta (vazão, códigos de erro, assinatura, janela de 24 h, nono dígito) conferidos na documentação oficial; teste de prontidão; webhook público; **primeiro envio real só com autorização expressa**; modelos com a instrução "responda PARAR" | `Test-ProntidaoWhatsApp.ps1 -Live` ok; mensagem recebida e resposta enviada ao próprio número do proprietário | H1, credenciais Meta, autorização |
| H5 | **Atendimento por IA e transferência humana** | IA do tenant (BYOK) com chave real em ambiente de teste e orçamento limitado; conversa com FAQ, pergunta fora do conhecimento, pedido de atendente; transferência para a fila; resposta humana; devolução à IA; mídia recebida | Fluxo completo sem resposta duplicada ou perdida; handoff com histórico e resumo; custo registrado | H4, saldo na conta do provedor de IA |
| H6 | **Histórico, permissões e consentimento** | Papéis (atendente, gestor, administrador) e escopos (organização, unidade, fila); `CustomerOnly` e `FullAttendant`; vínculo cliente-contato; registro e consulta de consentimento (`ConsentManage` só administrador e gestor); PARAR/SAIR/CANCELAR e frases de saída reais; resolução de `PossibleOptOut`; supressão da resposta automática | Matriz de permissões conferida na interface; revogação visível e sem resposta automática; pendências no `status` | H3, H5 |
| H7 | **Auditoria e observabilidade** | Auditoria entregue pelo Worker (derrubar o processo e conferir recuperação); `status` e sondas (inclusive `consentimento`); logs estruturados sem dado pessoal; alertas mínimos (Worker parado, evento parado, auditoria pendente, disco, backup); backup agendado cifrado com cópia externa | Falha induzida detectada e recuperada; nenhum telefone ou segredo em log | H1 |
| H8 | **Custos por atendimento** | Medir por conversa: chamadas e tokens de IA (ledger do AiGateway), mensagens e modelos da Meta (categoria/janela), armazenamento e tempo humano; consolidar por atendimento e por empresa durante o piloto | Planilha/relatório com custo médio por atendimento e por resolução pela IA; economia do FAQ direto separada | H5 |
| H9 | **Decisões não técnicas** | Avaliação jurídica/LGPD do consentimento e da retenção (BL-13), termos, política de privacidade e contrato com o piloto; propriedade dos dados; plano de comunicação de incidente | Parecer registrado; texto de consentimento aprovado | Jurídico, proprietário |

## Critérios mínimos para liberar um cliente piloto (todos obrigatórios)
1. H1 a H8 concluídas com evidência e **sem P0/P1 aberto**; falhas P2 conhecidas aceitas por escrito.
2. Backup restaurado com sucesso em ambiente isolado dentro do prazo combinado, e procedimento de recuperação com dono.
3. Número de WhatsApp real conectado, com ao menos uma conversa ponta a ponta (IA, transferência humana e consentimento/revogação) sem duplicidade ou perda.
4. Nenhum segredo no repositório; chaves de IA e da Meta em cofre/variáveis; usuários e senhas do piloto criados pelo proprietário.
5. H9 cumprida: parecer jurídico/LGPD e política de retenção ao menos provisória aprovada; termos com o cliente.
6. Custo por atendimento medido e dentro do limite que o proprietário definir; limite de gasto da IA configurado.
7. Operação definida: quem acompanha o `status` diariamente, quem decide as `PossibleOptOut` (meta provisória de 24 h corridas) e como o cliente aciona suporte.
8. Reversão: plano de desligar a IA e o canal (pausa de conta) sem perder mensagens.
9. Pendências P2 antes da produção tratadas ou aceitas: BL-12 (procedimento do `Down`), BL-13 (retenção); BL-10 só entra antes de qualquer início ativo (E3).

## Fora do escopo desta homologação
Início ativo de conversas e campanhas (E3), cartão de consentimento na interface (E4), novos canais, melhorias do Knowledge Engine, novas funcionalidades. Qualquer achado novo entra no backlog com prioridade, sem virar escopo.
