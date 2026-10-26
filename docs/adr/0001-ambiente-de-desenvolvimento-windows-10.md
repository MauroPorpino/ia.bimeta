# ADR 0001 — Ambiente de desenvolvimento local no Windows 10

- **Status:** Aceito (a decisão sobre pgvector é **provisória**)
- **Data:** 26/10/2026
- **Decisor:** Mauro Porpino
- **Contexto de referência:** `CLAUDE.md` e `ARCHITECTURE.md` (seções 8 e 13, "Compatibilidade de toolchain no Windows 10")

## Contexto

O desenvolvimento inicial do ia.bimeta ocorre em um computador com Windows 10 Pro 22H2 (build 19045), Intel i7-4790, 16 GB de RAM e disco com espaço limitado. A homologação e a produção serão em Linux. O editor é o VS Code, com Claude Code como agente de implementação.

Fatos verificados em 26/10/2026 na documentação oficial:

- **Windows 10 22H2** teve o suporte regular encerrado em 14/10/2025.
- **.NET 10** (LTS, suporte até 14/11/2028): a Microsoft lista no Windows 10 apenas as versões 21H2, 1809 e 1607, e somente nas edições LTSC/Enterprise. A edição Pro 22H2 **não consta** na tabela oficial. .NET 8 e 9 têm a mesma restrição e o suporte de ambos termina em 10/11/2026.
- **Node.js:** o projeto lista `>= Windows 10 / Server 2016` como Tier 1 (ramo principal). O v24 entrou em Maintenance LTS em 20/10/2026, com fim de vida em 30/04/2028. O v26 vira LTS em 28/10/2026.
- **PostgreSQL:** o instalador EDB lista como testados o Windows Server 2022 e 2019 para a versão 17, e 2025 e 2022 para a 18. O Windows 10 não consta. A página afirma que o instalador pode, em geral, funcionar em edições desktop comparáveis. O PostgreSQL 17 tem suporte da comunidade até 08/11/2029.
- **pgvector:** não há instalador oficial para Windows. O método oficial é compilar com `nmake`, exigindo Visual Studio ou Build Tools com C++. Os binários encontrados são de terceiros e foram testados no Windows 11.
- **Antivírus Avast:** intercepta TLS e substitui os certificados por uma raiz própria. Isso quebra clientes que não usam o repositório de certificados do Windows (por exemplo `curl` e, provavelmente, Node/npm).

## Decisão

1. **.NET 10 SDK 10.0.114**, mantido, fixado em `global.json` com `rollForward: latestPatch`.
2. **Node.js 24 LTS**, a ser instalado em fase posterior. Reavaliar a linha 26 depois de ela se tornar LTS.
3. **PostgreSQL 17**, instalado de forma independente do PostgreSQL 9.5 existente, em porta e pasta de dados próprias, preferencialmente por pacote zip, sem serviço automático. Instalação em fase posterior.
4. **Desenvolvimento no Windows 10 Pro 22H2**, exclusivamente com **dados sintéticos** e **sem credenciais de produção**.
5. **VS Code** e **Claude Code** como ferramentas de desenvolvimento.
6. **Docker não será instalado** neste momento.
7. **NuGet restrito ao nuget.org** no repositório (`nuget.config` com `<clear />`), sem as fontes locais da máquina.
8. **Finais de linha LF** no repositório, com CRLF preservado em `.bat` e `.cmd`.
9. **PostgreSQL 9.5 existente permanece intacto** até decisão posterior.
10. **pgvector (provisório):** não será instalado no Windows. A busca semântica é adiada para a homologação em Linux (pacote `postgresql-17-pgvector` do repositório PGDG). A possibilidade de busca semântica **deve ser preservada**: o domínio acessará busca de conhecimento por uma interface (por exemplo `IKnowledgeSearch`), sem dependência direta de `pgvector`. No Windows, a implementação usa busca textual do PostgreSQL (`tsvector`).

## Consequências

- **Suporte:** o conjunto não é oficialmente suportado pelo fabricante no Windows 10 Pro. Problemas podem não ser aceitos em chamados do fornecedor.
- **Segurança:** o sistema operacional não recebe atualizações de segurança regulares. Mitigações: dados sintéticos, serviços escutando apenas em `127.0.0.1`, nenhum segredo real, antivírus ativo.
- **Divergência entre ambientes:** a busca semântica só poderá ser exercitada em Linux. Testes de recuperação de conhecimento no Windows cobrem apenas a busca textual.
- **Avast:** ferramentas fora do repositório de certificados do Windows podem falhar em HTTPS. A solução (por exemplo `NODE_EXTRA_CA_CERTS`) exige decisão específica e não está aplicada.
- **Caminhos longos:** `LongPathsEnabled` permanece desativado. Manter caminhos curtos no repositório.
- **Linux:** o núcleo não pode depender de recursos exclusivos do Windows. Usar `Path.Combine`, configuração por ambiente e nenhum acesso a registro ou serviços do Windows.

## Revisão

Reavaliar esta decisão ao ocorrer qualquer um destes eventos: migração do desenvolvimento para Windows 11 ou Linux, instalação de ambiente de homologação Linux, problema de compatibilidade atribuível ao Windows 10, ou decisão de habilitar o pgvector.

## Pendências (não executadas)

- Instalação do Node.js 24 LTS e do PostgreSQL 17.
- Confiança do certificado HTTPS de desenvolvimento do .NET.
- Tratamento do certificado do Avast para Node/npm.
- Desativação e remoção do PostgreSQL 9.5, e retirada de `...\PostgreSQL\9.5\bin` do PATH.
- Política de `TreatWarningsAsErrors`, a definir ao criar os projetos.
