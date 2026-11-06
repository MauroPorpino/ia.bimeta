# ADR 0025 — Segurança de acesso, retenção de dados e backup para operar com empresas reais

- **Status:** **Proposto para ratificação.** Implementado em 06/11/2026 sob a autorização do decisor ("segurança de acesso e preparação operacional"), depois da ratificação do ADR 0024. Nenhuma chamada real à Meta ou à OpenAI; nenhuma infraestrutura externa.
- **Data:** 06/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0011 (DP-06, retenção inicial), 0012 (numeração nunca reemitida), 0018, 0020, 0024, `docs/operations/SENHAS-E-SESSOES.md`, `RETENCAO-DE-DADOS.md`, `BACKUP-E-RESTAURACAO.md`
- **Preserva:** o sistema de autenticação existente (sessões opacas revogáveis, PBKDF2, bloqueio por tentativas), o `RequestContext` só criado pelo host, o isolamento por tenant, a regra de que segredo não entra em log, arquivo, trilha nem commit, e a regra de que dado pessoal e registro de referência não se apagam sem decisão jurídica.

## Contexto

O MVP funcional estava pronto para homologação simulada, mas faltavam requisitos mínimos para operar com empresas reais: o usuário não trocava nem recuperava a própria senha, não havia política de retenção para dados pessoais e payloads, e só existia backup do banco de desenvolvimento, sem prova de que o backup restaura.

## Decisão

### 1. Troca e redefinição de senha, sem segundo sistema de autenticação

1. **`PUT /auth/me/password`** (autenticado): exige a senha **atual**. Erro de senha atual conta no **mesmo bloqueio do login** (mesma chave, mesmo limite, `429` com `Retry-After`), para uma sessão roubada não permitir adivinhar a senha. A política de senha (12 a 128) é conferida antes e não conta como tentativa. Ao trocar, **as outras sessões do usuário são revogadas** e a atual continua; tokens de redefinição pendentes são invalidados.
2. **Redefinição por token de uso único.** O token é opaco (`ibr_` + 256 bits aleatórios em base64url); **só o SHA-256 vai ao banco** (`identity.password_reset_tokens`); existe em claro apenas na resposta da emissão. Vale 30 minutos (`Identity:PasswordResetLifetime`). Emitir outro token **revoga** o anterior ainda não usado. O uso é **atômico**: `UPDATE` condicional (não usado, não revogado, não vencido), com um único vencedor sob concorrência (teste com 8 usos simultâneos).
3. **Sem serviço de e-mail, emissão controlada** de duas formas: **o administrador** (`POST /organizations/{id}/users/{userId}/password-reset`, permissão `UserManage`; quem administra só a organização **não** age sobre quem administra o tenant inteiro; usuário fora da organização responde `404`) e **o operador** (comando do host `reset-password` / `tools/ops/Redefinir-Senha.ps1`, contexto de sistema, sem rota HTTP). O token sai uma vez (resposta ou terminal) e **nunca** em log, trilha ou arquivo; a trilha registra só a emissão (quem, para quem) e o uso.
4. **Uso anônimo sem enumeração:** `POST /auth/password-reset/redeem` (tenant, e-mail, token, nova senha) tem o limite de taxa do login e **a mesma resposta (`400`, mesma mensagem)** para tenant inexistente, e-mail inexistente, token errado, vencido, usado ou revogado, com o mesmo custo de consulta. O bloqueio por tentativas é **por e-mail**, existindo ou não (chave `reset:<e-mail>`, separada da do login: quem tenta adivinhar o token não bloqueia o login do dono). A política de senha é conferida **antes** de consumir o token (senha fraca não queima o token). Ao redefinir, **todas as sessões** do usuário caem.
5. **Revogação de sessões:** `POST /organizations/{id}/users/{userId}/sessions/revoke` (administrador) revoga todas as sessões ativas de um usuário (aparelho perdido, suspeita de vazamento), com as mesmas regras de escopo. O logout e a validação de sessão não mudaram.

### 2. Retenção de dados

1. **Política por ambiente e por empresa** na seção `Retention` (padrões da DP-06: eventos processados 7 dias, marcas do Inbox 30 dias; sessões 30, tokens de redefinição 30, contadores de login 7). `Retention:Tenants:<slug>` sobrescreve só as categorias citadas. Valor inválido, ou marca do Inbox que expiraria antes do evento processado, **falha na partida**; empresa citada que não existe **falha fechada** (nunca se aplica prazo suposto).
2. **O que é expurgado (só dado técnico):** sessões vencidas ou revogadas, tokens de redefinição encerrados, contadores de login parados, eventos **processados** do outbox (é aqui que expira o payload do evento do canal, que carrega o texto do cliente) e marcas de idempotência do Inbox. **Pendentes, em andamento e mortos nunca são expurgados** por esta rotina (DP-06: mortos até reenfileiramento ou descarte autorizado).
3. **Bloqueio do Inbox por morto antigo:** reenfileirar um morto depois de a marca de idempotência sumir poderia repetir o efeito dele. O expurgo das marcas é **bloqueado** enquanto houver evento morto mais antigo que o corte da passada, e o relatório diz qual.
4. **O que NÃO é expurgado:** mensagens, traduções, contatos, conversas, atendimentos, histórico do ContactCenter, usuários, trilha de auditoria e ledger de IA. Dependem de decisão jurídica e das regras de referência (numeração de sessões nunca reemitida, ADR 0012; auditoria; pseudonimização da correlação do ledger). Aparecem **só no relatório**. Um teste obriga **classificar toda tabela** do banco (técnica, pessoal, referência, configuração, controle) e garante que só as técnicas têm tarefa de expurgo.
5. **Procedimento seguro:** comando do host `retention report` (somente leitura, dry-run) e `retention purge --confirm` (sem a confirmação, recusa), sem rota HTTP. O relay do Worker já expurgava os processados por um prazo único; agora usa a mesma política (prazo por empresa).
6. **O payload bruto do webhook não é guardado:** o Receiver grava só o evento normalizado no outbox do Channels (ADR 0018, item sobre retenção do payload bruto). Se um dia o bruto for guardado, ganha categoria e prazo próprios aqui.

### 3. Backup e restauração

Scripts em `tools/ops/` para qualquer origem (desenvolvimento, teste ou variável de ambiente da homologação): `Backup-Banco.ps1` (`pg_dump -Fc` + `manifesto.json` com SHA-256, tamanho, entradas e contagem exata por tabela), `Verificar-Backup.ps1` (integridade; recusa dump corrompido ou truncado), `Restaurar-Backup.ps1` (**sempre em banco novo**; **recusa** destino existente; sem opção de sobrescrever; compara contagens e restrições) e `Ensaiar-Restauracao.ps1` (backup + restauração em uma **instância PostgreSQL descartável** e isolada, ou, com `-Modo Servidor`, em banco temporário; só apaga o que criou). "Restaurar no lugar" é uma decisão humana: restaurar em banco novo, conferir e só então trocar a conexão ou renomear.

## O que fica fora (pendências)

- **Decisão jurídica** do prazo e da forma (apagar, anonimizar, pseudonimizar) para mensagens, contatos, atendimentos, trilha e ledger; a implementação do expurgo pessoal vem **depois** dela (com tratamento da numeração de sessões, de referências cruzadas e do histórico do ContactCenter).
- Descarte autorizado de eventos mortos (com trilha) e consulta de mortos pela administração.
- E-mail para entregar o token de redefinição; tela de emissão do token e de revogação de sessões para o administrador (a interface do atendente já troca a própria senha e usa o token); autenticação em dois fatores.
- Expurgo agendado (o `retention purge` roda sob demanda; o relay só expurga eventos processados); backup agendado, retenção e cópia externa dos backups, e criptografia do backup (infraestrutura).
- Auditoria por empresa do próprio expurgo (hoje o relatório do comando é o registro).

## Consequências

- Migração aditiva `AddPasswordResetTokens` (tabela nova, sem alterar dados). Contratos novos só no Identity (rotas e serviço) e no SharedKernel (tipos da política de retenção e contratos de tarefa, sem aresta nova na matriz). O Messaging ganha as tarefas genéricas de outbox e Inbox e o relay passa a aceitar a política (parâmetro opcional).
- Testes: 183 de unidade do Identity (20 novos de senha), 19 de integração de senha e sessões (fluxo, expiração, enumeração, bloqueio, tenant cruzado, corrida de 8 usos, segredos fora de logs, trilha e banco, comando do operador, escopo de administradores), 12 de integração de retenção (mutações confirmadas) e 11 unitários da política. Ensaio real de restauração do banco de teste (88,5 MB, 54 tabelas) com contagens idênticas.
- Defeito de teste investigado e explicado (não era do produto): a contagem de chamadas ao modelo ao devolver a conversa à IA competia com a substituição de turno prevista no ADR 0007 (T6); o teste agora espera a fila de turnos assentar e mantém a asserção estrita.
