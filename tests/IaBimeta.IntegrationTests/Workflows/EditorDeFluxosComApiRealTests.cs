using System.Diagnostics;
using IaBimeta.IntegrationTests.Api;
using IaBimeta.IntegrationTests.Conversations;

namespace IaBimeta.IntegrationTests.Workflows;

/// <summary>
/// O teste funcional do EDITOR React contra a API REAL e o PostgreSQL de teste (prontidão da V1): sobe a API em porta local, cria uma empresa com organização, unidade e canal, e roda o vitest (`npm run test:e2e`) apontado
/// para ela. Precisa do Node (variável <c>IABIMETA_NODE</c> ou o caminho padrão do projeto) e do <c>npm install</c> feito; sem eles o teste NÃO falha, só registra que não rodou (o roteiro de homologação manda executar à mão).
/// Fora do alcance: arrastar nós e conectar saídas com o mouse (o jsdom não tem layout nem mouse): isso é do navegador.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EditorDeFluxosComApiRealTests(PostgresFixture banco)
{
    private static string? RaizDoFrontend()
    {
        for (var pasta = new DirectoryInfo(AppContext.BaseDirectory); pasta is not null; pasta = pasta.Parent)
        {
            var candidata = Path.Combine(pasta.FullName, "frontend", "package.json");
            if (File.Exists(candidata))
            {
                return Path.GetDirectoryName(candidata);
            }
        }

        return null;
    }

    [Fact]
    public async Task O_administrador_cria_edita_valida_publica_simula_e_ativa_um_fluxo_pelo_editor_contra_a_API_real()
    {
        var frontend = RaizDoFrontend();
        var node = Environment.GetEnvironmentVariable("IABIMETA_NODE") ?? @"C:\Projetos\ferramentas\node-v24.22.1-win-x64\node.exe";
        var vitest = frontend is null ? null : Path.Combine(frontend, "node_modules", "vitest", "vitest.mjs");
        if (frontend is null || !File.Exists(node) || !File.Exists(vitest))
        {
            Console.WriteLine("EditorDeFluxosComApiRealTests: Node ou node_modules ausentes; não executado (ver docs/operations/HOMOLOGACAO-V1.md).");
            return;
        }

        var c = await CenarioDeConversas.CriarAsync(banco, "e2e-editor", canaisReais: true);
        var admin = await banco.CriarAdministradorDoTenantAsync(c.Tenant);
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", true);
        var canal = await c.CriarCanalAsync([unidade], unidade);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        var inicio = new ProcessStartInfo(node, $"\"{vitest}\" run --config vitest.e2e.config.ts")
        {
            WorkingDirectory = frontend,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        inicio.Environment["E2E_API_URL"] = api.Client.BaseAddress!.ToString().TrimEnd('/');
        inicio.Environment["E2E_TENANT"] = admin.TenantSlug;
        inicio.Environment["E2E_EMAIL"] = admin.Email;
        inicio.Environment["E2E_PASSWORD"] = admin.Senha;
        inicio.Environment["E2E_ORG"] = c.OrganizacaoId.ToString();
        inicio.Environment["E2E_UNIT"] = unidade.ToString();
        inicio.Environment["E2E_CHANNEL"] = canal.Value.ToString();

        using var processo = Process.Start(inicio)!;
        var saida = processo.StandardOutput.ReadToEndAsync();
        var erro = processo.StandardError.ReadToEndAsync();
        using var limite = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await processo.WaitForExitAsync(limite.Token);
        }
        catch (OperationCanceledException)
        {
            processo.Kill(entireProcessTree: true);
            Assert.Fail("O teste funcional do editor passou de 4 minutos.");
        }

        var texto = await saida + await erro;
        Assert.True(processo.ExitCode == 0, texto[^Math.Min(4000, texto.Length)..]);
        Assert.Contains("1 passed", texto);
        Assert.DoesNotContain("Unhandled", texto);
    }
}
