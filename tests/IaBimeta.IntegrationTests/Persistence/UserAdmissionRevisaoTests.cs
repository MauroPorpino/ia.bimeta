using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Api.Provisioning;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Api;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Persistence;

/// <summary>
/// Correções da revisão independente da Etapa 01, com PostgreSQL REAL e ordem DETERMINÍSTICA: em vez de atrasos artificiais, o teste segura uma trava (linha ou consultiva) numa conexão sua, espera até o PostgreSQL mostrar o outro lado
/// bloqueado nela (<c>pg_stat_activity</c>) e só então muda o estado e solta. Cada cenário falhava antes da correção correspondente.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UserAdmissionRevisaoTests(PostgresFixture banco)
{
    private sealed record Cenario(TenantInfo Tenant, OrganizationView Organizacao, UsuarioCriado Admin);

    private async Task<Cenario> MontarAsync(string prefixo, int? limite = null, bool cadastro = true)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var organizacao = await banco.CriarOrganizacaoAsync(tenant, $"Organização {prefixo}");
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        if (limite is not null || !cadastro)
        {
            await DefinirPoliticaAsync(tenant, limite, cadastro);
        }

        return new Cenario(tenant, organizacao, admin);
    }

    private async Task DefinirPoliticaAsync(TenantInfo tenant, int? limite, bool habilitado)
    {
        await using var escopo = banco.NovoEscopo();
        await escopo.ServiceProvider.GetRequiredService<TenantUserPolicyService>().SetAsync(tenant.Slug, limite, habilitado);
    }

    private static RequestContext ComoAdmin(TenantInfo tenant, Guid userId) =>
        PostgresFixture.ContextoDeUsuario(tenant, userId, RolePermissions.Expand(Role.OrganizationAdmin, null).ToArray());

    private static RequestContext ComoGestorExterno(TenantInfo tenant) => ComoAdmin(tenant, Guid.NewGuid());

    private async Task<SeatUsage> UsoAsync(TenantInfo tenant)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        return await escopo.ServiceProvider.GetRequiredService<IUserAdminStore>().GetSeatUsageAsync(tenant.TenantId, DateTimeOffset.UtcNow, default);
    }

    private async Task<(bool Ok, Exception? Erro)> TentarAsync(RequestContext? contexto, Func<IServiceProvider, Task> acao)
    {
        await using var escopo = banco.NovoEscopo(contexto);
        try
        {
            await acao(escopo.ServiceProvider);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private async Task<NpgsqlConnection> AbrirAsync()
    {
        var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        return conexao;
    }

    private static async Task ExecutarAsync(NpgsqlConnection conexao, string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor);
        }

        await comando.ExecuteNonQueryAsync();
    }

    /// <summary>Espera o PostgreSQL mostrar pelo menos <paramref name="minimo"/> sessões bloqueadas esperando uma trava (linha ou consultiva): a ordem do cenário passa a ser um fato observado, não uma suposição de tempo.</summary>
    private async Task EsperarBloqueadosAsync(int minimo = 1)
    {
        await using var conexao = await AbrirAsync();
        for (var i = 0; i < 100; i++)
        {
            await using var comando = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND pid <> pg_backend_pid()", conexao);
            if ((long)(await comando.ExecuteScalarAsync())! >= minimo)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Nenhuma operação ficou aguardando a trava (esperadas {minimo}): a operação NÃO é serializada com a trava que o teste segura.");
    }

    // P1-1: sessão criada depois da desativação (Standalone)

    [Fact]
    public async Task Login_concorrente_com_a_desativacao_nunca_cria_sessao_que_volte_a_valer_na_reativacao()
    {
        var c = await MontarAsync("sessdes");
        var ana = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);

        // A desativação está EM CURSO: o teste segura a linha do usuário como ela a seguraria (UPDATE ainda não confirmado).
        await using var desativando = await AbrirAsync();
        await ExecutarAsync(desativando, "BEGIN");
        await ExecutarAsync(desativando, "SELECT 1 FROM identity.users WHERE tenant_id = @t AND id = @u FOR UPDATE", ("t", c.Tenant.TenantId.Value), ("u", ana.Id));

        // O login (senha certa, usuário ainda Ativo para quem lê sem trava) chega agora: a confirmação da sessão tem de esperar a desativação.
        var login = Task.Run(async () =>
        {
            await using var escopo = banco.NovoEscopo();
            return await escopo.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(ana.TenantSlug, ana.Email, ana.Senha);
        });
        await EsperarBloqueadosAsync();

        // A desativação confirma (estado e revogação das sessões existentes) e só então o login pode prosseguir.
        await ExecutarAsync(desativando, "UPDATE identity.users SET status = 'Inactive', status_changed_at = now() WHERE tenant_id = @t AND id = @u", ("t", c.Tenant.TenantId.Value), ("u", ana.Id));
        await ExecutarAsync(desativando, "UPDATE identity.sessions SET revoked_at = now() WHERE tenant_id = @t AND user_id = @u AND revoked_at IS NULL", ("t", c.Tenant.TenantId.Value), ("u", ana.Id));
        await ExecutarAsync(desativando, "COMMIT");

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => login.WaitAsync(TimeSpan.FromSeconds(30)));

        // Nenhuma sessão nasceu: a reativação não tem o que ressuscitar.
        await using var verifica = await AbrirAsync();
        await ExecutarAsync(verifica, "UPDATE identity.users SET status = 'Active', status_changed_at = now() WHERE tenant_id = @t AND id = @u", ("t", c.Tenant.TenantId.Value), ("u", ana.Id));
        await using var comando = new NpgsqlCommand("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND user_id = @u", verifica);
        comando.Parameters.AddWithValue("t", c.Tenant.TenantId.Value);
        comando.Parameters.AddWithValue("u", ana.Id);
        Assert.Equal(0L, (long)(await comando.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Sessao_em_confirmacao_e_revogada_pela_desativacao_que_chega_depois_e_a_reativacao_nao_a_revive()
    {
        var c = await MontarAsync("sessdes2");
        var ana = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);

        // O login está confirmando a sessão: segura a linha do usuário em modo compartilhado (o que a confirmação da sessão faz).
        await using var logando = await AbrirAsync();
        await ExecutarAsync(logando, "BEGIN");
        await ExecutarAsync(logando, "SELECT 1 FROM identity.users WHERE tenant_id = @t AND id = @u FOR SHARE", ("t", c.Tenant.TenantId.Value), ("u", ana.Id));

        var desativacao = TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().DeactivateAsync(ana.Id));
        await EsperarBloqueadosAsync();

        // A sessão é gravada e confirmada; a desativação (que esperava) agora a revoga junto com as demais.
        var tokens = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        await ExecutarAsync(logando, "INSERT INTO identity.sessions (tenant_id, id, user_id, token_hash, created_at, expires_at, kind) VALUES (@t, @i, @u, @h, now(), now() + interval '8 hours', 'Interactive')",
            ("t", c.Tenant.TenantId.Value), ("i", Guid.CreateVersion7()), ("u", ana.Id), ("h", tokens));
        await ExecutarAsync(logando, "COMMIT");
        Assert.True((await desativacao.WaitAsync(TimeSpan.FromSeconds(30))).Ok);

        await using var verifica = await AbrirAsync();
        await using var comando = new NpgsqlCommand("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND user_id = @u AND revoked_at IS NULL", verifica);
        comando.Parameters.AddWithValue("t", c.Tenant.TenantId.Value);
        comando.Parameters.AddWithValue("u", ana.Id);
        Assert.Equal(0L, (long)(await comando.ExecuteScalarAsync())!);
        Assert.True((await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().ReactivateAsync(ana.Id))).Ok);
        Assert.Equal(0L, (long)(await comando.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Sessao_anterior_a_ultima_mudanca_de_status_nao_autentica_mesmo_que_tenha_escapado_da_revogacao()
    {
        var c = await MontarAsync("sessmarca");
        var ana = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);
        Assert.True((await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().DeactivateAsync(ana.Id))).Ok);
        Assert.True((await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().ReactivateAsync(ana.Id))).Ok);

        // Uma sessão criada ANTES da reativação que, por qualquer falha, ficou ativa: o status mudou depois dela, então ela não vale.
        var (token, hash) = SessionTokens.Generate();
        await using (var escopo = banco.NovoEscopo())
        {
            var antiga = Session.Create(c.Tenant.TenantId, ana.Id, hash, DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromHours(8));
            await escopo.ServiceProvider.GetRequiredService<IAuthenticationStore>().AddSessionAsync(antiga, default);
        }

        await using var verifica = banco.NovoEscopo();
        Assert.Null(await verifica.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(token));
        // Um login NOVO continua funcionando normalmente.
        var novo = await verifica.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(ana.TenantSlug, ana.Email, ana.Senha);
        Assert.NotNull(await verifica.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(novo.Token));
    }

    // Complemento: a recusa do último administrador não derruba a sessão que já existia

    [Fact]
    public async Task Sessao_preexistente_continua_valida_quando_a_desativacao_do_ultimo_administrador_e_recusada_e_revertida()
    {
        var c = await MontarAsync("sessult");
        await using var escopoLogin = banco.NovoEscopo();
        var login = await escopoLogin.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(c.Admin.TenantSlug, c.Admin.Email, c.Admin.Senha);
        Assert.NotNull(await escopoLogin.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(login.Token));

        var recusa = await TentarAsync(ComoGestorExterno(c.Tenant), sp => sp.GetRequiredService<UserAdminService>().DeactivateAsync(c.Admin.Id));

        Assert.IsType<LastAdministratorException>(recusa.Erro);
        await using var depois = banco.NovoEscopo();
        var principal = await depois.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(login.Token);
        Assert.NotNull(principal);
        Assert.Contains(principal!.Permissions, p => p.Permission == Permission.UserStatusManage);
    }

    // P1-2: primeiro acesso com a reserva vencida enquanto espera a trava

    [Fact]
    public async Task Primeiro_acesso_que_espera_a_trava_nao_ativa_uma_reserva_que_venceu_nesse_meio_tempo_e_a_vaga_fica_livre_para_outra_admissao()
    {
        var c = await MontarAsync("expresg", limite: 2);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);
        var email = $"vence{Guid.NewGuid().ToString("N")[..6]}@teste.local";
        Guid userId;
        string token;
        await using (var escopo = banco.NovoEscopo(contexto))
        {
            var admitido = await escopo.ServiceProvider.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, email, "Vence", Role.User, null);
            userId = admitido.User.Id;
            token = admitido.Token;
        }

        // Alguém segura a trava de admissão do tenant; o resgate chega e fica esperando por ela.
        await using var segurando = await AbrirAsync();
        await ExecutarAsync(segurando, "SELECT pg_advisory_lock(hashtextextended(@k, 0))", ("k", UserAdmissionLock.KeyFor(c.Tenant.TenantId)));
        var resgate = TentarAsync(new RequestContext(c.Tenant.TenantId, "anonimo"), sp => sp.GetRequiredService<PasswordService>().RedeemResetAsync(c.Tenant.Slug, email, token, PostgresFixture.PasswordPadrao));
        await EsperarBloqueadosAsync();

        // Enquanto espera, a reserva vence (daqui a 2 s) e o teste a deixa vencer com a trava ainda segura.
        await ExecutarAsync(segurando, "UPDATE identity.password_reset_tokens SET expires_at = now() + interval '2 seconds' WHERE tenant_id = @t AND user_id = @u", ("t", c.Tenant.TenantId.Value), ("u", userId));
        await Task.Delay(3500);
        await ExecutarAsync(segurando, "SELECT pg_advisory_unlock(hashtextextended(@k, 0))", ("k", UserAdmissionLock.KeyFor(c.Tenant.TenantId)));

        var resultado = await resgate.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(resultado.Ok, "o resgate aceitou uma reserva já vencida");
        Assert.IsType<InvalidPasswordResetException>(resultado.Erro);
        await using (var verifica = banco.NovoEscopo(PostgresFixture.Contexto(c.Tenant)))
        {
            Assert.Equal(UserStatus.Pending, (await verifica.ServiceProvider.GetRequiredService<IUserRepository>().FindByIdAsync(userId, default))!.Status);
        }

        // A vaga liberada pelo vencimento é de outra admissão (a reserva vencida não volta a ocupá-la).
        var outra = await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, $"outra{Guid.NewGuid().ToString("N")[..6]}@teste.local", "Outra", Role.User, null));
        Assert.True(outra.Ok, outra.Erro?.Message);
        Assert.Equal(2, (await UsoAsync(c.Tenant)).SeatsUsed);
    }

    // P1-3: ativação do vínculo externo decidida sob a trava

    private async Task<(User Federado, ExternalIdentity Vinculo, Guid ConexaoId)> CriarFederadoAsync(Cenario c)
    {
        await using var escopo = banco.NovoEscopo(ComoAdmin(c.Tenant, c.Admin.Id));
        var conexao = await escopo.ServiceProvider.GetRequiredService<IntegrationAdminService>().CreateConnectionAsync(
            c.Organizacao.Id, "ERP", EmbeddedMode.FullAttendant, ProvisioningMode.JitAttendant, new[] { "https://erp.exemplo.com.br" }, false);
        var agora = DateTimeOffset.UtcNow;
        var federado = User.CreateFederated(c.Tenant.TenantId, "Federado", agora);
        var concessao = UserOrganizationGrant.Create(c.Tenant.TenantId, federado.Id, c.Organizacao.Id, Role.Attendant, agora, null);
        var vinculo = ExternalIdentity.Create(c.Tenant.TenantId, conexao.Id, $"u-{Guid.NewGuid().ToString("N")[..8]}", federado.Id, IdentitySource.Jit, agora, null);
        var auditoria = PendingAudit.Create(c.Tenant.TenantId, new AuditEntry("identity.embed.identity.provision", "external_identity", vinculo.Id.ToString(), AuditOutcome.Success), federado.Id, PendingAudit.AuthenticationOrigin, agora);
        Assert.True(await escopo.ServiceProvider.GetRequiredService<IEmbeddedStore>().TryProvisionFederatedUserAsync(federado, concessao, vinculo, auditoria, _ => Task.CompletedTask, default));
        return (federado, vinculo, conexao.Id);
    }

    [Fact]
    public async Task Ativar_o_vinculo_externo_decide_sob_a_trava_com_o_estado_atual_e_nunca_ultrapassa_o_limite()
    {
        var c = await MontarAsync("vinculo");
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);
        var (federado, vinculo, conexaoId) = await CriarFederadoAsync(c);

        // O federado não ocupa vaga: usuário inativo e vínculo desativado. O limite deixa UMA vaga livre (o administrador usa a outra).
        Assert.True((await TentarAsync(contexto, sp => sp.GetRequiredService<IntegrationAdminService>().SetIdentityStatusAsync(conexaoId, vinculo.Id, ExternalIdentityStatus.Disabled))).Ok);
        Assert.True((await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().DeactivateAsync(federado.Id))).Ok);
        await DefinirPoliticaAsync(c.Tenant, 2, true);
        Assert.Equal(1, (await UsoAsync(c.Tenant)).SeatsUsed);

        // A ativação do vínculo CHEGA vendo o usuário ainda inativo (nada a conferir) e fica esperando a linha do vínculo, que o teste segura.
        await using var segurando = await AbrirAsync();
        await ExecutarAsync(segurando, "BEGIN");
        await ExecutarAsync(segurando, "SELECT 1 FROM identity.external_identities WHERE tenant_id = @t AND id = @i FOR UPDATE", ("t", c.Tenant.TenantId.Value), ("i", vinculo.Id));
        var ativacao = TentarAsync(contexto, sp => sp.GetRequiredService<IntegrationAdminService>().SetIdentityStatusAsync(conexaoId, vinculo.Id, ExternalIdentityStatus.Active));
        await EsperarBloqueadosAsync();

        // Enquanto espera: o usuário federado é reativado (sem vínculo ativo ainda não conta) e OUTRA admissão preenche a última vaga.
        await ExecutarAsync(segurando, "UPDATE identity.users SET status = 'Active', status_changed_at = now() WHERE tenant_id = @t AND id = @u", ("t", c.Tenant.TenantId.Value), ("u", federado.Id));
        await using (var outra = await AbrirAsync())
        {
            var hasher = (await TentarHasherAsync());
            await ExecutarAsync(outra,
                "INSERT INTO identity.users (tenant_id, id, email, display_name, password_hash, created_at, auth_mode, status) VALUES (@t, @i, @e, 'Ocupa a última vaga', @h, now(), 'Password', 'Active')",
                ("t", c.Tenant.TenantId.Value), ("i", Guid.CreateVersion7()), ("e", $"ultima{Guid.NewGuid().ToString("N")[..6]}@teste.local"), ("h", hasher));
        }

        await ExecutarAsync(segurando, "COMMIT");
        var resultado = await ativacao.WaitAsync(TimeSpan.FromSeconds(30));

        // Com o estado atual (usuário ativo, vínculo desativado, última vaga ocupada) a ativação AUMENTARIA o uso: tem de ser recusada.
        Assert.IsType<UserLimitReachedException>(resultado.Erro);
        Assert.True((await UsoAsync(c.Tenant)).SeatsUsed <= 2, "o limite contratado foi ultrapassado");
    }

    private async Task<string> TentarHasherAsync()
    {
        await using var escopo = banco.NovoEscopo();
        return escopo.ServiceProvider.GetRequiredService<PasswordHasher>().Hash(PostgresFixture.PasswordPadrao);
    }

    // P2-7: alterações independentes da política não se desfazem

    [Fact]
    public async Task Alteracoes_independentes_de_limite_e_de_habilitacao_nao_se_sobrescrevem()
    {
        var c = await MontarAsync("polind", limite: 5, cadastro: true);
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));

        // As duas alterações chegam juntas e ficam esperando a trava de admissão; cada uma muda SÓ o campo pedido.
        await using var segurando = await AbrirAsync();
        await ExecutarAsync(segurando, "SELECT pg_advisory_lock(hashtextextended(@k, 0))", ("k", UserAdmissionLock.KeyFor(c.Tenant.TenantId)));
        var soLimite = Task.Run(() => TenantUsersCommand.ApplyAsync(api.Services, c.Tenant.Slug, 7, false, null, new StringWriter()));
        var soCadastro = Task.Run(() => TenantUsersCommand.ApplyAsync(api.Services, c.Tenant.Slug, null, false, false, new StringWriter()));
        await EsperarBloqueadosAsync(2);
        await ExecutarAsync(segurando, "SELECT pg_advisory_unlock(hashtextextended(@k, 0))", ("k", UserAdmissionLock.KeyFor(c.Tenant.TenantId)));

        Assert.Equal(0, await soLimite.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, await soCadastro.WaitAsync(TimeSpan.FromSeconds(30)));
        await using var escopo = banco.NovoEscopo();
        var politica = (await escopo.ServiceProvider.GetRequiredService<TenantUserPolicyService>().GetAsync(c.Tenant.Slug))!.Value.Policy;
        Assert.Equal((7, false), (politica.MaxUsers, politica.RegistrationEnabled));
    }

    // P1-4: o operador só redefine o acesso de usuário ATIVO

    [Fact]
    public async Task Comando_reset_password_do_operador_recusa_admissao_pendente_expirada_com_limite_atingido_ou_cadastro_desabilitado()
    {
        var c = await MontarAsync("opreset", limite: 2);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);
        var email = $"pend{Guid.NewGuid().ToString("N")[..6]}@teste.local";
        Guid userId;
        await using (var escopo = banco.NovoEscopo(contexto))
        {
            userId = (await escopo.ServiceProvider.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, email, "Pend", Role.User, null)).User.Id;
        }

        await using (var conexao = await AbrirAsync())
        {
            await ExecutarAsync(conexao, "UPDATE identity.password_reset_tokens SET expires_at = now() - interval '1 hour' WHERE tenant_id = @t AND user_id = @u", ("t", c.Tenant.TenantId.Value), ("u", userId));
        }

        // A vaga da reserva vencida é preenchida por outra pessoa: o limite está atingido.
        await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, $"ocupa{Guid.NewGuid().ToString("N")[..6]}@teste.local", "Ocupa", Role.User, null));
        Assert.Equal(2, (await UsoAsync(c.Tenant)).SeatsUsed);

        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var saida = new StringWriter();
        Assert.Equal(2, await ResetPasswordCommand.IssueAsync(api.Services, c.Tenant.Slug, email, saida));
        Assert.DoesNotContain("ibr_", saida.ToString());

        // Cadastro desabilitado: o mesmo (e o pendente continua pendente, sem reserva).
        await DefinirPoliticaAsync(c.Tenant, 10, false);
        var saida2 = new StringWriter();
        Assert.Equal(2, await ResetPasswordCommand.IssueAsync(api.Services, c.Tenant.Slug, email, saida2));
        Assert.DoesNotContain("ibr_", saida2.ToString());
        Assert.Equal(1, (await UsoAsync(c.Tenant)).PendingExpired);
    }

    // Reemissão do token do administrador inicial (PostgreSQL real)

    private async Task<(TenantInfo Tenant, string Email)> TenantComPoliticaAsync(string prefixo, int? limite, bool cadastro = true)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        await DefinirPoliticaAsync(tenant, limite, cadastro);
        return (tenant, $"dono{Guid.NewGuid().ToString("N")[..6]}@teste.local");
    }

    private async Task VencerTokensAsync(TenantInfo tenant, string email)
    {
        await using var conexao = await AbrirAsync();
        await ExecutarAsync(conexao, "UPDATE identity.password_reset_tokens SET expires_at = now() - interval '1 hour' WHERE tenant_id = @t AND user_id = (SELECT id FROM identity.users WHERE tenant_id = @t AND email = @e)", ("t", tenant.TenantId.Value), ("e", email));
    }

    [Fact]
    public async Task Administrador_inicial_com_token_expirado_e_reemitido_na_mesma_conta_com_a_cadastro_desabilitado_e_o_limite_respeitado()
    {
        var (tenant, email) = await TenantComPoliticaAsync("reemite", limite: 2, cadastro: false);
        var contexto = PostgresFixture.Contexto(tenant);
        string primeiro;
        await using (var escopo = banco.NovoEscopo(contexto))
        {
            primeiro = (await escopo.ServiceProvider.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(email, "Dona")).Token;
        }

        await VencerTokensAsync(tenant, email);
        Assert.Equal(0, (await UsoAsync(tenant)).SeatsUsed);

        FirstAdministratorIssued novo;
        await using (var escopo = banco.NovoEscopo(contexto))
        {
            novo = await escopo.ServiceProvider.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(email, "Dona");
        }

        Assert.True(novo.Reissued);
        var uso = await UsoAsync(tenant);
        Assert.Equal((1, 1, 0), (uso.SeatsUsed, uso.PendingReserved, uso.Active));
        await using (var verifica = await AbrirAsync())
        {
            await using var comando = new NpgsqlCommand("SELECT count(*) FROM identity.users WHERE tenant_id = @t", verifica);
            comando.Parameters.AddWithValue("t", tenant.TenantId.Value);
            Assert.Equal(1L, (long)(await comando.ExecuteScalarAsync())!); // sem duplicar usuário
        }

        var anonimo = new RequestContext(tenant.TenantId, "anonimo");
        var antigo = await TentarAsync(anonimo, sp => sp.GetRequiredService<PasswordService>().RedeemResetAsync(tenant.Slug, email, primeiro, PostgresFixture.PasswordPadrao));
        Assert.IsType<InvalidPasswordResetException>(antigo.Erro);
        Assert.True((await TentarAsync(anonimo, sp => sp.GetRequiredService<PasswordService>().RedeemResetAsync(tenant.Slug, email, novo.Token, PostgresFixture.PasswordPadrao))).Ok);

        // Concluído o primeiro acesso: nunca mais, e o cadastro desabilitado continua barrando os demais.
        var depois = await TentarAsync(contexto, sp => sp.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(email, "Dona"));
        Assert.IsType<IdentityValidationException>(depois.Erro);
        var outro = await TentarAsync(contexto, sp => sp.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync("outro" + email, "Outro"));
        Assert.IsType<IdentityValidationException>(outro.Erro);
    }


    [Fact]
    public async Task Reemissao_que_espera_a_trava_nao_recria_a_reserva_vencida_acima_do_limite_quando_outro_ingresso_ocupa_a_ultima_vaga()
    {
        var (tenant, email) = await TenantComPoliticaAsync("reemcorr", limite: 1);
        var contexto = PostgresFixture.Contexto(tenant);
        await using (var escopo = banco.NovoEscopo(contexto))
        {
            await escopo.ServiceProvider.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(email, "Dona");
        }

        Assert.Equal(1, (await UsoAsync(tenant)).PendingReserved); // 1) a reserva está viva

        // 2) a reemissão começa e fica aguardando a trava de admissão (segurada pelo teste).
        await using var segurando = await AbrirAsync();
        await ExecutarAsync(segurando, "SELECT pg_advisory_lock(hashtextextended(@k, 0))", ("k", UserAdmissionLock.KeyFor(tenant.TenantId)));
        var reemissao = TentarAsync(contexto, sp => sp.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(email, "Dona"));
        await EsperarBloqueadosAsync();

        // 3) a reserva expira com a trava ainda segura (vence em 2 s e o teste espera passar).
        await ExecutarAsync(segurando, "UPDATE identity.password_reset_tokens SET expires_at = now() + interval '2 seconds' WHERE tenant_id = @t AND user_id = (SELECT id FROM identity.users WHERE tenant_id = @t AND email = @e)", ("t", tenant.TenantId.Value), ("e", email));
        await Task.Delay(3500);

        // 4) outro ingresso ocupa a última vaga (já confirmado quando a reemissão obtiver a trava).
        await ExecutarAsync(segurando,
            "INSERT INTO identity.users (tenant_id, id, email, display_name, password_hash, created_at, auth_mode, status) VALUES (@t, @i, @e, 'Ocupa a última vaga', 'x', now(), 'Password', 'Active')",
            ("t", tenant.TenantId.Value), ("i", Guid.CreateVersion7()), ("e", $"ultima{Guid.NewGuid().ToString("N")[..6]}@teste.local"));

        // 5) a reemissão obtém a trava.
        await ExecutarAsync(segurando, "SELECT pg_advisory_unlock(hashtextextended(@k, 0))", ("k", UserAdmissionLock.KeyFor(tenant.TenantId)));
        var resultado = await reemissao.WaitAsync(TimeSpan.FromSeconds(30));

        // 6) é recusada por limite atingido; 7) nenhum token novo nem reserva; 8) o limite continua respeitado.
        Assert.IsType<UserLimitReachedException>(resultado.Erro);
        var uso = await UsoAsync(tenant);
        Assert.Equal((1, 0, 1), (uso.SeatsUsed, uso.PendingReserved, uso.PendingExpired));
        await using var verifica = await AbrirAsync();
        await using var comando = new NpgsqlCommand("SELECT count(*) FROM identity.password_reset_tokens WHERE tenant_id = @t", verifica);
        comando.Parameters.AddWithValue("t", tenant.TenantId.Value);
        Assert.Equal(1L, (long)(await comando.ExecuteScalarAsync())!); // só o token original, já vencido
    }
    [Fact]
    public async Task Reemissao_com_o_limite_atingido_e_recusada_e_nada_e_criado()
    {
        var (tenant, email) = await TenantComPoliticaAsync("reemlim", limite: 1);
        var contexto = PostgresFixture.Contexto(tenant);
        await using (var escopo = banco.NovoEscopo(contexto))
        {
            await escopo.ServiceProvider.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(email, "Dona");
        }

        await VencerTokensAsync(tenant, email);
        // Outra pessoa ocupa a vaga liberada pelo vencimento.
        await using (var conexao = await AbrirAsync())
        {
            await ExecutarAsync(conexao,
                "INSERT INTO identity.users (tenant_id, id, email, display_name, password_hash, created_at, auth_mode, status) VALUES (@t, @i, @e, 'Ocupa', 'x', now(), 'Password', 'Active')",
                ("t", tenant.TenantId.Value), ("i", Guid.CreateVersion7()), ("e", $"ocupa{Guid.NewGuid().ToString("N")[..6]}@teste.local"));
        }

        var resultado = await TentarAsync(contexto, sp => sp.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(email, "Dona"));

        Assert.IsType<UserLimitReachedException>(resultado.Erro);
        var uso = await UsoAsync(tenant);
        Assert.Equal((1, 0), (uso.SeatsUsed, uso.PendingReserved));
        Assert.Equal(1, uso.PendingExpired);
    }

    [Fact]
    public async Task Reemissoes_concorrentes_deixam_um_unico_token_valido_na_mesma_conta()
    {
        var (tenant, email) = await TenantComPoliticaAsync("reemconc", limite: 3);
        var contexto = PostgresFixture.Contexto(tenant);
        await using (var escopo = banco.NovoEscopo(contexto))
        {
            await escopo.ServiceProvider.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(email, "Dona");
        }

        await VencerTokensAsync(tenant, email);
        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var escopo = banco.NovoEscopo(contexto);
            return await escopo.ServiceProvider.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(email, "Dona");
        }));

        Assert.All(resultados, r => Assert.True(r.Reissued));
        Assert.Equal(6, resultados.Select(r => r.Token).Distinct().Count());
        var uso = await UsoAsync(tenant);
        Assert.Equal((1, 1), (uso.SeatsUsed, uso.PendingReserved)); // uma reserva só, nunca duas

        // Exatamente UM dos seis tokens é o vigente no banco (os outros foram invalidados pela substituição seguinte); só ele conclui o primeiro acesso.
        // (Não se testa um a um por resgate: as falhas repetidas acionariam, corretamente, o bloqueio de tentativas do e-mail.)
        var vigentes = new List<byte[]>();
        await using (var conexaoVigente = await AbrirAsync())
        {
            await using var consulta = new NpgsqlCommand("SELECT token_hash FROM identity.password_reset_tokens WHERE tenant_id = @t AND used_at IS NULL AND revoked_at IS NULL AND expires_at > now()", conexaoVigente);
            consulta.Parameters.AddWithValue("t", tenant.TenantId.Value);
            await using var leitor = await consulta.ExecuteReaderAsync();
            while (await leitor.ReadAsync())
            {
                vigentes.Add((byte[])leitor[0]);
            }
        }

        Assert.Single(vigentes);
        var validos = resultados.Where(r => ResetTokens.TryHash(r.Token, out var hash) && hash.AsSpan().SequenceEqual(vigentes[0])).ToList();
        var unico = Assert.Single(validos);
        var anonimo = new RequestContext(tenant.TenantId, "anonimo");
        Assert.True((await TentarAsync(anonimo, sp => sp.GetRequiredService<PasswordService>().RedeemResetAsync(tenant.Slug, email, unico.Token, PostgresFixture.PasswordPadrao))).Ok);
        await using var verifica = await AbrirAsync();
        await using var comando = new NpgsqlCommand("SELECT count(*) FROM identity.users WHERE tenant_id = @t", verifica);
        comando.Parameters.AddWithValue("t", tenant.TenantId.Value);
        Assert.Equal(1L, (long)(await comando.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Comando_first_admin_repetido_reemite_para_o_mesmo_email_e_recusa_outro_e_a_conta_concluida()
    {
        var (tenant, email) = await TenantComPoliticaAsync("reemcmd", limite: 3);
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));

        var primeira = new StringWriter();
        Assert.Equal(0, await FirstAdminCommand.IssueAsync(api.Services, tenant.Slug, email, "Dona", primeira));
        Assert.Contains("provisionado", primeira.ToString());

        var repetida = new StringWriter();
        Assert.Equal(0, await FirstAdminCommand.IssueAsync(api.Services, tenant.Slug, email, "Dona", repetida));
        Assert.Contains("REEMITIDO", repetida.ToString());
        Assert.Contains("ibr_", repetida.ToString());

        var outro = new StringWriter();
        Assert.Equal(2, await FirstAdminCommand.IssueAsync(api.Services, tenant.Slug, "outro" + email, "Outro", outro));
        Assert.DoesNotContain("ibr_", outro.ToString());

        // Concluído o primeiro acesso (token da reemissão), o comando não reemite mais para a conta.
        var token = repetida.ToString().Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("ibr_", StringComparison.Ordinal));
        Assert.True((await TentarAsync(new RequestContext(tenant.TenantId, "anonimo"), sp => sp.GetRequiredService<PasswordService>().RedeemResetAsync(tenant.Slug, email, token, PostgresFixture.PasswordPadrao))).Ok);
        var depois = new StringWriter();
        Assert.Equal(2, await FirstAdminCommand.IssueAsync(api.Services, tenant.Slug, email, "Dona", depois));
        Assert.DoesNotContain("ibr_", depois.ToString());
    }
}
