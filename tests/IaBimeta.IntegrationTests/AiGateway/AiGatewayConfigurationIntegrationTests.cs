using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.AiGateway;

/// <summary>
/// A IA por tenant com PostgreSQL real (ADR 0017, C1.4b e C1.4c): a configuração de provedor e modelo, a credencial BYOK cifrada (nunca em claro, nem no banco, nem na auditoria), a
/// resolução na hora do uso, a revogação, os limites por tenant, o bloqueio pela plataforma, o isolamento entre tenants e o AiGatewayService de ponta a ponta sobre o banco. Os segredos são
/// CANÁRIOS sintéticos e o provedor é um dublê: nenhuma chave real, nenhuma rede.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AiGatewayConfigurationIntegrationTests(PostgresFixture banco)
{
    private const string Provedor = PostgresFixture.ProvedorDeIADeTeste;
    private const string Modelo = PostgresFixture.ModeloDeIADeTeste;

    private static string NovoCanario() => $"sk-CANARIO-{Guid.NewGuid():N}";

    private static readonly PermissionGrant Gerir = new(Permission.TenantSettingsManage, null);

    private sealed class Tenant(TenantInfo info)
    {
        public TenantInfo Info { get; } = info;

        public TenantId Id => Info.TenantId;

        /// <summary>Um usuário do tenant que gere as configurações (nunca tem SystemOperation).</summary>
        public RequestContext Admin => PostgresFixture.ContextoDeUsuario(Info, Guid.NewGuid(), Gerir);

        /// <summary>Um usuário do tenant SEM a permissão de gerir configurações.</summary>
        public RequestContext SemPermissao => PostgresFixture.ContextoDeUsuario(Info, Guid.NewGuid(), new PermissionGrant(Permission.ConversationRead, null));

        /// <summary>O operador da plataforma: contexto de sistema deste tenant.</summary>
        public RequestContext Plataforma => PostgresFixture.Contexto(Info);
    }

    private async Task<Tenant> NovoTenantAsync(string prefixo) => new(await banco.RegistrarTenantAsync(prefixo));

    private async Task<T> ComoAsync<T>(RequestContext contexto, Func<IServiceProvider, Task<T>> acao)
    {
        await using var escopo = banco.NovoEscopo(contexto);
        return await acao(escopo.ServiceProvider);
    }

    private Task<bool> ExecutarAsync(RequestContext contexto, Func<AiTenantConfigurationService, Task> acao) =>
        ComoAsync(contexto, async sp =>
        {
            await acao(sp.GetRequiredService<AiTenantConfigurationService>());
            return true;
        });

    private Task<List<StoredAiCredential>> CredenciaisAsync(RequestContext contexto) =>
        ComoAsync(contexto, sp => sp.GetRequiredService<AiGatewayDbContext>().Credentials.AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync());

    private Task<AiCredential?> ResolverAsync(RequestContext contexto, TenantId tenant, string provedor = Provedor) =>
        ComoAsync(contexto, sp => sp.GetRequiredService<IAiCredentialResolver>().ResolveAsync(tenant, provedor, CancellationToken.None));

    // Configuração

    [Fact]
    public async Task Configurar_provedor_e_modelo_grava_e_a_situacao_volta_sem_nenhum_segredo()
    {
        var t = await NovoTenantAsync("aicfg");

        await ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo));
        var situacao = await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiTenantConfigurationService>().GetStatusAsync());

        Assert.Equal(new AiTenantStatusView(true, Provedor, Modelo, AiTenantStatus.Enabled, false, null, situacao.UpdatedAt), situacao);
        Assert.NotNull(situacao.UpdatedAt);
    }

    [Fact]
    public async Task Sem_configuracao_a_situacao_diz_nao_configurado()
    {
        var t = await NovoTenantAsync("aicfgvazio");

        var situacao = await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiTenantConfigurationService>().GetStatusAsync());

        Assert.Equal(new AiTenantStatusView(false, null, null, null, false, null, null), situacao);
        Assert.Null(await ComoAsync(t.Plataforma, sp => sp.GetRequiredService<ITenantAiConfigurationReader>().GetAsync(t.Id, CancellationToken.None)));
    }

    [Theory]
    [InlineData("provider-a", "model-a2")] // desativado
    [InlineData("provider-a", "modelo-que-nao-existe")]
    [InlineData("provedor-que-nao-existe", "model-a1")]
    [InlineData("provider-b", "model-a1")] // o modelo é de outro provedor
    public async Task Modelo_fora_do_catalogo_ou_desativado_e_recusado_e_nada_e_gravado(string provedor, string modelo)
    {
        var t = await NovoTenantAsync("aicfgrecusa");

        await Assert.ThrowsAsync<AiGatewayValidationException>(() => ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(provedor, modelo)));

        Assert.Equal(0, await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiGatewayDbContext>().Settings.CountAsync()));
    }

    [Fact]
    public async Task O_tenant_so_habilita_ou_desabilita_e_trocar_o_modelo_nao_perde_a_versao()
    {
        var t = await NovoTenantAsync("aicfgstatus");
        await ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo));

        await Assert.ThrowsAsync<AiGatewayValidationException>(() => ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo, AiTenantStatus.Blocked)));
        await ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(PostgresFixture.OutroProvedorDeIADeTeste, PostgresFixture.OutroModeloDeIADeTeste, AiTenantStatus.Disabled));

        var linha = await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiGatewayDbContext>().Settings.AsNoTracking().SingleAsync());
        Assert.Equal((PostgresFixture.OutroProvedorDeIADeTeste, AiTenantStatus.Disabled, 2), (linha.ProviderId, linha.Status, linha.Version));
        Assert.Equal(AiCredentialSource.Byok, linha.CredentialSource);
    }

    [Fact]
    public async Task So_quem_tem_a_permissao_altera_e_os_limites_e_o_bloqueio_so_a_plataforma()
    {
        var t = await NovoTenantAsync("aipermissao");
        await ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo));

        await Assert.ThrowsAsync<AccessDeniedException>(() => ExecutarAsync(t.SemPermissao, s => s.SetConfigurationAsync(Provedor, Modelo)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => ExecutarAsync(t.SemPermissao, s => s.SetCredentialAsync(Provedor, NovoCanario())));
        await Assert.ThrowsAsync<AccessDeniedException>(() => ExecutarAsync(t.SemPermissao, s => s.RevokeCredentialAsync(Provedor)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => ComoAsync(t.SemPermissao, sp => sp.GetRequiredService<AiTenantConfigurationService>().GetStatusAsync()));

        // O administrador do tenant NÃO é a plataforma: não define limites nem bloqueia.
        await Assert.ThrowsAsync<AccessDeniedException>(() => ExecutarAsync(t.Admin, s => s.SetLimitOverridesAsync(new AiTenantLimitOverrides(MaxConcurrentCalls: 99))));
        await Assert.ThrowsAsync<AccessDeniedException>(() => ExecutarAsync(t.Admin, s => s.SetBlockedAsync(true)));
    }

    [Fact]
    public async Task O_leitor_funde_o_padrao_da_plataforma_com_a_sobrescrita_do_tenant()
    {
        var t = await NovoTenantAsync("ailimites");
        await ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo));

        var padrao = (await ComoAsync(t.Plataforma, sp => sp.GetRequiredService<ITenantAiConfigurationReader>().GetAsync(t.Id, CancellationToken.None)))!;
        await ExecutarAsync(t.Plataforma, s => s.SetLimitOverridesAsync(new AiTenantLimitOverrides(MaxConcurrentCalls: 2, Period: TimeSpan.FromHours(2), MaxTokensPerCall: 777)));
        var comSobrescrita = (await ComoAsync(t.Plataforma, sp => sp.GetRequiredService<ITenantAiConfigurationReader>().GetAsync(t.Id, CancellationToken.None)))!;

        Assert.Equal((8, 1000, 10_000_000L, TimeSpan.FromHours(1), 50, 500, (long?)null), (padrao.Limits.MaxConcurrentCalls, padrao.Limits.MaxCallsPerPeriod, padrao.Limits.MaxTokensPerPeriod, padrao.Limits.Period, padrao.Limits.MaxInputMessages, padrao.Limits.MaxOutputTokens, padrao.Limits.MaxTokensPerCall));
        Assert.Equal((2, 1000, TimeSpan.FromHours(2), (long?)777), (comSobrescrita.Limits.MaxConcurrentCalls, comSobrescrita.Limits.MaxCallsPerPeriod, comSobrescrita.Limits.Period, comSobrescrita.Limits.MaxTokensPerCall));
        Assert.True(comSobrescrita.Limits.IsValid);
        Assert.Equal(t.Id, comSobrescrita.TenantId);
    }

    [Fact]
    public async Task Sem_padrao_nem_sobrescrita_os_limites_voltam_invalidos_e_o_seletor_recusa_o_tenant()
    {
        var t = await NovoTenantAsync("aisemlimite");
        await ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo));

        var configuracao = await ComoAsync(t.Plataforma, async sp =>
        {
            var leitor = new TenantAiConfigurationReader(sp.GetRequiredService<AiGatewayDbContext>(), new AiDefaultLimits()); // a plataforma sem padrão
            return await leitor.GetAsync(t.Id, CancellationToken.None);
        });
        var selecao = new AiModelSelector(AiModelCatalogDeTeste(), new AiProviderRegistry(new IAiProvider[] { new ProvedorFalso(Provedor) })).Select(t.Id, configuracao);

        Assert.False(configuracao!.Limits.IsValid);
        Assert.Equal((AiSelectionOutcome.InvalidLimits, AiFailureCodes.LimitsInvalid), (selecao.Outcome, selecao.Code));
    }

    [Fact]
    public async Task O_bloqueio_e_da_plataforma_o_tenant_bloqueado_nao_se_reabilita_e_o_desbloqueio_volta_a_habilitado()
    {
        var t = await NovoTenantAsync("aibloqueio");
        await ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo));

        await ExecutarAsync(t.Plataforma, s => s.SetBlockedAsync(true));
        Assert.Equal(AiTenantStatus.Blocked, (await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiTenantConfigurationService>().GetStatusAsync())).Status);
        await Assert.ThrowsAsync<AiGatewayValidationException>(() => ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo)));

        await ExecutarAsync(t.Plataforma, s => s.SetBlockedAsync(false));
        Assert.Equal(AiTenantStatus.Enabled, (await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiTenantConfigurationService>().GetStatusAsync())).Status);
    }

    [Fact]
    public async Task Alteracoes_concorrentes_da_mesma_configuracao_nao_se_sobrescrevem_em_silencio_e_ha_uma_so_por_tenant()
    {
        var t = await NovoTenantAsync("aiconcorrencia");
        await ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo));

        // Duas leituras do mesmo ponto de partida: a segunda alteração perde, com conflito explícito.
        await using var escopoA = banco.NovoEscopo(t.Admin);
        await using var escopoB = banco.NovoEscopo(t.Admin);
        var armazenamentoA = escopoA.ServiceProvider.GetRequiredService<IAiTenantConfigurationStore>();
        var armazenamentoB = escopoB.ServiceProvider.GetRequiredService<IAiTenantConfigurationStore>();
        var a = (await armazenamentoA.FindSettingsAsync(CancellationToken.None))!;
        var b = (await armazenamentoB.FindSettingsAsync(CancellationToken.None))!;
        a.SetStatus(AiTenantStatus.Disabled, DateTimeOffset.UtcNow, null);
        b.SetStatus(AiTenantStatus.Blocked, DateTimeOffset.UtcNow, null);
        await armazenamentoA.SaveSettingsAsync(a, isNew: false, CancellationToken.None);

        await Assert.ThrowsAsync<AiConfigurationConflictException>(() => armazenamentoB.SaveSettingsAsync(b, isNew: false, CancellationToken.None));

        // E criar a primeira configuração do mesmo tenant ao mesmo tempo nunca deixa duas linhas.
        var novo = await NovoTenantAsync("aiprimeira");
        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ExecutarAsync(novo.Admin, s => s.SetConfigurationAsync(Provedor, Modelo)).ContinueWith(x => x.Exception?.GetBaseException())));
        Assert.All(resultados, e => Assert.True(e is null or AiConfigurationConflictException, $"Falha inesperada: {e}"));
        Assert.Equal(1, await ComoAsync(novo.Admin, sp => sp.GetRequiredService<AiGatewayDbContext>().Settings.CountAsync()));
    }

    // Credenciais

    [Fact]
    public async Task A_credencial_e_gravada_cifrada_e_o_valor_em_claro_nao_esta_em_nenhuma_coluna_nem_na_auditoria()
    {
        var t = await NovoTenantAsync("aicred");
        var segredo = NovoCanario();
        await ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo));

        await ExecutarAsync(t.Admin, s => s.SetCredentialAsync(Provedor, segredo));

        var credencial = Assert.Single(await CredenciaisAsync(t.Admin));
        Assert.Equal((Provedor, StoredAiCredentialStatus.Active, PostgresFixture.IdDaChaveDeIADeTeste, 1), (credencial.ProviderId, credencial.Status, credencial.KeyId, credencial.Version));
        Assert.StartsWith("v1:", credencial.Ciphertext);
        Assert.Matches("^[0-9a-f]{12}$", credencial.Fingerprint);
        Assert.DoesNotContain(segredo, credencial.Ciphertext);

        // O canário não aparece em NENHUMA linha de NENHUMA tabela do esquema nem na trilha de auditoria (a busca olha a linha inteira, em texto).
        var ocorrencias = await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiGatewayDbContext>().Database
            .SqlQueryRaw<int>(
                "select count(*)::int as \"Value\" from (" +
                "select to_jsonb(x)::text as t from ai_gateway.ai_credentials x union all select to_jsonb(x)::text from ai_gateway.ai_tenant_settings x " +
                "union all select to_jsonb(x)::text from ai_gateway.ai_requests x union all select to_jsonb(x)::text from ai_gateway.usage_windows x " +
                "union all select to_jsonb(x)::text from audit.audit_events x) f where t like {0}",
                "%" + segredo + "%")
            .SingleAsync());
        Assert.Equal(0, ocorrencias);
        var auditoria = await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiGatewayDbContext>().Database
            .SqlQueryRaw<string>("select operation as \"Value\" from audit.audit_events where tenant_id = {0} and operation like 'ai-gateway.%' order by occurred_at", t.Id.Value)
            .ToListAsync());
        Assert.Equal(new[] { "ai-gateway.settings.set", "ai-gateway.credential.set" }, auditoria);
    }

    [Fact]
    public async Task A_credencial_so_volta_em_claro_na_resolucao_em_memoria_e_a_situacao_mostra_so_a_impressao()
    {
        var t = await NovoTenantAsync("airesolve");
        var segredo = NovoCanario();
        await ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo));
        await ExecutarAsync(t.Admin, s => s.SetCredentialAsync(Provedor, segredo));

        var resolvida = await ResolverAsync(t.Plataforma, t.Id);
        var situacao = await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiTenantConfigurationService>().GetStatusAsync());

        Assert.Equal(segredo, resolvida!.Secret);
        Assert.True(situacao.CredentialPresent);
        Assert.Equal((await CredenciaisAsync(t.Admin)).Single().Fingerprint, situacao.CredentialFingerprint);
        Assert.DoesNotContain(segredo, situacao.ToString());
        Assert.Null(await ResolverAsync(t.Plataforma, t.Id, PostgresFixture.OutroProvedorDeIADeTeste)); // outro provedor: não há credencial
    }

    [Fact]
    public async Task Definir_de_novo_troca_o_valor_da_credencial_ativa_sem_criar_outra()
    {
        var t = await NovoTenantAsync("aitroca");
        var primeiro = NovoCanario();
        var segundo = NovoCanario();
        await ExecutarAsync(t.Admin, s => s.SetCredentialAsync(Provedor, primeiro));
        var antes = Assert.Single(await CredenciaisAsync(t.Admin));

        await ExecutarAsync(t.Admin, s => s.SetCredentialAsync(Provedor, segundo));

        var depois = Assert.Single(await CredenciaisAsync(t.Admin));
        Assert.Equal((antes.Id, 2), (depois.Id, depois.Version));
        Assert.NotEqual(antes.Fingerprint, depois.Fingerprint);
        Assert.NotEqual(antes.Ciphertext, depois.Ciphertext);
        Assert.Equal(segundo, (await ResolverAsync(t.Plataforma, t.Id))!.Secret);
    }

    [Fact]
    public async Task Revogar_apaga_o_texto_cifrado_a_resolucao_devolve_nulo_e_a_revogacao_e_idempotente()
    {
        var t = await NovoTenantAsync("airevoga");
        await ExecutarAsync(t.Admin, s => s.SetCredentialAsync(Provedor, NovoCanario()));

        var revogada = await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiTenantConfigurationService>().RevokeCredentialAsync(Provedor));
        var repetida = await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiTenantConfigurationService>().RevokeCredentialAsync(Provedor));

        Assert.True(revogada);
        Assert.False(repetida);
        var linha = Assert.Single(await CredenciaisAsync(t.Admin));
        Assert.Equal((StoredAiCredentialStatus.Revoked, (string?)null, (string?)null), (linha.Status, linha.Ciphertext, linha.KeyId));
        Assert.NotNull(linha.RevokedAt);
        Assert.Null(await ResolverAsync(t.Plataforma, t.Id));

        // Depois da revogação o tenant define outra: nasce uma ativa nova, e a revogada continua revogada.
        var novo = NovoCanario();
        await ExecutarAsync(t.Admin, s => s.SetCredentialAsync(Provedor, novo));
        var todas = await CredenciaisAsync(t.Admin);
        Assert.Equal(new[] { StoredAiCredentialStatus.Revoked, StoredAiCredentialStatus.Active }, todas.Select(c => c.Status).ToArray());
        Assert.Equal(novo, (await ResolverAsync(t.Plataforma, t.Id))!.Secret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("com\ncontrole")]
    public async Task Valor_invalido_de_credencial_e_recusado_e_nada_e_gravado(string valor)
    {
        var t = await NovoTenantAsync("aicredinv");

        await Assert.ThrowsAsync<AiGatewayValidationException>(() => ExecutarAsync(t.Admin, s => s.SetCredentialAsync(Provedor, valor)));
        await Assert.ThrowsAsync<AiGatewayValidationException>(() => ExecutarAsync(t.Admin, s => s.SetCredentialAsync("provedor-que-nao-existe", NovoCanario())));

        Assert.Empty(await CredenciaisAsync(t.Admin));
    }

    // Isolamento entre tenants

    [Fact]
    public async Task A_credencial_de_um_tenant_nunca_e_resolvida_para_outro_nem_abre_copiada_para_o_outro()
    {
        var a = await NovoTenantAsync("aiisoa");
        var b = await NovoTenantAsync("aiisob");
        var segredo = NovoCanario();
        await ExecutarAsync(a.Admin, s => s.SetCredentialAsync(Provedor, segredo));
        var deA = Assert.Single(await CredenciaisAsync(a.Admin));

        // O tenant B não tem credencial, não enxerga a de A, e não pode pedir a resolução da de A.
        Assert.Null(await ResolverAsync(b.Plataforma, b.Id));
        Assert.Empty(await CredenciaisAsync(b.Admin));
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => ResolverAsync(b.Plataforma, a.Id));

        // Mesmo COPIANDO a linha cifrada de A para B por SQL (um defeito grave), o texto não abre sob o tenant B: o AAD liga o tenant e a credencial.
        var idCopia = Guid.NewGuid();
        await ComoAsync(b.Admin, sp => sp.GetRequiredService<AiGatewayDbContext>().Database.ExecuteSqlRawAsync(
            "insert into ai_gateway.ai_credentials (tenant_id, id, provider_id, key_id, ciphertext, fingerprint, status, version, created_at, updated_at) values ({0}, {1}, {2}, {3}, {4}, {5}, 1, 1, now(), now())",
            b.Id.Value, idCopia, Provedor, deA.KeyId!, deA.Ciphertext!, deA.Fingerprint));
        var tentativa = await ResolverAsync(b.Plataforma, b.Id);
        Assert.Null(tentativa); // não autentica: tratada como ausente, e nunca devolve o segredo de A
        Assert.Equal(segredo, (await ResolverAsync(a.Plataforma, a.Id))!.Secret); // e a de A continua abrindo para A
    }

    [Fact]
    public async Task Cada_tenant_enxerga_so_a_propria_configuracao_e_o_leitor_recusa_pedir_a_de_outro()
    {
        var a = await NovoTenantAsync("aicfga");
        var b = await NovoTenantAsync("aicfgb");
        await ExecutarAsync(a.Admin, s => s.SetConfigurationAsync(Provedor, Modelo));
        await ExecutarAsync(b.Admin, s => s.SetConfigurationAsync(PostgresFixture.OutroProvedorDeIADeTeste, PostgresFixture.OutroModeloDeIADeTeste));

        var deA = await ComoAsync(a.Plataforma, sp => sp.GetRequiredService<ITenantAiConfigurationReader>().GetAsync(a.Id, CancellationToken.None));
        var deB = await ComoAsync(b.Plataforma, sp => sp.GetRequiredService<ITenantAiConfigurationReader>().GetAsync(b.Id, CancellationToken.None));

        Assert.Equal((a.Id, Provedor), (deA!.TenantId, deA.ProviderId));
        Assert.Equal((b.Id, PostgresFixture.OutroProvedorDeIADeTeste), (deB!.TenantId, deB.ProviderId));
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => ComoAsync(a.Plataforma, sp => sp.GetRequiredService<ITenantAiConfigurationReader>().GetAsync(b.Id, CancellationToken.None)));
    }

    // A chave mestra

    [Fact]
    public async Task Sem_a_chave_mestra_nada_e_gravado_e_a_leitura_da_credencial_falha_fechada()
    {
        var t = await NovoTenantAsync("aisemchave");
        await ExecutarAsync(t.Admin, s => s.SetCredentialAsync(Provedor, NovoCanario()));

        // Um protetor sem a chave que cifrou (e outro sem nenhuma chave ativa).
        var semAChave = new AesGcmCredentialProtector(new AiSecretOptions { ActiveKeyId = "outra", Keys = { ["outra"] = Convert.ToBase64String(new byte[32]) } });
        var semAtiva = new AesGcmCredentialProtector(new AiSecretOptions());
        await ComoAsync(t.Plataforma, async sp =>
        {
            var resolvedor = new AiCredentialResolver(sp.GetRequiredService<AiGatewayDbContext>(), semAChave);
            await Assert.ThrowsAsync<AiSecretsUnavailableException>(() => resolvedor.ResolveAsync(t.Id, Provedor, CancellationToken.None));
            return true;
        });

        var segundo = await NovoTenantAsync("aisemativa");
        await ComoAsync(segundo.Admin, async sp =>
        {
            var servico = new AiTenantConfigurationService(
                sp.GetRequiredService<IRequestContextAccessor>(), sp.GetRequiredService<IAiTenantConfigurationStore>(), sp.GetRequiredService<AiModelCatalog>(), semAtiva,
                sp.GetRequiredService<IaBimeta.Audit.Contracts.Commands.IAuditWriter>(), TimeProvider.System);
            await Assert.ThrowsAsync<AiSecretsUnavailableException>(() => servico.SetCredentialAsync(Provedor, NovoCanario()));
            return true;
        });
        Assert.Empty(await CredenciaisAsync(segundo.Admin)); // nada foi gravado
    }

    [Fact]
    public async Task Provedor_do_catalogo_sem_adaptador_registrado_nao_pode_ser_escolhido_nem_receber_credencial_e_o_registrado_pode()
    {
        var t = await NovoTenantAsync("aisemadap");
        var semAdaptador = new AiProviderRegistry(new IAiProvider[] { new ProvedorFalso("outro-provedor") });
        var comAdaptador = new AiProviderRegistry(new IAiProvider[] { new ProvedorFalso(Provedor) });

        AiTenantConfigurationService Servico(IServiceProvider sp, AiProviderRegistry registro) => new(
            sp.GetRequiredService<IRequestContextAccessor>(), sp.GetRequiredService<IAiTenantConfigurationStore>(), sp.GetRequiredService<AiModelCatalog>(), sp.GetRequiredService<IAiCredentialProtector>(),
            sp.GetRequiredService<IaBimeta.Audit.Contracts.Commands.IAuditWriter>(), TimeProvider.System, registro);

        // O catálogo lista o provedor, mas este host não tem o adaptador: nada é aceito em silêncio.
        await ComoAsync(t.Admin, async sp =>
        {
            var servico = Servico(sp, semAdaptador);
            await Assert.ThrowsAsync<AiGatewayValidationException>(() => servico.SetConfigurationAsync(Provedor, Modelo));
            await Assert.ThrowsAsync<AiGatewayValidationException>(() => servico.SetCredentialAsync(Provedor, NovoCanario()));
            return true;
        });
        Assert.False((await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiTenantConfigurationService>().GetStatusAsync())).Configured);
        Assert.Empty(await CredenciaisAsync(t.Admin));

        // Com o adaptador registrado, a mesma escolha funciona.
        await ComoAsync(t.Admin, async sp =>
        {
            var servico = Servico(sp, comAdaptador);
            await servico.SetConfigurationAsync(Provedor, Modelo);
            await servico.SetCredentialAsync(Provedor, NovoCanario());
            return true;
        });
        Assert.True((await ComoAsync(t.Admin, sp => sp.GetRequiredService<AiTenantConfigurationService>().GetStatusAsync())).Configured);
    }

    // O AiGatewayService de ponta a ponta sobre o banco

    private sealed class ProvedorFalso(string id) : IAiProvider
    {
        public string ProviderId { get; } = id;

        public List<AiCompletionRequest> Requisicoes { get; } = new();

        public Func<AiCompletionRequest, AiCompletionResult> Comportamento { get; set; } = r =>
            AiCompletionResult.Success(new AiCompletion("ok", AiStopReason.Completed), new AiUsage(100, 0, 0, 20), TimeSpan.FromMilliseconds(5), "req-1");

        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
        {
            Requisicoes.Add(request);
            return Task.FromResult(Comportamento(request));
        }
    }

    private AiModelCatalog AiModelCatalogDeTeste() => AiModelCatalog.Create(PostgresFixture.ModelosDeIADeTeste().Select(m => m.ToDescriptor()));

    private async Task<AiGenerationResult> GerarAsync(Tenant t, ProvedorFalso provedor, AiGenerationRequest? pedido = null, AiGatewayOptions? opcoes = null)
    {
        return await ComoAsync(t.Plataforma, sp =>
        {
            var gateway = new AiGatewayService(
                sp.GetRequiredService<IRequestContextAccessor>(),
                sp.GetRequiredService<ITenantAiConfigurationReader>(),
                new AiModelSelector(AiModelCatalogDeTeste(), new AiProviderRegistry(new IAiProvider[] { provedor })),
                sp.GetRequiredService<IAiCredentialResolver>(),
                sp.GetRequiredService<IAiUsageLedger>(),
                TimeProvider.System,
                opcoes);
            return gateway.GenerateAsync(pedido ?? new AiGenerationRequest("turno-ponta-a-ponta", "Você é a atendente virtual.", new[] { new AiMessage(AiMessageRole.User, "Qual o horário?") }, 100, TimeSpan.FromSeconds(5)));
        });
    }

    private async Task<Tenant> TenantConfiguradoAsync(string prefixo, string? segredo = null)
    {
        var t = await NovoTenantAsync(prefixo);
        await ExecutarAsync(t.Admin, s => s.SetConfigurationAsync(Provedor, Modelo));
        if (segredo is not null)
        {
            await ExecutarAsync(t.Admin, s => s.SetCredentialAsync(Provedor, segredo));
        }

        return t;
    }

    private Task<List<AiRequest>> RegistrosAsync(Tenant t) =>
        ComoAsync(t.Plataforma, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.AsNoTracking().OrderBy(r => r.StartedAt).ToListAsync());

    [Fact]
    public async Task De_ponta_a_ponta_o_provedor_recebe_a_credencial_decifrada_o_ledger_confirma_o_consumo_e_o_segredo_nao_aparece_em_nada()
    {
        var segredo = NovoCanario();
        var t = await TenantConfiguradoAsync("aiponta", segredo);
        var provedor = new ProvedorFalso(Provedor);

        var resultado = await GerarAsync(t, provedor);

        Assert.True(resultado.IsSuccess);
        var chamada = Assert.Single(provedor.Requisicoes);
        Assert.Equal(segredo, chamada.Credential.Secret); // decifrada só em memória, na hora
        Assert.Equal(100, chamada.MaxOutputTokens);
        var registro = Assert.Single(await RegistrosAsync(t));
        Assert.Equal((AiRequestState.Confirmed, 120L, Modelo, Provedor, "turno-ponta-a-ponta"), (registro.State, registro.AppliedTokens, registro.ModelId, registro.ProviderId, registro.CorrelationId));
        Assert.True(registro.EstimatedInputTokens > 0 && registro.ReservedTokens == registro.EstimatedInputTokens + 100);
        Assert.Equal(AiUsageCertainty.Reported, registro.UsageCertainty);
        Assert.DoesNotContain(segredo, resultado.ToString());
        var vazou = await ComoAsync(t.Plataforma, sp => sp.GetRequiredService<AiGatewayDbContext>().Database
            .SqlQueryRaw<int>("select count(*)::int as \"Value\" from ai_gateway.ai_requests x where to_jsonb(x)::text like {0}", "%" + segredo + "%").SingleAsync());
        Assert.Equal(0, vazou);
    }

    [Fact]
    public async Task Tenant_bloqueado_credencial_revogada_e_chave_mestra_ausente_nunca_chamam_o_provedor()
    {
        var segredo = NovoCanario();
        var t = await TenantConfiguradoAsync("aifalhas", segredo);
        var provedor = new ProvedorFalso(Provedor);

        await ExecutarAsync(t.Plataforma, s => s.SetBlockedAsync(true));
        var bloqueado = await GerarAsync(t, provedor);
        await ExecutarAsync(t.Plataforma, s => s.SetBlockedAsync(false));
        await ExecutarAsync(t.Admin, s => s.RevokeCredentialAsync(Provedor));
        var revogada = await GerarAsync(t, provedor);

        Assert.Equal((AiErrorKind.NotConfigured, AiFailureCodes.TenantAiDisabled), (bloqueado.Failure!.Kind, bloqueado.Failure.Code));
        Assert.Equal((AiErrorKind.InvalidCredentials, AiFailureCodes.CredentialMissing), (revogada.Failure!.Kind, revogada.Failure.Code));
        Assert.Empty(provedor.Requisicoes);
        Assert.Empty(await RegistrosAsync(t)); // nenhuma chamada foi tentada: nada contabilizado
    }

    [Fact]
    public async Task Falha_do_provedor_sem_prova_de_que_nada_foi_cobrado_fica_indeterminada_e_cobra_a_reserva_e_a_rejeicao_de_admissao_nao_cobra()
    {
        var t = await TenantConfiguradoAsync("aiindet", NovoCanario());
        var provedor = new ProvedorFalso(Provedor)
        {
            Comportamento = _ => AiCompletionResult.Failure(new AiError(AiErrorKind.Transient, null, "http-503"), TimeSpan.FromMilliseconds(9)), // 5xx: padrão Unknown
        };

        await GerarAsync(t, provedor);
        provedor.Comportamento = _ => AiCompletionResult.Failure(new AiError(AiErrorKind.InvalidCredentials, null, "http-401"), TimeSpan.FromMilliseconds(9), usageCertainty: AiUsageCertainty.NoneIncurred);
        await GerarAsync(t, provedor);

        var registros = await RegistrosAsync(t);
        Assert.Equal(new[] { AiRequestState.Indeterminate, AiRequestState.Confirmed }, registros.Select(r => r.State).ToArray());
        Assert.Equal(registros[0].ReservedTokens, registros[0].AppliedTokens); // cobra a reserva
        Assert.Equal(0L, registros[1].AppliedTokens); // evidência de rejeição de admissão: nada cobrado
        var janela = await ComoAsync(t.Plataforma, sp => sp.GetRequiredService<AiGatewayDbContext>().Windows.AsNoTracking().SingleAsync());
        Assert.Equal((registros[0].ReservedTokens, 0L), (janela.IndeterminateTokens, janela.ConfirmedTokens));
    }

    [Fact]
    public async Task O_limite_do_tenant_no_banco_e_respeitado_pelo_gateway_e_a_chamada_recusada_nao_chama_o_provedor()
    {
        var t = await TenantConfiguradoAsync("ailimitegw", NovoCanario());
        await ExecutarAsync(t.Plataforma, s => s.SetLimitOverridesAsync(new AiTenantLimitOverrides(MaxCallsPerPeriod: 2)));
        var provedor = new ProvedorFalso(Provedor);

        var resultados = new List<AiGenerationResult>();
        for (var i = 0; i < 3; i++)
        {
            resultados.Add(await GerarAsync(t, provedor));
        }

        Assert.Equal(new[] { true, true, false }, resultados.Select(r => r.IsSuccess).ToArray());
        Assert.Equal((AiErrorKind.TenantLimitExceeded, AiFailureCodes.PeriodCallLimit), (resultados[2].Failure!.Kind, resultados[2].Failure!.Code));
        Assert.Equal(2, provedor.Requisicoes.Count);
    }

    [Fact]
    public async Task A_contabilidade_fora_do_ar_nao_chama_o_provedor()
    {
        var t = await TenantConfiguradoAsync("aiforagw", NovoCanario());
        var provedor = new ProvedorFalso(Provedor);

        var resultado = await ComoAsync(t.Plataforma, sp =>
        {
            var holder = sp.GetRequiredService<IRequestContextAccessor>();
            var opcoes = new DbContextOptionsBuilder<AiGatewayDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 1, Database = "inexistente", Username = "x", Password = "x", Timeout = 2 }.ConnectionString).Options;
            var ledgerSemBanco = new PostgresAiUsageLedger(new AiGatewayDbContext(opcoes, holder));
            var gateway = new AiGatewayService(
                holder, sp.GetRequiredService<ITenantAiConfigurationReader>(), new AiModelSelector(AiModelCatalogDeTeste(), new AiProviderRegistry(new IAiProvider[] { provedor })),
                sp.GetRequiredService<IAiCredentialResolver>(), ledgerSemBanco, TimeProvider.System);
            return gateway.GenerateAsync(new AiGenerationRequest("turno", "sistema", new[] { new AiMessage(AiMessageRole.User, "oi") }, 100, TimeSpan.FromSeconds(5)));
        });

        Assert.Equal((AiErrorKind.Transient, AiFailureCodes.LedgerUnavailable), (resultado.Failure!.Kind, resultado.Failure.Code));
        Assert.True(resultado.Failure.Retryable);
        Assert.Empty(provedor.Requisicoes);
    }
}
