# syntax=docker/dockerfile:1
# Imagem do proxy HTTPS (Caddy) com a interface React ja compilada (ADR 0033). Contexto de build: a RAIZ do repositorio.
FROM node:24-bookworm-slim AS interface
WORKDIR /app
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend ./
RUN npm run build

FROM caddy:2
COPY --from=interface /app/dist /srv/frontend
COPY deploy/web/Caddyfile /etc/caddy/Caddyfile
