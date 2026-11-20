using System.Reflection;
using IaBimeta.Audit.Contracts;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Tests;

/// <summary>E2.4 (ADR 0041, seções 4 e 5): as regras do consentimento sem banco (o armazenamento é um dublê em memória que só enxerga o que a porta permite).</summary>
public sealed class ConsentServiceTests
{
    private static readonly TenantId Tenant = new TenantId(Guid.NewGuid());
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conexao = Guid.NewGuid();
    private static readonly Guid Sessao = Guid.NewGuid();
    private static readonly DateTimeOffset Agora = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class Relogio(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }

    private sealed class Armazenamento : IConsentStore
    {
        public List<ContactConsent> Linhas { get; } = new();

        public List<OptOutReview> Ocorrencias { get; } = new();

        public List<EventEnvelope> Auditoria { get; } = new();

        public ConsentSessionInfo? Sessao { get; set; } = new(Org, Guid.NewGuid(), "5511999990101");

        public int Confirmacoes { get; private set; }

        public int Travas { get; private set; }

        public Task<IConsentUnitOfWork> BeginAsync(CancellationToken cancellationToken) => Task.FromResult<IConsentUnitOfWork>(new Trabalho(this));

        private sealed class Trabalho(Armazenamento a) : IConsentUnitOfWork
        {
            private readonly List<ContactConsent> _novas = new();

            public Task<ConsentSessionInfo?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken) => Task.FromResult(sessionId == ConsentServiceTests.Sessao ? a.Sessao : null);

            public Task LockPhoneAsync(Guid organizationId, string phone, CancellationToken cancellationToken)
            {
                a.Travas++;
                return Task.CompletedTask;
            }

            public Task LockReviewAsync(Guid messageId, CancellationToken cancellationToken)
            {
                a.Travas++;
                return Task.CompletedTask;
            }

            public Task<ContactConsent?> GetLatestAsync(Guid organizationId, string phone, ConsentPurpose purpose, CancellationToken cancellationToken) =>
                Task.FromResult(a.Linhas.Concat(_novas).LastOrDefault(l => l.OrganizationId == organizationId && l.Phone == phone && l.Purpose == purpose));

            public Task<DateTimeOffset?> GetLatestRevocationCollectedAtAsync(Guid organizationId, string phone, ConsentPurpose purpose, CancellationToken cancellationToken) =>
                Task.FromResult(a.Linhas.Concat(_novas).Where(l => l.OrganizationId == organizationId && l.Phone == phone && l.Purpose == purpose && l.Status == ConsentStatus.Revoked).Select(l => (DateTimeOffset?)l.CollectedAt).Max());

            public void Add(ContactConsent consent) => _novas.Add(consent);

            public Task<OptOutReview?> FindReviewAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken) =>
                Task.FromResult(a.Ocorrencias.FirstOrDefault(o => o.SessionId == sessionId && o.MessageId == messageId));

            public Task CommitAsync(IReadOnlyCollection<EventEnvelope> events, CancellationToken cancellationToken)
            {
                a.Linhas.AddRange(_novas);
                a.Auditoria.AddRange(events);
                a.Confirmacoes++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Acessor(RequestContext? contexto) : IRequestContextAccessor
    {
        public RequestContext? Current => contexto;
    }

    private static RequestContext Contexto(string origem, ActorPrincipal? principal)
    {
        var ctor = typeof(RequestContext).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(TenantId), typeof(string), typeof(ActorPrincipal) })!;
        return (RequestContext)ctor.Invoke(new object?[] { Tenant, origem, principal });
    }

    private static RequestContext Usuario(Guid? usuario = null, Guid? organizacao = null, EmbeddedScope? embutido = null, bool comPermissao = true)
    {
        var concessoes = comPermissao ? new[] { new PermissionGrant(Permission.ConsentManage, organizacao ?? Org) } : Array.Empty<PermissionGrant>();
        return Contexto("session", new ActorPrincipal(usuario ?? Guid.NewGuid(), Guid.NewGuid(), concessoes, embutido));
    }

    private static ConsentService Servico(Armazenamento armazenamento, RequestContext? contexto) => new(armazenamento, new Acessor(contexto), new Relogio(Agora));

    private static ConsentRecordRequest Pedido(ConsentStatus situacao = ConsentStatus.Granted, ConsentPurpose finalidade = ConsentPurpose.Utility, DateTimeOffset? coleta = null, string? evidencia = "Aceite verbal") =>
        new(finalidade, situacao, evidencia, coleta ?? Agora.AddHours(-3));

    [Fact]
    public async Task Sem_ConsentManage_ou_em_sessao_embutida_ou_sem_usuario_o_registro_e_negado_e_nada_e_gravado()
    {
        var a = new Armazenamento();

        await Assert.ThrowsAsync<AccessDeniedException>(() => Servico(a, Usuario(comPermissao: false)).RecordForSessionContactAsync(Sessao, Pedido()));
        // Embutida: o escopo da conexão é a mesma organização, mas a permissão não é concedida ao Embedded.
        await Assert.ThrowsAsync<AccessDeniedException>(() => Servico(a, Usuario(embutido: new EmbeddedScope(Org, CustomerOnly: false), comPermissao: false)).RecordForSessionContactAsync(Sessao, Pedido()));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Servico(a, Contexto("system", null)).RecordForSessionContactAsync(Sessao, Pedido()));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Servico(a, Usuario(comPermissao: false)).ResolveOptOutReviewAsync(Sessao, Guid.NewGuid(), OptOutResolution.Kept));

        // ConsentManage numa OUTRA organização não vale para esta.
        await Assert.ThrowsAsync<AccessDeniedException>(() => Servico(a, Usuario(organizacao: Guid.NewGuid())).RecordForSessionContactAsync(Sessao, Pedido()));

        Assert.Empty(a.Linhas);
        Assert.Empty(a.Auditoria);
        Assert.Equal(0, a.Confirmacoes);
    }

    [Fact]
    public async Task Sessao_inexistente_ou_fora_do_escopo_embutido_e_nao_encontrada_sem_oraculo()
    {
        var a = new Armazenamento();

        Assert.Equal(ConsentRecordOutcome.NotFound, (await Servico(a, Usuario()).RecordForSessionContactAsync(Guid.NewGuid(), Pedido())).Outcome);
        // Sessão embutida com outra organização como teto: a sessão é "inexistente" para ela (antes de qualquer verificação de permissão).
        Assert.Equal(ConsentRecordOutcome.NotFound, (await Servico(a, Usuario(embutido: new EmbeddedScope(Guid.NewGuid(), CustomerOnly: false))).RecordForSessionContactAsync(Sessao, Pedido())).Outcome);
        Assert.Equal(OptOutResolveOutcome.NotFound, (await Servico(a, Usuario()).ResolveOptOutReviewAsync(Guid.NewGuid(), Guid.NewGuid(), OptOutResolution.Kept)).Outcome);
        Assert.Empty(a.Linhas);
    }

    [Fact]
    public async Task Registro_do_atendente_grava_com_origem_autor_sessao_e_telefone_do_contato_e_repetir_e_idempotente()
    {
        var a = new Armazenamento();
        var usuario = Guid.NewGuid();
        var s = Servico(a, Usuario(usuario));

        var primeiro = await s.RecordForSessionContactAsync(Sessao, Pedido());
        Assert.Equal((ConsentRecordOutcome.Recorded, ConsentPurpose.Utility, ConsentStatus.Granted), (primeiro.Outcome, primeiro.Purpose!.Value, primeiro.Status!.Value));

        var linha = Assert.Single(a.Linhas);
        Assert.Equal(("+5511999990101", Org, ConsentOrigin.AgentRecorded, ActorKind.User, usuario, Sessao), (linha.Phone, linha.OrganizationId, linha.Origin, linha.ActorKind, linha.ActorId, linha.SessionId));
        Assert.Single(a.Auditoria);

        // Repetir (mesma finalidade, situação e data da coleta): nada novo, nem auditoria.
        Assert.Equal(ConsentRecordOutcome.Unchanged, (await s.RecordForSessionContactAsync(Sessao, Pedido())).Outcome);
        Assert.Single(a.Linhas);
        Assert.Single(a.Auditoria);
    }

    [Fact]
    public async Task Coleta_com_precisao_maior_que_microssegundos_e_normalizada_e_a_repeticao_continua_idempotente()
    {
        var a = new Armazenamento();
        var s = Servico(a, Usuario());
        var coleta = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.FromHours(-3)).AddTicks(7); // 7 ticks = 0,7 µs: o banco guardaria só os microssegundos

        Assert.Equal(ConsentRecordOutcome.Recorded, (await s.RecordForSessionContactAsync(Sessao, Pedido(coleta: coleta))).Outcome);
        Assert.Equal(0, a.Linhas[0].CollectedAt.Ticks % 10);
        Assert.Equal(ConsentRecordOutcome.Unchanged, (await s.RecordForSessionContactAsync(Sessao, Pedido(coleta: coleta))).Outcome);
    }

    [Fact]
    public async Task Revogar_depois_de_consentir_e_novo_consentimento_so_depois_da_revogacao_e_o_registro_defasado_e_recusado()
    {
        var a = new Armazenamento();
        var s = Servico(a, Usuario());
        var t1 = Agora.AddHours(-5);
        var t2 = Agora.AddHours(-4);
        var t3 = Agora.AddHours(-3);

        Assert.Equal(ConsentRecordOutcome.Recorded, (await s.RecordForSessionContactAsync(Sessao, Pedido(coleta: t1))).Outcome);
        Assert.Equal(ConsentRecordOutcome.Recorded, (await s.RecordForSessionContactAsync(Sessao, Pedido(ConsentStatus.Revoked, coleta: t2))).Outcome);
        Assert.Equal(ConsentRecordOutcome.Superseded, (await s.RecordForSessionContactAsync(Sessao, Pedido(coleta: t1))).Outcome); // atrasado
        Assert.Equal(ConsentRecordOutcome.Superseded, (await s.RecordForSessionContactAsync(Sessao, Pedido(coleta: t2))).Outcome); // mesma data da revogação
        Assert.Equal(2, a.Linhas.Count);
        Assert.Equal(ConsentRecordOutcome.Recorded, (await s.RecordForSessionContactAsync(Sessao, Pedido(coleta: t3))).Outcome); // novo consentimento de verdade
        Assert.Equal(ConsentRecordOutcome.Recorded, (await s.RecordForSessionContactAsync(Sessao, Pedido(ConsentStatus.Revoked, coleta: t1))).Outcome); // revogar nunca é recusado por data
        Assert.Equal(4, a.Linhas.Count);

        // A outra finalidade é independente: nada a ver com a revogação de Utility.
        Assert.Equal(ConsentRecordOutcome.Recorded, (await s.RecordForSessionContactAsync(Sessao, Pedido(finalidade: ConsentPurpose.Marketing, coleta: t1))).Outcome);
    }

    // P1 da 1.ª auditoria da E2 (Codex): revogação ATRASADA reduzia a referência temporal e deixava reativar o consentimento. A concessão é comparada com a MAIOR coleta de revogação da chave.
    [Fact]
    public async Task Revogacao_atrasada_nao_reduz_a_referencia_e_a_concessao_anterior_a_revogacao_mais_recente_e_recusada()
    {
        var a = new Armazenamento();
        var s = Servico(a, Usuario());
        var h12 = Agora.AddHours(-12);
        var h11 = Agora.AddHours(-13);
        var h10 = Agora.AddHours(-14);

        Assert.Equal(ConsentRecordOutcome.Recorded, (await s.RecordForSessionContactAsync(Sessao, Pedido(ConsentStatus.Revoked, coleta: h12))).Outcome);
        Assert.Equal(ConsentRecordOutcome.Recorded, (await s.RecordForSessionContactAsync(Sessao, Pedido(ConsentStatus.Revoked, coleta: h10))).Outcome); // revogação antiga continua aceita
        Assert.Equal(ConsentRecordOutcome.Superseded, (await s.RecordForSessionContactAsync(Sessao, Pedido(coleta: h11))).Outcome); // anterior à revogação mais recente: recusada
        Assert.Equal(ConsentRecordOutcome.Superseded, (await s.RecordForSessionContactAsync(Sessao, Pedido(coleta: h12))).Outcome); // igual: recusada
        Assert.Equal(new[] { ConsentStatus.Revoked, ConsentStatus.Revoked }, a.Linhas.Select(l => l.Status).ToArray()); // o estado efetivo continua revogado

        // Uma concessão posterior à maior revogação é aceita; outra finalidade é independente.
        Assert.Equal(ConsentRecordOutcome.Recorded, (await s.RecordForSessionContactAsync(Sessao, Pedido(coleta: Agora.AddHours(-11)))).Outcome);
        Assert.Equal(ConsentRecordOutcome.Recorded, (await s.RecordForSessionContactAsync(Sessao, Pedido(finalidade: ConsentPurpose.Marketing, coleta: h11))).Outcome);
    }

    [Theory]
    [InlineData(null, "evidence-required")]
    [InlineData("   ", "evidence-required")]
    public async Task Evidencia_obrigatoria_e_data_da_coleta_obrigatoria_e_nao_futura_sao_recusadas_com_o_codigo(string? evidencia, string codigo)
    {
        var a = new Armazenamento();
        var s = Servico(a, Usuario());

        var r = await s.RecordForSessionContactAsync(Sessao, Pedido(evidencia: evidencia));

        Assert.Equal((ConsentRecordOutcome.Invalid, codigo), (r.Outcome, r.RejectionCode));
        Assert.Empty(a.Linhas);
        Assert.Equal(0, a.Confirmacoes);
    }

    [Fact]
    public async Task Evidencia_longa_data_ausente_ou_futura_e_enumeracoes_invalidas_sao_recusadas()
    {
        var a = new Armazenamento();
        var s = Servico(a, Usuario());

        Assert.Equal("evidence-too-long", (await s.RecordForSessionContactAsync(Sessao, Pedido(evidencia: new string('e', 2001)))).RejectionCode);
        Assert.Equal("collected-at-invalid", (await s.RecordForSessionContactAsync(Sessao, new ConsentRecordRequest(ConsentPurpose.Utility, ConsentStatus.Granted, "x", null))).RejectionCode);
        Assert.Equal("collected-at-invalid", (await s.RecordForSessionContactAsync(Sessao, Pedido(coleta: Agora.AddMinutes(6)))).RejectionCode);
        Assert.Equal(ConsentRecordOutcome.Recorded, (await s.RecordForSessionContactAsync(Sessao, Pedido(coleta: Agora.AddMinutes(4)))).Outcome); // dentro da tolerância do relógio
        Assert.Equal("invalid-purpose", (await s.RecordForSessionContactAsync(Sessao, Pedido(finalidade: (ConsentPurpose)99))).RejectionCode);
        Assert.Equal("invalid-status", (await s.RecordForSessionContactAsync(Sessao, Pedido(situacao: (ConsentStatus)0))).RejectionCode);
        Assert.Single(a.Linhas);
    }

    [Fact]
    public async Task Contato_de_referencia_opaca_nao_tem_telefone_e_nada_e_gravado()
    {
        var a = new Armazenamento { Sessao = new ConsentSessionInfo(Org, Guid.NewGuid(), "BR.opaco-9f3k2") };

        Assert.Equal(ConsentRecordOutcome.PhoneUnavailable, (await Servico(a, Usuario()).RecordForSessionContactAsync(Sessao, Pedido())).Outcome);
        Assert.Empty(a.Linhas);
    }

    [Fact]
    public async Task A_auditoria_cabe_no_identificador_da_trilha_e_nunca_leva_telefone_evidencia_ou_cliente()
    {
        var a = new Armazenamento();
        var s = Servico(a, Usuario());
        var t = Agora.AddHours(-10);
        foreach (var finalidade in new[] { ConsentPurpose.Utility, ConsentPurpose.Marketing })
        {
            foreach (var situacao in new[] { ConsentStatus.Granted, ConsentStatus.Revoked })
            {
                await s.RecordForSessionContactAsync(Sessao, Pedido(situacao, finalidade, t = t.AddMinutes(1), "evidência SECRETA 999990101"));
            }
        }

        Assert.Equal(4, a.Auditoria.Count);
        foreach (var evento in a.Auditoria)
        {
            var pedido = Assert.IsType<ConversationAuditRequestedV1>(evento.Payload);
            Assert.Equal("consent.record", pedido.Operation);
            Assert.True(pedido.ResourceId!.Length <= 100, $"O recurso da trilha passa de 100 caracteres: {pedido.ResourceId.Length}.");
            Assert.Matches("^[A-Za-z0-9_.:-]+$", pedido.ResourceId); // o padrão aceito pela trilha
            Assert.DoesNotContain("999990101", pedido.ResourceId);
            Assert.DoesNotContain("SECRETA", pedido.ResourceId);
        }
    }

    [Fact]
    public async Task Integracao_so_com_o_contexto_de_integracao_e_valida_telefone_estrito_organizacao_da_conexao()
    {
        var a = new Armazenamento();
        var integracao = Servico(a, Contexto(ContactLinkService.IntegrationContextOrigin, null));
        var coleta = Agora.AddHours(-1);

        var r = await integracao.RecordByIntegrationAsync(Conexao, Org, "CLI-1", "+55 11 99999-0101", ConsentPurpose.Marketing, ConsentStatus.Granted, "Cadastro", coleta);
        Assert.Equal(ConsentRecordOutcome.Recorded, r.Outcome);
        var linha = Assert.Single(a.Linhas);
        Assert.Equal((Org, ConsentOrigin.IntegrationAsserted, ActorKind.Integration, (Guid?)Conexao, "CLI-1", "+5511999990101"), (linha.OrganizationId, linha.Origin, linha.ActorKind, linha.ActorId, linha.CustomerRef, linha.Phone));

        // Telefone sem prefixo internacional: recusado com o código (pode ser nacional).
        var invalido = await Assert.ThrowsAsync<ConversationsValidationException>(() => integracao.RecordByIntegrationAsync(Conexao, Org, "CLI-1", "5511999990101", ConsentPurpose.Utility, ConsentStatus.Granted, "x", coleta));
        Assert.Equal("invalid-phone", invalido.Code);
        Assert.Equal("invalid-customer-ref", (await Assert.ThrowsAsync<ConversationsValidationException>(() => integracao.RecordByIntegrationAsync(Conexao, Org, " ", "+5511999990101", ConsentPurpose.Utility, ConsentStatus.Granted, "x", coleta))).Code);

        // Um usuário de sessão, o contexto de sistema e uma origem diferente NUNCA chamam a operação de integração.
        await Assert.ThrowsAsync<AccessDeniedException>(() => Servico(a, Usuario()).RecordByIntegrationAsync(Conexao, Org, "CLI-1", "+5511999990101", ConsentPurpose.Utility, ConsentStatus.Granted, "x", coleta));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Servico(a, Contexto("system", null)).RecordByIntegrationAsync(Conexao, Org, "CLI-1", "+5511999990101", ConsentPurpose.Utility, ConsentStatus.Granted, "x", coleta));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => integracao.RecordByIntegrationAsync(Conexao, Guid.Empty, "CLI-1", "+5511999990101", ConsentPurpose.Utility, ConsentStatus.Granted, "x", coleta));
        Assert.Single(a.Linhas);
    }

    // ------------------------------------------------------------------ ocorrência de possível saída

    private static OptOutReview Ocorrencia(Guid? organizacao = null, Guid? mensagem = null) =>
        OptOutReview.Open(Tenant, organizacao ?? Org, Sessao, mensagem ?? Guid.NewGuid(), OptOutReviewReason.ConversationalContext, " sair ", Agora.AddHours(-1));

    [Fact]
    public void A_ocorrencia_guarda_a_palavra_normalizada_nunca_texto_livre_e_resolve_uma_unica_vez()
    {
        var o = Ocorrencia();
        Assert.Equal("SAIR", o.Keyword);
        Assert.False(o.IsResolved);

        var usuario = Guid.NewGuid();
        Assert.True(o.Resolve(OptOutResolution.Kept, usuario, Agora));
        Assert.Equal((OptOutResolution.Kept, usuario, Agora), (o.Resolution!.Value, o.ResolvedBy!.Value, o.ResolvedAt!.Value));
        Assert.False(o.Resolve(OptOutResolution.Revoked, Guid.NewGuid(), Agora.AddMinutes(1))); // nunca duas vezes
        Assert.Equal(OptOutResolution.Kept, o.Resolution);
        Assert.Equal(usuario, o.ResolvedBy);

        Assert.Throws<ConversationsValidationException>(() => OptOutReview.Open(Tenant, Org, Sessao, Guid.NewGuid(), OptOutReviewReason.FlowFreeText, new string('p', 33), Agora));
        Assert.Throws<ConversationsValidationException>(() => OptOutReview.Open(Tenant, Guid.Empty, Sessao, Guid.NewGuid(), OptOutReviewReason.FlowFreeText, null, Agora));
        Assert.Throws<ConversationsValidationException>(() => Ocorrencia().Resolve(OptOutResolution.Kept, Guid.Empty, Agora));
    }

    [Fact]
    public async Task Revoked_revoga_todas_as_finalidades_ainda_nao_revogadas_com_origem_do_atendente_e_Kept_nao_altera_o_consentimento()
    {
        var a = new Armazenamento();
        var usuario = Guid.NewGuid();
        var s = Servico(a, Usuario(usuario));
        var mensagem = Guid.NewGuid();
        a.Ocorrencias.Add(Ocorrencia(mensagem: mensagem));
        await s.RecordForSessionContactAsync(Sessao, Pedido(finalidade: ConsentPurpose.Utility));
        await s.RecordForSessionContactAsync(Sessao, Pedido(ConsentStatus.Revoked, ConsentPurpose.Marketing));
        var antes = a.Linhas.Count;

        var r = await s.ResolveOptOutReviewAsync(Sessao, mensagem, OptOutResolution.Revoked);

        Assert.Equal((OptOutResolveOutcome.Resolved, OptOutResolution.Revoked), (r.Outcome, r.Resolution!.Value));
        var nova = Assert.Single(a.Linhas.Skip(antes)); // só Utility: Marketing já estava revogada
        Assert.Equal((ConsentPurpose.Utility, ConsentStatus.Revoked, ConsentOrigin.AgentRecorded, usuario, mensagem), (nova.Purpose, nova.Status, nova.Origin, nova.ActorId!.Value, nova.SourceMessageId!.Value));
        Assert.DoesNotContain("sair", nova.Evidence.Replace("SAIR", string.Empty), StringComparison.Ordinal); // a evidência traz a palavra normalizada, nunca texto livre
        Assert.Contains(a.Auditoria, e => e.Payload is ConversationAuditRequestedV1 { Operation: "consent.possible-optout-resolve" });

        // A mesma decisão: idempotente; a oposta: conflito. Nenhuma das duas grava nem audita.
        var confirmacoes = a.Confirmacoes;
        Assert.Equal(OptOutResolveOutcome.AlreadyResolved, (await s.ResolveOptOutReviewAsync(Sessao, mensagem, OptOutResolution.Revoked)).Outcome);
        Assert.Equal(OptOutResolveOutcome.Conflict, (await s.ResolveOptOutReviewAsync(Sessao, mensagem, OptOutResolution.Kept)).Outcome);
        Assert.Equal(confirmacoes, a.Confirmacoes);

        // Kept não toca o consentimento.
        var outra = Guid.NewGuid();
        a.Ocorrencias.Add(Ocorrencia(mensagem: outra));
        var linhasAntes = a.Linhas.Count;
        Assert.Equal(OptOutResolveOutcome.Resolved, (await s.ResolveOptOutReviewAsync(Sessao, outra, OptOutResolution.Kept)).Outcome);
        Assert.Equal(linhasAntes, a.Linhas.Count);
    }

    [Fact]
    public async Task Ocorrencia_de_outra_organizacao_e_inexistente_e_telefone_opaco_nao_revoga_e_deixa_pendente()
    {
        var a = new Armazenamento();
        var s = Servico(a, Usuario());
        var deOutraOrg = Guid.NewGuid();
        a.Ocorrencias.Add(Ocorrencia(organizacao: Guid.NewGuid(), mensagem: deOutraOrg));
        Assert.Equal(OptOutResolveOutcome.NotFound, (await s.ResolveOptOutReviewAsync(Sessao, deOutraOrg, OptOutResolution.Kept)).Outcome);
        Assert.Equal(OptOutResolveOutcome.NotFound, (await s.ResolveOptOutReviewAsync(Sessao, Guid.NewGuid(), OptOutResolution.Kept)).Outcome);
        Assert.False(a.Ocorrencias[0].IsResolved);

        a.Sessao = new ConsentSessionInfo(Org, Guid.NewGuid(), "BR.opaco-9f3k2");
        var daSessao = Guid.NewGuid();
        a.Ocorrencias.Add(Ocorrencia(mensagem: daSessao));
        Assert.Equal(OptOutResolveOutcome.PhoneUnavailable, (await s.ResolveOptOutReviewAsync(Sessao, daSessao, OptOutResolution.Revoked)).Outcome);
        Assert.False(a.Ocorrencias[1].IsResolved);
        Assert.Empty(a.Linhas);
        await Assert.ThrowsAsync<ConversationsValidationException>(() => s.ResolveOptOutReviewAsync(Sessao, daSessao, (OptOutResolution)7));
    }

    // ------------------------------------------------------------------ imutabilidade

    [Fact]
    public void O_consentimento_nao_tem_caminho_de_alteracao_nem_exclusao_no_dominio_nem_na_porta_de_gravacao()
    {
        // Domínio: nenhuma propriedade pública com setter e nenhum método público de instância que mude estado (só a comparação de repetição).
        var tipo = typeof(ContactConsent);
        Assert.All(tipo.GetProperties(BindingFlags.Instance | BindingFlags.Public), p => Assert.True(p.SetMethod is null || !p.SetMethod.IsPublic, $"{p.Name} tem setter público."));
        var metodos = tipo.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).Select(m => m.Name).ToList();
        Assert.Equal(new[] { nameof(ContactConsent.IsSameAssertionAs) }, metodos);

        // Porta de gravação: só se ACRESCENTA consentimento (Add). Nenhum método de atualização ou exclusão.
        var porta = typeof(IConsentUnitOfWork).GetMethods().Select(m => m.Name).ToList();
        Assert.Contains(nameof(IConsentUnitOfWork.Add), porta);
        Assert.DoesNotContain(porta, n => n.Contains("Update", StringComparison.OrdinalIgnoreCase) || n.Contains("Delete", StringComparison.OrdinalIgnoreCase) || n.Contains("Remove", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(IConsentReader).GetMethods().Select(m => m.Name), n => n.Contains("Update", StringComparison.OrdinalIgnoreCase) || n.Contains("Delete", StringComparison.OrdinalIgnoreCase));
    }
}
