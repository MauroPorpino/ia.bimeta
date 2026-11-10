# deploy/: implantação do ia.bimeta em uma VM Linux (Docker Compose)

Tudo para instalar, operar e recuperar uma pilha única do ia.bimeta (Caddy com HTTPS e a interface, Api, Worker, PostgreSQL 17 e ClamAV) numa VM **Ubuntu Server 24.04**. O passo a passo está em **`docs/operations/IMPLANTACAO-LINUX.md`**; a decisão, em `docs/adr/0033-*.md`.

| Arquivo ou pasta | Para quê |
|---|---|
| `docker-compose.yml` | a pilha (só o proxy publica as portas 80 e 443) |
| `Dockerfile` | imagens do Api e do Worker (.NET 10, sem root) |
| `web/` | imagem do proxy: Caddy, `Caddyfile` e a interface React compilada |
| `postgres/init/` | cria o papel da aplicação (sem superusuário) na primeira inicialização |
| `.env.example`, `app.env.example`, `backup.env.example` | modelos **sem valores**; `scripts/gerar-segredos.sh` cria os arquivos reais (permissão 600, **nunca no repositório**) |
| `scripts/` | instalar o host, gerar segredos, preflight, subir, atualizar, status, monitor, backup, verificação, restauração, ensaio de restauração, teste do ClamAV, aceitação |
| `systemd/` | modelos dos timers de backup e monitor (instalados por `scripts/agendar.sh`) |
| `tests/` | teste dos scripts de backup e restauração contra um `docker` falso (`bash deploy/tests/testar-scripts.sh`) |

**Ordem curta** (detalhes e cuidados no guia): `instalar-host.sh` → `gerar-segredos.sh` → guardar os segredos **fora** da VM → `preflight.sh` → `subir.sh` → `aceitacao.sh --confirmo-que-e-teste` (VM de teste) → `empresa.sh` → `backup.sh` → `agendar.sh`.

**Estado de validação:** conferido por texto, por compilação (`dotnet publish`, build da interface) e por testes do que roda sem Docker. **Nada foi executado com Docker de verdade**: o primeiro uso real é o `aceitacao.sh` na VM.
