using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// Um tenant com uma organização e um canal, e atalhos para as chamadas que o Channels e o Agents fariam. Cada chamada
/// abre o próprio escopo (uma "requisição") com o contexto de SISTEMA do tenant, como o worker de ingestão faria, e usa a
/// mesma composição de DI do host, com PostgreSQL real.
/// </summary>
internal sealed class CenarioDeConversas
{
    private readonly PostgresFixture _banco;

    private enum Canais
    {
        Dubles,
        Reais,
        Indisponiveis,
    }

    private readonly Canais _canais;

    private CenarioDeConversas(PostgresFixture banco, TenantInfo tenant, Guid organizacaoId, Canais canais)
    {
        _banco = banco;
        Tenant = tenant;
        OrganizacaoId = organizacaoId;
        _canais = canais;
    }

    public TenantInfo Tenant { get; }

    public Guid OrganizacaoId { get; }

    public ChannelInstanceId CanalId { get; } = ChannelInstanceId.New();

    /// <param name="canaisReais">
    /// Falso (padrão): a autorização de unidade por canal é um dublê que aceita tudo, e o <see cref="CanalId"/> é só um
    /// identificador. Verdadeiro: é a implementação do host sobre o Channels, e só as unidades que o cadastro do canal autoriza
    /// são aceitas (use <see cref="CriarCanalAsync"/> para cadastrar o número).
    /// </param>
    public static async Task<CenarioDeConversas> CriarAsync(PostgresFixture banco, string prefixo, bool canaisReais = false)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var organizacao = await banco.CriarOrganizacaoAsync(tenant, $"Organização {prefixo}");
        return new CenarioDeConversas(banco, tenant, organizacao.Id, canaisReais ? Canais.Reais : Canais.Dubles);
    }

    /// <summary>
    /// O mesmo tenant e a mesma organização, mas com o banco do Channels fora do ar: toda pergunta de autorização de unidade
    /// falha. As demais operações (Conversations, Tenancy, Audit) continuam funcionando.
    /// </summary>
    public CenarioDeConversas ComCanaisIndisponiveis() => new(_banco, Tenant, OrganizacaoId, Canais.Indisponiveis);

    private AsyncServiceScope NovoEscopo(RequestContext? contexto) => _canais switch
    {
        Canais.Reais => _banco.NovoEscopoComCanaisReais(contexto),
        Canais.Indisponiveis => _banco.NovoEscopoComCanaisIndisponiveis(contexto),
        _ => _banco.NovoEscopo(contexto),
    };

    private InMemoryEventPublisher Eventos => _canais switch
    {
        Canais.Reais => _banco.EventosComCanaisReais,
        Canais.Indisponiveis => _banco.EventosComCanaisIndisponiveis,
        _ => _banco.Eventos,
    };

    /// <summary>Quantos eventos deste tenant o dublê em memória recebeu, de qualquer tipo.</summary>
    public int TotalDeEventosDoTenant => Eventos.Published.Count(e => e.TenantId == Tenant.TenantId);

    /// <summary>
    /// Cadastra um número (conta, instância, unidades autorizadas, padrões e ativação) como um administrador faria, e devolve o
    /// identificador da instância. As unidades precisam existir (<see cref="CriarUnidadeAsync"/>).
    /// </summary>
    public async Task<ChannelInstanceId> CriarCanalAsync(
        IEnumerable<Guid>? unidadesAutorizadas = null,
        Guid? unidadePadrao = null,
        Guid? setorPadrao = null,
        bool ativar = true,
        Guid? organizacao = null,
        string? idiomaPadrao = null)
    {
        await using var escopo = NovoEscopo(ContextoDeSistema);
        var contas = escopo.ServiceProvider.GetRequiredService<IaBimeta.Channels.Application.ChannelAccountService>();
        var instancias = escopo.ServiceProvider.GetRequiredService<IaBimeta.Channels.Application.ChannelInstanceService>();

        var sufixo = Guid.NewGuid().ToString("N")[..12];
        var conta = await contas.CreateAsync(organizacao ?? OrganizacaoId, IaBimeta.Channels.Contracts.ChannelProvider.WhatsAppCloud, $"waba-{sufixo}", $"Conta {sufixo}");
        var instancia = await instancias.RegisterAsync(
            conta.Id, $"Número {sufixo}", $"num-{sufixo}", null, idiomaPadrao is null ? null : Locale.Parse(idiomaPadrao));
        foreach (var unidade in unidadesAutorizadas ?? Array.Empty<Guid>())
        {
            await instancias.AuthorizeUnitAsync(instancia.Id, unidade);
        }

        if (unidadePadrao is not null)
        {
            await instancias.SetDefaultsAsync(instancia.Id, unidadePadrao, setorPadrao);
        }

        if (ativar)
        {
            await instancias.ActivateAsync(instancia.Id);
        }

        return new ChannelInstanceId(instancia.Id);
    }

    public RequestContext ContextoDeSistema => PostgresFixture.Contexto(Tenant);

    public async Task<IngestInboundMessageResult> EntrarAsync(
        string contato,
        string? texto = "Olá",
        string? evento = null,
        DateTimeOffset? quando = null,
        MessageKind tipo = MessageKind.Text,
        Guid? organizacao = null,
        Guid? unidade = null,
        ChannelInstanceId? canal = null,
        string? idiomaDoContato = null,
        string? idiomaDaMensagem = null,
        Guid? setor = null,
        string? idiomaPadraoDoCanal = null,
        HandlingMode modoInicial = HandlingMode.AiAutonomous)
    {
        await using var escopo = NovoEscopo(ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IConversationIngestion>().IngestInboundMessageAsync(new IngestInboundMessageCommand(
            canal ?? CanalId,
            contato,
            null,
            organizacao ?? OrganizacaoId,
            unidade,
            evento ?? $"evt-{Guid.NewGuid():N}",
            quando ?? DateTimeOffset.UtcNow,
            tipo,
            texto,
            idiomaDoContato is null ? null : Locale.Parse(idiomaDoContato),
            idiomaDaMensagem is null ? null : Locale.Parse(idiomaDaMensagem),
            setor,
            idiomaPadraoDoCanal is null ? null : Locale.Parse(idiomaPadraoDoCanal),
            modoInicial));
    }

    // Unidades, setores e encaminhamento

    public async Task<Guid> CriarUnidadeAsync(Guid organizacao, string nome, bool matriz = false)
    {
        await using var escopo = NovoEscopo(ContextoDeSistema);
        var criada = await escopo.ServiceProvider.GetRequiredService<BusinessUnitService>()
            .CreateAsync(organizacao, nome, matriz ? IaBimeta.Tenancy.Domain.BusinessUnitKind.Headquarters : IaBimeta.Tenancy.Domain.BusinessUnitKind.Branch);
        return criada.Id;
    }

    public async Task<Guid> CriarSetorAsync(Guid unidade, string nome)
    {
        await using var escopo = NovoEscopo(ContextoDeSistema);
        return (await escopo.ServiceProvider.GetRequiredService<DepartmentService>().CreateAsync(unidade, nome)).Id;
    }

    public async Task<TransferSessionResult> EncaminharAsync(Guid sessao, Guid unidadeDestino, Guid? setorDestino, long versaoEsperada, Guid? transferId = null, RequestContext? contexto = null, string motivo = "specialty-unit")
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IHandlingCommands>().TransferSessionAsync(
            new TransferSessionCommand(sessao, unidadeDestino, setorDestino, versaoEsperada, transferId ?? Guid.NewGuid(), motivo));
    }

    public async Task<IReadOnlyList<SessionTransferView>?> RastroAsync(Guid sessao, RequestContext? contexto = null)
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IConversationQueries>().GetTransfersAsync(sessao);
    }

    // Idioma

    /// <summary>Define o idioma padrão do tenant, como o administrador do tenant faria (contexto de sistema do teste).</summary>
    public async Task DefinirIdiomaPadraoDoTenantAsync(string idioma)
    {
        await using var escopo = NovoEscopo(ContextoDeSistema);
        await escopo.ServiceProvider.GetRequiredService<TenantSettingsService>().SetDefaultLocaleAsync(idioma);
    }

    public async Task<SetLocaleResult> AlterarIdiomaAsync(Guid sessao, string idioma, long versaoEsperada, RequestContext? contexto = null)
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IHandlingCommands>().SetConversationLocaleAsync(
            new SetConversationLocaleCommand(sessao, Locale.Parse(idioma), versaoEsperada));
    }

    public async Task<SetContactLocaleResult> DefinirPreferenciaDoContatoAsync(Guid sessao, string? idioma, RequestContext? contexto = null)
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IHandlingCommands>().SetContactPreferredLocaleAsync(
            new SetContactPreferredLocaleCommand(sessao, idioma is null ? null : Locale.Parse(idioma)));
    }

    public async Task<RecordTranslationResult> TraduzirAsync(Guid mensagem, string idioma, string texto, string origem = "human", RequestContext? contexto = null)
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IConversationIngestion>().RecordMessageTranslationAsync(
            new RecordMessageTranslationCommand(mensagem, Locale.Parse(idioma), texto, origem));
    }

    public async Task<IReadOnlyList<MessageTranslationView>?> TraducoesAsync(Guid sessao, Guid mensagem, RequestContext? contexto = null)
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IConversationQueries>().GetMessageTranslationsAsync(sessao, mensagem);
    }

    public async Task<BeginTurnResult> IniciarTurnoAsync(Guid sessao, Guid turno, long lease = 1, int mensagens = 50)
    {
        await using var escopo = NovoEscopo(ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<ITurnCommands>().BeginTurnAsync(new BeginTurnCommand(sessao, turno, lease, mensagens));
    }

    /// <summary>Responde exatamente como o grant mandou: época, versão do estado e cerca de entrada do snapshot.</summary>
    public async Task<RegisterAiReplyResult> ResponderAsync(
        Guid sessao,
        Guid turno,
        BeginTurnResult inicio,
        Guid conversa,
        string texto = "Sim, são 8 horas de jejum.",
        ReplyKind tipo = ReplyKind.Generated)
    {
        var grant = inicio.Grant ?? throw new InvalidOperationException("O turno não foi concedido.");
        return await ResponderComAsync(new RegisterAiReplyCommand(
            sessao, turno, grant.Epoch, grant.StateVersion, grant.BasedOnInboundSeq, tipo, conversa, texto, new[] { Guid.NewGuid() }));
    }

    public async Task<RegisterAiReplyResult> ResponderComAsync(RegisterAiReplyCommand comando, RequestContext? contexto = null)
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<ITurnCommands>().RegisterAiReplyAsync(comando);
    }

    public async Task<EndTurnResult> EncerrarTurnoAsync(Guid sessao, Guid turno, long epoca, TurnEndReason motivo = TurnEndReason.Discarded)
    {
        await using var escopo = NovoEscopo(ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<ITurnCommands>().EndTurnAsync(new EndTurnCommand(sessao, turno, epoca, motivo));
    }

    public async Task<CancelTurnResult> CancelarTurnoAsync(Guid sessao, string motivo = "manual")
    {
        await using var escopo = NovoEscopo(ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<ITurnCommands>().CancelTurnAsync(new CancelTurnCommand(sessao, motivo));
    }

    public async Task<TransitionResult> TransicionarAsync(Guid sessao, HandlingMode para, Guid? responsavel, long versaoEsperada, Guid? transicao = null, RequestContext? contexto = null)
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IHandlingCommands>().TransitionHandlingModeAsync(
            new TransitionHandlingModeCommand(sessao, para, responsavel, versaoEsperada, transicao ?? Guid.NewGuid(), "manual"));
    }

    public async Task<CloseSessionResult> EncerrarSessaoAsync(Guid sessao, RequestContext? contexto = null)
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IHandlingCommands>().CloseSessionAsync(new CloseSessionCommand(sessao, "manual"));
    }

    public async Task<RequestResendResult> PedirReenvioAsync(Guid mensagem, Guid pedido, RequestContext? contexto = null)
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IHandlingCommands>().RequestResendAsync(new RequestResendCommand(mensagem, pedido));
    }

    public async Task<Page<SessionView>> ListarSessoesAsync(RequestContext contexto, int take = 50, string? cursor = null, SessionStatus? status = null)
    {
        await using var escopo = NovoEscopo(contexto);
        return await escopo.ServiceProvider.GetRequiredService<IConversationQueries>().ListSessionsAsync(new ListSessionsQuery(take, cursor, status));
    }

    /// <summary>Roda uma ação num escopo próprio (uma "requisição") com o contexto informado, ou sem contexto nenhum se for nulo.</summary>
    public async Task<T> ComoAsync<T>(RequestContext? contexto, Func<IServiceProvider, Task<T>> acao)
    {
        await using var escopo = NovoEscopo(contexto);
        return await acao(escopo.ServiceProvider);
    }

    public async Task<DeliveryStatusResult> EntregaAsync(Guid mensagem, DeliveryState estado, string? motivo = null)
    {
        await using var escopo = NovoEscopo(ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IConversationIngestion>().RecordDeliveryStatusAsync(
            new RecordDeliveryStatusCommand(mensagem, estado, motivo));
    }

    public async Task<SessionView?> SessaoAsync(Guid sessao, RequestContext? contexto = null)
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IConversationQueries>().GetSessionAsync(sessao);
    }

    public async Task<Page<MessageView>?> HistoricoAsync(Guid sessao, int take = 100, string? cursor = null, HistoryOrder ordem = HistoryOrder.OldestFirst, RequestContext? contexto = null)
    {
        await using var escopo = NovoEscopo(contexto ?? ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IConversationQueries>().GetHistoryAsync(new GetHistoryQuery(sessao, take, cursor, ordem));
    }

    /// <summary>Um usuário autenticado com a permissão de leitura de conversas na organização informada (e só nela).</summary>
    public RequestContext Usuario(Guid? organizacao, params Permission[] permissoes) => PostgresFixture.ContextoDeUsuario(
        Tenant,
        Guid.NewGuid(),
        permissoes.Select(p => new PermissionGrant(p, organizacao)).ToArray());

    /// <summary>Quantos eventos deste tenant e deste tipo o dublê em memória recebeu para a sessão.</summary>
    public int EventosDe(string tipo, Guid sessao) =>
        Eventos.Published.Count(e => e.TenantId == Tenant.TenantId && e.Type == tipo && e.OrderingKey == $"session:{sessao}");

    public IReadOnlyList<object> PayloadsDe(string tipo, Guid sessao) =>
        Eventos.Published.Where(e => e.TenantId == Tenant.TenantId && e.Type == tipo && e.OrderingKey == $"session:{sessao}").Select(e => e.Payload).ToList();
}
