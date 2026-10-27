using IaBimeta.Tenancy.Application;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Isolation;

/// <summary>
/// Substitui o antigo teste ignorado <c>ContextoDeAtendimentoTests</c> no nível em que o comportamento existe hoje:
/// o ISOLAMENTO ENTRE TENANTS na persistência. Mauro e Pedro são tenants distintos, e nada de um aparece para o outro.
///
/// ATENÇÃO: isto NÃO cobre o cenário de aceitação do CLAUDE.md no nível de conversa (contexto do atendimento #1001
/// nunca no #1002, resposta só ao #1001). Esse cenário depende do Conversation Context Manager (etapa 3) e deve ser
/// implementado e ativado quando o Conversations existir. Enquanto isso, ele está registrado como pendência da
/// arquitetura e não como teste ignorado.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MauroPedroIsolamentoDeTenantsTests(PostgresFixture banco)
{
    [Fact]
    public async Task Dados_do_tenant_de_Mauro_nao_vazam_para_o_tenant_de_Pedro_e_vice_versa()
    {
        var mauro = await banco.RegistrarTenantAsync("mauro");
        var pedro = await banco.RegistrarTenantAsync("pedro");

        Guid idDoMauro;
        Guid idDoPedro;
        await using (var escopo = banco.NovoEscopo(PostgresFixture.Contexto(mauro)))
        {
            idDoMauro = (await escopo.ServiceProvider.GetRequiredService<OrganizationService>().CreateAsync("Laboratório do Mauro")).Id;
        }

        await using (var escopo = banco.NovoEscopo(PostgresFixture.Contexto(pedro)))
        {
            idDoPedro = (await escopo.ServiceProvider.GetRequiredService<OrganizationService>().CreateAsync("Clínica do Pedro")).Id;
        }

        await using var escopoMauro = banco.NovoEscopo(PostgresFixture.Contexto(mauro));
        await using var escopoPedro = banco.NovoEscopo(PostgresFixture.Contexto(pedro));
        var servicoMauro = escopoMauro.ServiceProvider.GetRequiredService<OrganizationService>();
        var servicoPedro = escopoPedro.ServiceProvider.GetRequiredService<OrganizationService>();

        // A requisição de Mauro só enxerga o que é de Mauro...
        Assert.Equal(new[] { "Laboratório do Mauro" }, (await servicoMauro.ListAsync()).Select(o => o.Name));
        Assert.NotNull(await servicoMauro.GetAsync(idDoMauro));
        Assert.Null(await servicoMauro.GetAsync(idDoPedro));

        // ...e a de Pedro só o que é de Pedro.
        Assert.Equal(new[] { "Clínica do Pedro" }, (await servicoPedro.ListAsync()).Select(o => o.Name));
        Assert.NotNull(await servicoPedro.GetAsync(idDoPedro));
        Assert.Null(await servicoPedro.GetAsync(idDoMauro));
    }
}
