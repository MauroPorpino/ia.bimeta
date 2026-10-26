# ADR 0004 — Instalação portátil de Node.js 24 e PostgreSQL 17 no ambiente local

- **Status:** Aceito
- **Data:** 26/10/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0001 (itens 2, 3 e 6 e pendências), `CLAUDE.md` (ambiente do desenvolvedor)

## Contexto

O ADR 0001 decidiu Node.js 24 LTS e PostgreSQL 17, instalados "em fase posterior", de forma independente do PostgreSQL 9.5 existente, preferencialmente por pacote zip e sem serviço automático. Esta etapa executa essa decisão.

## Decisão e fatos verificados (26/10/2026)

### Pasta e método

Os dois pacotes ficam **fora do repositório**, em `C:\Projetos\ferramentas\`, extraídos de zip, **sem alterar o PATH**, sem instalador, sem serviço do Windows e sem privilégios de administrador. Os comandos usam o caminho completo.

### Node.js

| Item | Valor |
|---|---|
| Versão | 24.22.1 (npm 11.19.1) |
| Pasta | `C:\Projetos\ferramentas\node-v24.22.1-win-x64` |
| Origem | `https://nodejs.org/dist/v24.22.1/node-v24.22.1-win-x64.zip` (36 MB) |
| Integridade | SHA256 `d2046d28c2c3dcd211ac5145c0bf30e2047ef9dfb306693c8c6e2eccd8b95fa5`, igual ao `SHASUMS256.txt` do nodejs.org; assinatura de `node.exe` válida (OpenJS Foundation) |
| Não verificado | acesso do `npm` à internet (o Avast pode interceptar o HTTPS; ver ADR 0001) |

### PostgreSQL

| Item | Valor |
|---|---|
| Versão | 17.11 (versão atual da série 17 na data) |
| Pasta dos binários | `C:\Projetos\ferramentas\postgresql-17.11` |
| Pasta de dados | `C:\Projetos\ferramentas\pgdata\iabimeta` |
| Origem | zip de binários da EDB (`https://sbp.enterprisedb.com/getfile.jsp?fileid=1260616`, 364 MB), linkado pela página `postgresql.org/download/windows` |
| Integridade | SHA256 `80379b2c04d51c30225532e0ae04509899141e9957ed096fe749d7fd9df8f82f`, **calculado por nós**: a EDB não publica checksum e os executáveis do zip **não são assinados**. Metadados dos executáveis: 17.11, PostgreSQL Global Development Group |
| Autorização | a execução dos binários não assinados foi autorizada expressamente pelo decisor em 26/10/2026 |
| Rede | `listen_addresses = '127.0.0.1'` e `port = 5433`; a porta 5432 continua do PostgreSQL 9.5 |
| Autenticação | `scram-sha-256` em todas as linhas ativas de `pg_hba.conf` |
| Codificação | UTF8, usuário `postgres` |
| Senha | gerada localmente e guardada em `C:\Projetos\ferramentas\pgdata\senha-postgres-dev.txt`, fora do repositório. Nunca copiar para o Git, para logs ou para o chat |
| Serviço | nenhum; o servidor é iniciado e parado manualmente |

### Testes executados

- `select version()` retornou PostgreSQL 17.11 em `x86_64-windows`.
- Três conexões seguidas por `127.0.0.1:5433` funcionaram; senha errada foi recusada.
- Busca textual em português funciona: `to_tsvector('portuguese', 'exames de sangue em jejum')` devolve `'exam':1 'jejum':5 'sang':3`.
- Extensões disponíveis: `citext`, `pg_trgm`, `pgcrypto` e `unaccent`. A extensão `vector` (pgvector) **não** está disponível, conforme o ADR 0001.
- Parada limpa com `pg_ctl stop`; a porta 5433 fecha e a 5432 permanece como estava.

## Como usar

Num terminal comum (PowerShell), com `$pg = 'C:\Projetos\ferramentas\postgresql-17.11\bin'`:

```powershell
& "$pg\pg_ctl.exe" -D 'C:\Projetos\ferramentas\pgdata\iabimeta' -l 'C:\Projetos\ferramentas\pgdata\iabimeta.log' start
& "$pg\psql.exe" -h 127.0.0.1 -p 5433 -U postgres -d postgres
& "$pg\pg_ctl.exe" -D 'C:\Projetos\ferramentas\pgdata\iabimeta' stop -m fast
```

O `psql` que está no PATH é o do PostgreSQL 9.5; use sempre o caminho completo acima e a porta 5433.

## Consequências

- **Suporte:** o PostgreSQL 17 não consta entre as plataformas testadas pela EDB no Windows 10 (ADR 0001); os testes acima não substituem homologação.
- **Cadeia de suprimentos:** como o zip não é assinado nem tem checksum publicado, a confiança vem da origem (página oficial do PostgreSQL apontando para a EDB) e do SHA256 registrado aqui. Usar o mesmo SHA256 para detectar troca do arquivo em reinstalações.
- **Processo servidor e ferramentas que capturam saída:** observado nesta instalação: ao iniciar o servidor por um processo que captura a saída (como um agente de código), o comando só retorna quando o servidor termina, e, uma vez, encerrar o processo que o iniciou foi seguido de queda do processo de conexão com a exceção `0xC0000142`. Iniciar e parar o servidor num terminal próprio deve evitar o problema, mas isso não foi testado.
- **Dados:** somente sintéticos, sem credenciais reais (ADR 0001, item 4). Não há rotina de backup do cluster de desenvolvimento.
- **Espaço:** a pasta `ferramentas` ocupa cerca de 1,1 GB.

## Pendências

- PostgreSQL 9.5: escuta em `0.0.0.0:5432` (todas as interfaces) e está fora de suporte; desativar e remover é pendência do ADR 0001, não executada.
- Collation do cluster não conferida; definir antes das primeiras migrations.
- Tratamento do certificado do Avast para o `npm`, a testar quando o `frontend/` for criado.
- Frontend (`frontend/`, React + TypeScript) ainda não criado.
- Confiança do certificado HTTPS de desenvolvimento do .NET (ADR 0001).

## Revisão

Reavaliar ao migrar o desenvolvimento para Windows 11 ou Linux, ao surgir problema atribuível ao Windows 10 ou ao zip da EDB, ou quando a EDB passar a publicar assinatura ou checksum.
