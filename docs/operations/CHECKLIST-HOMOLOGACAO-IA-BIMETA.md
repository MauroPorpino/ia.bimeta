# Checklist de implantação e homologação do ia.bimeta

**Documento C de quatro** (A inventário · B manual · C este checklist · D pendências e decisões). Use-o como registro: marque cada item com data, responsável e **evidência** (saída do comando, captura sem dados pessoais, link do log). Itens marcados **[AUT]** exigem **autorização expressa do proprietário antes de executar**. Itens **[DEV]** são da equipe de desenvolvimento (não do especialista de infraestrutura). Regra: **uma etapa só começa quando a anterior foi aprovada**; falha numa etapa = parar, registrar e avisar, sem contornar.

**Escopo permitido na homologação:** dados sintéticos e contas de teste. **Proibido sem autorização [AUT]:** conectar clientes reais, enviar mensagem real para terceiros, usar chave de IA com orçamento sem limite, abrir o serviço publicamente além do necessário ao teste.

## Etapa H1 — Implantação na VM dedicada (adaptada ao ambiente de homologação)

Responsável: infraestrutura. Referência: Manual B §§0–13.

| # | Item | Critério objetivo de aprovação | Evidência |
|---|---|---|---|
| H1.1 | VM dedicada criada (Ubuntu 24.04, 4 vCPU, 8 GB, 100 GB) | `lsb_release -a` = 24.04; `nproc` ≥ 4; `free -h` ≥ 8 GB; `df -h` ≥ 100 GB | saídas |
| H1.2 | Ubuntu atualizado; NTP ativo | `timedatectl` mostra `System clock synchronized: yes` | saída |
| H1.3 | Acesso SSH só por chave, `root` sem SSH, usuário nominal com `sudo` | `sshd -T \| grep -E "passwordauthentication\|permitrootlogin"` = `no`/`no`; segundo acesso testado | saída |
| H1.4 | DNS A do `<DOMINIO>` → IP público; NAT/portas 80 e 443 | `dig +short <DOMINIO>` (de fora) = IP; `curl -I http://<DOMINIO>` alcança a VM | saídas |
| H1.5 | Pacote verificado e descompactado em `/opt/iabimeta` | `sha256sum -c` = OK; hash do commit conferido com o informado pelo desenvolvimento | saída |
| H1.6 | `instalar-host.sh` executado | `docker --version`, `docker compose version`; `ufw status verbose` com 22 só da rede de administração, 80/443 públicas | saídas |
| H1.7 | Segredos gerados e **copiados para o cofre fora da VM** | `ls -l deploy/{.env,app.env,backup.env}` = `-rw-------` root; cofre confirmado pelo responsável | `ls`; confirmação por escrito |
| H1.8 | `preflight.sh` sem ERRO | saída 0 | saída |
| H1.9 | `subir.sh` conclui; pilha saudável | `docker compose ps` com `db`, `clamav`, `api` *healthy*, `worker` e `web` *running* | saída |
| H1.10 | HTTPS (certificado de homologação do Let's Encrypt) | `curl -I https://<DOMINIO>` responde (certificado não confiável esperado); `curl https://<DOMINIO>/api/health` = `{"status":"ok"}` | saídas |
| H1.11 | **Teste de aceitação** (VM de teste) | `aceitacao.sh --confirmo-que-e-teste` com **todos os itens `PASSOU`** | log integral do script |
| H1.12 | Só 80 e 443 expostas | `nmap -Pn -p 22,80,443,3310,5432,8080 <IP_PUBLICO>` de fora: só 80/443 abertas | saída |
| H1.13 | IP real do cliente nos logs do `web` | `logs web \| grep remote_ip` mostra o IP público de teste | trecho |
| H1.14 | Reinício da VM | `sudo reboot`; depois `status.sh` com saída 0 sem intervenção | saída |
| H1.15 | Troca para o certificado **real** do Let's Encrypt | navegador sem alerta; renovação sem erro nos logs do `web` | captura |

**Aprovação H1:** todos os itens acima. Autorização do proprietário: **não** necessária para H1, **exceto** abrir o endereço a terceiros externos **[AUT]**.

## Etapa H2 — Migrations controladas, backup e recuperação

Responsável: infraestrutura (execução) + desenvolvimento (conferência das migrations).

| # | Item | Critério objetivo de aprovação | Evidência |
|---|---|---|---|
| H2.1 | Backup manual completo | `backup.sh` sai 0; arquivo `.tar.gpg` + `.sha256` em `BACKUP_DIR` | saída; `ls -l` |
| H2.2 | **Cópia externa** conferida | arquivo no `BACKUP_EXTERNAL_DEST`; `sha256` confere | saída |
| H2.3 | Verificação completa do backup | `verificar-backup.sh <arquivo>` sai 0; um backup adulterado de teste é **reprovado** (já coberto pelo `aceitacao.sh`) | saída |
| H2.4 | Ensaio de restauração | `ensaiar-restauracao.sh` sai 0; `attachments check` sem divergência; **tempo anotado** | saída; tempo |
| H2.5 | Migrations em ordem | `cli.sh migrate --list` sem pendência; as três migrations da E2 de conversas presentes: `ContactLinksAndConsents`, `OptOutReviews`, `MessageOptOutCommandMarker` **[DEV conferir]** | saída |
| H2.6 | Aplicação controlada de migration **com dados** (simulada: restaurar o backup em banco de ensaio e rodar `migrate`) | sem erro; `status` sem aviso crítico | saída |
| H2.7 | Backup e monitor agendados | `agendar.sh`; `systemctl list-timers \| grep iabimeta` mostra os dois | saída |
| H2.8 | Procedimento de recuperação escrito e com responsável e tempo-alvo | documento aprovado pelo proprietário **[AUT]** | documento |
| H2.9 | **Proibido:** executar `Down` de migration ou truncar tabelas de consentimento | confirmação escrita | — |

**Aprovação H2:** H2.1–H2.8 cumpridos; restauração real comprovada com tempo registrado.

## Etapa H3 — Autenticação Standalone e Embedded

Responsável: desenvolvimento (cadastro funcional) com apoio da infraestrutura (rede, relógio, logs).

| # | Item | Critério objetivo de aprovação |
|---|---|---|
| H3.1 | Empresa de homologação criada (`empresa.sh` validado e aplicado) **[DEV]** | login do administrador funciona; senha errada = 401 |
| H3.2 | Papéis (administrador, gestor, atendente) | cada um vê apenas o que seu papel permite |
| H3.3 | Troca e redefinição de senha | funcionam; sessões antigas revogadas |
| H3.4 | Limite de login por origem | rajada de tentativas resulta em 429 **por origem** (e não para todos) |
| H3.5 | Embedded: conexão, chave HMAC, ticket, troca e sessão embutida a partir de sistema externo de teste **em outra origem** **[DEV]** | sessão embutida criada; logout revoga; origem não autorizada bloqueada |
| H3.6 | Chamada assinada com relógio fora da janela, chave errada, replay | sempre 401 genérico; falha de infraestrutura = 503 (não 401) |
| H3.7 | Lacuna L1 (CSP/frame-ancestors) tratada **[DEV]** antes de iframe com sistema externo real **[AUT]** | cabeçalho presente e testado |
| H3.8 | Navegadores | Edge, Chrome e Safari em HTTPS real |

## Etapa H4 — WhatsApp real

Responsável: desenvolvimento + proprietário (conta Meta). Infraestrutura: URL pública, certificado, saída 443, logs.

| # | Item | Critério objetivo de aprovação |
|---|---|---|
| H4.1 | App Meta, WABA, número de **teste**, usuário do sistema, token permanente, App Secret **[AUT: o proprietário cria e entrega por canal seguro]** | todos disponíveis; nenhum em arquivo do servidor |
| H4.2 | Parâmetros da Meta conferidos na documentação oficial (vazão, códigos de erro, janela de 24 h, nono dígito/`wa_id`, preços) **[DEV]** | registro das conferências |
| H4.3 | Conta cadastrada **sem enviar nada** (credenciais cifradas) **[DEV]** | sem envio; auditoria registrada |
| H4.4 | Webhook provado **sem a Meta** (teste de webhook assinado) **[DEV]** | assinatura válida = 200; inválida = 401; id desconhecido = 404 |
| H4.5 | Webhook cadastrado na Meta (URL, verify token, campo `messages`) | verificação da Meta aceita |
| H4.6 | Teste de prontidão (leitura) | token e Phone Number ID provados |
| H4.7 | **Primeiro envio real** **[AUT obrigatória]**, só para o número do próprio proprietário | mensagem recebida e resposta enviada; sem duplicidade |
| H4.8 | Modelos de mensagem aprovados; instrução "responda PARAR" nos modelos de campanha | modelo enviado só dentro das regras |
| H4.9 | Mídia recebida e enviada (ClamAV real em ação) | arquivo limpo disponível; amostra de teste bloqueada |

## Etapa H5 — Atendimento por IA e transferência humana

| # | Item | Critério objetivo de aprovação |
|---|---|---|
| H5.1 | IA da empresa configurada com chave **de teste e limite de gasto** no painel do provedor **[AUT: chave e orçamento]** | chave cifrada no banco; limites do `AiGateway` conferidos |
| H5.2 | Conversa: FAQ, pergunta fora do conhecimento, pedido de atendente | respostas coerentes; sem inventar preço/horário; handoff quando previsto |
| H5.3 | Transferência para a fila humana, resposta humana, devolução à IA | histórico e resumo preservados; sem resposta duplicada ou perdida |
| H5.4 | Falha do provedor de IA simulada (chave inválida/limite) | atendimento humano continua; erro visível; sem laço de tentativas |
| H5.5 | Conhecimento híbrido (Ollama) **desligado** | `Knowledge__Semantic__Enabled=false` mantido, salvo decisão **[AUT]** |

## Etapa H6 — Histórico, permissões e consentimento

| # | Item | Critério objetivo de aprovação |
|---|---|---|
| H6.1 | Matriz de permissões por papel e escopo (organização, unidade, fila) | acesso fora do escopo = 404; sem permissão = 403 |
| H6.2 | Multiempresa: duas empresas de teste | nenhuma enxerga dados da outra (conversas, contatos, consentimento) |
| H6.3 | `CustomerOnly` e `FullAttendant` (Embedded) | `CustomerOnly` só lê o cliente autorizado; nunca escreve |
| H6.4 | Vínculo cliente-contato e histórico por cliente | só o cliente autorizado na sessão; outro = 404 |
| H6.5 | Consentimento: registro e consulta (somente administrador/gestor) | atendente comum e sessão embutida = 403; histórico imutável |
| H6.6 | Revogação por PARAR/SAIR/CANCELAR e frases de saída (ex.: "pare de me ligar") | PARAR revoga todas as finalidades; SAIR/CANCELAR em conversa em curso e frases = **possível saída** (não revoga); **nenhuma resposta automática** depois do comando confirmado; mensagem continua visível |
| H6.7 | Resolução de "possível saída" (`Revoked` ou `Kept`) por gestor | decisão única e auditada |
| H6.8 | Contato com referência opaca (sem telefone) que manda PARAR **[DEV]** | resposta automática suprimida; sem linha de consentimento (limitação conhecida BL-10); atendente avisado |
| H6.9 | Nenhum telefone, mensagem ou evidência em log/auditoria | verificado por amostragem nos logs e na trilha |

## Etapa H7 — Auditoria e observabilidade

| # | Item | Critério objetivo de aprovação |
|---|---|---|
| H7.1 | Auditoria entregue pelo Worker | após operações de teste, a trilha recebe os eventos; pendências em zero |
| H7.2 | **Falha induzida do Worker** | `docker kill` do worker: reiniciado sozinho; trilha recupera sem perda nem duplicata |
| H7.3 | Falha induzida do banco e do Api | voltam sozinhos; `status.sh` volta a 0 |
| H7.4 | ClamAV fora do ar | arquivos ficam pendentes e voltam ao normal; `attachments report` acusa |
| H7.5 | Alertas mínimos operando | `monitor.sh` agendado; um alerta de teste chega em `alertas.log` e no webhook |
| H7.6 | Disco, backup atrasado e Worker parado geram alerta | simulados ou comprovados por leitura do monitor |
| H7.7 | Sonda `consentimento` no `status` | mostra pendências e idade; aviso acima de 24 h corridas |
| H7.8 | Logs sem segredo | `aceitacao.sh` (item de segredos) e amostragem manual |

## Etapa H8 — Custos por atendimento

| # | Item | Critério objetivo de aprovação |
|---|---|---|
| H8.1 | Consumo de IA por conversa (ledger do `AiGateway`) | relatório por atendimento e por empresa |
| H8.2 | Mensagens e modelos da Meta (categorias/janela) | contagem por conversa e custo do painel Meta |
| H8.3 | Recursos da VM durante o teste (CPU, memória, disco, tempo de restauração) | medições registradas (resolve a lacuna L9 do Documento A) |
| H8.4 | Custo médio por atendimento e por resolução pela IA | consolidado em planilha; limite de gasto aceito pelo proprietário **[AUT]** |

## Etapa H9 — Decisões não técnicas
| # | Item | Responsável |
|---|---|---|
| H9.1 | Parecer jurídico/LGPD do consentimento e da retenção (hoje: sem prazo e sem expurgo) | proprietário + jurídico **[AUT]** |
| H9.2 | Termos, política de privacidade e contrato com o cliente piloto | proprietário |
| H9.3 | Plano de comunicação de incidente e contato de suporte | proprietário + infraestrutura |

## Segurança (varredura transversal — marcar ao final de H1 e repetir antes do piloto)
- [ ] Nenhuma porta além de 80/443 pública (e 22 só da administração).
- [ ] Banco e ClamAV sem porta publicada; papel `iabimeta` sem superusuário (`\du` no `psql` do contêiner `db`).
- [ ] HTTPS obrigatório e certificado real; HSTS presente.
- [ ] Arquivos `.env`, `app.env`, `backup.env` com 600 e **cópias no cofre fora da VM** confirmadas.
- [ ] Nenhum segredo no repositório, no pacote, nos logs, em chats ou tickets.
- [ ] Acesso administrativo só por chave, usuários nominais, `fail2ban`/MFA **se adotados** (não vêm no pacote).
- [ ] Atualizações de segurança do Ubuntu aplicadas; política definida (Manual B §22).
- [ ] Backups cifrados com cópia externa e ensaio de restauração concluído.
- [ ] Separação homologação × produção respeitada (outra VM, domínio, banco, chaves, contas).
- [ ] Consentimento/revogação e auditoria: tabelas de consentimento **sem** rotina de apagamento; Worker no ar e trilha em dia.

## Critérios mínimos para liberar um cliente piloto (todos obrigatórios; decisão final do proprietário **[AUT]**)
1. H1–H8 aprovadas, com evidência, **sem falha P0/P1 aberta**; falhas P2 aceitas por escrito.
2. Backup restaurado com sucesso em ambiente isolado e procedimento de recuperação com dono.
3. Ao menos uma conversa real de ponta a ponta (IA, transferência humana, consentimento/revogação) sem duplicidade ou perda.
4. Nenhum segredo fora do cofre; usuários e senhas do piloto criados pelo proprietário.
5. H9 cumprida: parecer jurídico/LGPD e política de retenção ao menos provisória aprovada; termos assinados.
6. Custo por atendimento medido e limite de gasto da IA configurado.
7. Operação definida: quem acompanha o `status` todos os dias, quem decide as "possíveis saídas" (meta provisória de 24 h corridas) e como o cliente aciona suporte.
8. Plano de reversão: pausar a IA e o canal sem perder mensagens (pausa de conta da aplicação) e restaurar a partir do backup.
9. Pendências "antes da produção" do Documento D tratadas ou aceitas por escrito.

## Registro de execução
| Etapa | Início | Fim | Responsável | Resultado (aprovada / reprovada) | Observações e evidências |
|---|---|---|---|---|---|
| H1 | | | | | |
| H2 | | | | | |
| H3 | | | | | |
| H4 | | | | | |
| H5 | | | | | |
| H6 | | | | | |
| H7 | | | | | |
| H8 | | | | | |
| H9 | | | | | |
