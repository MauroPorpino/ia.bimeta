using System.Reflection;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Contracts;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Tests;

/// <summary>
/// A fachada de <see cref="IHandoffRequests"/> (ADR 0017, seção B; plano C1.1): delega ao caso de uso que já existe e só TRADUZ o resultado e as recusas de
/// negócio. Nenhum teste aqui usa banco: o que se prova é o contrato, a ordem dos argumentos entregues ao caso de uso e a tradução, em que nenhuma recusa
/// vira sucesso.
/// </summary>
public sealed class HandoffRequestsTests
{
    private static readonly Guid Sessao = Guid.NewGuid();
    private static readonly Guid Pedido = Guid.NewGuid();
    private static readonly Guid Fila = Guid.NewGuid();
    private static readonly Guid Item = Guid.NewGuid();

    private static QueueItemOperationResult Resultado(OperationOutcome desfecho, Guid? fila = null) =>
        new(
            new QueueItemView(
                Item, Sessao, Guid.NewGuid(), null, null, fila ?? Fila, QueueItemStatus.Waiting, null, 1, DateTimeOffset.UnixEpoch, null, null, null, null,
                DateTimeOffset.UnixEpoch),
            desfecho);

    private static HandoffRequests Fachada(Func<Task<QueueItemOperationResult>> operacao) =>
        new((_, _, _, _, _) => operacao());

    // Contrato

    [Fact]
    public void O_Contracts_tem_so_o_RequestHandoff_e_so_interface_record_e_enum()
    {
        var tipos = typeof(IHandoffRequests).Assembly.GetExportedTypes();

        Assert.Equal(
            new[] { "IHandoffRequests", "RequestHandoffCommand", "RequestHandoffOutcome", "RequestHandoffResult" },
            tipos.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        foreach (var tipo in tipos)
        {
            var record = tipo.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;
            Assert.True(tipo.IsInterface || tipo.IsEnum || (tipo.IsSealed && record), $"{tipo.Name} não é interface, enum nem record selado (ARQ-07).");
        }

        var metodos = typeof(IHandoffRequests).GetMethods();
        Assert.Equal("RequestHandoffAsync", Assert.Single(metodos).Name);
        Assert.DoesNotContain(typeof(IHandoffRequests).Assembly.GetReferencedAssemblies(), r => r.Name!.StartsWith("IaBimeta.", StringComparison.Ordinal));
    }

    [Fact]
    public void O_comando_tem_os_campos_do_plano_e_nenhum_tenant()
    {
        var propriedades = typeof(RequestHandoffCommand).GetProperties().Select(p => p.Name).ToArray();

        Assert.Equal(new[] { "SessionId", "RequestId", "Reason", "QueueId" }, propriedades);
        Assert.Equal(typeof(Guid?), typeof(RequestHandoffCommand).GetProperty("QueueId")!.PropertyType);
    }

    [Fact]
    public void O_resultado_do_contrato_e_um_enum_e_as_recusas_nunca_se_confundem_com_sucesso()
    {
        Assert.Equal(
            new[] { "Completed", "AlreadyApplied", "Pending", "SessionEnded", "NotRoutable", "Refused" },
            Enum.GetNames<RequestHandoffOutcome>());
    }

    // Delegação

    [Fact]
    public async Task A_fachada_entrega_ao_caso_de_uso_a_sessao_a_fila_o_pedido_e_o_motivo_na_ordem_certa()
    {
        (Guid Sessao, Guid? Fila, Guid Pedido, string Motivo, CancellationToken Token)? recebido = null;
        var fachada = new HandoffRequests((sessao, fila, pedido, motivo, token) =>
        {
            recebido = (sessao, fila, pedido, motivo, token);
            return Task.FromResult(Resultado(OperationOutcome.Completed));
        });
        using var cancelamento = new CancellationTokenSource();

        await fachada.RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "ai-decision.price", Fila), cancelamento.Token);

        Assert.Equal((Sessao, (Guid?)Fila, Pedido, "ai-decision.price", cancelamento.Token), recebido);
    }

    [Fact]
    public async Task Sem_fila_pedida_o_roteamento_decide_e_a_fachada_entrega_nulo()
    {
        Guid? filaRecebida = Guid.NewGuid();
        var fachada = new HandoffRequests((_, fila, _, _, _) =>
        {
            filaRecebida = fila;
            return Task.FromResult(Resultado(OperationOutcome.Completed));
        });

        await fachada.RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "content-unavailable"));

        Assert.Null(filaRecebida);
    }

    [Fact]
    public async Task A_fachada_nao_aceita_comando_nulo_e_nao_chama_o_caso_de_uso()
    {
        var chamado = false;
        var fachada = new HandoffRequests((_, _, _, _, _) =>
        {
            chamado = true;
            return Task.FromResult(Resultado(OperationOutcome.Completed));
        });

        await Assert.ThrowsAsync<ArgumentNullException>(() => fachada.RequestHandoffAsync(null!));
        Assert.Throws<ArgumentNullException>(() => new HandoffRequests((QueueItemService)null!));

        Assert.False(chamado);
    }

    // Tradução do resultado

    [Theory]
    [InlineData(OperationOutcome.Completed, RequestHandoffOutcome.Completed)]
    [InlineData(OperationOutcome.AlreadyApplied, RequestHandoffOutcome.AlreadyApplied)]
    [InlineData(OperationOutcome.Pending, RequestHandoffOutcome.Pending)]
    [InlineData(OperationOutcome.SessionEnded, RequestHandoffOutcome.SessionEnded)]
    public async Task Cada_desfecho_do_caso_de_uso_vira_o_do_contrato_com_o_item_e_a_fila(OperationOutcome desfecho, RequestHandoffOutcome esperado)
    {
        var resultado = await Fachada(() => Task.FromResult(Resultado(desfecho))).RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "motivo"));

        Assert.Equal(new RequestHandoffResult(esperado, Item, Fila), resultado);
    }

    [Fact]
    public async Task Todo_desfecho_conhecido_do_caso_de_uso_tem_traducao_e_um_desconhecido_nunca_vira_sucesso()
    {
        foreach (var desfecho in Enum.GetValues<OperationOutcome>())
        {
            var resultado = await Fachada(() => Task.FromResult(Resultado(desfecho))).RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "motivo"));
            Assert.Equal(desfecho.ToString(), resultado.Outcome.ToString()); // os quatro desfechos têm o mesmo nome nos dois lados
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Fachada(() => Task.FromResult(Resultado((OperationOutcome)99))).RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "motivo")));
    }

    // Tradução das recusas

    // Cada regra: o desfecho, se a causa é TRANSITÓRIA (repetir com o mesmo identificador pode dar outro resultado) e o código estável. Esta tabela é a decisão de
    // classificação: mudá-la é decisão de contrato, não detalhe.
    [Theory]
    [InlineData(ContactCenterRule.QueueDoesNotCoverSession, RequestHandoffOutcome.NotRoutable, false, "queue-does-not-cover-session")]
    [InlineData(ContactCenterRule.QueuePaused, RequestHandoffOutcome.NotRoutable, true, "queue-paused")]
    [InlineData(ContactCenterRule.TargetNotQueueMember, RequestHandoffOutcome.NotRoutable, false, "target-not-queue-member")]
    [InlineData(ContactCenterRule.UnitNotAuthorizedForChannel, RequestHandoffOutcome.NotRoutable, false, "unit-not-authorized-for-channel")]
    [InlineData(ContactCenterRule.TargetUnitNotAllowed, RequestHandoffOutcome.NotRoutable, false, "target-unit-not-allowed")]
    [InlineData(ContactCenterRule.ConversationAlreadyHandledByHuman, RequestHandoffOutcome.Refused, false, "already-handled-by-human")]
    [InlineData(ContactCenterRule.SessionClosed, RequestHandoffOutcome.SessionEnded, false, "session-closed")]
    [InlineData(ContactCenterRule.QueueClosed, RequestHandoffOutcome.NotRoutable, true, "queue-closed")]
    [InlineData(ContactCenterRule.NoRoute, RequestHandoffOutcome.NotRoutable, false, "no-route")]
    [InlineData(ContactCenterRule.AgentAtCapacity, RequestHandoffOutcome.NotRoutable, true, "agent-at-capacity")]
    [InlineData(ContactCenterRule.ConversationChanged, RequestHandoffOutcome.Refused, true, "conversation-changed")]
    public async Task Cada_regra_do_ContactCenter_vira_o_desfecho_a_classificacao_transitoria_ou_definitiva_e_o_codigo_sem_item(
        ContactCenterRule regra, RequestHandoffOutcome esperado, bool retentavel, string codigo)
    {
        var resultado = await Fachada(() => throw new ContactCenterRuleException(regra, "mensagem interna")).RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "motivo"));

        Assert.Equal(new RequestHandoffResult(esperado, null, null, retentavel, codigo), resultado);
        Assert.Null(resultado.QueueItemId);
        Assert.Null(resultado.QueueId);
    }

    [Fact]
    public async Task Toda_regra_do_ContactCenter_e_coberta_e_nenhuma_recusa_vira_sucesso()
    {
        var regras = Enum.GetValues<ContactCenterRule>();
        Assert.NotEmpty(regras);

        // Inclui uma regra que esta fachada ainda não conhece: falha para o lado de recusar, e de recusar de forma DEFINITIVA (nunca repetição às cegas).
        foreach (var regra in regras.Append((ContactCenterRule)999))
        {
            var resultado = await Fachada(() => throw new ContactCenterRuleException(regra, "x")).RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "motivo"));

            Assert.True(
                resultado.Outcome is RequestHandoffOutcome.NotRoutable or RequestHandoffOutcome.Refused or RequestHandoffOutcome.SessionEnded,
                $"A regra {regra} virou {resultado.Outcome}.");
            Assert.Matches("^[a-z0-9-]+$", resultado.Code ?? string.Empty);
        }

        var desconhecida = await Fachada(() => throw new ContactCenterRuleException((ContactCenterRule)999, "x")).RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "motivo"));
        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Refused, null, null, false, "refused"), desconhecida);
    }

    // Cada recusa de negócio que não é regra: o desfecho é sempre Refused, e a classificação transitória ou definitiva.
    public static IEnumerable<object[]> RecusasDeNegocio()
    {
        yield return new object[] { new ContactCenterResourceNotFoundException("Conversa"), false, "session-unavailable" };
        yield return new object[] { new AccessDeniedException(Permission.ConversationManage), false, "session-unavailable" };
        yield return new object[] { new ContactCenterValidationException("motivo inválido"), false, "invalid-request" };
        yield return new object[] { new ContactCenterConflictException(), true, "concurrent-update" };
        yield return new object[] { new OperationIdInUseException(), true, "request-id-in-use" };
        yield return new object[] { new QueueItemConflictException(QueueItemConflict.AlreadyAssigned, "conflito"), false, "already-assigned" };
        yield return new object[] { new QueueItemConflictException(QueueItemConflict.OperationInProgress, "conflito"), true, "operation-in-progress" };
        yield return new object[] { new QueueItemConflictException(QueueItemConflict.ItemClosed, "conflito"), false, "item-closed" };
        yield return new object[] { new QueueItemConflictException(QueueItemConflict.ActiveItemExists, "conflito"), false, "already-in-another-queue" };
        yield return new object[] { new QueueItemConflictException(QueueItemConflict.ConcurrentChange, "conflito"), true, "concurrent-change" };
        yield return new object[] { new QueueItemConflictException(QueueItemConflict.NothingToChange, "conflito"), false, "nothing-to-change" };
        yield return new object[] { new QueueItemConflictException(QueueItemConflict.RequestIdReused, "conflito"), false, "request-id-reused" };
        yield return new object[] { new QueueItemConflictException((QueueItemConflict)99, "conflito futuro"), false, "conflict" };
    }

    [Theory]
    [MemberData(nameof(RecusasDeNegocio))]
    public async Task As_demais_recusas_de_negocio_viram_Refused_com_a_classificacao_e_sem_revelar_o_motivo_interno(Exception recusa, bool retentavel, string codigo)
    {
        var resultado = await Fachada(() => throw recusa).RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "motivo"));

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Refused, null, null, retentavel, codigo), resultado);
    }

    [Fact]
    public void Todo_conflito_de_item_do_ContactCenter_tem_classificacao_explicita()
    {
        // Um conflito novo no ContactCenter não pode cair, em silêncio, no código genérico: este teste obriga a decidir se ele é transitório.
        var cobertos = RecusasDeNegocio().Select(linha => linha[0]).OfType<QueueItemConflictException>().Select(e => e.Conflict).ToHashSet();

        foreach (var conflito in Enum.GetValues<QueueItemConflict>())
        {
            Assert.Contains(conflito, cobertos);
        }
    }

    [Fact]
    public async Task A_conversa_inexistente_e_a_que_o_chamador_nao_enxerga_tem_exatamente_o_mesmo_resultado()
    {
        var comando = new RequestHandoffCommand(Sessao, Pedido, "motivo");

        var inexistente = await Fachada(() => throw new ContactCenterResourceNotFoundException("Conversa")).RequestHandoffAsync(comando);
        var negada = await Fachada(() => throw new AccessDeniedException(Permission.ConversationManage)).RequestHandoffAsync(comando);

        // TEN-2: nem o desfecho, nem o código, nem a classificação revelam qual das duas situações ocorreu.
        Assert.Equal(inexistente, negada);
    }

    [Fact]
    public async Task So_as_causas_transitorias_documentadas_sao_retentaveis_e_nenhum_sucesso_leva_classificacao()
    {
        var retentaveis = new List<string>();

        foreach (var regra in Enum.GetValues<ContactCenterRule>())
        {
            var resultado = await Fachada(() => throw new ContactCenterRuleException(regra, "x")).RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "motivo"));
            if (resultado.Retryable)
            {
                retentaveis.Add(resultado.Code!);
            }
        }

        foreach (var linha in RecusasDeNegocio())
        {
            var resultado = await Fachada(() => throw (Exception)linha[0]).RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "motivo"));
            if (resultado.Retryable)
            {
                retentaveis.Add(resultado.Code!);
            }
        }

        // Exatamente as causas que o próprio ContactCenter descreve como "releia e repita" ou que se resolvem sozinhas (pausa, horário, vaga).
        Assert.Equal(
            new[] { "agent-at-capacity", "concurrent-change", "concurrent-update", "conversation-changed", "operation-in-progress", "queue-closed", "queue-paused", "request-id-in-use" },
            retentaveis.OrderBy(c => c, StringComparer.Ordinal).ToArray());

        // Os desfechos de sucesso nunca carregam classificação nem código.
        foreach (var desfecho in Enum.GetValues<OperationOutcome>())
        {
            var resultado = await Fachada(() => Task.FromResult(Resultado(desfecho))).RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "motivo"));
            Assert.False(resultado.Retryable);
            Assert.Null(resultado.Code);
        }
    }

    [Fact]
    public void O_resultado_tem_a_classificacao_e_o_codigo_como_campos_opcionais_no_fim()
    {
        var parametros = typeof(RequestHandoffResult).GetConstructors().Single().GetParameters();

        Assert.Equal(new[] { "Outcome", "QueueItemId", "QueueId", "Retryable", "Code" }, parametros.Select(p => p.Name).ToArray());
        Assert.All(parametros.Skip(1), p => Assert.True(p.HasDefaultValue, $"{p.Name} precisa ser opcional."));

        // Os chamadores que só conhecem os três primeiros campos seguem válidos e não recebem classificação.
        var antigo = new RequestHandoffResult(RequestHandoffOutcome.Completed, Item, Fila);
        Assert.False(antigo.Retryable);
        Assert.Null(antigo.Code);
    }

    [Fact]
    public async Task O_que_nao_e_recusa_de_negocio_continua_excecao_contexto_ausente_falha_de_infraestrutura_e_cancelamento()
    {
        var comando = new RequestHandoffCommand(Sessao, Pedido, "motivo");

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Fachada(() => throw new TenantContextRequiredException()).RequestHandoffAsync(comando));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fachada(() => throw new InvalidOperationException("banco fora")).RequestHandoffAsync(comando));
        await Assert.ThrowsAsync<TimeoutException>(() => Fachada(() => throw new TimeoutException()).RequestHandoffAsync(comando));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fachada(() => throw new OperationCanceledException()).RequestHandoffAsync(comando));
    }

    [Fact]
    public async Task Uma_excecao_assincrona_do_caso_de_uso_tambem_e_traduzida()
    {
        var fachada = new HandoffRequests(async (_, _, _, _, _) =>
        {
            await Task.Yield();
            throw new ContactCenterRuleException(ContactCenterRule.NoRoute, "sem rota");
        });

        var resultado = await fachada.RequestHandoffAsync(new RequestHandoffCommand(Sessao, Pedido, "motivo"));

        Assert.Equal(RequestHandoffOutcome.NotRoutable, resultado.Outcome);
    }
}
