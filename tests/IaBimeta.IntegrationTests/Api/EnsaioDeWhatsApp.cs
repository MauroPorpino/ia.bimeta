using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>O que o "servidor da Graph API" da Meta recebeu: o token do cabeçalho, o caminho e o corpo.</summary>
internal sealed record CapturaGraph(string Credencial, string Caminho, string Corpo)
{
    public JsonNode Json => JsonNode.Parse(Corpo)!;

    public string Para => Json["to"]!.GetValue<string>();

    public string Texto => Json["text"]!["body"]!.GetValue<string>();

    public string? DadoOpaco => Json["biz_opaque_callback_data"]?.GetValue<string>();
}

/// <summary>
/// Um "servidor da Graph API" por HTTP de verdade, em porta local aleatória (loopback): recebe o <c>POST /{versão}/{phone_number_id}/messages</c> que o adaptador REAL do WhatsApp faz e responde pelo roteiro.
/// Nenhuma rede, nenhum token real. Sem roteiro, aceita a mensagem com um identificador novo a cada chamada.
/// </summary>
internal sealed class ServidorGraphLoopback : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<CapturaGraph> _capturas = new();
    private int _contador;

    private ServidorGraphLoopback(WebApplication app)
    {
        _app = app;
        Roteiro = _ => Aceita();
    }

    public string EnderecoBase { get; private set; } = string.Empty;

    public IReadOnlyList<CapturaGraph> Capturas => _capturas.ToList();

    /// <summary>O que responder a cada chamada: o status HTTP e o corpo.</summary>
    public Func<CapturaGraph, (int Status, string Corpo)> Roteiro { get; set; }

    public static async Task<ServidorGraphLoopback> IniciarAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        var servidor = new ServidorGraphLoopback(app);
        app.MapPost("/{versao}/{numero}/messages", servidor.AtenderAsync);
        await app.StartAsync();
        var endereco = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        servidor.EnderecoBase = endereco.TrimEnd('/') + "/";
        return servidor;
    }

    /// <summary>A resposta de sucesso da Graph API: o identificador da mensagem de saída (wamid).</summary>
    public (int Status, string Corpo) Aceita() =>
        (200, new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["contacts"] = new JsonArray(new JsonObject { ["input"] = "5511999990000", ["wa_id"] = "5511999990000" }),
            ["messages"] = new JsonArray(new JsonObject { ["id"] = $"wamid.SAIDA{Interlocked.Increment(ref _contador):D4}" }),
        }.ToJsonString());

    /// <summary>Um erro da Graph API: o status HTTP e o código do erro (por exemplo, 131047, a janela de 24 horas fechada).</summary>
    public static (int Status, string Corpo) Erro(int status, int codigo) =>
        (status, new JsonObject { ["error"] = new JsonObject { ["message"] = "mensagem do provedor", ["type"] = "OAuthException", ["code"] = codigo, ["fbtrace_id"] = "AbCdEf" } }.ToJsonString());

    private async Task AtenderAsync(HttpContext http)
    {
        var corpo = await new StreamReader(http.Request.Body, Encoding.UTF8).ReadToEndAsync(http.RequestAborted);
        var autorizacao = http.Request.Headers.Authorization.ToString();
        var captura = new CapturaGraph(autorizacao.StartsWith("Bearer ", StringComparison.Ordinal) ? autorizacao[7..] : string.Empty, http.Request.Path.Value ?? string.Empty, corpo);
        _capturas.Enqueue(captura);

        var (status, resposta) = Roteiro(captura);
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(resposta, http.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// O ensaio do fluxo ponta a ponta com o WhatsApp: o Api e o Worker REAIS (PostgreSQL, Conversations, Agents, AiGateway, ContactCenter, Channels), o adaptador REAL do WhatsApp, a OpenAI e a Graph API trocadas por
/// servidores HTTP locais (loopback). O canal é cadastrado como um administrador faria (conta, número, unidade, segredos cifrados e webhook), e a "Meta" fala com o Receiver por HTTP, com a assinatura de verdade.
/// </summary>
internal sealed class EnsaioDeWhatsApp : IAsyncDisposable
{
    public const string SegredoDoApp = "segredo-do-aplicativo-de-teste-0123456789";
    public const string TokenDeAcesso = "EAAG-token-de-acesso-canario-0123456789";
    public const string TokenDeVerificacao = "token-de-verificacao-de-teste";

    private readonly PostgresFixture _banco;

    private EnsaioDeWhatsApp(PostgresFixture banco, EnsaioDeIa ia, ServidorGraphLoopback graph)
    {
        _banco = banco;
        Ia = ia;
        Graph = graph;
    }

    public EnsaioDeIa Ia { get; }

    public ServidorGraphLoopback Graph { get; }

    public Guid WebhookId { get; private set; }

    public string NumeroDoCanal { get; private set; } = string.Empty;

    public string ContaDoProvedor { get; private set; } = string.Empty;

    public Guid OrganizacaoId { get; private set; }

    public Guid FilaId { get; private set; }

    public static async Task<EnsaioDeWhatsApp> IniciarAsync(PostgresFixture banco, string prefixo)
    {
        var graph = await ServidorGraphLoopback.IniciarAsync();
        var adicionais = new Dictionary<string, string?>
        {
            ["Channels:Providers:WhatsApp:BaseAddress"] = graph.EnderecoBase,

            // A espera entre as tentativas do relay é curta no ensaio (a de produção é de segundos): a recusa transitória do provedor se prova sem esperar de verdade.
            ["Messaging:BaseBackoff"] = "00:00:00.300",
            ["Messaging:MaxBackoff"] = "00:00:01",
            ["Channels:Secrets:ActiveKeyId"] = PostgresFixture.IdDaChaveDeTeste,
            [$"Channels:Secrets:Keys:{PostgresFixture.IdDaChaveDeTeste}"] = PostgresFixture.ChaveDeCifraDeTeste,
        };

        var ia = await EnsaioDeIa.IniciarAsync(banco, prefixo, adicionais: adicionais);
        var ensaio = new EnsaioDeWhatsApp(banco, ia, graph);
        await ensaio.CadastrarCanalAsync();
        return ensaio;
    }

    /// <summary>O canal cadastrado como um administrador faria: unidade, conta, número (autorizado e ativo), as três credenciais cifradas e o identificador opaco do webhook.</summary>
    private async Task CadastrarCanalAsync()
    {
        var status = await Ia.StatusAsync();
        OrganizacaoId = status["organizationId"]!.GetValue<Guid>();
        FilaId = status["queueId"]!.GetValue<Guid>();
        var sufixo = Guid.NewGuid().ToString("N")[..10];
        ContaDoProvedor = $"waba-{sufixo}";
        NumeroDoCanal = $"5511{Random.Shared.NextInt64(100000000, 999999999)}";

        WebhookId = await Ia.ComoSistemaAsync(async sp =>
        {
            var unidade = await sp.GetRequiredService<BusinessUnitService>().CreateAsync(OrganizacaoId, $"Matriz {sufixo}", IaBimeta.Tenancy.Domain.BusinessUnitKind.Headquarters);
            var contas = sp.GetRequiredService<ChannelAccountService>();
            var instancias = sp.GetRequiredService<ChannelInstanceService>();
            var conta = await contas.CreateAsync(OrganizacaoId, ChannelProvider.WhatsAppCloud, ContaDoProvedor, $"Conta {sufixo}");
            var instancia = await instancias.RegisterAsync(conta.Id, $"Número {sufixo}", NumeroDoCanal, null, null);
            await instancias.AuthorizeUnitAsync(instancia.Id, unidade.Id);
            await instancias.SetDefaultsAsync(instancia.Id, unidade.Id, null);
            await instancias.ActivateAsync(instancia.Id);
            await contas.SetSecretAsync(conta.Id, IaBimeta.Channels.Domain.ChannelSecretKind.AppSecret, SegredoDoApp);
            await contas.SetSecretAsync(conta.Id, IaBimeta.Channels.Domain.ChannelSecretKind.AccessToken, TokenDeAcesso);
            await contas.SetSecretAsync(conta.Id, IaBimeta.Channels.Domain.ChannelSecretKind.WebhookVerifyToken, TokenDeVerificacao);
            return (await sp.GetRequiredService<ChannelWebhookAdminService>().EnsureAsync(conta.Id)).WebhookId;
        });
    }

    // O que a "Meta" envia

    /// <summary>O corpo de um webhook de mensagem de texto, na forma da Cloud API.</summary>
    public byte[] PayloadDeTexto(string idDaMensagem, string waId, string nome, string texto, string? numero = null) =>
        MontarPayloadDeTexto(ContaDoProvedor, numero ?? NumeroDoCanal, idDaMensagem, waId, nome, texto);

    /// <summary>O mesmo corpo, sem depender de um ensaio (a conta e o número vêm do chamador).</summary>
    public static byte[] MontarPayloadDeTexto(string conta, string numero, string idDaMensagem, string waId, string nome, string texto) =>
        Encoding.UTF8.GetBytes(Envelope(
            conta,
            new JsonObject
            {
                ["messaging_product"] = "whatsapp",
                ["metadata"] = new JsonObject { ["display_phone_number"] = "551130000000", ["phone_number_id"] = numero },
                ["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = nome }, ["wa_id"] = waId }),
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["from"] = waId,
                    ["id"] = idDaMensagem,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                    ["type"] = "text",
                    ["text"] = new JsonObject { ["body"] = texto },
                }),
            }));

    /// <summary>O corpo de um webhook de status de entrega de uma mensagem de saída.</summary>
    public byte[] PayloadDeStatus(string idDoProvedor, string status, string? dadoOpaco, string waId) =>
        Encoding.UTF8.GetBytes(Envelope(
            ContaDoProvedor,
            new JsonObject
            {
                ["messaging_product"] = "whatsapp",
                ["metadata"] = new JsonObject { ["display_phone_number"] = "551130000000", ["phone_number_id"] = NumeroDoCanal },
                ["statuses"] = new JsonArray(new JsonObject
                {
                    ["id"] = idDoProvedor,
                    ["status"] = status,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                    ["recipient_id"] = waId,
                    ["biz_opaque_callback_data"] = dadoOpaco,
                }),
            }));

    private static string Envelope(string conta, JsonObject valor) =>
        new JsonObject
        {
            ["object"] = "whatsapp_business_account",
            ["entry"] = new JsonArray(new JsonObject { ["id"] = conta, ["changes"] = new JsonArray(new JsonObject { ["field"] = "messages", ["value"] = valor }) }),
        }.ToJsonString();

    /// <summary>A assinatura do corpo, como a Meta a calcula: <c>sha256=</c> e o HMAC-SHA256 dos bytes, em hexadecimal minúsculo.</summary>
    public static string Assinar(byte[] corpo, string segredo = SegredoDoApp) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), corpo)).ToLowerInvariant();

    /// <summary>Envia o webhook ao Receiver do Api, pela URL da conta, com a assinatura (ou outra, se o teste quiser).</summary>
    public async Task<HttpStatusCode> EnviarWebhookAsync(byte[] corpo, string? assinatura = null, Guid? webhookId = null, bool semAssinatura = false)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/whatsapp/{webhookId ?? WebhookId}") { Content = new ByteArrayContent(corpo) };
        pedido.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (!semAssinatura)
        {
            pedido.Headers.TryAddWithoutValidation("X-Hub-Signature-256", assinatura ?? Assinar(corpo));
        }

        using var resposta = await Ia.Api.Client.SendAsync(pedido);
        return resposta.StatusCode;
    }

    // O que o teste olha

    /// <summary>A sessão do contato (pelo nome), esperando ela existir.</summary>
    public async Task<Guid> EsperarSessaoAsync(string nome)
    {
        Guid sessao = Guid.Empty;
        await EnsaioDeIa.AguardarAsync(
            async () =>
            {
                var pagina = await Ia.ComoSistemaAsync(sp => sp.GetRequiredService<IConversationQueries>().ListSessionsAsync(new ListSessionsQuery(50, null, null)));
                sessao = pagina.Items.FirstOrDefault(s => s.ContactDisplayName == nome)?.Id ?? Guid.Empty;
                return sessao != Guid.Empty;
            },
            $"a sessão de {nome} deveria existir");
        return sessao;
    }

    public async Task EsperarAsync(Func<Task<bool>> condicao, string descricao) => await EnsaioDeIa.AguardarAsync(condicao, descricao);

    /// <summary>O estado de entrega da mensagem de saída de uma sessão (a de autor e ordem informados), como o histórico o mostra.</summary>
    public async Task<string?> EstadoDeEntregaAsync(Guid sessao, string autor, int ordem = 0) =>
        (await Ia.MensagensAsync(sessao)).Where(m => m["author"]!.GetValue<string>() == autor).Skip(ordem).FirstOrDefault()?["deliveryState"]?.GetValue<string>();

    /// <summary>Um atendente de verdade (usuário do Identity na organização), membro da fila de atendimento, com a sessão aberta (token).</summary>
    public async Task<(UsuarioCriado Usuario, string Token)> NovoAtendenteAsync()
    {
        TenantInfo tenant;
        await using (var consulta = _banco.NovoEscopo())
        {
            tenant = await consulta.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(Ia.Slug) ?? throw new InvalidOperationException("O tenant do ensaio não existe.");
        }

        var usuario = await _banco.CriarUsuarioAsync(tenant, OrganizacaoId, Role.User);
        await Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ServiceQueueService>().AddMemberAsync(FilaId, usuario.Id, QueueMemberRole.Agent));
        return (usuario, await Ia.Api.LoginAsync(usuario));
    }

    public async ValueTask DisposeAsync()
    {
        await Ia.DisposeAsync();
        await Graph.DisposeAsync();
    }
}
