using IaBimeta.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// Sobe a API real (a mesma composição de <see cref="ApiHost"/>) em uma porta local aleatória, por HTTP de verdade.
/// Nada de servidor de teste em memória: o cabeçalho, a origem loopback e o pipeline são os da aplicação.
/// </summary>
internal sealed class ApiServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ApiServer(WebApplication app, HttpClient cliente)
    {
        _app = app;
        Client = cliente;
    }

    public HttpClient Client { get; }

    /// <param name="ambiente">Development, Production, Staging...</param>
    /// <param name="configuracao">Sobrescreve a configuração (por exemplo, a string de conexão e a seção Tenancy:Development).</param>
    public static async Task<ApiServer> StartAsync(string ambiente, IDictionary<string, string?> configuracao)
    {
        var app = ApiHost.Build(
            new[] { "--environment=" + ambiente },
            builder => builder.Configuration.AddInMemoryCollection(configuracao));
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        var endereco = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new ApiServer(app, new HttpClient { BaseAddress = new Uri(endereco) });
    }

    /// <summary>Configuração típica de desenvolvimento: banco de teste e cabeçalho X-Dev-Tenant habilitado.</summary>
    public static Dictionary<string, string?> ConfiguracaoDeDesenvolvimento(bool cabecalhoHabilitado = true) => new()
    {
        ["ConnectionStrings:Tenancy"] = TestDatabase.ConnectionString,
        ["Tenancy:Development:HeaderEnabled"] = cabecalhoHabilitado ? "true" : "false",
    };

    public HttpRequestMessage Requisicao(HttpMethod metodo, string caminho, string? tenantSlug = null, HttpContent? conteudo = null)
    {
        var requisicao = new HttpRequestMessage(metodo, caminho) { Content = conteudo };
        if (tenantSlug is not null)
        {
            requisicao.Headers.Add("X-Dev-Tenant", tenantSlug);
        }

        return requisicao;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
