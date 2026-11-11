using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IaBimeta.Capabilities.Domain;
using IaBimeta.Capabilities.Infrastructure.Outbound;
using IaBimeta.Capabilities.Tests.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IaBimeta.Capabilities.Tests;

/// <summary>Um "sistema externo" em loopback (Kestrel real, sem rede externa) que registra o que recebeu.</summary>
internal sealed class TinyErp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TinyErp(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public int Hits => _hits;

    public List<string> Hosts { get; } = [];

    public List<string> Cookies { get; } = [];

    public string? LastBody { get; private set; }

    public Dictionary<string, string> LastHeaders { get; } = [];

    private int _hits;

    public static async Task<TinyErp> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        TinyErp? self = null;

        app.Use(async (ctx, next) =>
        {
            Interlocked.Increment(ref self!._hits);
            self.Hosts.Add(ctx.Request.Host.Value ?? string.Empty);
            self.Cookies.Add(ctx.Request.Headers.Cookie.ToString());
            self.LastHeaders.Clear();
            foreach (var h in ctx.Request.Headers)
            {
                self.LastHeaders[h.Key] = h.Value.ToString();
            }

            using var leitor = new StreamReader(ctx.Request.Body);
            self.LastBody = await leitor.ReadToEndAsync();
            await next();
        });
        app.MapGet("/ok", () => Results.Json(new { status = "ABERTO" }));
        app.MapPost("/echo", (HttpContext ctx) => Results.Json(new { recebido = self!.LastBody }));
        app.MapGet("/cookie", (HttpContext ctx) =>
        {
            ctx.Response.Headers.SetCookie = "sessao=abc; Path=/";
            return Results.Json(new { ok = true });
        });
        app.MapGet("/redirect", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Location = $"http://127.0.0.1:{self!.Port}/segredo";
            return Task.CompletedTask;
        });
        app.MapGet("/segredo", () => Results.Json(new { segredo = "nao-deve-ser-alcancado" }));
        app.MapGet("/big", () => Results.Text(new string('x', 10_000), "application/json"));
        app.MapGet("/chunked", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            for (var i = 0; i < 50; i++)
            {
                await ctx.Response.WriteAsync(new string('y', 1000));
                await ctx.Response.Body.FlushAsync();
            }
        });
        app.MapGet("/gzip-bomb", async (HttpContext ctx) =>
        {
            using var saida = new MemoryStream();
            await using (var gz = new GZipStream(saida, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                await gz.WriteAsync(new byte[5_000_000]);
            }

            ctx.Response.Headers.ContentEncoding = "gzip";
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(saida.ToArray());
        });
        app.MapGet("/slow", async (HttpContext ctx) =>
        {
            await Task.Delay(3000, ctx.RequestAborted);
            await ctx.Response.WriteAsync("{}");
        });

        await app.StartAsync();
        var porta = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        self = new TinyErp(app, porta);
        return self;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

public class GuardedHttpTransportTests
{
    private static OutboundDestinationPolicy Policy(int port, bool allowLoopback)
    {
        var o = new OutboundOptions { AllowHttp = true, AllowedPorts = [port] };
        if (allowLoopback)
        {
            o.AllowedPrivateCidrs.Add("127.0.0.0/8");
        }

        return new OutboundDestinationPolicy(o);
    }

    private static OutboundRequest Get(string url, int maxBytes = 4096, int timeoutMs = 5000, HttpMethod? method = null, byte[]? body = null, params KeyValuePair<string, string>[] headers) =>
        new(method ?? HttpMethod.Get, new Uri(url), headers, body, body is null ? null : "application/json", TimeSpan.FromMilliseconds(timeoutMs), maxBytes);

    [Fact]
    public async Task Com_o_CIDR_liberado_pelo_operador_conecta_ao_endereco_resolvido_e_preserva_o_Host()
    {
        await using var erp = await TinyErp.StartAsync();
        var resolver = new FakeResolver { Map = { ["erp.example.com"] = [IPAddress.Loopback] } };
        using var transporte = new GuardedHttpTransport(Policy(erp.Port, allowLoopback: true), resolver);

        var r = await transporte.SendAsync(Get($"http://erp.example.com:{erp.Port}/ok", headers: new KeyValuePair<string, string>("X-Filial", "01")), CancellationToken.None);

        Assert.Equal(200, r.Status);
        Assert.Contains("ABERTO", Encoding.UTF8.GetString(r.Body), StringComparison.Ordinal);
        Assert.Equal($"erp.example.com:{erp.Port}", erp.Hosts.Single());
        Assert.Equal("01", erp.LastHeaders["X-Filial"]);
        Assert.Equal("application/json", erp.LastHeaders["Accept"]);
        Assert.Equal(["erp.example.com"], resolver.Asked);
    }

    [Fact]
    public async Task Nome_publico_que_resolve_para_loopback_e_recusado_sem_conectar_DNS_rebinding()
    {
        await using var erp = await TinyErp.StartAsync();
        var resolver = new FakeResolver { Map = { ["parece-publico.example.com"] = [IPAddress.Loopback] } };
        using var transporte = new GuardedHttpTransport(Policy(erp.Port, allowLoopback: false), resolver);

        var ex = await Assert.ThrowsAsync<OutboundBlockedException>(() => transporte.SendAsync(Get($"http://parece-publico.example.com:{erp.Port}/ok"), CancellationToken.None));

        Assert.Equal("private-address", ex.Reason);
        Assert.Equal(0, erp.Hits);
    }

    [Fact]
    public async Task Resolucao_mista_com_um_endereco_interno_e_recusada_por_inteiro()
    {
        await using var erp = await TinyErp.StartAsync();
        var resolver = new FakeResolver { Map = { ["misto.example.com"] = [IPAddress.Parse("93.184.216.34"), IPAddress.Loopback] } };
        using var transporte = new GuardedHttpTransport(Policy(erp.Port, allowLoopback: false), resolver);

        await Assert.ThrowsAsync<OutboundBlockedException>(() => transporte.SendAsync(Get($"http://misto.example.com:{erp.Port}/ok"), CancellationToken.None));
        Assert.Equal(0, erp.Hits);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("fd00:ec2::254")]
    public async Task Metadados_de_nuvem_nunca_sao_alcancados_nem_com_o_CIDR_liberado(string endereco)
    {
        var resolver = new FakeResolver { Map = { ["meta.example.com"] = [IPAddress.Parse(endereco)] } };
        var opcoes = new OutboundOptions { AllowHttp = true, AllowedPorts = [80], AllowedPrivateCidrs = { "169.254.0.0/16", "100.64.0.0/10", "fd00::/8", "0.0.0.0/0" } };
        using var transporte = new GuardedHttpTransport(new OutboundDestinationPolicy(opcoes), resolver);

        var ex = await Assert.ThrowsAsync<OutboundBlockedException>(() => transporte.SendAsync(Get("http://meta.example.com/latest/meta-data/"), CancellationToken.None));

        Assert.Equal("blocked-address", ex.Reason);
    }

    [Theory]
    [InlineData("http://127.0.0.1/ok")]
    [InlineData("http://localhost/ok")]
    [InlineData("http://[::1]/ok")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://2130706433/ok")]
    public async Task Urls_internas_literais_ou_internas_por_nome_sao_recusadas_antes_de_qualquer_conexao(string url)
    {
        using var transporte = new GuardedHttpTransport(new OutboundDestinationPolicy(new OutboundOptions { AllowHttp = true, AllowedPorts = [80] }), new FakeResolver());

        await Assert.ThrowsAsync<OutboundBlockedException>(() => transporte.SendAsync(Get(url), CancellationToken.None));
    }

    [Fact]
    public async Task Redirecionamento_nunca_e_seguido_nem_para_um_destino_permitido()
    {
        await using var erp = await TinyErp.StartAsync();
        using var transporte = new GuardedHttpTransport(Policy(erp.Port, allowLoopback: true), new FakeResolver { Map = { ["erp.example.com"] = [IPAddress.Loopback] } });

        var r = await transporte.SendAsync(Get($"http://erp.example.com:{erp.Port}/redirect"), CancellationToken.None);

        Assert.Equal(302, r.Status);
        Assert.Empty(r.Body);
        Assert.Equal(1, erp.Hits);
    }

    [Fact]
    public async Task Resposta_acima_do_limite_e_cortada_com_ou_sem_content_length_e_apos_descompressao()
    {
        await using var erp = await TinyErp.StartAsync();
        using var transporte = new GuardedHttpTransport(Policy(erp.Port, allowLoopback: true), new FakeResolver { Map = { ["erp.example.com"] = [IPAddress.Loopback] } });
        var baseUrl = $"http://erp.example.com:{erp.Port}";

        await Assert.ThrowsAsync<OutboundTooLargeException>(() => transporte.SendAsync(Get(baseUrl + "/big", maxBytes: 1024), CancellationToken.None));
        await Assert.ThrowsAsync<OutboundTooLargeException>(() => transporte.SendAsync(Get(baseUrl + "/chunked", maxBytes: 20_000), CancellationToken.None));
        await Assert.ThrowsAsync<OutboundTooLargeException>(() => transporte.SendAsync(Get(baseUrl + "/gzip-bomb", maxBytes: 64 * 1024), CancellationToken.None));

        var ok = await transporte.SendAsync(Get(baseUrl + "/big", maxBytes: 20_000), CancellationToken.None);
        Assert.Equal(10_000, ok.Body.Length);
    }

    [Fact]
    public async Task Tempo_esgotado_vira_timeout_mas_o_cancelamento_do_chamador_continua_sendo_cancelamento()
    {
        await using var erp = await TinyErp.StartAsync();
        using var transporte = new GuardedHttpTransport(Policy(erp.Port, allowLoopback: true), new FakeResolver { Map = { ["erp.example.com"] = [IPAddress.Loopback] } });

        await Assert.ThrowsAsync<OutboundTimeoutException>(() => transporte.SendAsync(Get($"http://erp.example.com:{erp.Port}/slow", timeoutMs: 300), CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transporte.SendAsync(Get($"http://erp.example.com:{erp.Port}/slow", timeoutMs: 10_000), cts.Token));
    }

    [Fact]
    public async Task Conexao_recusada_e_erro_de_rede_de_fase_de_conexao_nada_chegou_ao_destino()
    {
        var ocioso = new TcpListener(IPAddress.Loopback, 0);
        ocioso.Start();
        var porta = ((IPEndPoint)ocioso.LocalEndpoint).Port;
        ocioso.Stop();
        using var transporte = new GuardedHttpTransport(Policy(porta, allowLoopback: true), new FakeResolver { Map = { ["erp.example.com"] = [IPAddress.Loopback] } });

        var ex = await Assert.ThrowsAsync<OutboundNetworkException>(() => transporte.SendAsync(Get($"http://erp.example.com:{porta}/ok"), CancellationToken.None));

        Assert.True(ex.ConnectStage);
    }

    [Fact]
    public async Task Nome_que_nao_resolve_e_erro_de_conexao()
    {
        using var transporte = new GuardedHttpTransport(Policy(80, allowLoopback: false), new FakeResolver());

        var ex = await Assert.ThrowsAsync<OutboundNetworkException>(() => transporte.SendAsync(Get("http://nao-existe.example.com/ok"), CancellationToken.None));

        Assert.True(ex.ConnectStage);
    }

    [Fact]
    public async Task Cookies_nao_sao_guardados_e_o_corpo_e_o_tipo_do_conteudo_seguem_como_montados()
    {
        await using var erp = await TinyErp.StartAsync();
        using var transporte = new GuardedHttpTransport(Policy(erp.Port, allowLoopback: true), new FakeResolver { Map = { ["erp.example.com"] = [IPAddress.Loopback] } });
        var baseUrl = $"http://erp.example.com:{erp.Port}";

        await transporte.SendAsync(Get(baseUrl + "/cookie"), CancellationToken.None);
        await transporte.SendAsync(Get(baseUrl + "/echo", method: HttpMethod.Post, body: Encoding.UTF8.GetBytes("""{"a":1}""")), CancellationToken.None);

        Assert.All(erp.Cookies, c => Assert.Equal(string.Empty, c));
        Assert.Equal("""{"a":1}""", erp.LastBody);
        Assert.StartsWith("application/json", erp.LastHeaders["Content-Type"], StringComparison.Ordinal);
    }
}
