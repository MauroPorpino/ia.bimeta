using System.Text.Json;
using System.Text.Json.Serialization;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// A forma dos contratos aditivos da fatia A1 (ADRs 0009, 0011, 0014 e 0016): nomes, tipos, ordem e valores numéricos aprovados, a compatibilidade com os
/// chamadores existentes e as restrições dos contratos públicos (sem <c>TenantId</c>, sem chave de aceite defasado). Quando uma fatia seguinte estender um
/// destes tipos de propósito (por exemplo, anexos na A4), este teste é atualizado junto.
/// </summary>
public class DelegatedContractsShapeTests
{
    private static (string Tipo, string Nome)[] Parametros(Type tipo) =>
        tipo.GetConstructors().Single().GetParameters().Select(p => (NomeDoTipo(p.ParameterType), p.Name!)).ToArray();

    private static string NomeDoTipo(Type tipo)
    {
        var anulavel = Nullable.GetUnderlyingType(tipo);
        return anulavel is null ? tipo.Name : anulavel.Name + "?";
    }

    private static string[] ComPadrao(Type tipo) =>
        tipo.GetConstructors().Single().GetParameters().Where(p => p.HasDefaultValue).Select(p => p.Name!).ToArray();

    private static (string Nome, int Valor)[] Valores<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(v => (v.ToString(), Convert.ToInt32(v))).OrderBy(v => v.Item2).ToArray();

    // Resposta humana (7ª operação, ADR 0014, seção A)

    [Fact]
    public void RegisterHumanReplyCommand_tem_a_forma_aprovada_no_ADR_0014()
    {
        Assert.Equal(
            new[]
            {
                ("Guid", "SessionId"), ("Guid", "OperationId"), ("Guid", "AuthorUserId"), ("ReplyPrecondition", "Precondition"),
                ("Int64", "ExpectedStateVersion"), ("Int64", "FenceToken"), ("ReplyContent", "Content"),
            },
            Parametros(typeof(RegisterHumanReplyCommand)));
        Assert.Empty(ComPadrao(typeof(RegisterHumanReplyCommand)));
    }

    [Fact]
    public void ReplyPrecondition_distingue_o_responsavel_exato_do_qualquer_e_so_o_modo_humano_e_padrao()
    {
        Assert.Equal(
            new[] { ("Int64", "HandlingPeriod"), ("Int64", "PlacementEpoch"), ("ResponderMatch", "ResponderMatch"), ("Guid?", "ResponderRef"), ("HandlingMode", "Mode") },
            Parametros(typeof(ReplyPrecondition)));
        Assert.Equal(new[] { "ResponderRef", "Mode" }, ComPadrao(typeof(ReplyPrecondition)));
        Assert.Equal(HandlingMode.HumanAssisted, new ReplyPrecondition(1, 1, ResponderMatch.Exact).Mode);
        Assert.Equal(new[] { ("Exact", 1), ("Any", 2) }, Valores<ResponderMatch>());
    }

    [Fact]
    public void ReplyContent_do_MVP_nao_tem_destinatarios_nem_anexos_que_ficaram_fora_do_escopo_aprovado()
    {
        Assert.Equal(
            new[] { ("String", "Text"), ("Guid?", "InReplyToMessageId"), ("ChannelData", "ChannelData") },
            Parametros(typeof(ReplyContent)));
        Assert.Equal(new[] { "InReplyToMessageId", "ChannelData" }, ComPadrao(typeof(ReplyContent)));
    }

    [Fact]
    public void Os_resultados_da_resposta_humana_seguem_a_lista_do_ADR_0014_e_so_o_desfecho_e_obrigatorio()
    {
        Assert.Equal(
            new[]
            {
                ("Applied", 1), ("AlreadyApplied", 2), ("OperationIdReused", 3), ("SessionNotFound", 4), ("SessionClosed", 5), ("HandlingChanged", 6),
                ("StaleState", 7), ("FencedOut", 8), ("ContentRejected", 9), ("AuthorNotFound", 10),
            },
            Valores<RegisterHumanReplyOutcome>());
        Assert.Equal(new[] { ("Current", 1), ("Overtaken", 2) }, Valores<FenceObservation>());
        Assert.Equal(
            new[] { "MessageId", "MessageSeq", "StateVersionAfter", "RegisteredAt", "Fence", "CurrentDeliveryState" },
            ComPadrao(typeof(RegisterHumanReplyResult)));
    }

    // Encerramento delegado (9ª operação, ADR 0009, item 10; ADR 0016, seção M)

    [Fact]
    public void CloseSessionDelegatedCommand_tem_a_forma_do_item_10_do_ADR_0009_sem_os_campos_do_satelite()
    {
        Assert.Equal(
            new[]
            {
                ("Guid", "SessionId"), ("Guid", "OperationId"), ("Guid?", "AuthorUserId"), ("String", "ActorOrigin"), ("HandlingPrecondition", "Precondition"),
                ("Int64", "ExpectedStateVersion"), ("Int64", "FenceToken"), ("String", "ReasonCode"),
            },
            Parametros(typeof(CloseSessionDelegatedCommand)));
        Assert.Empty(ComPadrao(typeof(CloseSessionDelegatedCommand)));
    }

    [Fact]
    public void Os_resultados_do_encerramento_delegado_sao_os_do_MVP_e_a_extensao_do_satelite_nao_existe()
    {
        Assert.Equal(
            new[]
            {
                ("Applied", 1), ("AlreadyApplied", 2), ("AlreadyClosed", 3), ("HandlingChanged", 4), ("StaleState", 5), ("FencedOut", 6),
                ("SessionNotFound", 7), ("OperationIdReused", 8),
            },
            Valores<CloseSessionDelegatedOutcome>());
        Assert.Equal(new[] { "StateVersionAfter", "LastMessageSeq", "Reason" }, ComPadrao(typeof(CloseSessionDelegatedResult)));

        // A extensão do WebChat (SatelliteDecision e afins) é da 5.2w e continua Proposta: nenhum tipo, propriedade ou valor de enum a menciona.
        var nomes = typeof(CloseSessionDelegatedCommand).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties().Select(p => p.Name).Concat(t.IsEnum ? Enum.GetNames(t) : Array.Empty<string>()).Append(t.Name))
            .ToList();
        Assert.NotEmpty(nomes);
        Assert.DoesNotContain(nomes, n => n.Contains("Satellite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void O_CloseSession_direto_do_Conversations_nao_mudou()
    {
        Assert.Equal(new[] { ("Guid", "SessionId"), ("String", "Reason") }, Parametros(typeof(CloseSessionCommand)));
        Assert.Equal(new[] { ("Boolean", "SessionFound"), ("Boolean", "Closed"), ("Int64?", "StateVersion") }, Parametros(typeof(CloseSessionResult)));
    }

    // Consulta de efeito (ADR 0009, itens 9 e 10; ADR 0014, seção A)

    [Fact]
    public void EffectKind_ganha_HumanReply_e_SessionClose_sem_mudar_os_valores_existentes()
    {
        Assert.Equal(new[] { ("Transition", 1), ("Transfer", 2), ("HumanReply", 3), ("SessionClose", 4) }, Valores<EffectKind>());
    }

    // Evidência de entrega e metadados do canal (ADR 0011, seções D e F; ADR 0014, seção E; DP-08)

    [Fact]
    public void EvidenceSource_tem_as_quatro_origens_do_ADR_0011_e_o_padrao_e_a_do_provedor()
    {
        Assert.Equal(new[] { ("ProviderStatus", 1), ("ProviderLookup", 2), ("ProviderRejection", 3), ("Manual", 4) }, Valores<EvidenceSource>());
        Assert.Equal(EvidenceSource.ProviderStatus, new RecordDeliveryStatusCommand(Guid.NewGuid(), DeliveryState.Sent, null).Evidence);
    }

    [Fact]
    public void DeliveryStatusOutcome_ganha_EvidenceNotAllowed_no_fim_sem_mudar_os_valores_existentes()
    {
        Assert.Equal(
            new[] { ("Applied", 1), ("Ignored", 2), ("MessageNotFound", 3), ("NotAnOutboundMessage", 4), ("EvidenceNotAllowed", 5) },
            Valores<DeliveryStatusOutcome>());
    }

    [Fact]
    public void Os_comandos_existentes_so_ganharam_parametros_opcionais_no_fim_e_os_chamadores_antigos_compilam_iguais()
    {
        var evento = Parametros(typeof(IngestInboundMessageCommand));

        // A1 acrescentou ChannelData; A2 acrescentou, depois dele, o modo de associação. Os dois são opcionais e vêm no fim.
        Assert.Equal(("ChannelData", "ChannelData"), evento[^2]);
        Assert.Equal(("AssociationMode", "AssociationMode"), evento[^1]);
        Assert.Contains("ChannelData", ComPadrao(typeof(IngestInboundMessageCommand)));
        Assert.Contains("AssociationMode", ComPadrao(typeof(IngestInboundMessageCommand)));
        Assert.Equal(
            new[] { "ChannelInstanceId", "ContactExternalRef", "ContactDisplayName", "OrganizationId", "BusinessUnitId", "ProviderEventId", "ProviderTimestamp", "Kind", "Text" },
            evento.Take(9).Select(p => p.Nome));

        // O chamador de antes da A1, com os nove argumentos, segue válido e sem metadados.
        var antigo = new IngestInboundMessageCommand(ChannelInstanceId.New(), "5511999990001", "Mauro", Guid.NewGuid(), null, "wamid.1", DateTimeOffset.UtcNow, MessageKind.Text, "Oi");
        Assert.Null(antigo.ChannelData);

        Assert.Equal(
            new[] { ("Guid", "MessageId"), ("DeliveryState", "State"), ("String", "FailureReason"), ("EvidenceSource", "Evidence") },
            Parametros(typeof(RecordDeliveryStatusCommand)));
        Assert.Equal(new[] { "Evidence" }, ComPadrao(typeof(RecordDeliveryStatusCommand)));
    }

    [Fact]
    public void O_corpo_HTTP_do_simulador_sem_a_evidencia_continua_valido_e_com_ela_entra_como_texto()
    {
        // O simulador liga o corpo HTTP direto ao comando, e os enums entram como texto (ApiHost): a opção abaixo é a do host.
        var opcoes = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var id = Guid.NewGuid();

        var semEvidencia = JsonSerializer.Deserialize<RecordDeliveryStatusCommand>($$"""{"messageId":"{{id}}","state":"Sent","failureReason":null}""", opcoes)!;
        var comEvidencia = JsonSerializer.Deserialize<RecordDeliveryStatusCommand>($$"""{"messageId":"{{id}}","state":"Sent","failureReason":null,"evidence":"Manual"}""", opcoes)!;

        Assert.Equal((id, DeliveryState.Sent, EvidenceSource.ProviderStatus), (semEvidencia.MessageId, semEvidencia.State, semEvidencia.Evidence));
        Assert.Equal(EvidenceSource.Manual, comEvidencia.Evidence);
    }

    [Fact]
    public void ChannelData_e_um_record_imutavel_de_versao_e_valores()
    {
        Assert.Equal(new[] { ("Int32", "Version"), ("IReadOnlyDictionary`2", "Values") }, Parametros(typeof(ChannelData)));
        Assert.All(
            typeof(ChannelData).GetProperties().Where(p => p.Name is "Version" or "Values"),
            p => Assert.True(p.SetMethod is null || p.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit"), $"{p.Name} não pode ter setter público"));

        var lido = JsonSerializer.Deserialize<ChannelData>("""{"version":2,"values":{"media.kind":"image"}}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2, lido.Version);
        Assert.Equal("image", lido.Values["media.kind"]);
    }

    // Restrições dos contratos públicos

    [Fact]
    public void Nenhum_contrato_novo_carrega_TenantId_nem_chave_de_aceite_defasado_e_todos_sao_records_selados()
    {
        var novos = new[]
        {
            typeof(ChannelData), typeof(RegisterHumanReplyCommand), typeof(ReplyPrecondition), typeof(ReplyContent), typeof(RegisterHumanReplyResult),
            typeof(CloseSessionDelegatedCommand), typeof(CloseSessionDelegatedResult), typeof(RecordDeliveryStatusCommand), typeof(IngestInboundMessageCommand),
        };

        foreach (var tipo in novos)
        {
            var membros = tipo.GetProperties().Select(p => p.Name).Concat(tipo.GetConstructors().SelectMany(c => c.GetParameters().Select(p => p.Name!))).ToList();
            Assert.DoesNotContain(membros, n => n.Contains("TenantId", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(membros, n => n.Contains("allowStaleInput", StringComparison.OrdinalIgnoreCase) || n.Contains("StaleReplyMode", StringComparison.OrdinalIgnoreCase));
            Assert.All(tipo.GetProperties(), p => Assert.NotEqual(typeof(TenantId), p.PropertyType));
            Assert.True(tipo.IsSealed && tipo.GetMethods().Any(m => m.Name == "<Clone>$"), $"{tipo.Name} deve ser um record selado");
        }
    }
}
