#!/bin/sh
# Roda UMA vez, na primeira inicializacao do volume do PostgreSQL (docker-entrypoint-initdb.d). Cria o papel da aplicacao SEM privilegios de
# superusuario (ele e dono do banco "iabimeta" e nada alem disso) e o banco. O superusuario (postgres) fica so para o backup e a manutencao.
# A senha chega pela variavel APP_DB_PASSWORD e entra no SQL como literal citado pelo psql (:'senha'): nunca por concatenacao.
# Sem "set -u": se o arquivo nao tiver o bit de execucao, o entrypoint o le com "." (no shell DELE), e "set -u" afetaria o resto da inicializacao.
set -e

psql -v ON_ERROR_STOP=1 -v senha="$APP_DB_PASSWORD" --username "$POSTGRES_USER" --dbname postgres <<'SQL'
CREATE ROLE iabimeta LOGIN PASSWORD :'senha' NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE DATABASE iabimeta OWNER iabimeta ENCODING 'UTF8';
SQL

# O indice semantico do Knowledge (ADR 0034) usa a extensao pgvector, que SO o superusuario cria (o papel da aplicacao nao e superusuario). A imagem pgvector/pgvector:pg17 a traz; numa imagem
# sem ela o passo e pulado (o modo semantico fica desligado e nada depende dela). Banco ja existente: docs/operations/CONHECIMENTO-HIBRIDO.md.
if [ "$(psql -tA --username "$POSTGRES_USER" --dbname postgres -c "SELECT 1 FROM pg_available_extensions WHERE name = 'vector'")" = "1" ]; then
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname iabimeta -c "CREATE EXTENSION IF NOT EXISTS vector"
fi
