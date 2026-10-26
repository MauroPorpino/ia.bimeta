# ADR 0003 — Estrutura inicial da solução .NET

- **Status:** Aceito
- **Data:** 26/10/2026
- **Decisor:** Mauro Porpino
- **Referências:** `CLAUDE.md` (organização sugerida), `ARCHITECTURE.md` (seções 8 e 11) e ADR 0001

## Contexto

O repositório tinha apenas documentação e arquivos de configuração. A etapa 1 de `ARCHITECTURE.md` (preparação do repositório, build, testes e execução mínima) exige uma solução que compile e rode testes, sem antecipar módulos de etapas posteriores.

## Decisão

1. Solução `backend/IaBimeta.slnx` (formato `slnx`, padrão do SDK 10) com quatro projetos de produção em camadas:
   - `IaBimeta.Domain`: entidades e regras, sem dependências.
   - `IaBimeta.Application`: casos de uso e contratos; referencia o Domain.
   - `IaBimeta.Infrastructure`: banco, provedores de IA e conectores; referencia o Application.
   - `IaBimeta.Api`: ASP.NET Core mínimo, com apenas `GET /health`; referencia o Application e a Infrastructure.
2. Testes em `tests/`, fora de `backend/`, começando por `IaBimeta.Domain.Tests` (xUnit 2). Inclui o teste de aceitação Mauro/Pedro **marcado como pulado**, com o motivo, até existir o Conversation Context Manager. Ele não conta como cobertura.
3. **`TreatWarningsAsErrors` ativado** em `Directory.Build.props` para todos os projetos, resolvendo a pendência do ADR 0001.
4. Primeiro tipo de domínio: `TenantId`, que rejeita valor vazio.
5. **Fora desta etapa:** `IaBimeta.Worker` (etapa 5, webhooks e tarefas assíncronas), `frontend/` (exige Node.js, ainda não instalado), banco de dados e migrations.

## Consequências

- Nenhum pacote além dos do template xUnit (`Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `coverlet.collector`) foi adicionado.
- O template `xunit3` não está instalado nesta máquina; usou-se o xUnit 2.9.3 do template padrão. Migrar para xUnit 3 exige instalar o template ou editar os pacotes manualmente.
- Com `TreatWarningsAsErrors`, qualquer aviso novo (inclusive de pacotes NuGet com vulnerabilidade conhecida) quebra o build e precisa ser tratado de forma explícita.
- Os limites entre as camadas ainda não são verificados por teste automático. Um teste de arquitetura pode ser adicionado quando houver código suficiente para justificá-lo.

## Revisão

Reavaliar ao introduzir o Worker, o frontend ou o banco, ou se a separação em quatro projetos gerar atrito sem benefício.
