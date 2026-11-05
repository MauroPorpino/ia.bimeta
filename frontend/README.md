# ia.bimeta - interface do atendente (MVP)

SPA em **Vite + React + TypeScript estrito**, sem framework de UI, roteador, gerenciador de estado nem biblioteca de requisições (só `fetch`). Telas: entrar, fila de atendimento (Aguardando / Meus, filtro por fila, presença) e conversa (histórico, assumir, responder, devolver à fila, devolver à IA, encerrar). Atualiza por polling: a fila a cada 3 s e a conversa a cada 2 s (sem WebSocket).

## Como rodar

Pré-requisitos: Node 24 (nesta máquina, o portátil em `C:\Projetos\ferramentas\node-v24.22.1-win-x64`, que não está no PATH) e a API do ia.bimeta no ar em `http://localhost:5155` (`dotnet run --project backend/host/IaBimeta.Api`, perfil `http`, ambiente Development).

```powershell
$env:PATH = 'C:\Projetos\ferramentas\node-v24.22.1-win-x64;' + $env:PATH
cd frontend
npm install
npm run dev        # abrir http://localhost:5173
```

Outros scripts: `npm run build` (`tsc --noEmit` + `vite build`, saída em `dist/`) e `npm test` (Vitest + Testing Library, com `fetch` simulado; não precisa da API).

## Proxy

O navegador fala só com o Vite (mesma origem, sem CORS). Em `vite.config.ts`, `/api/*` vira `http://localhost:5155/*` (o prefixo `/api` é removido): por exemplo, `/api/auth/login` chega à API como `/auth/login`. Com a API desligada, o proxy responde 500 e a tela mostra "Erro no servidor" ou "Sem conexão com a API" numa faixa, sem quebrar.

## Entrar (usuários de demonstração)

Só em Development, a API semeia dois administradores sintéticos (`backend/host/IaBimeta.Api/appsettings.Development.json`, seção `Tenancy:Development:SeedAdministrators`):

| Tenant | E-mail | Senha |
|---|---|---|
| `demo-a` | `admin@demo-a.local` | `DevLocal-Admin-2026!` |
| `demo-b` | `admin@demo-b.local` | `DevLocal-Admin-2026!` |

A senha é sintética, de desenvolvimento (e pode ser trocada pela variável `IABIMETA_DEV_ADMIN_PASSWORD` na API). O campo "Tenant" é o slug. O token de sessão fica só em memória e em `sessionStorage` (some ao fechar a aba), vai em `Authorization: Bearer` e um 401 de qualquer chamada volta à tela de entrada. Nada de credencial real no front nem no repositório.

Para ver itens na fila é preciso que o usuário seja membro de uma fila (`/contact-center/me/queues`) e que existam conversas em atendimento humano pendente (por exemplo, pelo simulador de desenvolvimento da API).

## Contrato com o backend

Os tipos em `src/api/types.ts` foram lidos do código do backend (enums como texto, JSON em camelCase). As ações `claim`, `release` e `return-to-ai` já existem em `/contact-center/items/{id}/...`. As rotas abaixo **ainda não existem no backend**: o front assume este contrato e, até elas existirem, mostra o erro da API (404) numa faixa.

- `POST /contact-center/items/{id}/reply` com `{ "requestId": "<guid>", "text": "<texto>" }` - 200 em JSON.
- `POST /contact-center/items/{id}/close` com `{ "requestId": "<guid>", "reasonCode": "resolved" }` - 200 em JSON.

Em ambas, `requestId` é a chave de idempotência: o cliente gera um GUID novo por ação e o **reutiliza se o usuário repetir a mesma ação** (mesmo item e, no `reply`, mesmo texto) depois de falha de rede, 5xx, 409 ou resposta pendente. Descarta o GUID quando a ação dá certo ou é rejeitada de vez (400, 403, 404, 422). Erros chegam em ProblemDetails (`status`, `title`, `detail`) e a tela mostra `title` e `detail`.

## Estrutura

```text
frontend/
  src/api/        client.ts (fetch tipado, erros, requestId) · session.ts (token) · types.ts (DTOs)
  src/components/ LoginScreen · QueuePanel · ConversationPanel · MessageList · ErrorBanner
  src/App.tsx · main.tsx · hooks.ts (polling, nomes dos contatos) · format.ts · styles.css
  src/test/       fetch simulado e fixtures dos testes
```
