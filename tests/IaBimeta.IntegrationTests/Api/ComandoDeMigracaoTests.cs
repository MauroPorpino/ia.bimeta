using IaBimeta.Api.Provisioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O comando <c>migrate</c> (ADR 0033): em produção o Api e o Worker NÃO aplicam migrations ao subir, então a implantação roda este comando, depois de um backup. Com PostgreSQL real (o banco de teste, já migrado pela fixture):
/// lista e aplica sem nada pendente, cobre os OITO módulos, recusa uso inválido sem subir o Api e, com o banco inacessível, falha sem vazar a senha.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ComandoDeMigracaoTests
{
    private static readonly string[] Modulos = ["Tenancy", "Audit", "Identity", "Conversations", "Channels", "ContactCenter", "AiGateway", "Agents", "Capabilities", "Workflows"];

    [Fact]
    public async Task Com_o_banco_em_dia_listar_e_aplicar_cobrem_os_oito_modulos_sem_nada_pendente()
    {
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        var lista = new StringWriter();
        var codigoDaLista = await MigrateCommand.ExecuteAsync(api.Services, apply: false, lista);
        var aplicacao = new StringWriter();
        var codigoDaAplicacao = await MigrateCommand.ExecuteAsync(api.Services, apply: true, aplicacao);

        Assert.Equal((0, 0), (codigoDaLista, codigoDaAplicacao));
        foreach (var modulo in Modulos)
        {
            Assert.Contains($"{modulo,-14} em dia", lista.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{modulo,-14} em dia", aplicacao.ToString(), StringComparison.Ordinal);
        }

        Assert.Contains("só listando; nada é alterado", lista.ToString(), StringComparison.Ordinal);
        Assert.Contains("Todos os módulos estão em dia.", aplicacao.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lista_de_modulos_do_comando_e_a_do_provisionamento_e_cobre_todos_os_contextos_do_host()
    {
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        await using var escopo = api.Services.CreateAsyncScope();

        var nomes = MigrateCommand.Modules(escopo.ServiceProvider).Select(m => m.Name).ToArray();

        Assert.Equal(Modulos, nomes);
    }

    [Fact]
    public async Task Uso_invalido_sai_com_2_sem_subir_o_Api()
    {
        string[][] casos = [["--bogus"], ["--list", "extra"], ["apagar"]];
        foreach (var argumentos in casos)
        {
            var saida = new StringWriter();
            Assert.Equal(2, await MigrateCommand.RunAsync(argumentos, saida));
            Assert.Contains("Uso: migrate [--list]", saida.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Com_o_banco_inacessivel_falha_com_codigo_1_e_nunca_repete_a_senha_nem_o_servidor()
    {
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var configuracao = api.Services.GetRequiredService<IConfiguration>();
        var original = configuracao["ConnectionStrings:Tenancy"];
        var saida = new StringWriter();

        configuracao["ConnectionStrings:Tenancy"] = "Host=127.0.0.1;Port=1;Database=nenhum;Username=u;Password=senha-secreta-do-teste;Timeout=2";
        try
        {
            var codigo = await MigrateCommand.ExecuteAsync(api.Services, apply: true, saida);

            Assert.Equal(1, codigo);
            Assert.Contains("FALHA ao consultar ou aplicar as migrations", saida.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("senha-secreta-do-teste", saida.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", saida.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            configuracao["ConnectionStrings:Tenancy"] = original;
        }
    }
}
