using IaBimeta.Audit.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// Vários números (instâncias de canal) por tenant, com PostgreSQL real: a mesma pessoa em números diferentes tem contatos,
/// sessões e conversas separados; a identificação inclui tenant, instância de canal e contato, nunca só o telefone; a
/// resposta sai pelo número da conversa; o tenant nunca vem do canal; e o roteamento por unidade e setor, o encaminhamento
/// entre filiais (com autorização e rastro) e o isolamento valem entre os números e entre os tenants.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationsMultiChannelTests(PostgresFixture banco)
{
    private const string Pessoa = "5511999990001";

    private static RequestContext Atendente(CenarioDeConversas c) =>
        c.Usuario(c.OrganizacaoId, Permission.ConversationRead, Permission.ConversationManage);

    private async Task<int> SqlAsync(RequestContext contexto, string sql, params object[] parametros)
    {
        await using var escopo = banco.NovoEscopo(contexto);
        return await escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>().Database.ExecuteSqlRawAsync(sql, parametros);
    }

    private async Task<T> ConsultaAsync<T>(RequestContext contexto, Func<ConversationsDbContext, Task<T>> consulta)
    {
        await using var escopo = banco.NovoEscopo(contexto);
        return await consulta(escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>());
    }

    // Vários números do mesmo tenant

    [Fact]
    public async Task A_mesma_pessoa_em_tres_numeros_do_tenant_tem_tres_contatos_tres_sessoes_e_tres_conversas_separados()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "tresnumeros");
        var n1 = ChannelInstanceId.New();
        var n2 = ChannelInstanceId.New();
        var n3 = ChannelInstanceId.New();

        var r1 = await c.EntrarAsync(Pessoa, "Pergunta pelo número 1", canal: n1);
        var r2 = await c.EntrarAsync(Pessoa, "Pergunta pelo número 2", canal: n2);
        var r3 = await c.EntrarAsync(Pessoa, "Pergunta pelo número 3", canal: n3);
        var r1b = await c.EntrarAsync(Pessoa, "Segunda pelo número 1", canal: n1);

        Assert.Equal(3, new[] { r1.SessionId, r2.SessionId, r3.SessionId }.Distinct().Count());
        Assert.Equal(3, new[] { r1.ConversationId, r2.ConversationId, r3.ConversationId }.Distinct().Count());
        Assert.Equal((r1.SessionId, r1.ConversationId, 2L), (r1b.SessionId, r1b.ConversationId, r1b.InboundSeq)); // o mesmo número continua na mesma sessão
        Assert.Equal(new long[] { 1, 1, 1 }, new[] { r1.InboundSeq, r2.InboundSeq, r3.InboundSeq }); // cada sessão tem a própria sequência

        foreach (var (r, n, textos) in new[]
                 {
                     (r1, n1, new[] { "Pergunta pelo número 1", "Segunda pelo número 1" }),
                     (r2, n2, new[] { "Pergunta pelo número 2" }),
                     (r3, n3, new[] { "Pergunta pelo número 3" }),
                 })
        {
            var sessao = (await c.SessaoAsync(r.SessionId))!;
            var historico = (await c.HistoricoAsync(r.SessionId))!.Items;
            Assert.Equal(n, sessao.ChannelInstanceId);
            Assert.Equal(textos, historico.Select(m => m.Text)); // nenhuma mensagem de outro número
            Assert.All(historico, m => Assert.Equal(n, m.ChannelInstanceId));
        }

        Assert.Equal(3, await ConsultaAsync(c.ContextoDeSistema, db => db.Contacts.CountAsync(x => x.ExternalRef == Pessoa)));
        var lista = await c.ListarSessoesAsync(c.Usuario(null, Permission.ConversationRead));
        Assert.Equal(new[] { n1, n2, n3 }.Order(Comparer<ChannelInstanceId>.Create((x, y) => x.Value.CompareTo(y.Value))), lista.Items.Select(s => s.ChannelInstanceId).Order(Comparer<ChannelInstanceId>.Create((x, y) => x.Value.CompareTo(y.Value))));
    }

    [Fact]
    public async Task Mensagens_simultaneas_da_mesma_pessoa_em_cinco_numeros_nao_se_misturam_e_cada_sessao_tem_a_propria_ordem()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cincosnumeros");
        var numeros = Enumerable.Range(1, 5).Select(_ => ChannelInstanceId.New()).ToArray();

        var chamadas = numeros.SelectMany((n, i) => Enumerable.Range(1, 6).Select(m => (N: n, I: i + 1, M: m)));
        var resultados = await Task.WhenAll(chamadas.Select(async x => (x, R: await c.EntrarAsync(Pessoa, $"número {x.I}, mensagem {x.M}", canal: x.N))));

        var porNumero = resultados.GroupBy(r => r.x.N).ToList();
        Assert.Equal(5, porNumero.Count);
        foreach (var grupo in porNumero)
        {
            Assert.Single(grupo.Select(r => r.R.SessionId).Distinct());
            Assert.Equal(Enumerable.Range(1, 6).Select(i => (long)i), grupo.Select(r => r.R.InboundSeq).Order());
            var historico = (await c.HistoricoAsync(grupo.First().R.SessionId))!.Items;
            Assert.Equal(6, historico.Count);
            Assert.All(historico, m => Assert.StartsWith($"número {grupo.First().x.I},", m.Text));
        }
    }

    [Fact]
    public async Task O_mesmo_evento_do_provedor_em_numeros_diferentes_do_mesmo_tenant_sao_mensagens_diferentes()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "eventoigual");

        var noUm = await c.EntrarAsync(Pessoa, "Oi", evento: "wamid.IGUAL", canal: ChannelInstanceId.New());
        var noDois = await c.EntrarAsync(Pessoa, "Oi", evento: "wamid.IGUAL", canal: ChannelInstanceId.New());
        var repetidaNoUm = await c.EntrarAsync(Pessoa, "Oi", evento: "wamid.IGUAL", canal: (await c.SessaoAsync(noUm.SessionId))!.ChannelInstanceId);

        Assert.Equal((IngestOutcome.Registered, IngestOutcome.Registered), (noUm.Outcome, noDois.Outcome));
        Assert.NotEqual(noUm.MessageId, noDois.MessageId);
        Assert.Equal((IngestOutcome.Duplicate, noUm.MessageId), (repetidaNoUm.Outcome, repetidaNoUm.MessageId)); // só no mesmo número é duplicata
    }

    [Fact]
    public async Task A_resposta_sai_pelo_numero_da_conversa_e_nao_pode_ser_desviada_para_a_conversa_de_outro_numero()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "respostanumero");
        var a = ChannelInstanceId.New();
        var b = ChannelInstanceId.New();
        var emA = await c.EntrarAsync(Pessoa, "Pergunta pelo A", canal: a);
        var emB = await c.EntrarAsync(Pessoa, "Pergunta pelo B", canal: b);
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(emA.SessionId, turno);

        // Desviar a resposta da sessão A para a conversa do número B é recusado.
        var desviada = await c.ResponderComAsync(new RegisterAiReplyCommand(
            emA.SessionId, turno, inicio.Grant!.Epoch, inicio.Grant.StateVersion, inicio.Grant.BasedOnInboundSeq, ReplyKind.Generated, emB.ConversationId, "para o B?", new[] { Guid.NewGuid() }));
        var certa = await c.ResponderAsync(emA.SessionId, turno, inicio, emA.ConversationId, "Resposta pelo A");

        Assert.Equal(RegisterReplyOutcome.ShapeNotAllowed, desviada.Outcome);
        Assert.Equal(RegisterReplyOutcome.Accepted, certa.Outcome);
        var saidaDeA = (await c.HistoricoAsync(emA.SessionId))!.Items.Single(m => m.Direction == MessageDirection.Outbound);
        Assert.Equal((a, "Resposta pelo A"), (saidaDeA.ChannelInstanceId, saidaDeA.Text));
        Assert.DoesNotContain((await c.HistoricoAsync(emB.SessionId))!.Items, m => m.Direction == MessageDirection.Outbound);

        // O evento diz de qual número sai, e só existe o do A.
        var evento = Assert.IsType<OutboundMessageRequestedV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.OutboundMessageRequested, emA.SessionId)));
        Assert.Equal((a, emA.ConversationId, certa.MessageId), (evento.ChannelInstanceId, evento.ConversationId, evento.MessageId));
        Assert.Empty(c.PayloadsDe(ConversationEventTypes.OutboundMessageRequested, emB.SessionId));
    }

    [Fact]
    public async Task O_evento_de_entrada_o_snapshot_do_turno_e_o_reenvio_carregam_a_instancia_de_canal()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "instanciaeventos");
        var a = ChannelInstanceId.New();
        var b = ChannelInstanceId.New();
        var emA = await c.EntrarAsync(Pessoa, "Pelo A", canal: a);
        var emB = await c.EntrarAsync(Pessoa, "Pelo B", canal: b);

        var entradaA = Assert.IsType<InboundMessageRegisteredV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.InboundMessageRegistered, emA.SessionId)));
        var entradaB = Assert.IsType<InboundMessageRegisteredV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.InboundMessageRegistered, emB.SessionId)));
        Assert.Equal((a, b), (entradaA.ChannelInstanceId, entradaB.ChannelInstanceId));

        var turnoA = Guid.NewGuid();
        var inicioA = await c.IniciarTurnoAsync(emA.SessionId, turnoA);
        var inicioB = await c.IniciarTurnoAsync(emB.SessionId, Guid.NewGuid());
        Assert.Equal((a, b), (inicioA.Snapshot!.ChannelInstanceId, inicioB.Snapshot!.ChannelInstanceId));

        var resposta = await c.ResponderAsync(emA.SessionId, turnoA, inicioA, emA.ConversationId);
        await c.EntregaAsync(resposta.MessageId!.Value, DeliveryState.Failed, "template-rejected");
        var reenvio = await c.PedirReenvioAsync(resposta.MessageId.Value, Guid.NewGuid(), Atendente(c));
        var eventosDeSaida = c.PayloadsDe(ConversationEventTypes.OutboundMessageRequested, emA.SessionId).Cast<OutboundMessageRequestedV1>().ToList();

        Assert.Equal(ResendOutcome.Requested, reenvio.Outcome);
        Assert.Equal(2, eventosDeSaida.Count);
        Assert.All(eventosDeSaida, e => Assert.Equal(a, e.ChannelInstanceId)); // o reenvio sai pelo mesmo número da original
    }

    [Fact]
    public async Task Cada_numero_tem_o_proprio_idioma_estado_e_encerramento_mesmo_para_a_mesma_pessoa()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "numerosindependentes");
        var a = ChannelInstanceId.New();
        var b = ChannelInstanceId.New();
        var emA = await c.EntrarAsync(Pessoa, "Hola", canal: a, idiomaDoContato: "es-ES");
        var emB = await c.EntrarAsync(Pessoa, "Hello", canal: b, idiomaDoContato: "en-US");

        Assert.Equal(Locale.Parse("es-ES"), (await c.SessaoAsync(emA.SessionId))!.EffectiveLocale);
        Assert.Equal(Locale.Parse("en-US"), (await c.SessaoAsync(emB.SessionId))!.EffectiveLocale);

        Assert.True((await c.EncerrarSessaoAsync(emA.SessionId)).Closed);
        var respostaEmB = await c.EntrarAsync(Pessoa, "Still there?", canal: b);
        var novaEmA = await c.EntrarAsync(Pessoa, "¿Sigue abierto?", canal: a);

        Assert.Equal(emB.SessionId, respostaEmB.SessionId); // o número B não foi afetado
        Assert.Equal(SessionStatus.Open, (await c.SessaoAsync(emB.SessionId))!.Status);
        Assert.NotEqual(emA.SessionId, novaEmA.SessionId); // o número A abriu uma sessão nova, e a anterior continua encerrada
        Assert.Equal(SessionStatus.Closed, (await c.SessaoAsync(emA.SessionId))!.Status);
        Assert.Equal(1, (await c.SessaoAsync(emB.SessionId))!.StateVersion);
    }

    // Isolamento entre tenants que compartilham um ChannelInstanceId

    [Fact]
    public async Task Um_tenant_que_usa_o_ChannelInstanceId_de_outro_nao_enxerga_nem_altera_nada_dele()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "donoa");
        var b = await CenarioDeConversas.CriarAsync(banco, "donob");
        var canal = ChannelInstanceId.New(); // por engano ou por ataque, o mesmo identificador nos dois tenants

        var deA = await a.EntrarAsync(Pessoa, "Mensagem do tenant A", canal: canal);
        var deB = await b.EntrarAsync(Pessoa, "Mensagem do tenant B", canal: canal);

        Assert.NotEqual(deA.SessionId, deB.SessionId);
        Assert.Equal(new[] { "Mensagem do tenant A" }, (await a.HistoricoAsync(deA.SessionId))!.Items.Select(m => m.Text));
        Assert.Equal(new[] { "Mensagem do tenant B" }, (await b.HistoricoAsync(deB.SessionId))!.Items.Select(m => m.Text));
        Assert.Null(await b.SessaoAsync(deA.SessionId));
        Assert.Null(await a.SessaoAsync(deB.SessionId));
        Assert.Equal(1, await ConsultaAsync(a.ContextoDeSistema, db => db.Contacts.CountAsync(x => x.ChannelInstanceId == canal)));
        Assert.Equal(1, await ConsultaAsync(b.ContextoDeSistema, db => db.Contacts.CountAsync(x => x.ChannelInstanceId == canal)));

        // O tenant B, com o canal de A, não alcança a conversa de A: nem turno, nem resposta, nem encaminhamento.
        Assert.Equal(BeginTurnOutcome.SessionNotFound, (await b.IniciarTurnoAsync(deA.SessionId, Guid.NewGuid())).Outcome);
        var turnoA = Guid.NewGuid();
        var inicioA = await a.IniciarTurnoAsync(deA.SessionId, turnoA);
        var invasora = await b.ResponderComAsync(new RegisterAiReplyCommand(
            deA.SessionId, turnoA, inicioA.Grant!.Epoch, inicioA.Grant.StateVersion, inicioA.Grant.BasedOnInboundSeq, ReplyKind.Generated, deA.ConversationId, "intruso", new[] { Guid.NewGuid() }));
        Assert.Equal(RegisterReplyOutcome.SessionNotFound, invasora.Outcome);
        Assert.Equal(TransferOutcome.SessionNotFound, (await b.EncaminharAsync(deA.SessionId, Guid.NewGuid(), null, 1)).Outcome);
        Assert.Equal(RegisterReplyOutcome.Accepted, (await a.ResponderAsync(deA.SessionId, turnoA, inicioA, deA.ConversationId, "do dono")).Outcome);
    }

    [Fact]
    public async Task O_tenant_nunca_vem_do_canal_nem_de_nenhum_contrato_de_entrada_do_modulo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "semtenant");
        var outro = await CenarioDeConversas.CriarAsync(banco, "semtenantb");
        var canal = ChannelInstanceId.New();

        // Dois contextos de sistema, dois tenants, o mesmo canal e o mesmo comando: o tenant é sempre o do contexto.
        var comando = new IngestInboundMessageCommand(canal, Pessoa, null, c.OrganizacaoId, null, "wamid.contexto", DateTimeOffset.UtcNow, MessageKind.Text, "Oi");
        var noTenantC = await c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<IConversationIngestion>().IngestInboundMessageAsync(comando));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => outro.ComoAsync(outro.ContextoDeSistema, s => s.GetRequiredService<IConversationIngestion>().IngestInboundMessageAsync(comando)));

        // No outro tenant, a organização do comando não existe (é do tenant C): nada foi gravado lá, e a sessão é só do C.
        Assert.NotNull(await c.SessaoAsync(noTenantC.SessionId));
        Assert.Equal(0, await ConsultaAsync(outro.ContextoDeSistema, db => db.Sessions.CountAsync()));
    }

    // Roteamento por unidade e setor

    [Fact]
    public async Task O_setor_define_a_unidade_e_a_sessao_nasce_roteada_para_os_dois()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "roteamento");
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial Centro");
        var coleta = await c.CriarSetorAsync(filial, "Coleta");

        var soUnidade = await c.EntrarAsync("5511999990001", "Só unidade", unidade: filial);
        var comSetor = await c.EntrarAsync("5511999990002", "Com setor", setor: coleta); // a unidade vem do setor
        var consistente = await c.EntrarAsync("5511999990003", "Unidade e setor", unidade: filial, setor: coleta);
        var semRoteamento = await c.EntrarAsync("5511999990004", "Sem unidade");

        var vistas = new[] { soUnidade, comSetor, consistente, semRoteamento }.Select(r => c.SessaoAsync(r.SessionId).Result!).ToArray();
        Assert.Equal(new (Guid?, Guid?)[] { (filial, null), (filial, coleta), (filial, coleta), (null, null) }, vistas.Select(s => (s.BusinessUnitId, s.DepartmentId)));
        var snapshot = (await c.IniciarTurnoAsync(comSetor.SessionId, Guid.NewGuid())).Snapshot!;
        Assert.Equal((filial, coleta), (snapshot.BusinessUnitId, snapshot.DepartmentId));
    }

    [Fact]
    public async Task Unidade_ou_setor_de_outra_organizacao_de_outro_tenant_ou_inexistente_e_recusado_antes_de_gravar()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "roteamentoinv");
        var outro = await CenarioDeConversas.CriarAsync(banco, "roteamentoinvb");
        var org2 = (await banco.CriarOrganizacaoAsync(c.Tenant, "Segunda organização")).Id;
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var outraFilial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Outra filial");
        var deOutraOrg = await c.CriarUnidadeAsync(org2, "Filial da outra organização");
        var setor = await c.CriarSetorAsync(filial, "Coleta");
        var setorDaOutraOrg = await c.CriarSetorAsync(deOutraOrg, "Setor alheio");
        var unidadeDoOutroTenant = await outro.CriarUnidadeAsync(outro.OrganizacaoId, "Filial de outro tenant");
        var setorDoOutroTenant = await outro.CriarSetorAsync(unidadeDoOutroTenant, "Setor de outro tenant");

        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync(Pessoa, "x", unidade: deOutraOrg));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync(Pessoa, "x", unidade: unidadeDoOutroTenant));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync(Pessoa, "x", unidade: Guid.NewGuid()));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync(Pessoa, "x", setor: Guid.NewGuid()));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync(Pessoa, "x", setor: setorDoOutroTenant));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync(Pessoa, "x", setor: setorDaOutraOrg)); // a unidade do setor é de outra organização
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync(Pessoa, "x", unidade: outraFilial, setor: setor)); // o setor não é dessa unidade

        Assert.Equal(0, await ConsultaAsync(c.ContextoDeSistema, db => db.Sessions.CountAsync()));
        Assert.Equal(0, await ConsultaAsync(c.ContextoDeSistema, db => db.Messages.CountAsync()));
    }

    [Fact]
    public async Task Mensagem_nova_nao_reencaminha_uma_sessao_aberta_e_a_unidade_do_canal_so_vale_na_criacao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "naoreroteia");
        var filialA = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial A");
        var filialB = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial B");
        var r = await c.EntrarAsync(Pessoa, "Primeira", unidade: filialA);
        Assert.Equal(TransferOutcome.Applied, (await c.EncaminharAsync(r.SessionId, filialB, null, 1)).Outcome);

        await c.EntrarAsync(Pessoa, "Segunda, pelo canal que aponta para a filial A", unidade: filialA);

        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((filialB, 2L), (sessao.BusinessUnitId, sessao.StateVersion)); // continua na filial para onde foi encaminhada
    }

    // Encaminhamento entre filiais

    [Fact]
    public async Task O_encaminhamento_entre_filiais_exige_autorizacao_muda_unidade_e_setor_e_deixa_rastro_completo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "encaminha");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial Norte");
        var triagem = await c.CriarSetorAsync(matriz, "Triagem");
        var exames = await c.CriarSetorAsync(filial, "Exames especiais");
        var r = await c.EntrarAsync(Pessoa, "Preciso de exame especial", unidade: matriz, setor: triagem);
        var atendente = Atendente(c);
        var transferId = Guid.NewGuid();

        var feito = await c.EncaminharAsync(r.SessionId, filial, exames, 1, transferId, atendente, "specialty-unit");

        Assert.Equal((TransferOutcome.Applied, (long?)2), (feito.Outcome, feito.StateVersion));
        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((filial, exames, 2L), (sessao.BusinessUnitId, sessao.DepartmentId, sessao.StateVersion));

        // Rastro: de onde, para onde, quem pediu, por quê, em que versão.
        var rastro = Assert.Single((await c.RastroAsync(r.SessionId))!);
        Assert.Equal((transferId, matriz, triagem, filial, exames, atendente.Principal!.UserId, "specialty-unit", 2L), (rastro.TransferId, rastro.FromBusinessUnitId, rastro.FromDepartmentId, rastro.ToBusinessUnitId, rastro.ToDepartmentId, rastro.RequestedBy, rastro.Reason, rastro.StateVersionAfter));

        // Evento e auditoria.
        var evento = Assert.IsType<SessionTransferredV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.SessionTransferred, r.SessionId)));
        Assert.Equal((transferId, matriz, triagem, filial, exames, 2L), (evento.TransferId, evento.FromBusinessUnitId, evento.FromDepartmentId, evento.ToBusinessUnitId, evento.ToDepartmentId, evento.StateVersion));
        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        Assert.Equal(1, await escopo.ServiceProvider.GetRequiredService<AuditDbContext>().Events
            .CountAsync(e => e.Operation == "conversations.session.transfer" && e.ResourceId == r.SessionId.ToString()));

        // A conversa, o contato, o canal e o histórico são os mesmos: só a responsabilidade mudou.
        var historico = (await c.HistoricoAsync(r.SessionId))!.Items;
        Assert.Equal(new[] { "Preciso de exame especial" }, historico.Select(m => m.Text));
        Assert.Equal((r.ConversationId, sessao.ChannelInstanceId), (historico[0].ConversationId, historico[0].ChannelInstanceId));
    }

    [Fact]
    public async Task Sem_a_permissao_de_gerir_a_conversa_nao_ha_encaminhamento_e_a_organizacao_alheia_responde_como_inexistente()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "encaminhaperm");
        var org2 = (await banco.CriarOrganizacaoAsync(c.Tenant, "Segunda organização")).Id;
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var r = await c.EntrarAsync(Pessoa, "Oi", unidade: matriz);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.EncaminharAsync(r.SessionId, filial, null, 1, contexto: c.Usuario(c.OrganizacaoId, Permission.ConversationRead)));
        Assert.Equal(TransferOutcome.SessionNotFound, (await c.EncaminharAsync(r.SessionId, filial, null, 1, contexto: c.Usuario(org2, Permission.ConversationRead, Permission.ConversationManage))).Outcome);
        Assert.Equal(TransferOutcome.SessionNotFound, (await c.EncaminharAsync(r.SessionId, filial, null, 1, contexto: c.Usuario(c.OrganizacaoId))).Outcome);
        Assert.Equal((matriz, 1L), ((await c.SessaoAsync(r.SessionId))!.BusinessUnitId, (await c.SessaoAsync(r.SessionId))!.StateVersion));
        Assert.Empty((await c.RastroAsync(r.SessionId))!);

        // O contexto de sistema (o ContactCenter) pode, e o rastro mostra que não foi um usuário.
        Assert.Equal(TransferOutcome.Applied, (await c.EncaminharAsync(r.SessionId, filial, null, 1)).Outcome);
        Assert.Null(Assert.Single((await c.RastroAsync(r.SessionId))!).RequestedBy);
    }

    [Fact]
    public async Task Destino_invalido_nao_grava_nada_e_o_encaminhamento_so_vale_dentro_da_mesma_organizacao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "encaminhainv");
        var outro = await CenarioDeConversas.CriarAsync(banco, "encaminhainvb");
        var org2 = (await banco.CriarOrganizacaoAsync(c.Tenant, "Segunda organização")).Id;
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var setorDaMatriz = await c.CriarSetorAsync(matriz, "Triagem");
        var deOutraOrg = await c.CriarUnidadeAsync(org2, "Filial alheia");
        var doOutroTenant = await outro.CriarUnidadeAsync(outro.OrganizacaoId, "Filial de outro tenant");
        var r = await c.EntrarAsync(Pessoa, "Oi", unidade: matriz);

        var destinos = new[]
        {
            (Unidade: Guid.NewGuid(), Setor: (Guid?)null), // não existe
            (Unidade: deOutraOrg, Setor: null), // de outra organização do mesmo tenant
            (Unidade: doOutroTenant, Setor: null), // de outro tenant
            (Unidade: filial, Setor: setorDaMatriz), // o setor não é da unidade de destino
            (Unidade: filial, Setor: Guid.NewGuid()), // o setor não existe
        };
        foreach (var (unidade, setor) in destinos)
        {
            var resultado = await c.EncaminharAsync(r.SessionId, unidade, setor, 1);
            Assert.Equal((TransferOutcome.TargetNotAllowed, (long?)1), (resultado.Outcome, resultado.StateVersion));
        }

        Assert.Equal((matriz, 1L), ((await c.SessaoAsync(r.SessionId))!.BusinessUnitId, (await c.SessaoAsync(r.SessionId))!.StateVersion));
        Assert.Empty((await c.RastroAsync(r.SessionId))!);
        Assert.Equal(0, c.EventosDe(ConversationEventTypes.SessionTransferred, r.SessionId));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EncaminharAsync(r.SessionId, filial, null, 1, motivo: "Texto livre com espaços"));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EncaminharAsync(r.SessionId, Guid.Empty, null, 1));
    }

    [Fact]
    public async Task O_encaminhamento_e_idempotente_respeita_a_versao_esperada_e_o_estado_da_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "encaminhaidem");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var outra = await c.CriarUnidadeAsync(c.OrganizacaoId, "Outra filial");
        var r = await c.EntrarAsync(Pessoa, "Oi", unidade: matriz);
        var transferId = Guid.NewGuid();

        var primeiro = await c.EncaminharAsync(r.SessionId, filial, null, 1, transferId);
        var repetido = await c.EncaminharAsync(r.SessionId, filial, null, 1, transferId); // a mesma ação repetida
        var semMudar = await c.EncaminharAsync(r.SessionId, filial, null, 2); // já está lá
        var atrasado = await c.EncaminharAsync(r.SessionId, outra, null, 1); // baseado numa versão antiga
        var encadeado = await c.EncaminharAsync(r.SessionId, outra, null, 2);

        Assert.Equal((TransferOutcome.Applied, (long?)2), (primeiro.Outcome, primeiro.StateVersion));
        Assert.Equal((TransferOutcome.AlreadyApplied, (long?)2), (repetido.Outcome, repetido.StateVersion));
        Assert.Equal((TransferOutcome.Unchanged, (long?)2), (semMudar.Outcome, semMudar.StateVersion));
        Assert.Equal((TransferOutcome.StaleState, (long?)2), (atrasado.Outcome, atrasado.StateVersion));
        Assert.Equal((TransferOutcome.Applied, (long?)3), (encadeado.Outcome, encadeado.StateVersion));

        // O rastro é só o que aconteceu, em ordem: matriz -> filial -> outra filial.
        var rastro = (await c.RastroAsync(r.SessionId))!;
        Assert.Equal(new (Guid?, Guid)[] { (matriz, filial), (filial, outra) }, rastro.Select(t => (t.FromBusinessUnitId, t.ToBusinessUnitId)));
        Assert.Equal(2, c.EventosDe(ConversationEventTypes.SessionTransferred, r.SessionId));

        // Sessão encerrada não é encaminhada, mas a repetição de um encaminhamento antigo continua devolvendo o resultado dele.
        Assert.True((await c.EncerrarSessaoAsync(r.SessionId)).Closed);
        Assert.Equal(TransferOutcome.SessionClosed, (await c.EncaminharAsync(r.SessionId, matriz, null, 4)).Outcome);
        Assert.Equal(TransferOutcome.AlreadyApplied, (await c.EncaminharAsync(r.SessionId, filial, null, 1, transferId)).Outcome);
        Assert.Equal(TransferOutcome.SessionNotFound, (await c.EncaminharAsync(Guid.NewGuid(), filial, null, 1)).Outcome);
    }

    [Fact]
    public async Task Encaminhamentos_simultaneos_aplicam_um_so_e_a_repeticao_simultanea_da_mesma_acao_aplica_uma_vez()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "encaminhacorrida");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filiais = new List<Guid>();
        for (var i = 1; i <= 10; i++)
        {
            filiais.Add(await c.CriarUnidadeAsync(c.OrganizacaoId, $"Filial {i}"));
        }

        var r = await c.EntrarAsync(Pessoa, "Oi", unidade: matriz);
        var outra = await c.EntrarAsync("5511999990002", "Oi", unidade: matriz);

        var concorrentes = await Task.WhenAll(filiais.Select(f => c.EncaminharAsync(r.SessionId, f, null, 1, contexto: Atendente(c))));
        var mesmaAcao = Guid.NewGuid();
        var repeticoes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => c.EncaminharAsync(outra.SessionId, filiais[0], null, 1, mesmaAcao)));

        Assert.Equal(1, concorrentes.Count(x => x.Outcome == TransferOutcome.Applied));
        Assert.Equal(9, concorrentes.Count(x => x.Outcome == TransferOutcome.StaleState));
        Assert.Single((await c.RastroAsync(r.SessionId))!);
        Assert.Equal(2, (await c.SessaoAsync(r.SessionId))!.StateVersion);
        Assert.Equal(1, repeticoes.Count(x => x.Outcome == TransferOutcome.Applied));
        Assert.Equal(19, repeticoes.Count(x => x.Outcome == TransferOutcome.AlreadyApplied));
        Assert.Single((await c.RastroAsync(outra.SessionId))!);
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.SessionTransferred, outra.SessionId));
    }

    [Fact]
    public async Task O_encaminhamento_invalida_a_resposta_da_IA_montada_para_a_unidade_anterior()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "encaminhaturno");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var r = await c.EntrarAsync(Pessoa, "Preciso de ajuda", unidade: matriz);
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        Assert.Equal(matriz, inicio.Snapshot!.BusinessUnitId);

        await c.EncaminharAsync(r.SessionId, filial, null, inicio.Grant!.StateVersion);
        var tardia = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId, "Resposta com o contexto da matriz");
        var novoTurno = Guid.NewGuid();
        var novoInicio = await c.IniciarTurnoAsync(r.SessionId, novoTurno, lease: 2);
        var nova = await c.ResponderAsync(r.SessionId, novoTurno, novoInicio, r.ConversationId, "Resposta com o contexto da filial");

        Assert.Equal(RegisterReplyOutcome.StaleState, tardia.Outcome);
        Assert.Equal(filial, novoInicio.Snapshot!.BusinessUnitId);
        Assert.Equal(RegisterReplyOutcome.Accepted, nova.Outcome);
    }

    [Fact]
    public async Task A_leitura_do_rastro_respeita_tenant_organizacao_e_permissao()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "rastroa");
        var b = await CenarioDeConversas.CriarAsync(banco, "rastrob");
        var org2 = (await banco.CriarOrganizacaoAsync(a.Tenant, "Segunda organização")).Id;
        var matriz = await a.CriarUnidadeAsync(a.OrganizacaoId, "Matriz", matriz: true);
        var filial = await a.CriarUnidadeAsync(a.OrganizacaoId, "Filial");
        var r = await a.EntrarAsync(Pessoa, "Oi", unidade: matriz);
        await a.EncaminharAsync(r.SessionId, filial, null, 1);

        Assert.Single((await a.RastroAsync(r.SessionId, a.Usuario(a.OrganizacaoId, Permission.ConversationRead)))!);
        Assert.Null(await a.RastroAsync(r.SessionId, a.Usuario(a.OrganizacaoId))); // sem leitura
        Assert.Null(await a.RastroAsync(r.SessionId, a.Usuario(org2, Permission.ConversationRead))); // outra organização
        Assert.Null(await b.RastroAsync(r.SessionId)); // outro tenant, mesmo com contexto de sistema
        Assert.Null(await a.RastroAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task O_banco_recusa_setor_sem_unidade_e_protege_o_rastro_de_encaminhamentos()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "bancorastro");
        var b = await CenarioDeConversas.CriarAsync(banco, "bancorastrob");
        var matriz = await a.CriarUnidadeAsync(a.OrganizacaoId, "Matriz", matriz: true);
        var filial = await a.CriarUnidadeAsync(a.OrganizacaoId, "Filial");
        var setor = await a.CriarSetorAsync(matriz, "Triagem");
        var r = await a.EntrarAsync(Pessoa, "Oi", unidade: matriz, setor: setor);
        await a.EncaminharAsync(r.SessionId, filial, null, 1);
        var tenant = a.Tenant.TenantId.Value;

        var semUnidade = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(a.ContextoDeSistema,
            "update conversations.service_sessions set business_unit_id = null, department_id = {2} where tenant_id = {0} and id = {1}", tenant, r.SessionId, setor));
        Assert.Equal("ck_service_sessions_department", semUnidade.ConstraintName);

        var setorSemUnidade = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(a.ContextoDeSistema,
            "insert into conversations.session_transfers (tenant_id, session_id, transfer_id, from_department_id, to_business_unit_id, reason, state_version_after, transferred_at) values ({0}, {1}, {2}, {3}, {4}, 'x', 9, now())",
            tenant, r.SessionId, Guid.NewGuid(), setor, filial));
        Assert.Equal("ck_session_transfers_departments", setorSemUnidade.ConstraintName);

        var versaoInvalida = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(a.ContextoDeSistema,
            "insert into conversations.session_transfers (tenant_id, session_id, transfer_id, to_business_unit_id, reason, state_version_after, transferred_at) values ({0}, {1}, {2}, {3}, 'x', 1, now())",
            tenant, r.SessionId, Guid.NewGuid(), filial));
        Assert.Equal("ck_session_transfers_state_version", versaoInvalida.ConstraintName);

        var mesmaVersao = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(a.ContextoDeSistema,
            "insert into conversations.session_transfers (tenant_id, session_id, transfer_id, to_business_unit_id, reason, state_version_after, transferred_at) values ({0}, {1}, {2}, {3}, 'x', 2, now())",
            tenant, r.SessionId, Guid.NewGuid(), filial));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, mesmaVersao.SqlState);

        var deOutroTenant = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(b.ContextoDeSistema,
            "insert into conversations.session_transfers (tenant_id, session_id, transfer_id, to_business_unit_id, reason, state_version_after, transferred_at) values ({0}, {1}, {2}, {3}, 'x', 5, now())",
            b.Tenant.TenantId.Value, r.SessionId, Guid.NewGuid(), filial));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, deOutroTenant.SqlState);

        // No DbContext: o rastro de um tenant não existe para o outro, nem se grava em nome dele, nem sem contexto.
        Assert.Equal(1, await ConsultaAsync(a.ContextoDeSistema, db => db.Transfers.CountAsync()));
        Assert.Equal(0, await ConsultaAsync(b.ContextoDeSistema, db => db.Transfers.CountAsync()));
        await using (var escopoB = banco.NovoEscopo(b.ContextoDeSistema))
        {
            var dbB = escopoB.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            dbB.Transfers.Add(SessionTransfer.Create(a.Tenant.TenantId, r.SessionId, Guid.NewGuid(), null, null, filial, null, null, "x", 7, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<IaBimeta.Conversations.Infrastructure.CrossTenantAccessException>(() => dbB.SaveChangesAsync());
        }

        await using var semContexto = banco.NovoEscopo();
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => semContexto.ServiceProvider.GetRequiredService<ConversationsDbContext>().Transfers.CountAsync());
    }

    [Fact]
    public async Task Nenhum_contrato_do_modulo_carrega_TenantId_o_tenant_so_vem_do_contexto_confiavel()
    {
        var contratos = typeof(IConversationIngestion).Assembly.GetExportedTypes();

        var comTenant = contratos
            .SelectMany(t => t.GetProperties().Select(p => (Tipo: t, Nome: p.Name, p.PropertyType))
                .Concat(t.GetConstructors().SelectMany(k => k.GetParameters().Select(p => (Tipo: t, Nome: p.Name ?? string.Empty, PropertyType: p.ParameterType))))
                .Concat(t.GetMethods().SelectMany(m => m.GetParameters().Select(p => (Tipo: t, Nome: p.Name ?? string.Empty, PropertyType: p.ParameterType)))))
            .Where(x => x.PropertyType == typeof(TenantId) || x.Nome.Equals("TenantId", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Tipo.Name}.{x.Nome}")
            .Distinct()
            .ToList();

        Assert.NotEmpty(contratos); // a regra tem o que verificar
        Assert.Empty(comTenant);
        await Task.CompletedTask;
    }
}
