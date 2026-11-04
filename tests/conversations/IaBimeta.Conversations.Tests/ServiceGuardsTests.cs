using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// As recusas acontecem ANTES de qualquer acesso ao armazenamento: um armazenamento que explode se for tocado prova que
/// requisição sem identidade, sem autorização ou com dado inválido não chega a ler nem a gravar nada. O comportamento com
/// persistência real é provado com PostgreSQL em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
public class ServiceGuardsTests
{
    private sealed class StoreProibido : IConversationStore
    {
        private static Exception Tocado() => new InvalidOperationException("O armazenamento foi tocado, e não podia ser.");

        public Task<IConversationUnitOfWork> BeginAsync(CancellationToken cancellationToken) => throw Tocado();

        public Task<SessionRow?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw Tocado();

        public Task<IReadOnlyList<SessionRow>> ListSessionsAsync(SessionListFilter filter, CancellationToken cancellationToken) => throw Tocado();

        public Task<IReadOnlyList<Message>> ListMessagesAsync(Guid sessionId, MessagePageFilter filter, CancellationToken cancellationToken) => throw Tocado();

        public Task<Message?> FindMessageByProviderEventAsync(ChannelInstanceId channelInstanceId, string providerEventId, CancellationToken cancellationToken) => throw Tocado();

        public Task<Message?> FindMessageAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken) => throw Tocado();

        public Task<IReadOnlyList<MessageTranslation>> ListTranslationsAsync(Guid messageId, CancellationToken cancellationToken) => throw Tocado();

        public Task<IReadOnlyList<SessionTransfer>> ListTransfersAsync(Guid sessionId, CancellationToken cancellationToken) => throw Tocado();

        public Task<HandlingTransition?> FindTransitionReadOnlyAsync(Guid sessionId, Guid transitionId, CancellationToken cancellationToken) => throw Tocado();

        public Task<SessionTransfer?> FindTransferReadOnlyAsync(Guid sessionId, Guid transferId, CancellationToken cancellationToken) => throw Tocado();
    }

    private sealed class ConfiguracoesDoTenant : ITenantSettings
    {
        public Task<Locale> GetDefaultLocaleAsync(CancellationToken cancellationToken = default) => Task.FromResult(Locale.Parse("pt-BR"));
    }

    private sealed class Organizacoes : IOrganizationDirectory
    {
        public Task<bool> ExistsAsync(Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class Unidades : IBusinessUnitDirectory
    {
        public Task<Guid?> FindOrganizationIdAsync(Guid businessUnitId, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
    }

    private sealed class Setores : IDepartmentDirectory
    {
        public Task<Guid?> FindBusinessUnitIdAsync(Guid departmentId, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
    }

    private sealed class AuditoriaNula : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteForAuthenticationAsync(TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static readonly TenantId Tenant = new(Guid.NewGuid());

    private sealed class Montagem
    {
        public RequestContextHolder Holder { get; } = new();

        public InboundMessageService Ingestao => new(new StoreProibido(), new Organizacoes(), new Unidades(), new Setores(), new ConfiguracoesDoTenant(), Holder, TimeProvider.System, new DenyAllChannelUnitAuthorization());
        public TurnService Turnos => new(new StoreProibido(), Holder, TimeProvider.System);
        public HandlingService Atendimento => new(new StoreProibido(), Holder, TimeProvider.System, new AuditoriaNula(), new Unidades(), new Setores(), new DenyAllChannelUnitAuthorization());
        public ConversationQueryService Consultas => new(new StoreProibido(), Holder);

        public void ComoSistema() => Holder.Set(new RequestContext(Tenant, "development-header"));

        /// <summary>Um usuário com TODAS as permissões de usuário: ainda assim, comandos de máquina lhe são vedados.</summary>
        public void ComoUsuarioComTudo() => Holder.Set(new RequestContext(
            Tenant,
            "session",
            new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), Enum.GetValues<Permission>().Where(p => p != Permission.SystemOperation).Select(p => new PermissionGrant(p, null)))));

        public void ComoUsuarioSemLeitura() => Holder.Set(new RequestContext(
            Tenant, "session", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), new[] { new PermissionGrant(Permission.OrganizationRead, null) })));
    }

    private static IngestInboundMessageCommand Entrada() => new(
        ChannelInstanceId.New(), "5511999990001", "Mauro", Guid.NewGuid(), null, "wamid.1", DateTimeOffset.UtcNow, MessageKind.Text, "Oi");

    private static async Task SemContexto(Montagem m)
    {
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Ingestao.IngestInboundMessageAsync(Entrada()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Ingestao.SetMessageContentAsync(new SetMessageContentCommand(id, "texto")));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Ingestao.FailMessageContentAsync(new FailMessageContentCommand(id, "timeout")));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(id, DeliveryState.Sent, null)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Turnos.BeginTurnAsync(new BeginTurnCommand(id, id, 1)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Turnos.EndTurnAsync(new EndTurnCommand(id, id, 1, TurnEndReason.Cancelled)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Turnos.CancelTurnAsync(new CancelTurnCommand(id, "manual")));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Turnos.RegisterAiReplyAsync(new RegisterAiReplyCommand(id, id, 1, 1, 1, ReplyKind.Generated, id, "oi", Array.Empty<Guid>())));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Turnos.RaiseSessionAttentionAsync(new RaiseSessionAttentionCommand(id, SessionAttentionReason.ChattyBurst, 10, 3)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Atendimento.TransitionHandlingModeAsync(new TransitionHandlingModeCommand(id, HandlingMode.HumanAssisted, null, 1, id, "manual")));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Atendimento.CloseSessionAsync(new CloseSessionCommand(id, "manual")));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Atendimento.RequestResendAsync(new RequestResendCommand(id, id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Consultas.GetSessionAsync(id));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Consultas.ListSessionsAsync(new ListSessionsQuery(10, null, null)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Consultas.GetHistoryAsync(new GetHistoryQuery(id, 10, null, HistoryOrder.NewestFirst)));
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_toda_operacao_falha_fechada_sem_tocar_o_armazenamento()
    {
        await SemContexto(new Montagem());
    }

    [Fact]
    public async Task Usuario_autenticado_nunca_chama_comando_de_maquina_nem_com_todas_as_permissoes()
    {
        var m = new Montagem();
        m.ComoUsuarioComTudo();
        var id = Guid.NewGuid();

        var recusas = new Func<Task>[]
        {
            () => m.Ingestao.IngestInboundMessageAsync(Entrada()),
            () => m.Ingestao.SetMessageContentAsync(new SetMessageContentCommand(id, "texto")),
            () => m.Ingestao.FailMessageContentAsync(new FailMessageContentCommand(id, "timeout")),
            () => m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(id, DeliveryState.Sent, null)),
            () => m.Turnos.BeginTurnAsync(new BeginTurnCommand(id, id, 1)),
            () => m.Turnos.EndTurnAsync(new EndTurnCommand(id, id, 1, TurnEndReason.Cancelled)),
            () => m.Turnos.CancelTurnAsync(new CancelTurnCommand(id, "manual")),
            () => m.Turnos.RegisterAiReplyAsync(new RegisterAiReplyCommand(id, id, 1, 1, 1, ReplyKind.Generated, id, "resposta forjada", Array.Empty<Guid>())),
            () => m.Turnos.RaiseSessionAttentionAsync(new RaiseSessionAttentionCommand(id, SessionAttentionReason.ChattyBurst, 10, 3)),
        };

        foreach (var chamada in recusas)
        {
            var falha = await Assert.ThrowsAsync<AccessDeniedException>(chamada);
            Assert.Equal(Permission.SystemOperation, falha.Permission);
        }
    }

    [Fact]
    public async Task Contexto_de_sistema_nao_e_barrado_pela_permissao_de_maquina()
    {
        // O contexto de sistema passa pela cerca de permissão: o que acontece depois é do armazenamento, que aqui explode.
        var m = new Montagem();
        m.ComoSistema();

        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Turnos.BeginTurnAsync(new BeginTurnCommand(Guid.NewGuid(), Guid.NewGuid(), 1)));
    }

    [Fact]
    public async Task Dado_invalido_e_recusado_antes_de_qualquer_acesso_ao_armazenamento()
    {
        var m = new Montagem();
        m.ComoSistema();
        var valido = Entrada();

        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.IngestInboundMessageAsync(valido with { Text = " " }));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.IngestInboundMessageAsync(valido with { ContactExternalRef = "" }));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.IngestInboundMessageAsync(valido with { ChannelInstanceId = default }));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.IngestInboundMessageAsync(valido with { ProviderTimestamp = default }));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.IngestInboundMessageAsync(valido with { Kind = (MessageKind)99 }));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.SetMessageContentAsync(new SetMessageContentCommand(Guid.NewGuid(), " ")));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.FailMessageContentAsync(new FailMessageContentCommand(Guid.NewGuid(), "Texto Livre com senha")));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), (DeliveryState)99, null)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), DeliveryState.Failed, null)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Turnos.BeginTurnAsync(new BeginTurnCommand(Guid.NewGuid(), Guid.Empty, 1)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Turnos.BeginTurnAsync(new BeginTurnCommand(Guid.NewGuid(), Guid.NewGuid(), -1)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Turnos.CancelTurnAsync(new CancelTurnCommand(Guid.NewGuid(), "Sem Padrao")));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Turnos.EndTurnAsync(new EndTurnCommand(Guid.NewGuid(), Guid.NewGuid(), 1, (TurnEndReason)99)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Turnos.RaiseSessionAttentionAsync(new RaiseSessionAttentionCommand(Guid.NewGuid(), SessionAttentionReason.ChattyBurst, -1, 0)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Atendimento.TransitionHandlingModeAsync(new TransitionHandlingModeCommand(Guid.NewGuid(), HandlingMode.HumanAssisted, null, 1, Guid.Empty, "manual")));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Atendimento.TransitionHandlingModeAsync(new TransitionHandlingModeCommand(Guid.NewGuid(), HandlingMode.HumanAssisted, null, 1, Guid.NewGuid(), "Motivo Livre")));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Atendimento.RequestResendAsync(new RequestResendCommand(Guid.NewGuid(), Guid.Empty)));
    }

    [Fact]
    public async Task Reenvio_nunca_nasce_de_contexto_de_sistema_pois_exige_o_autor_humano_autenticado()
    {
        var m = new Montagem();
        m.ComoSistema();

        var resultado = await m.Atendimento.RequestResendAsync(new RequestResendCommand(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(ResendOutcome.NotAuthorized, resultado.Outcome);
        Assert.Null(resultado.NewMessageId);
    }

    // Metadados do canal (ChannelData) e evidência de entrega (EvidenceSource): fatia A1. Tudo é recusado ANTES de qualquer acesso ao armazenamento.

    private static ChannelData DadosDoCanal() => new(1, new Dictionary<string, string> { ["media.kind"] = "image", ["media.mime"] = "image/jpeg" });

    [Fact]
    public async Task ChannelData_valido_e_recusado_como_nao_suportado_e_nunca_descartado_em_silencio()
    {
        var m = new Montagem();
        m.ComoSistema();

        // O armazenamento explodiria se fosse tocado: a recusa é uma ConversationsValidationException, e nada foi lido nem gravado.
        var falha = await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.IngestInboundMessageAsync(Entrada() with { ChannelData = DadosDoCanal() }));

        Assert.Contains("ainda não são suportados", falha.Message);
        Assert.Contains("não foi registrada", falha.Message);
    }

    [Fact]
    public async Task ChannelData_invalido_e_recusado_pela_forma_e_nao_pela_falta_de_suporte()
    {
        var m = new Montagem();
        m.ComoSistema();
        var invalidos = new[]
        {
            new ChannelData(0, new Dictionary<string, string>()),
            new ChannelData(1, new Dictionary<string, string> { ["Chave Invalida"] = "v" }),
            new ChannelData(1, new Dictionary<string, string> { ["k"] = new string('v', ConversationLimits.ChannelDataValueMaxLength + 1) }),
            new ChannelData(1, Enumerable.Range(0, ConversationLimits.ChannelDataMaxEntries + 1).ToDictionary(i => $"k{i}", _ => "v")),
        };

        foreach (var dados in invalidos)
        {
            var falha = await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.IngestInboundMessageAsync(Entrada() with { ChannelData = dados }));
            Assert.DoesNotContain("ainda não são suportados", falha.Message);
        }
    }

    [Fact]
    public async Task Sem_ChannelData_a_ingestao_segue_o_caminho_de_antes_ate_o_armazenamento()
    {
        var m = new Montagem();
        m.ComoSistema();

        // Sem metadados a recusa não existe: a validação passa e a ingestão chega ao armazenamento, que aqui explode.
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Ingestao.IngestInboundMessageAsync(Entrada()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Ingestao.IngestInboundMessageAsync(Entrada() with { ChannelData = null }));
    }

    // Modo de associação (fatia A2): hoje só existe o do contato. Qualquer outro valor é recusado ANTES de qualquer acesso ao armazenamento e nunca é rebaixado.

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task Modo_de_associacao_que_nao_e_o_do_contato_e_recusado_antes_do_armazenamento_e_nunca_rebaixado(int valor)
    {
        var m = new Montagem();
        m.ComoSistema();

        // O armazenamento explodiria se fosse tocado: a recusa é uma ConversationsValidationException (e não a InvalidOperationException do armazenamento).
        var falha = await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.IngestInboundMessageAsync(Entrada() with { AssociationMode = (AssociationMode)valor }));

        Assert.Contains("modo de associação", falha.Message);
    }

    [Fact]
    public async Task Sem_informar_o_modo_de_associacao_ou_informando_o_do_contato_a_ingestao_segue_ate_o_armazenamento()
    {
        var m = new Montagem();
        m.ComoSistema();

        // A validação passa e a ingestão chega ao armazenamento, que aqui explode: o caminho de antes da A2 está intacto.
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Ingestao.IngestInboundMessageAsync(Entrada()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Ingestao.IngestInboundMessageAsync(Entrada() with { AssociationMode = AssociationMode.Contact }));
    }

    [Fact]
    public async Task O_modo_de_associacao_nao_abre_a_ingestao_a_quem_nao_e_de_sistema()
    {
        var m = new Montagem();
        m.ComoUsuarioComTudo();

        var falha = await Assert.ThrowsAsync<AccessDeniedException>(() => m.Ingestao.IngestInboundMessageAsync(Entrada() with { AssociationMode = (AssociationMode)2 }));

        Assert.Equal(Permission.SystemOperation, falha.Permission);
    }

    [Fact]
    public async Task Referencia_e_evento_alargados_chegam_ao_armazenamento_e_os_acima_do_limite_sao_recusados_antes_dele()
    {
        var m = new Montagem();
        m.ComoSistema();

        // No limite novo (254 e 512): a validação passa e o armazenamento é alcançado (aqui ele explode).
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Ingestao.IngestInboundMessageAsync(
            Entrada() with { ContactExternalRef = new string('c', ConversationLimits.ContactRefMaxLength), ProviderEventId = new string('e', ConversationLimits.ProviderEventIdMaxLength) }));

        // Um acima: recusado antes de qualquer acesso.
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.IngestInboundMessageAsync(
            Entrada() with { ContactExternalRef = new string('c', ConversationLimits.ContactRefMaxLength + 1) }));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.IngestInboundMessageAsync(
            Entrada() with { ProviderEventId = new string('e', ConversationLimits.ProviderEventIdMaxLength + 1) }));
    }

    [Fact]
    public async Task ChannelData_nao_abre_a_ingestao_a_quem_nao_e_de_sistema()
    {
        var m = new Montagem();
        m.ComoUsuarioComTudo();

        var falha = await Assert.ThrowsAsync<AccessDeniedException>(() => m.Ingestao.IngestInboundMessageAsync(Entrada() with { ChannelData = DadosDoCanal() }));

        Assert.Equal(Permission.SystemOperation, falha.Permission);
    }

    [Theory]
    [InlineData(DeliveryState.Requested)]
    [InlineData(DeliveryState.Queued)]
    [InlineData(DeliveryState.Sending)]
    [InlineData(DeliveryState.SendUnknown)]
    [InlineData(DeliveryState.Delivered)]
    [InlineData(DeliveryState.Read)]
    [InlineData(DeliveryState.Failed)]
    [InlineData(DeliveryState.Expired)]
    [InlineData(DeliveryState.Dead)]
    public async Task Evidencia_manual_so_confirma_o_envio_e_qualquer_outro_estado_e_recusado_sem_tocar_o_armazenamento(DeliveryState estado)
    {
        var m = new Montagem();
        m.ComoSistema();

        var resultado = await m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), estado, "motivo-curto", EvidenceSource.Manual));

        Assert.Equal(DeliveryStatusOutcome.EvidenceNotAllowed, resultado.Outcome);
        Assert.Null(resultado.CurrentState);
    }

    [Fact]
    public async Task A_recusa_da_evidencia_manual_vem_antes_da_validacao_do_motivo_pois_nao_depende_dele()
    {
        var m = new Montagem();
        m.ComoSistema();

        var semMotivo = await m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), DeliveryState.Failed, null, EvidenceSource.Manual));
        var motivoLivre = await m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), DeliveryState.Dead, "Texto Livre", EvidenceSource.Manual));

        Assert.Equal(DeliveryStatusOutcome.EvidenceNotAllowed, semMotivo.Outcome);
        Assert.Equal(DeliveryStatusOutcome.EvidenceNotAllowed, motivoLivre.Outcome);
    }

    [Fact]
    public async Task Evidencia_manual_com_Sent_passa_pela_cerca_e_chega_ao_armazenamento()
    {
        var m = new Montagem();
        m.ComoSistema();

        // Não é recusada: o que acontece depois é do armazenamento, que aqui explode.
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), DeliveryState.Sent, null, EvidenceSource.Manual)));
    }

    [Theory]
    [InlineData(EvidenceSource.ProviderStatus)]
    [InlineData(EvidenceSource.ProviderLookup)]
    [InlineData(EvidenceSource.ProviderRejection)]
    public async Task As_evidencias_do_provedor_mantem_o_comportamento_de_antes(EvidenceSource origem)
    {
        var m = new Montagem();
        m.ComoSistema();

        // Estado de envio: chega ao armazenamento. Falha definitiva sem motivo: a mesma ConversationsValidationException de antes. Nenhuma é "EvidenceNotAllowed".
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), DeliveryState.Sent, null, origem)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), DeliveryState.Failed, "recipient-unreachable", origem)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), DeliveryState.Failed, null, origem)));
    }

    [Fact]
    public async Task Origem_de_evidencia_inexistente_e_recusada()
    {
        var m = new Montagem();
        m.ComoSistema();

        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), DeliveryState.Sent, null, (EvidenceSource)99)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), DeliveryState.Sent, null, (EvidenceSource)0)));
    }

    [Fact]
    public async Task Evidencia_manual_so_e_aceita_de_contexto_de_sistema()
    {
        var m = new Montagem();
        m.ComoUsuarioComTudo();

        var falha = await Assert.ThrowsAsync<AccessDeniedException>(() => m.Ingestao.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(Guid.NewGuid(), DeliveryState.Sent, null, EvidenceSource.Manual)));

        Assert.Equal(Permission.SystemOperation, falha.Permission);
    }

    // Consultas

    [Fact]
    public async Task Usuario_sem_leitura_de_conversas_tem_a_listagem_negada_sem_tocar_o_armazenamento()
    {
        var m = new Montagem();
        m.ComoUsuarioSemLeitura();

        var falha = await Assert.ThrowsAsync<AccessDeniedException>(() => m.Consultas.ListSessionsAsync(new ListSessionsQuery(10, null, null)));

        Assert.Equal(Permission.ConversationRead, falha.Permission);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(100000)]
    public async Task Tamanho_de_pagina_fora_do_intervalo_e_recusado(int take)
    {
        var m = new Montagem();
        m.ComoSistema();

        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Consultas.ListSessionsAsync(new ListSessionsQuery(take, null, null)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Consultas.GetHistoryAsync(new GetHistoryQuery(Guid.NewGuid(), take, null, HistoryOrder.NewestFirst)));
    }

    [Fact]
    public async Task Cursor_e_ordem_invalidos_sao_recusados_antes_do_armazenamento()
    {
        var m = new Montagem();
        m.ComoSistema();

        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Consultas.ListSessionsAsync(new ListSessionsQuery(10, "cursor-adulterado", null)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Consultas.GetHistoryAsync(new GetHistoryQuery(Guid.NewGuid(), 10, "cursor-adulterado", HistoryOrder.NewestFirst)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Consultas.GetHistoryAsync(new GetHistoryQuery(Guid.NewGuid(), 10, null, (HistoryOrder)0)));
    }

    // Consulta de efeito: só a transição e o encaminhamento são respondidos; os tipos reservados e os desconhecidos falham fechado, antes de tocar o armazenamento.

    [Theory]
    [InlineData(EffectKind.HumanReply)]
    [InlineData(EffectKind.SessionClose)]
    public async Task A_consulta_de_efeito_recusa_os_tipos_reservados_ainda_nao_suportados_sem_tocar_o_armazenamento(EffectKind tipo)
    {
        var m = new Montagem();
        m.ComoSistema();

        // O armazenamento explodiria se fosse tocado: a recusa é uma ConversationsValidationException, e nada foi lido.
        var falha = await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Consultas.GetEffectAsync(new GetEffectQuery(Guid.NewGuid(), tipo, Guid.NewGuid())));

        Assert.Contains("ainda não suporta", falha.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task A_consulta_de_efeito_com_tipo_desconhecido_falha_fechada_sem_tocar_o_armazenamento(int valor)
    {
        var m = new Montagem();
        m.ComoSistema();

        var falha = await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Consultas.GetEffectAsync(new GetEffectQuery(Guid.NewGuid(), (EffectKind)valor, Guid.NewGuid())));

        Assert.DoesNotContain("ainda não suporta", falha.Message);
    }

    [Theory]
    [InlineData(EffectKind.Transition)]
    [InlineData(EffectKind.Transfer)]
    public async Task A_consulta_de_efeito_da_transicao_e_do_encaminhamento_segue_ate_o_armazenamento_como_antes(EffectKind tipo)
    {
        var m = new Montagem();
        m.ComoSistema();

        // Não são recusados: a consulta chega ao armazenamento (que aqui explode). O identificador vazio continua recusado para todos os tipos.
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Consultas.GetEffectAsync(new GetEffectQuery(Guid.NewGuid(), tipo, Guid.NewGuid())));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => m.Consultas.GetEffectAsync(new GetEffectQuery(Guid.NewGuid(), tipo, Guid.Empty)));
    }

    [Fact]
    public async Task A_consulta_de_efeito_continua_exigindo_o_contexto_de_tenant_antes_de_qualquer_recusa()
    {
        var m = new Montagem();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Consultas.GetEffectAsync(new GetEffectQuery(Guid.NewGuid(), EffectKind.HumanReply, Guid.NewGuid())));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => m.Consultas.GetEffectAsync(new GetEffectQuery(Guid.NewGuid(), EffectKind.Transition, Guid.NewGuid())));
    }

    [Fact]
    public void O_contrato_de_resposta_da_IA_nao_tem_como_aceitar_resposta_defasada()
    {
        // STL-4 e STL-5: o contrato do MVP não tem allowStaleInput, nem StaleReplyMode, nem nada que o valha.
        var campos = typeof(RegisterAiReplyCommand).GetProperties().Select(p => p.Name).ToList();
        var todosOsNomes = typeof(ITurnCommands).Assembly.GetTypes().SelectMany(t => t.GetProperties().Select(p => p.Name).Append(t.Name)).ToList();

        Assert.DoesNotContain(campos, c => c.Contains("Stale", StringComparison.OrdinalIgnoreCase) || c.Contains("Allow", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(todosOsNomes, n => n.Contains("allowStaleInput", StringComparison.OrdinalIgnoreCase) || n.Contains("StaleReplyMode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("TurnId", campos);
        Assert.Contains("Epoch", campos);
        Assert.Contains("BasedOnInboundSeq", campos);
        Assert.Contains("ExpectedStateVersion", campos);
    }

    [Fact]
    public void O_modelo_de_opcoes_do_Conversations_nao_tem_chave_de_aceite_defasado()
    {
        // STL-5: nenhuma opção, de tenant ou de ambiente, altera a regra. O módulo nem expõe classe de opções.
        var tipos = typeof(TurnService).Assembly.GetTypes().Select(t => t.Name).ToList();

        Assert.DoesNotContain(tipos, n => n.EndsWith("Options", StringComparison.Ordinal) || n.Contains("Stale", StringComparison.OrdinalIgnoreCase));
    }
}
