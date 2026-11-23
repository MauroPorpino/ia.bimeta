# Pendências e decisões para a implantação e a homologação do ia.bimeta

**Documento D de quatro** (A inventário · B manual · C checklist · D este documento). Reúne o que ainda falta saber, decidir ou desenvolver, **separado por responsável**. Criticidade: **Alta** = impede começar a instalação ou a etapa indicada; **Média** = tratar antes de clientes reais ou do uso de Embedded; **Baixa** = melhoria. "Antes da produção" = não bloqueia a homologação técnica, mas bloqueia colocar clientes reais em produção.

## 1. Decisões e providências do PROPRIETÁRIO

| Id | Decisão / informação | Criticidade | Necessária antes de |
|---|---|---|---|
| P1 | **Domínio** de homologação (`<DOMINIO>`) e quem administra o DNS | Alta | H1 |
| P2 | **IP público fixo** e responsável pelo NAT/firewall de borda (80/443 de entrada); confirmação de que o provedor de internet não bloqueia 80/443 | Alta | H1 |
| P3 | **Destino da cópia externa dos backups** (outro servidor ou disco/compartilhamento que não seja a VM) e, opcionalmente, o webhook de chat para alertas | Alta | H2 |
| P4 | **Cofre/gerenciador de senhas** onde as cópias de `.env`, `app.env` e `backup.env` serão guardadas, e quem pode acessá-lo | Alta | H1 (logo após gerar os segredos) |
| P5 | **Pacote de código aprovado**: autorizar a geração e a entrega do pacote (hash do commit) ao especialista | Alta | H1 |
| P6 | **Conta Meta** (aplicativo, WABA, número de **teste**, usuário do sistema, token permanente, App Secret), entregues por canal seguro e **autorização expressa para o primeiro envio real** | Alta | H4 |
| P7 | **Provedor de IA**: chave de **teste**, limite de gasto no painel do provedor e orçamento aceito para a homologação | Alta | H5 |
| P8 | **Autorização** para abrir o endereço a terceiros externos e para conectar qualquer cliente real | Alta | piloto |
| P9 | **Parecer jurídico/LGPD**: consentimento, revogação e **política de retenção** (hoje: sem prazo e sem expurgo; "ausência de prazo não autoriza retenção ilimitada") | Alta antes da produção | produção |
| P10 | Termos, política de privacidade, contrato e plano de comunicação de incidente com o cliente piloto | Alta antes do piloto | piloto |
| P11 | **Limites de custo**: custo máximo por atendimento aceitável e teto de gasto mensal da IA | Média | H8 |
| P12 | Quem acompanha o `status` diariamente e quem decide as "possíveis saídas" de clientes (alerta provisório de 24 h corridas, que **não** equivale a um dia útil) | Média | piloto |
| P13 | Se haverá **sistema externo (Embedded)** já na homologação e qual (origem do iframe, quem opera o servidor dele) | Média | H3 |
| P14 | Janela de manutenção e política de atualização da VM (quem autoriza reinícios) | Média | H1 |
| P15 | **DECIDIDO pelo proprietário: a IA local (Ollama) NÃO entra na primeira homologação** (`Knowledge__Semantic__Enabled=false`, sem o perfil `ia-local`). Reavaliar depois | Encerrada | n/a |

## 2. Decisões e providências do ESPECIALISTA DE INFRAESTRUTURA

| Id | Item | Criticidade | Quando |
|---|---|---|---|
| I1 | Confirmar o **dimensionamento** (4 vCPU, 8 GB, 100 GB SSD) com o virtualizador; **snapshot** antes de atualizações | Alta | H1 |
| I2 | Instalar e endurecer o Ubuntu (SSH por chave, usuários nominais, `ufw` via `instalar-host.sh`); decidir sobre `fail2ban`, MFA e `unattended-upgrades` (**não vêm no pacote**) | Média | H1 |
| I3 | Executar o **teste de aceitação** e entregar o log integral ao desenvolvimento (é a primeira prova real do Compose/Dockerfiles/Caddy/systemd/ClamAV) | **Alta** | H1 |
| I4 | **Fixar versões/digests** de imagens após a 1ª validação (`caddy:2`, `node:24-bookworm-slim`, `ollama:latest`) e do Docker Engine | Média | após H1 |
| I5 | Confirmar a **preservação do IP real do cliente** nos logs do `web` (NAT sem mascarar a origem) | Média | H1 |
| I6 | Definir **monitoramento externo** (uptime do HTTPS, alerta de disco/CPU/memória, destino dos alertas) além de `status.sh`/`monitor.sh` | Média | H7 |
| I7 | **Rotação e retenção** de logs do host e política de backup local (14 dias por padrão) e externo | Média | H2 |
| I8 | Testar a **restauração em VM nova** (desastre) e anotar o tempo | Alta | H2 |
| I9 | Decidir o destino definitivo da **produção** (nuvem), com os mesmos artefatos; domínio, rede e contas separados | Média | antes da produção |
| I10 | Medir **desempenho e memória reais** da pilha durante os testes | Média | H8 |
| I11 | Se Ollama for usado em outra máquina: rede privada/VPN (`deploy/ollama/`; TLS entre máquinas ainda pendente de decisão) | Baixa | só se P15 = sim |

## 3. Pendências do DESENVOLVIMENTO

| Id | Item | Criticidade | Observação |
|---|---|---|---|
| D1 | **`Content-Security-Policy`/`frame-ancestors`** no proxy (e/ou aplicação) restringindo o iframe às origens autorizadas por conexão | Média (**decidido: tratar ANTES da homologação Embedded com sistemas externos reais**; não impede o H1) | Lacuna L1; hoje a interface pode ser incorporada por qualquer site |
| D2 | Criar a **empresa de homologação** (JSON do `empresa.sh`) e entregar ao especialista; cadastrar conta WhatsApp, IA e webhook | Alta | Funcional; depende de P6/P7 |
| D3 | Equivalente em shell (ou execução assistida) dos scripts **PowerShell** de conexão do WhatsApp, prontidão e teste do webhook (hoje exigem PC Windows chamando a API por HTTPS) | Média | Lacuna L7 |
| D4 | **Adaptador do provedor Anthropic** (hoje só OpenAI e Ollama) | Baixa | Lacuna L8; só se o piloto exigir |
| D5 | `/health` **profundo** (verificar banco) e algum sinal HTTP para o Worker | Baixa/Média | Lacuna L3; hoje a saúde real vem do comando `status` |
| D6 | Corrigir `docs/operations/IMPLANTACAO-LINUX.md`: cita a branch antiga `feature/mvp-c-ia-minima`; o pacote atual é gerado do commit aprovado da `feature/v1-embedded` | Baixa | Este conjunto de 4 documentos prevalece |
| D7 | **BL-10 (antes de início ativo/E3):** contato de referência opaca que pede PARAR não gera linha de consentimento (a resposta automática é suprimida e o atendente é avisado) | Média | Sem risco enquanto não houver início ativo de conversas |
| D8 | **BL-12 (antes da produção):** `Down` das migrations de consentimento é destrutivo; definir procedimento de preservação | Média | Nunca executar `Down` com dados |
| D9 | **BL-11:** a lista do modo `CustomerOnly` considera as 100 sessões mais recentes do cliente | Baixa/Média | Limitação conhecida |
| D10 | **BL-25:** teste de integração intermitente de infraestrutura de teste (rota de troca de ticket do Embedded); não afeta o produto | Baixa | Estabilizar o teste |
| D11 | Primeira chamada **real** à Meta e ao provedor de IA (nunca foi feita pelo produto) | Alta para o piloto | H4/H5, com P6/P7 |
| D12 | Medir **custo por atendimento** (consolidação do ledger de IA e das mensagens Meta) | Média | H8 |
| D13 | Ajustes que o teste de aceitação revelar (qualquer defeito dos arquivos `deploy/`) | Alta (se ocorrer) | O especialista **não** altera `deploy/`: registra e devolve |

## 4. O que impede iniciar a homologação (resumo)
Para iniciar **H1** faltam: P1 (domínio), P2 (IP fixo e NAT), P4 (cofre), P5 (pacote autorizado) e I1 (VM provisionada). Para iniciar **H2**: P3 (destino do backup externo). Para **H4**: P6 e autorização do primeiro envio. Para **H5**: P7. Nada disso depende de novas funcionalidades.

## 5. Estado de validação do que existe (para não superestimar)
- **Provado:** compilação e publicação do backend; build e testes do frontend; sintaxe dos scripts; testes dos scripts de backup/restauração contra um Docker falso; suíte automatizada do produto (milhares de testes, com PostgreSQL real, no ambiente de desenvolvimento Windows).
- **Não provado em ambiente real:** Docker Compose, Dockerfiles, Caddy com certificado real, systemd, ClamAV real, pgvector/Ollama, restauração com dados reais, desempenho, e qualquer chamada real à Meta ou ao provedor de IA.
- O resultado da homologação dirá o que falta; **não presuma que um componente está pronto só porque aparece no pacote ou no planejamento.**
