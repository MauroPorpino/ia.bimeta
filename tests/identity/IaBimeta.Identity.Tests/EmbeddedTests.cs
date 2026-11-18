using System.Security.Cryptography;
using System.Text;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;

namespace IaBimeta.Identity.Tests;

/// <summary>Domínio e regras do Embedded (ADR 0039) sem banco: origens, chaves, tickets, PKCE, assinatura, teto de permissões, elegibilidade e o provisionamento que nunca vincula por e-mail.</summary>
public class EmbeddedDomainTests
{
    [Theory]
    [InlineData("https://erp.exemplo.com.br", "https://erp.exemplo.com.br")]
    [InlineData("HTTPS://ERP.Exemplo.com.br:443", "https://erp.exemplo.com.br")]
    [InlineData("https://erp.exemplo.com.br:8443", "https://erp.exemplo.com.br:8443")]
    [InlineData("http://localhost:5100", "http://localhost:5100")]
    [InlineData("http://127.0.0.1:5100", "http://127.0.0.1:5100")]
    [InlineData("http://erp.localhost:8080", "http://erp.localhost:8080")]
    [InlineData("https://erp.exemplo.com.br/", "https://erp.exemplo.com.br")]
    public void Origem_valida_e_normalizada(string entrada, string esperada)
    {
        Assert.True(IntegrationConnection.TryNormalizeOrigin(entrada, out var origem));
        Assert.Equal(esperada, origem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("erp.exemplo.com.br")]
    [InlineData("http://erp.exemplo.com.br")]
    [InlineData("https://*.exemplo.com.br")]
    [InlineData("https://erp.exemplo.com.br/painel")]
    [InlineData("https://erp.exemplo.com.br/?x=1")]
    [InlineData("https://erp.exemplo.com.br#frag")]
    [InlineData("https://usuario:senha@erp.exemplo.com.br")]
    [InlineData("ftp://erp.exemplo.com.br")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://erp exemplo.com.br")]
    public void Origem_invalida_e_recusada(string entrada)
    {
        Assert.False(IntegrationConnection.TryNormalizeOrigin(entrada, out _));
    }

    [Fact]
    public void Conexao_exige_de_1_a_10_origens_nome_e_modo_validos_e_nasce_sem_inicio_ativo()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var agora = DateTimeOffset.UtcNow;
        var origens = Enumerable.Range(0, 11).Select(i => $"https://erp{i}.exemplo.com.br").ToArray();

        Assert.Throws<IdentityValidationException>(() => IntegrationConnection.Create(tenant, Guid.NewGuid(), "ERP", EmbeddedMode.FullAttendant, ProvisioningMode.MappedOnly, origens, false, agora, null));
        Assert.Throws<IdentityValidationException>(() => IntegrationConnection.Create(tenant, Guid.NewGuid(), "ERP", EmbeddedMode.FullAttendant, ProvisioningMode.MappedOnly, Array.Empty<string>(), false, agora, null));
        Assert.Throws<IdentityValidationException>(() => IntegrationConnection.Create(tenant, Guid.NewGuid(), " ", EmbeddedMode.FullAttendant, ProvisioningMode.MappedOnly, new[] { "https://a.exemplo.com.br" }, false, agora, null));
        Assert.Throws<IdentityValidationException>(() => IntegrationConnection.Create(tenant, Guid.Empty, "ERP", EmbeddedMode.FullAttendant, ProvisioningMode.MappedOnly, new[] { "https://a.exemplo.com.br" }, false, agora, null));
        Assert.Throws<IdentityValidationException>(() => IntegrationConnection.Create(tenant, Guid.NewGuid(), "ERP", (EmbeddedMode)99, ProvisioningMode.MappedOnly, new[] { "https://a.exemplo.com.br" }, false, agora, null));

        var ok = IntegrationConnection.Create(tenant, Guid.NewGuid(), "ERP", EmbeddedMode.CustomerOnly, ProvisioningMode.JitAttendant, new[] { "https://a.exemplo.com.br", "https://A.exemplo.com.br" }, false, agora, null);
        Assert.Equal(new[] { "https://a.exemplo.com.br" }, ok.AllowedOrigins);
        Assert.False(ok.AllowStartConversation);
        Assert.True(ok.IsOriginAllowed("https://A.exemplo.com.br:443"));
        Assert.False(ok.IsOriginAllowed("https://b.exemplo.com.br"));
    }

    [Fact]
    public void Chave_publica_tem_formato_fixo_e_a_chave_revogada_ou_vencida_nunca_e_utilizavel()
    {
        var (id, segredo) = IntegrationKey.GenerateIdentifiers();
        var tenant = new TenantId(Guid.NewGuid());
        var agora = DateTimeOffset.UtcNow;
        var chave = IntegrationKey.Create(tenant, Guid.NewGuid(), id, "t1", "v1:x", agora, null);

        Assert.StartsWith("ibsk_", segredo);
        Assert.Equal(5 + 43, segredo.Length);
        Assert.True(IntegrationKey.TryParsePublicId(chave.PublicId, out var lido));
        Assert.Equal(id, lido);
        Assert.False(IntegrationKey.TryParsePublicId("ibk_" + new string('z', 32), out _));
        Assert.False(IntegrationKey.TryParsePublicId("ibk_" + Guid.Empty.ToString("N"), out _));
        Assert.False(IntegrationKey.TryParsePublicId(null, out _));
        Assert.False(IntegrationKey.TryParsePublicId(id.ToString("N"), out _));

        Assert.True(chave.IsUsableAt(agora));
        chave.ExpireAt(agora.AddHours(1));
        Assert.True(chave.IsUsableAt(agora.AddMinutes(59)));
        Assert.False(chave.IsUsableAt(agora.AddHours(1)));

        // O fim programado só encurta, nunca prolonga.
        chave.ExpireAt(agora.AddDays(3));
        Assert.False(chave.IsUsableAt(agora.AddHours(2)));

        Assert.True(chave.Revoke(agora));
        Assert.False(chave.Revoke(agora));
        Assert.False(chave.IsUsableAt(agora));
    }

    [Fact]
    public void Ticket_tem_formato_fixo_e_so_o_hash_sai_do_gerador_e_lixo_nem_vira_hash()
    {
        var (token, hash) = TicketTokens.Generate();

        Assert.StartsWith("ibt_", token);
        Assert.Equal(4 + 43, token.Length);
        Assert.Equal(32, hash.Length);
        Assert.True(TicketTokens.TryHash(token, out var recalculado));
        Assert.Equal(hash, recalculado);
        Assert.NotEqual(token, TicketTokens.Generate().Token);

        foreach (var lixo in new[] { null, "", "ibt_", "ibs_" + new string('a', 43), "ibt_" + new string('a', 42), "ibt_" + new string('!', 43), " " + token })
        {
            Assert.False(TicketTokens.TryHash(lixo, out _), $"'{lixo}' não deveria ser aceito");
        }
    }

    [Fact]
    public void PKCE_confere_o_vetor_da_RFC_7636_e_recusa_verifier_fora_do_formato_ou_de_outro_challenge()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        Assert.Equal(challenge, PkceVerifier.ComputeChallenge(verifier));
        Assert.True(PkceVerifier.IsValidChallenge(challenge));
        Assert.True(PkceVerifier.Matches(challenge, verifier));
        Assert.False(PkceVerifier.Matches(challenge, verifier.Replace('d', 'e')));
        Assert.False(PkceVerifier.Matches(challenge, verifier[..^1]));
        Assert.False(PkceVerifier.IsValidVerifier(new string('a', 42)));
        Assert.False(PkceVerifier.IsValidVerifier(new string('a', 129)));
        Assert.False(PkceVerifier.IsValidVerifier(new string('a', 43) + "!"));
        Assert.False(PkceVerifier.IsValidChallenge(challenge + "="));
        Assert.False(PkceVerifier.IsValidChallenge(null));
    }

    [Fact]
    public void Assinatura_HMAC_do_servidor_e_a_do_contrato_e_nenhuma_forma_alternativa_do_caminho_vale()
    {
        var corpo = Encoding.UTF8.GetBytes("{\"externalUserId\":\"u-1\"}");
        const string segredo = "ibsk_segredo-de-teste";
        const string nonce = "n0nc3-n0nc3-n0nc3-123";

        // Calculado aqui, de forma independente, pelo que o ADR 0039 descreve.
        var canonico = $"v1\n1700000000\n{nonce}\nPOST\n/embed/v1/launch-tickets\n{Convert.ToHexStringLower(SHA256.HashData(corpo))}";
        var esperado = HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes(canonico));

        Assert.Equal(canonico, IntegrationRequestAuthenticator.CanonicalString("1700000000", nonce, "post", "/embed/v1/launch-tickets", corpo));
        Assert.Equal(esperado, IntegrationRequestAuthenticator.ComputeSignature(segredo, "1700000000", nonce, "POST", "/embed/v1/launch-tickets", corpo));
        // O prefixo do proxy NÃO é ignorado (E1-R): com /api a assinatura é outra, ninguém assina uma forma alternativa do caminho.
        Assert.NotEqual(esperado, IntegrationRequestAuthenticator.ComputeSignature(segredo, "1700000000", nonce, "POST", "/api/embed/v1/launch-tickets", corpo));
        Assert.NotEqual(esperado, IntegrationRequestAuthenticator.ComputeSignature(segredo, "1700000000", nonce, "POST", "/embed/v1/launch-tickets/", corpo));
        Assert.NotEqual(esperado, IntegrationRequestAuthenticator.ComputeSignature(segredo, "1700000000", nonce, "POST", "/EMBED/v1/launch-tickets", corpo));
        Assert.NotEqual(esperado, IntegrationRequestAuthenticator.ComputeSignature(segredo, "1700000001", nonce, "POST", "/embed/v1/launch-tickets", corpo));
        Assert.NotEqual(esperado, IntegrationRequestAuthenticator.ComputeSignature(segredo, "1700000000", nonce, "PUT", "/embed/v1/launch-tickets", corpo));
        Assert.NotEqual(esperado, IntegrationRequestAuthenticator.ComputeSignature(segredo, "1700000000", nonce, "POST", "/embed/v1/context-tickets", corpo));
    }

    [Fact]
    public void Usuario_federado_tem_email_sintetico_unico_nenhuma_senha_serve_e_nome_sem_controle()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var hasher = new PasswordHasher(Senhas.Rapidas());

        var a = User.CreateFederated(tenant, "  Maria\u0007  ", DateTimeOffset.UtcNow);
        var b = User.CreateFederated(tenant, null, DateTimeOffset.UtcNow);

        Assert.Equal(UserAuthMode.Federated, a.AuthMode);
        Assert.EndsWith("@federated.invalid", a.Email);
        Assert.NotEqual(a.Email, b.Email);
        Assert.Equal("Usuário externo", b.DisplayName);
        Assert.Equal(UserAuthMode.Password, User.Create(tenant, "x@y.com", "X", "h", DateTimeOffset.UtcNow).AuthMode);
        foreach (var senha in new[] { "!federated", "", "qualquer", Senhas.Valida })
        {
            Assert.Equal(PasswordVerification.Failed, hasher.Verify(senha, a.PasswordHash));
        }
    }

    [Fact]
    public void Sessao_embutida_e_curta_leva_a_conexao_e_a_interativa_nao()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var agora = DateTimeOffset.UtcNow;
        var hash = SHA256.HashData(Guid.NewGuid().ToByteArray());

        var interativa = Session.Create(tenant, Guid.NewGuid(), hash, agora, TimeSpan.FromHours(8));
        var embutida = Session.CreateEmbedded(tenant, Guid.NewGuid(), hash, agora, TimeSpan.FromMinutes(30), Guid.NewGuid(), "u-1", "https://erp.exemplo.com.br");

        Assert.Equal(SessionKind.Interactive, interativa.Kind);
        Assert.Null(interativa.IntegrationId);
        Assert.Equal(SessionKind.Embedded, embutida.Kind);
        Assert.Equal("u-1", embutida.ExternalUserId);
        Assert.True(embutida.IsActiveAt(agora.AddMinutes(29)));
        Assert.False(embutida.IsActiveAt(agora.AddMinutes(30)));
        Assert.Throws<ArgumentException>(() => Session.CreateEmbedded(tenant, Guid.NewGuid(), hash, agora, TimeSpan.FromMinutes(30), Guid.Empty, "u", "https://e.com"));
        Assert.Throws<ArgumentException>(() => Session.CreateEmbedded(tenant, Guid.NewGuid(), hash, agora, TimeSpan.FromMinutes(30), Guid.NewGuid(), " ", "https://e.com"));
    }

    [Fact]
    public void Ticket_valida_challenge_cliente_e_identificador_externo()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var hash = SHA256.HashData(Guid.NewGuid().ToByteArray());
        var challenge = PkceVerifier.ComputeChallenge(new string('a', 43));
        var agora = DateTimeOffset.UtcNow;

        var ticket = LaunchTicket.CreateLaunch(tenant, Guid.NewGuid(), hash, challenge, "https://erp.exemplo.com.br", "u-1", Guid.NewGuid(), "  CLI-1 ", agora, TimeSpan.FromSeconds(60));
        Assert.Equal("CLI-1", ticket.CustomerRef);
        Assert.Equal(agora.AddSeconds(60), ticket.ExpiresAt);
        Assert.Equal(TicketKind.Launch, ticket.Kind);

        Assert.Throws<IdentityValidationException>(() => LaunchTicket.CreateLaunch(tenant, Guid.NewGuid(), hash, "curto", "https://e.com", "u", Guid.NewGuid(), null, agora, TimeSpan.FromSeconds(60)));
        Assert.Throws<IdentityValidationException>(() => LaunchTicket.CreateLaunch(tenant, Guid.NewGuid(), hash, challenge, "https://e.com", "u", Guid.NewGuid(), new string('c', 129), agora, TimeSpan.FromSeconds(60)));
        Assert.Throws<IdentityValidationException>(() => LaunchTicket.CreateContext(tenant, Guid.NewGuid(), hash, "u", Guid.NewGuid(), " ", agora, TimeSpan.FromSeconds(60)));
        Assert.Throws<IdentityValidationException>(() => ExternalIdentity.NormalizeExternalUserId(new string('x', 129)));
        Assert.Throws<IdentityValidationException>(() => ExternalIdentity.NormalizeExternalUserId("a\nb"));
        Assert.Equal("Ab-1", ExternalIdentity.NormalizeExternalUserId("  Ab-1 "));
        Assert.NotEqual(ExternalIdentity.NormalizeExternalUserId("Ab"), ExternalIdentity.NormalizeExternalUserId("ab"));
    }

    [Fact]
    public void Atendente_nao_tem_permissao_administrativa_e_so_o_administrador_do_tenant_tem_EmbedManage()
    {
        var org = Guid.NewGuid();
        var atendente = RolePermissions.Expand(Role.Attendant, org).Select(g => g.Permission).ToHashSet();

        Assert.Equal(
            new[] { Permission.OrganizationRead, Permission.StructureRead, Permission.ConversationRead, Permission.ConversationManage, Permission.ChannelRead, Permission.QueueRead }.ToHashSet(),
            atendente);
        Assert.True(atendente.IsSubsetOf(EmbeddedSessionPolicy.AllowedPermissions));

        Assert.DoesNotContain(RolePermissions.Expand(Role.OrganizationAdmin, org), g => g.Permission == Permission.EmbedManage);
        Assert.DoesNotContain(RolePermissions.Expand(Role.Manager, org), g => g.Permission == Permission.EmbedManage);
        Assert.Contains(RolePermissions.Expand(Role.OrganizationAdmin, null), g => g.Permission == Permission.EmbedManage && g.OrganizationId is null);
    }

    [Fact]
    public void O_teto_da_sessao_embutida_so_deixa_o_atendimento_e_so_da_organizacao_da_conexao()
    {
        var org = Guid.NewGuid();
        var outra = Guid.NewGuid();
        var concessoes = RolePermissions.Expand(Role.Manager, org)
            .Concat(RolePermissions.Expand(Role.OrganizationAdmin, outra))
            .Concat(RolePermissions.Expand(Role.OrganizationAdmin, null));

        var filtradas = EmbeddedSessionPolicy.Filter(concessoes, org);

        Assert.NotEmpty(filtradas);
        Assert.All(filtradas, g => Assert.Equal(org, g.OrganizationId));
        Assert.All(filtradas, g => Assert.Contains(g.Permission, EmbeddedSessionPolicy.AllowedPermissions));
        Assert.DoesNotContain(filtradas, g => g.Permission is Permission.StructureManage or Permission.UserRead or Permission.UserManage or Permission.WorkflowRead or Permission.IntegrationRead or Permission.EmbedManage or Permission.AuditRead or Permission.SystemOperation);
        Assert.Equal(6, filtradas.Count);
    }

    [Fact]
    public void Elegibilidade_recusa_conta_administrativa_de_qualquer_escopo_e_exige_acesso_a_organizacao_da_conexao()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var usuario = Guid.NewGuid();
        var org = Guid.NewGuid();
        var agora = DateTimeOffset.UtcNow;
        UserOrganizationGrant G(Guid? o, Role r) => UserOrganizationGrant.Create(tenant, usuario, o, r, agora, null);

        Assert.True(EmbeddedSessionPolicy.IsEligible(new[] { G(org, Role.Attendant) }, org, out _));
        Assert.True(EmbeddedSessionPolicy.IsEligible(new[] { G(org, Role.Manager) }, org, out _));
        Assert.True(EmbeddedSessionPolicy.IsEligible(new[] { G(org, Role.Attendant), G(Guid.NewGuid(), Role.User) }, org, out _));
        Assert.False(EmbeddedSessionPolicy.IsEligible(new[] { G(org, Role.OrganizationAdmin) }, org, out var motivo));
        Assert.Contains("administrativa", motivo);
        Assert.False(EmbeddedSessionPolicy.IsEligible(new[] { G(org, Role.Attendant), G(Guid.NewGuid(), Role.OrganizationAdmin) }, org, out _));
        Assert.False(EmbeddedSessionPolicy.IsEligible(new[] { G(null, Role.User) }, org, out _));
        Assert.False(EmbeddedSessionPolicy.IsEligible(new[] { G(Guid.NewGuid(), Role.Attendant) }, org, out var semAcesso));
        Assert.Contains("organização", semAcesso);
        Assert.False(EmbeddedSessionPolicy.IsEligible(Array.Empty<UserOrganizationGrant>(), org, out _));
    }
}

/// <summary>Os serviços do Embedded com a persistência em memória: o provisionamento, a elegibilidade, o consumo do ticket e a autenticação da sessão embutida a CADA requisição.</summary>
public class EmbeddedServicesTests
{
    private sealed class Cenario
    {
        public RelogioManual Relogio { get; } = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));

        public IdentidadeEmMemoria Dados { get; } = new();

        public EmbeddedEmMemoria Embedded { get; } = new();

        public TenantsEmMemoria Tenants { get; } = new();

        public AuditoriaEmMemoria Auditoria { get; } = new();

        public IdentityOptions Opcoes { get; } = Senhas.Rapidas();

        public ProtetorDeTeste Protetor { get; } = new();

        public RequestContextHolder Holder { get; } = new();

        public TenantInfo Tenant { get; }

        public Guid Organizacao { get; } = Guid.NewGuid();

        public IntegrationConnection Conexao { get; private set; } = null!;

        public Cenario(ProvisioningMode provisionamento = ProvisioningMode.JitAttendant)
        {
            Embedded.Identidade = Dados;
            Tenant = Tenants.Adicionar("lab-a");
            Conexao = IntegrationConnection.Create(
                Tenant.TenantId, Organizacao, "ERP", EmbeddedMode.FullAttendant, provisionamento, new[] { "https://erp.exemplo.com.br" }, false, Relogio.GetUtcNow(), null);
            Embedded.Conexoes.Add(Conexao);
            Chave = IntegrationKey.Create(Tenant.TenantId, Conexao.Id, Guid.NewGuid(), "id1", "cifrado", Relogio.GetUtcNow(), null);
            Embedded.Chaves.Add(Chave);
        }

        public EmbeddedTicketService Tickets => new(Embedded, Dados, Opcoes, Relogio, Auditoria);

        public EmbeddedExchangeService Troca => new(Embedded, Dados, Tenants, Opcoes, Holder, Relogio, Auditoria);

        public SessionAuthenticator Autenticador => new(Dados, Embedded, Tenants, Relogio);

        public IntegrationKey Chave { get; private set; } = null!;

        public AuthenticatedIntegration Integracao => new(Tenant.TenantId, Conexao.Id, Chave.Id, Organizacao, Conexao.Mode, Conexao.Provisioning, Conexao.AllowedOrigins, false);

        public User NovoUsuario(string email, Role? papel, Guid? organizacao = null)
        {
            var usuario = User.Create(Tenant.TenantId, email, "Pessoa", "hash", Relogio.GetUtcNow());
            Dados.Usuarios.Add(usuario);
            if (papel is { } r)
            {
                Dados.Concessoes.Add(UserOrganizationGrant.Create(Tenant.TenantId, usuario.Id, organizacao ?? Organizacao, r, Relogio.GetUtcNow(), null));
            }

            return usuario;
        }

        public IssueLaunchTicketRequest Pedido(string externalUserId, string verifier, string? nome = "Ana", string origem = "https://erp.exemplo.com.br", string? cliente = null) =>
            new(externalUserId, nome, PkceVerifier.ComputeChallenge(verifier), origem, cliente);
    }

    private static readonly string Verifier = new('v', 50);

    [Fact]
    public async Task JIT_cria_usuario_federado_e_nunca_vincula_a_conta_existente_pelo_email()
    {
        var c = new Cenario();
        var existente = c.NovoUsuario("ana@exemplo.com", Role.OrganizationAdmin);

        var ticket = await c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("ana@exemplo.com", Verifier, nome: "ana@exemplo.com"));

        var provisionado = Assert.Single(c.Embedded.Provisionados);
        Assert.NotEqual(existente.Id, provisionado.Usuario.Id);
        Assert.EndsWith("@federated.invalid", provisionado.Usuario.Email);
        Assert.Equal(Role.Attendant, provisionado.Concessao.Role);
        Assert.Equal(c.Organizacao, provisionado.Concessao.OrganizationId);
        Assert.Single(c.Embedded.Vinculos);
        Assert.Equal(IdentitySource.Jit, c.Embedded.Vinculos[0].Source);
        Assert.StartsWith("ibt_", ticket.Ticket);
        Assert.Equal(c.Relogio.GetUtcNow().AddSeconds(60), ticket.ExpiresAt);
    }

    [Fact]
    public async Task MappedOnly_recusa_nao_mapeado_e_nao_cria_nada_e_audita_a_negacao()
    {
        var c = new Cenario(ProvisioningMode.MappedOnly);

        await Assert.ThrowsAsync<ExternalIdentityNotAllowedException>(() => c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("u-1", Verifier)));

        Assert.Empty(c.Embedded.Provisionados);
        Assert.Empty(c.Embedded.Vinculos);
        Assert.Empty(c.Embedded.Tickets);
        Assert.Contains(c.Auditoria.Eventos, e => e.Entrada.Operation == "identity.embed.ticket.issue" && e.Entrada.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Denied);
    }

    [Fact]
    public async Task Chave_revogada_entre_a_autenticacao_e_a_gravacao_nao_emite_ticket_de_abertura_nem_de_contexto()
    {
        // E1-R (2.ª rodada): a integração foi autenticada com a chave ainda ativa; a gravação do ticket acontece sob a trava da chave e a recusa se ela foi revogada no meio.
        var c = new Cenario();
        var integracao = c.Integracao;
        _ = await c.Tickets.IssueLaunchTicketAsync(integracao, c.Pedido("u-1", Verifier)); // cria o vínculo e prova que a chave ativa emite
        c.Chave.Revoke(c.Relogio.GetUtcNow());
        var antes = c.Embedded.Tickets.Count;

        await Assert.ThrowsAsync<EmbeddedAuthenticationException>(() => c.Tickets.IssueLaunchTicketAsync(integracao, c.Pedido("u-1", Verifier)));
        await Assert.ThrowsAsync<ExternalIdentityNotAllowedException>(() => c.Tickets.IssueContextTicketAsync(integracao, "u-1", Guid.NewGuid(), "CLI-1")); // sem sessão alvo nem chega à gravação

        Assert.Equal(antes, c.Embedded.Tickets.Count);
        Assert.Contains(c.Auditoria.Eventos, e => e.Entrada.Operation == "identity.embed.ticket.issue" && e.Entrada.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Denied);
    }

    [Fact]
    public async Task Identidade_mapeada_para_conta_que_virou_administrativa_nao_recebe_ticket()
    {
        var c = new Cenario(ProvisioningMode.MappedOnly);
        var usuario = c.NovoUsuario("a@exemplo.com", Role.Attendant);
        c.Embedded.Vinculos.Add(ExternalIdentity.Create(c.Tenant.TenantId, c.Conexao.Id, "u-1", usuario.Id, IdentitySource.Mapped, c.Relogio.GetUtcNow(), null));
        _ = await c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("u-1", Verifier));

        c.Dados.Concessoes.Clear();
        c.Dados.Concessoes.Add(UserOrganizationGrant.Create(c.Tenant.TenantId, usuario.Id, c.Organizacao, Role.OrganizationAdmin, c.Relogio.GetUtcNow(), null));

        await Assert.ThrowsAsync<ExternalIdentityNotAllowedException>(() => c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("u-1", Verifier)));
    }

    [Fact]
    public async Task Ticket_de_abertura_recusa_origem_fora_da_lista_challenge_invalido_e_identificador_vazio()
    {
        var c = new Cenario();

        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("u-1", Verifier, origem: "https://atacante.exemplo.com")));
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Tickets.IssueLaunchTicketAsync(c.Integracao, new IssueLaunchTicketRequest("u-1", "A", "curto", "https://erp.exemplo.com.br", null)));
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Tickets.IssueLaunchTicketAsync(c.Integracao, new IssueLaunchTicketRequest(" ", "A", PkceVerifier.ComputeChallenge(Verifier), "https://erp.exemplo.com.br", null)));
        Assert.Empty(c.Embedded.Tickets);
    }

    [Fact]
    public async Task Troca_cria_sessao_embutida_curta_com_o_cliente_em_foco_e_so_uma_vez()
    {
        var c = new Cenario();
        var ticket = await c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("u-1", Verifier, cliente: "CLI-1"));

        var troca = await c.Troca.ExchangeAsync(ticket.Ticket, Verifier, "https://erp.exemplo.com.br");

        Assert.StartsWith("ibe_", troca.Token);
        Assert.Equal(c.Relogio.GetUtcNow().AddMinutes(30), troca.ExpiresAt);
        Assert.Equal("CLI-1", troca.CustomerRef);
        Assert.Equal(c.Organizacao, troca.OrganizationId);
        var sessao = Assert.Single(c.Embedded.Sessoes);
        Assert.Equal(SessionKind.Embedded, sessao.Kind);
        Assert.Equal(c.Conexao.Id, sessao.IntegrationId);
        Assert.Contains((c.Tenant.TenantId, sessao.Id, "CLI-1"), c.Embedded.Clientes);
        await Assert.ThrowsAsync<EmbeddedAuthenticationException>(() => c.Troca.ExchangeAsync(ticket.Ticket, Verifier, "https://erp.exemplo.com.br"));
    }

    [Fact]
    public async Task Contencao_ao_concluir_a_troca_nao_gasta_o_ticket_e_a_repeticao_funciona_uma_unica_vez()
    {
        var c = new Cenario();
        var ticket = await c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("u-1", Verifier, cliente: "CLI-1"));

        // Antes: o ticket era consumido ANTES da transação que grava a sessão; um 503 nela deixava o ticket gasto e a repetição respondia 401.
        c.Embedded.FalhaAoConcluir = new EmbeddedBusyException(new TimeoutException());
        await Assert.ThrowsAsync<EmbeddedBusyException>(() => c.Troca.ExchangeAsync(ticket.Ticket, Verifier, "https://erp.exemplo.com.br"));
        Assert.Empty(c.Embedded.TicketsUsados);
        Assert.Empty(c.Embedded.Sessoes);

        c.Embedded.FalhaAoConcluir = null;
        var troca = await c.Troca.ExchangeAsync(ticket.Ticket, Verifier, "https://erp.exemplo.com.br");
        Assert.StartsWith("ibe_", troca.Token);

        // O uso único continua: o terceiro uso é recusado e só existe uma sessão.
        await Assert.ThrowsAsync<EmbeddedAuthenticationException>(() => c.Troca.ExchangeAsync(ticket.Ticket, Verifier, "https://erp.exemplo.com.br"));
        Assert.Single(c.Embedded.Sessoes);
    }

    [Fact]
    public async Task O_sucesso_da_troca_e_auditado_pelo_outbox_junto_do_efeito_e_nao_pela_gravacao_sincrona()
    {
        var c = new Cenario();
        var ticket = await c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("u-1", Verifier));

        var troca = await c.Troca.ExchangeAsync(ticket.Ticket, Verifier, "https://erp.exemplo.com.br");

        // Antes: o sucesso era gravado na trilha DEPOIS do commit (uma falha ali deixava o ticket consumido com a resposta perdida). Agora a entrada vai com o efeito, com o ator e a origem da hora da operação.
        var pendente = Assert.Single(c.Embedded.AuditoriaPendente, a => a.Operation == "identity.embed.exchange");
        Assert.Equal("identity.embed.exchange", pendente.Operation);
        Assert.Equal(troca.SessionId.ToString(), pendente.ResourceId);
        Assert.Equal(IaBimeta.Audit.Contracts.AuditOutcome.Success, pendente.Outcome);
        Assert.Equal(troca.User.UserId, pendente.ActorUserId);
        Assert.Equal(PendingAudit.AuthenticationOrigin, pendente.ActorOrigin);
        Assert.Equal(c.Relogio.GetUtcNow(), pendente.CreatedAt);
        Assert.DoesNotContain(c.Auditoria.Eventos, e => e.Entrada.Operation == "identity.embed.exchange" && e.Entrada.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Success);
    }

    [Fact]
    public async Task A_emissao_do_ticket_e_o_provisionamento_sao_auditados_pelo_outbox_e_nao_pela_gravacao_sincrona()
    {
        var c = new Cenario();

        var ticket = await c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("u-1", Verifier));

        Assert.Contains(c.Embedded.AuditoriaPendente, a => a.Operation == "identity.embed.ticket.issue" && a.ResourceType == "launch_ticket" && a.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Success);
        Assert.DoesNotContain(c.Auditoria.Eventos, e => e.Entrada.Operation == "identity.embed.ticket.issue" && e.Entrada.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Success);
        Assert.False(string.IsNullOrEmpty(ticket.Ticket));
    }

    [Theory]
    [InlineData("verifier")]
    [InlineData("verifier-malformado")]
    [InlineData("origem-invalida")]
    [InlineData("origem")]
    [InlineData("vencido")]
    [InlineData("conexao")]
    [InlineData("vinculo")]
    [InlineData("tenant")]
    public async Task Toda_falha_da_troca_e_a_mesma_excecao_e_queima_o_ticket(string cenario)
    {
        var c = new Cenario();
        var ticket = await c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("u-1", Verifier));
        var verifier = Verifier;
        var origem = "https://erp.exemplo.com.br";
        var troca = c.Troca;
        switch (cenario)
        {
            case "verifier":
                verifier = new string('w', 50);
                break;
            case "verifier-malformado":
                verifier = "curto";
                break;
            case "origem-invalida":
                origem = "nao-e-uma-origem";
                break;
            case "origem":
                origem = "https://outro.exemplo.com.br";
                break;
            case "vencido":
                c.Relogio.Avancar(TimeSpan.FromSeconds(61));
                break;
            case "conexao":
                c.Conexao.SetStatus(ConnectionStatus.Disabled, c.Relogio.GetUtcNow());
                break;
            case "vinculo":
                c.Embedded.Vinculos[0].SetStatus(ExternalIdentityStatus.Disabled);
                break;
            case "tenant":
                c.Tenants.Suspender(c.Tenant.TenantId);
                break;
        }

        await Assert.ThrowsAsync<EmbeddedAuthenticationException>(() => troca.ExchangeAsync(ticket.Ticket, verifier, origem));

        Assert.Empty(c.Embedded.Sessoes);
        // O ticket não volta: nem com tudo certo depois (exceto o vencido, que já estava fora do prazo).
        c.Conexao.SetStatus(ConnectionStatus.Active, c.Relogio.GetUtcNow());
        await Assert.ThrowsAsync<EmbeddedAuthenticationException>(() => c.Troca.ExchangeAsync(ticket.Ticket, Verifier, "https://erp.exemplo.com.br"));
    }

    [Fact]
    public async Task Sessao_embutida_so_leva_o_teto_do_atendimento_na_organizacao_da_conexao()
    {
        var c = new Cenario(ProvisioningMode.MappedOnly);
        var gerente = c.NovoUsuario("g@exemplo.com", Role.Manager);
        c.Dados.Concessoes.Add(UserOrganizationGrant.Create(c.Tenant.TenantId, gerente.Id, Guid.NewGuid(), Role.User, c.Relogio.GetUtcNow(), null));
        c.Embedded.Vinculos.Add(ExternalIdentity.Create(c.Tenant.TenantId, c.Conexao.Id, "u-g", gerente.Id, IdentitySource.Mapped, c.Relogio.GetUtcNow(), null));
        var ticket = await c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("u-g", Verifier));
        var troca = await c.Troca.ExchangeAsync(ticket.Ticket, Verifier, "https://erp.exemplo.com.br");

        var principal = await c.Autenticador.AuthenticateAsync(troca.Token);

        Assert.NotNull(principal);
        Assert.Equal(SessionKind.Embedded, principal!.Kind);
        Assert.Equal(c.Conexao.Id, principal.IntegrationId);
        Assert.All(principal.Permissions, g => Assert.Equal(c.Organizacao, g.OrganizationId));
        Assert.All(principal.Permissions, g => Assert.Contains(g.Permission, EmbeddedSessionPolicy.AllowedPermissions));
        Assert.DoesNotContain(principal.Permissions, g => g.Permission is Permission.StructureManage or Permission.WorkflowRead or Permission.UserRead);
    }

    [Theory]
    [InlineData("conexao")]
    [InlineData("vinculo")]
    [InlineData("promovido")]
    [InlineData("sem-acesso")]
    [InlineData("revogada")]
    [InlineData("expirada")]
    [InlineData("tenant")]
    public async Task A_sessao_embutida_e_conferida_contra_o_estado_atual_a_cada_requisicao(string mudanca)
    {
        var c = new Cenario();
        var ticket = await c.Tickets.IssueLaunchTicketAsync(c.Integracao, c.Pedido("u-1", Verifier));
        var troca = await c.Troca.ExchangeAsync(ticket.Ticket, Verifier, "https://erp.exemplo.com.br");
        Assert.NotNull(await c.Autenticador.AuthenticateAsync(troca.Token));
        var autenticador = c.Autenticador;

        switch (mudanca)
        {
            case "conexao":
                c.Conexao.SetStatus(ConnectionStatus.Disabled, c.Relogio.GetUtcNow());
                break;
            case "vinculo":
                c.Embedded.Vinculos[0].SetStatus(ExternalIdentityStatus.Disabled);
                break;
            case "promovido":
                c.Dados.Concessoes.Clear();
                c.Dados.Concessoes.Add(UserOrganizationGrant.Create(c.Tenant.TenantId, c.Embedded.Vinculos[0].UserId, c.Organizacao, Role.OrganizationAdmin, c.Relogio.GetUtcNow(), null));
                break;
            case "sem-acesso":
                c.Dados.Concessoes.Clear();
                break;
            case "revogada":
                c.Embedded.Sessoes[0].Revoke(c.Relogio.GetUtcNow());
                break;
            case "expirada":
                c.Relogio.Avancar(TimeSpan.FromMinutes(31));
                break;
            case "tenant":
                c.Tenants.Suspender(c.Tenant.TenantId);
                break;
        }

        Assert.Null(await autenticador.AuthenticateAsync(troca.Token));
    }

    [Fact]
    public async Task Sessao_interativa_de_usuario_federado_nao_vale_e_o_login_por_senha_do_federado_e_recusado()
    {
        var c = new Cenario();
        var federado = User.CreateFederated(c.Tenant.TenantId, "F", c.Relogio.GetUtcNow());
        c.Dados.Usuarios.Add(federado);
        c.Dados.Concessoes.Add(UserOrganizationGrant.Create(c.Tenant.TenantId, federado.Id, c.Organizacao, Role.Attendant, c.Relogio.GetUtcNow(), null));
        var (token, hash) = SessionTokens.Generate();
        c.Dados.Sessoes.Add(Session.Create(c.Tenant.TenantId, federado.Id, hash, c.Relogio.GetUtcNow(), TimeSpan.FromHours(1)));

        Assert.Null(await c.Autenticador.AuthenticateAsync(token));

        var hasher = new PasswordHasher(c.Opcoes);
        var autenticacao = new AuthenticationService(c.Tenants, c.Dados, c.Dados, hasher, c.Opcoes, c.Relogio, c.Auditoria, c.Holder);
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => autenticacao.LoginAsync("lab-a", federado.Email, "!federated"));
    }

    // A assinatura das chamadas do servidor do ERP

    private sealed class CenarioHmac
    {
        public Cenario Base { get; } = new();

        public IntegrationKey Chave { get; }

        public string Segredo { get; }

        public CenarioHmac()
        {
            var (id, segredo) = IntegrationKey.GenerateIdentifiers();
            Segredo = segredo;
            var protegido = Base.Protetor.Protect(segredo, new IntegrationSecretBinding(Base.Tenant.TenantId, Base.Conexao.Id, id));
            Chave = IntegrationKey.Create(Base.Tenant.TenantId, Base.Conexao.Id, id, protegido.MasterKeyId, protegido.Ciphertext, Base.Relogio.GetUtcNow(), null);
            Base.Embedded.Chaves.Add(Chave);
        }

        public IntegrationRequestAuthenticator Autenticador => new(Base.Embedded, Base.Tenants, Base.Protetor, Base.Opcoes, Base.Relogio, Base.Auditoria);

        public IntegrationSignedRequest Assinada(string? nonce = null, DateTimeOffset? instante = null, string? segredo = null, string path = "/embed/v1/launch-tickets", byte[]? corpo = null, byte[]? corpoAssinado = null)
        {
            var bytes = corpoAssinado ?? corpo ?? Encoding.UTF8.GetBytes("{}");
            var ts = (instante ?? Base.Relogio.GetUtcNow()).ToUnixTimeSeconds().ToString();
            var n = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            var assinatura = Convert.ToHexStringLower(IntegrationRequestAuthenticator.ComputeSignature(segredo ?? Segredo, ts, n, "POST", path, bytes));
            return new IntegrationSignedRequest(Chave.PublicId, ts, n, assinatura, "POST", path, corpo ?? bytes);
        }
    }

    [Fact]
    public async Task Chamada_assinada_valida_devolve_a_conexao_e_o_tenant_dela()
    {
        var h = new CenarioHmac();

        var integracao = await h.Autenticador.AuthenticateAsync(h.Assinada());

        Assert.Equal(h.Base.Tenant.TenantId, integracao.TenantId);
        Assert.Equal(h.Base.Conexao.Id, integracao.ConnectionId);
        Assert.Equal(h.Base.Organizacao, integracao.OrganizationId);
        Assert.Equal(h.Chave.Id, integracao.KeyId);
    }

    [Fact]
    public async Task Toda_falha_da_assinatura_e_a_mesma_excecao_e_o_replay_e_recusado()
    {
        var h = new CenarioHmac();
        var a = h.Autenticador;
        var primeira = h.Assinada(nonce: "nonce-unico-123456");

        _ = await a.AuthenticateAsync(primeira);

        var falhas = new[]
        {
            primeira,                                                                          // replay
            h.Assinada(segredo: "ibsk_errado"),                                                // segredo
            h.Assinada(instante: h.Base.Relogio.GetUtcNow().AddMinutes(-3)),                   // fora da janela
            h.Assinada(instante: h.Base.Relogio.GetUtcNow().AddMinutes(3)),
            h.Assinada(corpo: Encoding.UTF8.GetBytes("{\"x\":1}"), corpoAssinado: Encoding.UTF8.GetBytes("{}")), // corpo adulterado
            h.Assinada(nonce: "curto"),                                                        // nonce inválido
            primeira with { KeyId = "ibk_" + Guid.NewGuid().ToString("N") },                   // chave desconhecida
            primeira with { Signature = new string('0', 64) },
            primeira with { Signature = "zz" },
            primeira with { Timestamp = "abc" },
        };

        foreach (var falha in falhas)
        {
            await Assert.ThrowsAsync<EmbeddedAuthenticationException>(() => a.AuthenticateAsync(falha));
        }
    }

    [Fact]
    public async Task Chave_revogada_chave_vencida_conexao_desativada_e_tenant_suspenso_nao_autenticam()
    {
        var h = new CenarioHmac();
        var a = h.Autenticador;
        _ = await a.AuthenticateAsync(h.Assinada());

        h.Chave.ExpireAt(h.Base.Relogio.GetUtcNow().AddMinutes(10));
        _ = await a.AuthenticateAsync(h.Assinada());
        h.Base.Relogio.Avancar(TimeSpan.FromMinutes(11));
        await Assert.ThrowsAsync<EmbeddedAuthenticationException>(() => a.AuthenticateAsync(h.Assinada()));

        var h2 = new CenarioHmac();
        h2.Chave.Revoke(h2.Base.Relogio.GetUtcNow());
        await Assert.ThrowsAsync<EmbeddedAuthenticationException>(() => h2.Autenticador.AuthenticateAsync(h2.Assinada()));

        var h3 = new CenarioHmac();
        h3.Base.Conexao.SetStatus(ConnectionStatus.Disabled, h3.Base.Relogio.GetUtcNow());
        await Assert.ThrowsAsync<EmbeddedAuthenticationException>(() => h3.Autenticador.AuthenticateAsync(h3.Assinada()));

        var h4 = new CenarioHmac();
        h4.Base.Tenants.Suspender(h4.Base.Tenant.TenantId);
        await Assert.ThrowsAsync<EmbeddedAuthenticationException>(() => h4.Autenticador.AuthenticateAsync(h4.Assinada()));
    }

    [Fact]
    public async Task Segredo_cifrado_de_outro_tenant_ou_conexao_nao_abre_e_a_chamada_e_recusada_e_auditada()
    {
        var h = new CenarioHmac();
        var intruso = new IntegrationKey_Trocado(h);

        await Assert.ThrowsAsync<EmbeddedAuthenticationException>(() => intruso.Autenticar());

        Assert.Contains(h.Base.Auditoria.Eventos, e => e.Entrada.Operation == "identity.embed.integration.authenticate" && e.Entrada.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Denied);
    }

    /// <summary>Uma chave cujo texto cifrado pertence a OUTRA conexão (o vínculo não confere).</summary>
    private sealed class IntegrationKey_Trocado(CenarioHmac h)
    {
        public async Task Autenticar()
        {
            var (id, segredo) = IntegrationKey.GenerateIdentifiers();
            var protegidoParaOutraConexao = h.Base.Protetor.Protect(segredo, new IntegrationSecretBinding(h.Base.Tenant.TenantId, Guid.NewGuid(), id));
            var chave = IntegrationKey.Create(h.Base.Tenant.TenantId, h.Base.Conexao.Id, id, protegidoParaOutraConexao.MasterKeyId, protegidoParaOutraConexao.Ciphertext, h.Base.Relogio.GetUtcNow(), null);
            h.Base.Embedded.Chaves.Add(chave);

            var ts = h.Base.Relogio.GetUtcNow().ToUnixTimeSeconds().ToString();
            var assinatura = Convert.ToHexStringLower(IntegrationRequestAuthenticator.ComputeSignature(segredo, ts, "nonce-1234567890123", "POST", "/embed/v1/launch-tickets", Array.Empty<byte>()));
            await h.Autenticador.AuthenticateAsync(new IntegrationSignedRequest(chave.PublicId, ts, "nonce-1234567890123", assinatura, "POST", "/embed/v1/launch-tickets", Array.Empty<byte>()));
        }
    }
}
