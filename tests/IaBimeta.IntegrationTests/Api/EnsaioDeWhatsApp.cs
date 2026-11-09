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
internal sealed record CapturaGraph(string Credencial, string Caminho, string Corpo, string Metodo = "POST", DateTimeOffset? Quando = null)
{
    public JsonNode Json => JsonNode.Parse(Corpo)!;

    public string Para => Json["to"]!.GetValue<string>();

    public string Texto => Json["text"]!["body"]!.GetValue<string>();

    public string? DadoOpaco => Json["biz_opaque_callback_data"]?.GetValue<string>();

    /// <summary>O tipo da mensagem do envio: <c>text</c> (livre) ou <c>template</c> (modelo aprovado).</summary>
    public string? Tipo => Json["type"]?.GetValue<string>();

    public string? NomeDoModelo => Json["template"]?["name"]?.GetValue<string>();

    public string? IdiomaDoModelo => Json["template"]?["language"]?["code"]?.GetValue<string>();

    /// <summary>Os componentes do modelo (cabeçalho, corpo, botões) com os parâmetros, como a Meta os recebeu; nulo se o modelo não tem parâmetros.</summary>
    public JsonArray? Componentes => Json["template"]?["components"]?.AsArray();

    /// <summary>O objeto da mensagem de arquivo (<c>image</c>, <c>audio</c> ou <c>document</c>, ADR 0031): o identificador do arquivo subido, a legenda e o nome; nulo se a mensagem não é de arquivo.</summary>
    public JsonNode? Arquivo => Tipo is "image" or "audio" or "document" ? Json[Tipo] : null;

    public string? IdDoArquivo => Arquivo?["id"]?.GetValue<string>();

    public string? Legenda => Arquivo?["caption"]?.GetValue<string>();

    public string? NomeDoDocumento => Arquivo?["filename"]?.GetValue<string>();
}

/// <summary>
/// Uma SUBIDA de arquivo que a "Meta" recebeu (ADR 0031): <c>POST /{versão}/{phone_number_id}/media</c>, formulário multipart com <c>messaging_product</c>, <c>type</c> e <c>file</c>. Guarda o token do cabeçalho, os
/// campos, o nome e o tipo da parte do arquivo e os bytes, e o identificador que devolveu (<see cref="Id"/>).
/// </summary>
internal sealed record SubidaDaMeta(string Credencial, string Caminho, string? Produto, string? Tipo, string? NomeDoArquivo, string? TipoDoArquivo, byte[] Conteudo, DateTimeOffset Quando)
{
    /// <summary>O identificador que a "Meta" devolveu para este arquivo (nulo se a subida foi recusada).</summary>
    public string? Id { get; set; }
}

/// <summary>
/// Um arquivo que a "Meta" tem para entregar (ADR 0028): a consulta <c>GET /{versão}/{media-id}</c> devolve os dados (endereço do conteúdo, tipo, resumo e tamanho) e o endereço devolvido serve o conteúdo. Cada
/// comportamento é uma propriedade para o teste provocar: mídia expirada, resumo errado, falha passageira, resposta presa. Guarda o que recebeu (o token do cabeçalho e quantas vezes).
/// </summary>
internal sealed class MidiaDaMeta
{
    private readonly ConcurrentQueue<string> _credenciaisDaConsulta = new();
    private readonly ConcurrentQueue<string> _credenciaisDoConteudo = new();
    private int _consultas;
    private int _downloads;
    private int _falhasRestantes;

    public required string Id { get; init; }

    public required byte[] Conteudo { get; set; }

    public string? Mime { get; set; }

    /// <summary>O resumo SHA-256 informado na consulta: nulo calcula o certo; <see cref="SemResumo"/> não informa nenhum.</summary>
    public string? Sha256 { get; set; }

    public bool SemResumo { get; set; }

    /// <summary>O estado HTTP da CONSULTA (404 é a mídia expirada ou inexistente).</summary>
    public int StatusDaConsulta { get; set; } = 200;

    /// <summary>O estado HTTP do CONTEÚDO (depois das falhas passageiras).</summary>
    public int StatusDoConteudo { get; set; } = 200;

    /// <summary>Quantas respostas 500 o conteúdo dá antes de entregar o arquivo (falha passageira do provedor).</summary>
    public int FalhasPassageirasDoConteudo
    {
        get => Volatile.Read(ref _falhasRestantes);
        set => Volatile.Write(ref _falhasRestantes, value);
    }

    /// <summary>O tamanho declarado na consulta (nulo: o real).</summary>
    public long? TamanhoDeclarado { get; set; }

    /// <summary>O endereço do conteúdo informado na consulta (nulo: o do próprio servidor).</summary>
    public string? EnderecoDoConteudo { get; set; }

    /// <summary>Roda antes de entregar o conteúdo: serve para segurar o download (o Worker cai com ele em andamento). O cancelamento é o da conexão.</summary>
    public Func<CancellationToken, Task>? AntesDoConteudo { get; set; }

    public int Consultas => Volatile.Read(ref _consultas);

    public int Downloads => Volatile.Read(ref _downloads);

    public IReadOnlyList<string> CredenciaisDaConsulta => _credenciaisDaConsulta.ToList();

    public IReadOnlyList<string> CredenciaisDoConteudo => _credenciaisDoConteudo.ToList();

    internal void RegistrarConsulta(string credencial)
    {
        Interlocked.Increment(ref _consultas);
        _credenciaisDaConsulta.Enqueue(credencial);
    }

    internal void RegistrarDownload(string credencial)
    {
        Interlocked.Increment(ref _downloads);
        _credenciaisDoConteudo.Enqueue(credencial);
    }

    /// <summary>Consome uma das falhas passageiras programadas, se ainda houver.</summary>
    internal bool ConsumirFalhaPassageira()
    {
        while (true)
        {
            var atual = Volatile.Read(ref _falhasRestantes);
            if (atual <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _falhasRestantes, atual - 1, atual) == atual)
            {
                return true;
            }
        }
    }

    internal string ResumoInformado() => SemResumo ? string.Empty : Sha256 ?? Convert.ToHexString(SHA256.HashData(Conteudo)).ToLowerInvariant();
}

/// <summary>
/// Um "servidor da Graph API" por HTTP de verdade, em porta local aleatória (loopback): recebe o <c>POST /{versão}/{phone_number_id}/messages</c> que o adaptador REAL do WhatsApp faz e responde pelo roteiro.
/// Nenhuma rede, nenhum token real. Sem roteiro, aceita a mensagem com um identificador novo a cada chamada. Também entrega arquivos de mídia (<see cref="RegistrarMidia"/>): a consulta dos dados pelo identificador e o
/// conteúdo no endereço devolvido, ambos exigindo o token da conta no cabeçalho.
/// </summary>
internal sealed class ServidorGraphLoopback : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<CapturaGraph> _capturas = new();
    private readonly ConcurrentQueue<CapturaGraph> _leituras = new();
    private readonly ConcurrentQueue<SubidaDaMeta> _subidas = new();
    private int _contadorDeSubidas;
    private readonly ConcurrentDictionary<string, MidiaDaMeta> _midias = new(StringComparer.Ordinal);
    private int _contador;

    private ServidorGraphLoopback(WebApplication app)
    {
        _app = app;
        Roteiro = _ => Aceita();
        RoteiroDeLeitura = captura => NumeroValido(captura.Caminho.Split('/', StringSplitOptions.RemoveEmptyEntries).Last());
    }

    public string EnderecoBase { get; private set; } = string.Empty;

    /// <summary>Os ENVIOS (<c>POST .../messages</c>) que chegaram. As leituras da sonda (<c>GET</c>) ficam em <see cref="Leituras"/>: um teste de prontidão nunca pode produzir um envio.</summary>
    public IReadOnlyList<CapturaGraph> Capturas => _capturas.ToList();

    /// <summary>As leituras do número (a sonda do teste de prontidão), <c>GET /{versão}/{phone_number_id}</c>.</summary>
    public IReadOnlyList<CapturaGraph> Leituras => _leituras.ToList();

    /// <summary>O que responder a cada envio: o status HTTP e o corpo.</summary>
    public Func<CapturaGraph, (int Status, string Corpo)> Roteiro { get; set; }

    /// <summary>Quando definido, responde ao envio com o <c>Retry-After</c> (em segundos) além do status e do corpo; vale no lugar de <see cref="Roteiro"/>.</summary>
    public Func<CapturaGraph, (int Status, string Corpo, int? RetryAfter)>? RoteiroComEspera { get; set; }

    /// <summary>
    /// Roda ANTES de responder a cada envio (já capturado): serve para segurar a chamada (uma resposta que nunca vem, para provar o que acontece quando o processo cai no meio da chamada). O cancelamento é o da
    /// conexão: quem derruba o chamador solta a chamada presa.
    /// </summary>
    public Func<CapturaGraph, CancellationToken, Task>? Antes { get; set; }

    /// <summary>O que responder à leitura do número (a sonda). Por padrão, o número existe e o token é aceito.</summary>
    public Func<CapturaGraph, (int Status, string Corpo)> RoteiroDeLeitura { get; set; }

    /// <summary>As SUBIDAS de arquivo (<c>POST .../media</c>, ADR 0031) que chegaram. Uma subida nunca é um envio: só a mensagem que a usa aparece em <see cref="Capturas"/>.</summary>
    public IReadOnlyList<SubidaDaMeta> Subidas => _subidas.ToList();

    /// <summary>O que responder a cada subida: o status HTTP e o corpo. Por padrão, aceita e devolve um identificador novo (<c>{"id": "..."}</c>, a forma documentada).</summary>
    public Func<SubidaDaMeta, (int Status, string Corpo)>? RoteiroDaSubida { get; set; }

    /// <summary>A espera (em segundos) que a "Meta" manda no <c>Retry-After</c> das subidas recusadas (4xx e 5xx): permite provar a espera entre as tentativas sem demorar.</summary>
    public int? EsperaDaSubida { get; set; }

    /// <summary>Roda ANTES de responder a cada subida (já capturada): serve para segurar a chamada. O cancelamento é o da conexão.</summary>
    public Func<SubidaDaMeta, CancellationToken, Task>? AntesDaSubida { get; set; }

    public static async Task<ServidorGraphLoopback> IniciarAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        var servidor = new ServidorGraphLoopback(app);
        app.MapPost("/{versao}/{numero}/messages", servidor.AtenderAsync);
        app.MapPost("/{versao}/{numero}/media", servidor.AtenderSubidaAsync);
        app.MapGet("/media-content/{id}", servidor.AtenderConteudoDeMidiaAsync);
        app.MapGet("/{versao}/{numero}", servidor.AtenderLeituraAsync);
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

    /// <summary>A resposta de uma leitura bem-sucedida do número (a forma documentada: id, display_phone_number, verified_name, quality_rating).</summary>
    public static (int Status, string Corpo) NumeroValido(string id, string nome = "Laboratório do Ensaio", string exibicao = "+55 11 3000-0000", string qualidade = "GREEN") =>
        (200, new JsonObject { ["id"] = id, ["display_phone_number"] = exibicao, ["verified_name"] = nome, ["quality_rating"] = qualidade }.ToJsonString());

    private async Task AtenderAsync(HttpContext http)
    {
        var corpo = await new StreamReader(http.Request.Body, Encoding.UTF8).ReadToEndAsync(http.RequestAborted);
        var autorizacao = http.Request.Headers.Authorization.ToString();
        var captura = new CapturaGraph(
            autorizacao.StartsWith("Bearer ", StringComparison.Ordinal) ? autorizacao[7..] : string.Empty, http.Request.Path.Value ?? string.Empty, corpo, "POST", DateTimeOffset.UtcNow);
        _capturas.Enqueue(captura);

        if (Antes is { } antes)
        {
            await antes(captura, http.RequestAborted);
        }

        int status;
        string resposta;
        if (RoteiroComEspera is { } comEspera)
        {
            var (s, r, espera) = comEspera(captura);
            (status, resposta) = (s, r);
            if (espera is { } segundos)
            {
                http.Response.Headers["Retry-After"] = segundos.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        else
        {
            (status, resposta) = Roteiro(captura);
        }

        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(resposta, http.RequestAborted);
    }

    /// <summary>A subida de um arquivo (ADR 0031): lê o formulário como a Meta o recebe, captura, e responde pelo roteiro (padrão: aceita com um identificador novo).</summary>
    private async Task AtenderSubidaAsync(HttpContext http)
    {
        var formulario = await http.Request.ReadFormAsync(http.RequestAborted);
        var arquivo = formulario.Files.GetFile("file");
        byte[] bytes = [];
        if (arquivo is not null)
        {
            await using var fluxo = arquivo.OpenReadStream();
            using var memoria = new MemoryStream();
            await fluxo.CopyToAsync(memoria, http.RequestAborted);
            bytes = memoria.ToArray();
        }

        var subida = new SubidaDaMeta(
            CredencialDe(http), http.Request.Path.Value ?? string.Empty, formulario["messaging_product"], formulario["type"], arquivo?.FileName, arquivo?.ContentType, bytes, DateTimeOffset.UtcNow);
        _subidas.Enqueue(subida);

        if (AntesDaSubida is { } antes)
        {
            await antes(subida, http.RequestAborted);
        }

        var (status, resposta) = RoteiroDaSubida is { } roteiro
            ? roteiro(subida)
            : (200, new JsonObject { ["id"] = $"UPLOAD{Interlocked.Increment(ref _contadorDeSubidas):D4}" }.ToJsonString());
        if (status is >= 200 and < 300 && JsonNode.Parse(resposta)?["id"]?.GetValue<string>() is { } id)
        {
            subida.Id = id;
        }

        if (status >= 400 && EsperaDaSubida is { } segundos)
        {
            http.Response.Headers["Retry-After"] = segundos.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(resposta, http.RequestAborted);
    }

    /// <summary>Registra um arquivo que a "Meta" tem: a consulta pelo identificador e o conteúdo passam a ser atendidos. O <paramref name="mime"/> é o tipo informado na consulta e no conteúdo.</summary>
    public MidiaDaMeta RegistrarMidia(string id, byte[] conteudo, string? mime)
    {
        var midia = new MidiaDaMeta { Id = id, Conteudo = conteudo, Mime = mime };
        _midias[id] = midia;
        return midia;
    }

    private static string CredencialDe(HttpContext http)
    {
        var autorizacao = http.Request.Headers.Authorization.ToString();
        return autorizacao.StartsWith("Bearer ", StringComparison.Ordinal) ? autorizacao[7..] : string.Empty;
    }

    private static async Task ResponderErroAsync(HttpContext http, int status)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(Erro(status, status == 404 ? 100 : 1).Corpo, http.RequestAborted);
    }

    /// <summary>A consulta dos dados da mídia: sem o token, 401; mídia desconhecida ou expirada, 404.</summary>
    private async Task AtenderConsultaDeMidiaAsync(HttpContext http, MidiaDaMeta midia)
    {
        var credencial = CredencialDe(http);
        midia.RegistrarConsulta(credencial);
        if (credencial.Length == 0)
        {
            await ResponderErroAsync(http, 401);
            return;
        }

        if (midia.StatusDaConsulta != 200)
        {
            await ResponderErroAsync(http, midia.StatusDaConsulta);
            return;
        }

        var dados = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["url"] = midia.EnderecoDoConteudo ?? $"{EnderecoBase}media-content/{midia.Id}",
            ["mime_type"] = midia.Mime,
            ["file_size"] = (midia.TamanhoDeclarado ?? midia.Conteudo.Length).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["id"] = midia.Id,
        };
        if (!midia.SemResumo)
        {
            dados["sha256"] = midia.ResumoInformado();
        }

        http.Response.StatusCode = 200;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(dados.ToJsonString(), http.RequestAborted);
    }

    /// <summary>O conteúdo da mídia: exige o token da conta no cabeçalho (como a Meta), dá as falhas passageiras programadas e entrega os bytes.</summary>
    private async Task AtenderConteudoDeMidiaAsync(HttpContext http, string id)
    {
        if (!_midias.TryGetValue(id, out var midia))
        {
            await ResponderErroAsync(http, 404);
            return;
        }

        var credencial = CredencialDe(http);
        midia.RegistrarDownload(credencial);
        if (credencial.Length == 0)
        {
            await ResponderErroAsync(http, 401);
            return;
        }

        if (midia.AntesDoConteudo is { } antes)
        {
            await antes(http.RequestAborted);
        }

        if (midia.ConsumirFalhaPassageira())
        {
            await ResponderErroAsync(http, 500);
            return;
        }

        if (midia.StatusDoConteudo != 200)
        {
            await ResponderErroAsync(http, midia.StatusDoConteudo);
            return;
        }

        http.Response.StatusCode = 200;
        http.Response.ContentType = midia.Mime ?? "application/octet-stream";
        http.Response.ContentLength = midia.Conteudo.Length;
        await http.Response.Body.WriteAsync(midia.Conteudo, http.RequestAborted);
    }

    private async Task AtenderLeituraAsync(HttpContext http)
    {
        // O mesmo caminho serve a consulta de uma mídia (o identificador é o último trecho) e a sonda do número: mídia registrada é mídia.
        var ultimo = (http.Request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
        if (_midias.TryGetValue(ultimo, out var registrada))
        {
            await AtenderConsultaDeMidiaAsync(http, registrada);
            return;
        }

        var autorizacao = http.Request.Headers.Authorization.ToString();
        var captura = new CapturaGraph(
            autorizacao.StartsWith("Bearer ", StringComparison.Ordinal) ? autorizacao[7..] : string.Empty, http.Request.Path.Value ?? string.Empty, string.Empty, "GET", DateTimeOffset.UtcNow);
        _leituras.Enqueue(captura);

        var (status, resposta) = RoteiroDeLeitura(captura);
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

    public Guid ContaId { get; private set; }

    public Guid InstanciaId { get; private set; }

    /// <param name="configuracao">Configuração a mais para o Api e o Worker (por exemplo, a vazão e o endereço público do webhook).</param>
    public static async Task<EnsaioDeWhatsApp> IniciarAsync(PostgresFixture banco, string prefixo, IReadOnlyDictionary<string, string?>? configuracao = null)
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

        foreach (var (chave, valor) in configuracao ?? new Dictionary<string, string?>())
        {
            adicionais[chave] = valor;
        }

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
            ContaId = conta.Id;
            var instancia = await instancias.RegisterAsync(conta.Id, $"Número {sufixo}", NumeroDoCanal, "+55 11 3000-0000", null);
            InstanciaId = instancia.Id;
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

    /// <summary>O corpo de um webhook de mensagem de texto, na forma da Cloud API. <paramref name="momento"/> é o relógio do provedor (padrão: agora): uma mensagem antiga fecha a janela de 24 horas.</summary>
    public byte[] PayloadDeTexto(string idDaMensagem, string waId, string nome, string texto, string? numero = null, DateTimeOffset? momento = null) =>
        MontarPayloadDeTexto(ContaDoProvedor, numero ?? NumeroDoCanal, idDaMensagem, waId, nome, texto, momento);

    /// <summary>O mesmo corpo, sem depender de um ensaio (a conta e o número vêm do chamador).</summary>
    public static byte[] MontarPayloadDeTexto(string conta, string numero, string idDaMensagem, string waId, string nome, string texto, DateTimeOffset? momento = null) =>
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
                    ["timestamp"] = (momento ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(),
                    ["type"] = "text",
                    ["text"] = new JsonObject { ["body"] = texto },
                }),
            }));

    /// <summary>
    /// O corpo de um webhook de mensagem de MÍDIA (<c>image</c>, <c>audio</c> ou <c>document</c>), na forma da Cloud API: o objeto do tipo traz o <c>id</c> da mídia, o tipo, o resumo, e conforme o caso a legenda,
    /// o nome do arquivo e o <c>voice</c>. Nenhum arquivo vai no webhook: o Channels o busca depois, pelo identificador.
    /// </summary>
    public byte[] PayloadDeMidia(string tipo, string idDaMensagem, string waId, string nome, string idDaMidia, string mime, string? legenda = null, string? nomeDoArquivo = null, bool? voz = null, string? numero = null)
    {
        var midia = new JsonObject { ["mime_type"] = mime, ["sha256"] = "resumo-do-webhook-nao-e-lido", ["id"] = idDaMidia };
        if (legenda is not null)
        {
            midia["caption"] = legenda;
        }

        if (nomeDoArquivo is not null)
        {
            midia["filename"] = nomeDoArquivo;
        }

        if (voz is { } eVoz)
        {
            midia["voice"] = eVoz;
        }

        return Encoding.UTF8.GetBytes(Envelope(
            ContaDoProvedor,
            new JsonObject
            {
                ["messaging_product"] = "whatsapp",
                ["metadata"] = new JsonObject { ["display_phone_number"] = "551130000000", ["phone_number_id"] = numero ?? NumeroDoCanal },
                ["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = nome }, ["wa_id"] = waId }),
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["from"] = waId,
                    ["id"] = idDaMensagem,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                    ["type"] = tipo,
                    [tipo] = midia,
                }),
            }));
    }

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

    /// <summary>Um administrador (ou o papel informado) da organização do canal, com a sessão aberta (token).</summary>
    public async Task<string> TokenDeAsync(Role papel)
    {
        TenantInfo tenant;
        await using (var consulta = _banco.NovoEscopo())
        {
            tenant = await consulta.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(Ia.Slug) ?? throw new InvalidOperationException("O tenant do ensaio não existe.");
        }

        var usuario = await _banco.CriarUsuarioAsync(tenant, OrganizacaoId, papel);
        return await Ia.Api.LoginAsync(usuario);
    }

    /// <summary>Uma chamada HTTP ao Api como o usuário do token (corpo JSON opcional). Devolve o status e o JSON da resposta (nulo se não houver corpo JSON).</summary>
    public async Task<(HttpStatusCode Status, JsonNode? Corpo)> ChamarAsync(string token, HttpMethod metodo, string caminho, JsonNode? corpo = null)
    {
        using var pedido = Ia.Api.RequisicaoDe(token, metodo, caminho, corpo is null ? null : new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json"));
        using var resposta = await Ia.Api.Client.SendAsync(pedido);
        var texto = await resposta.Content.ReadAsStringAsync();
        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(texto) && resposta.Content.Headers.ContentType?.MediaType is "application/json" or "application/problem+json")
        {
            json = JsonNode.Parse(texto);
        }

        return (resposta.StatusCode, json);
    }

    /// <summary>Responde com um ARQUIVO (ADR 0031) como a interface faz: formulário multipart com o <c>requestId</c>, o arquivo (nome e tipo informados pelo navegador) e a legenda. Devolve o status e o JSON.</summary>
    public async Task<(HttpStatusCode Status, JsonNode? Corpo)> EnviarArquivoAsync(
        string token, string itemId, byte[] conteudo, string nome, string? tipo, string? legenda = null, Guid? pedido = null)
    {
        using var formulario = new MultipartFormDataContent();
        formulario.Add(new StringContent((pedido ?? Guid.NewGuid()).ToString()), "requestId");
        if (legenda is not null)
        {
            formulario.Add(new StringContent(legenda, Encoding.UTF8), "caption");
        }

        var arquivo = new ByteArrayContent(conteudo);
        if (tipo is not null)
        {
            arquivo.Headers.ContentType = new MediaTypeHeaderValue(tipo);
        }

        formulario.Add(arquivo, "file", nome);

        using var requisicao = Ia.Api.RequisicaoDe(token, HttpMethod.Post, $"/contact-center/items/{itemId}/reply-media", formulario);
        using var resposta = await Ia.Api.Client.SendAsync(requisicao);
        var texto = await resposta.Content.ReadAsStringAsync();
        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(texto) && resposta.Content.Headers.ContentType?.MediaType is "application/json" or "application/problem+json")
        {
            json = JsonNode.Parse(texto);
        }

        return (resposta.StatusCode, json);
    }

    /// <summary>Espera o estado de entrega da mensagem de saída de uma sessão chegar ao valor.</summary>
    public Task EsperarEntregaAsync(Guid sessao, string estado, string autor = "Ai", int ordem = 0, int segundos = 30) =>
        EnsaioDeIa.AguardarAsync(async () => await EstadoDeEntregaAsync(sessao, autor, ordem) == estado, $"a mensagem de {autor} deveria ficar em {estado}", segundos);

    public async ValueTask DisposeAsync()
    {
        await Ia.DisposeAsync();
        await Graph.DisposeAsync();
    }
}
