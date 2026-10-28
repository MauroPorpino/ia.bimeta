using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Channels;

/// <summary>
/// As garantias do próprio banco, independentes do aplicativo (TEN-3 e DAD-2): uma chave estrangeira composta liga a rota à
/// instância, ao tenant e ao mesmo identificador de número; a unidade padrão precisa estar entre as autorizadas; as restrições
/// de domínio recusam valores inválidos de qualquer caminho. A rota de entrada resolve o tenant sem contexto, expondo só as
/// chaves de roteamento.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelsDatabaseGuardsTests(PostgresFixture banco)
{
    private async Task<(CenarioDeConversas Cenario, ChannelInstanceView Numero, Guid Unidade)> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var numero = await c.NovoNumeroAsync();
        await c.AutorizarAsync(numero.Id, unidade);
        return (c, numero, unidade);
    }

    private static Guid Tenant(CenarioDeConversas c) => c.Tenant.TenantId.Value;

    // A rota de entrada

    [Fact]
    public async Task A_rota_nasce_com_a_instancia_e_resolve_o_tenant_sem_nenhum_contexto()
    {
        var (c, numero, _) = await MontarAsync("rotaresolve");

        var resolucao = await c.ComoAsync(null, sp => sp.GetRequiredService<ChannelRouteResolver>().ResolveAsync(ChannelProvider.WhatsAppCloud, numero.PhoneNumberId));

        Assert.NotNull(resolucao);
        Assert.Equal((numero.Id, c.Tenant.TenantId, ChannelStatus.Inactive), (resolucao.ChannelInstanceId.Value, resolucao.TenantId, resolucao.Status));
    }

    [Fact]
    public async Task A_resolucao_mostra_a_situacao_atual_do_numero_e_o_tenant_dono_mesmo_com_o_contexto_de_outro()
    {
        var (c, numero, _) = await MontarAsync("rotasituacao");
        var intruso = await CenarioDeConversas.CriarAsync(banco, "rotaintruso");
        await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelInstanceService>().ActivateAsync(numero.Id));

        var ativo = await c.ComoAsync(intruso.ContextoDeSistema, sp => sp.GetRequiredService<ChannelRouteResolver>().ResolveAsync(ChannelProvider.WhatsAppCloud, numero.PhoneNumberId));
        await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelInstanceService>().SuspendAsync(numero.Id));
        var suspenso = await c.ComoAsync(null, sp => sp.GetRequiredService<ChannelRouteResolver>().ResolveAsync(ChannelProvider.WhatsAppCloud, numero.PhoneNumberId));

        // O dono é o do cadastro, nunca o do contexto de quem pergunta.
        Assert.Equal((c.Tenant.TenantId, ChannelStatus.Active), (ativo!.TenantId, ativo.Status));
        Assert.Equal((c.Tenant.TenantId, ChannelStatus.Suspended), (suspenso!.TenantId, suspenso.Status));
    }

    [Theory]
    [InlineData("numero-que-nao-existe")]
    [InlineData("")]
    [InlineData("com espaco")]
    [InlineData("a/b")]
    [InlineData("'; DROP TABLE channels.channel_routes; --")]
    public async Task Identificador_desconhecido_ou_mal_formado_resolve_para_nada_sem_excecao(string identificador)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rotadesconhecida");

        Assert.Null(await c.ComoAsync(null, sp => sp.GetRequiredService<ChannelRouteResolver>().ResolveAsync(ChannelProvider.WhatsAppCloud, identificador)));
        Assert.Null(await c.ComoAsync(null, sp => sp.GetRequiredService<ChannelRouteResolver>().ResolveAsync((ChannelProvider)99, "123")));
        Assert.Null(await c.ComoAsync(null, sp => sp.GetRequiredService<ChannelRouteResolver>().ResolveAsync(ChannelProvider.WhatsAppCloud, null)));
    }

    [Fact]
    public void A_resolucao_expoe_so_as_chaves_de_roteamento_e_nunca_configuracao_nem_credencial()
    {
        var propriedades = typeof(ChannelRouteResolution).GetProperties().Select(p => p.Name).Order().ToList();

        Assert.Equal(new[] { "ChannelInstanceId", "Status", "TenantId" }, propriedades);
    }

    [Fact]
    public async Task A_tabela_de_rotas_so_tem_as_chaves_de_roteamento()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rotacolunas");

        var colunas = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelsDbContext>().Database
            .SqlQueryRaw<string>("select column_name::text as \"Value\" from information_schema.columns where table_schema = 'channels' and table_name = 'channel_routes' order by column_name")
            .ToListAsync());

        Assert.Equal(new[] { "channel_instance_id", "created_at", "external_id", "provider", "tenant_id" }, colunas);
    }

    [Fact]
    public async Task O_identificador_do_provedor_e_unico_na_rota_por_indice_unico_global()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rotaindice");

        var indices = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelsDbContext>().Database
            .SqlQueryRaw<string>("select indexdef as \"Value\" from pg_indexes where schemaname = 'channels' and indexname in ('ux_channel_routes_provider_external_id', 'ux_channel_instances_provider_phone_number_id', 'ux_channel_accounts_provider_account_id')")
            .ToListAsync());

        Assert.Equal(3, indices.Count);
        Assert.All(indices, definicao => Assert.Contains("CREATE UNIQUE INDEX", definicao, StringComparison.Ordinal));
        Assert.Contains(indices, i => i.Contains("(provider, external_id)", StringComparison.Ordinal));
        Assert.Contains(indices, i => i.Contains("(provider, phone_number_id)", StringComparison.Ordinal));
        Assert.Contains(indices, i => i.Contains("(provider, provider_account_id)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Banco_recusa_rota_que_aponta_para_a_instancia_de_outro_tenant()
    {
        var (a, numeroA, _) = await MontarAsync("rotaforaa");
        var b = await CenarioDeConversas.CriarAsync(banco, "rotaforab");

        await using var escopo = banco.NovoEscopo(b.ContextoDeSistema);
        var db = escopo.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var falha = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "insert into channels.channel_routes (tenant_id, provider, external_id, channel_instance_id, created_at) values ({0}, 1, {1}, {2}, now())",
            Tenant(b), $"pendurada-{Guid.NewGuid():N}", numeroA.Id));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, falha.SqlState);
        Assert.Equal(numeroA.Id, (await a.LerNumeroAsync(numeroA.Id))!.Id);
    }

    [Fact]
    public async Task Banco_recusa_rota_cujo_identificador_diverge_do_numero_da_instancia()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rotadiverge");
        var conta = await c.CriarContaAsync();
        var numero = await c.RegistrarNumeroAsync(conta.Id);

        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        var db = escopo.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var falha = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "insert into channels.channel_routes (tenant_id, provider, external_id, channel_instance_id, created_at) values ({0}, 1, {1}, {2}, now())",
            Tenant(c), $"outro-{Guid.NewGuid():N}", numero.Id));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, falha.SqlState);
    }

    [Fact]
    public async Task Banco_recusa_trocar_o_numero_de_uma_instancia_que_ja_tem_rota()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rotaimutavel");
        var conta = await c.CriarContaAsync();
        var numero = await c.RegistrarNumeroAsync(conta.Id);

        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        var db = escopo.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var falha = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "update channels.channel_instances set phone_number_id = {0} where tenant_id = {1} and id = {2}", $"trocado-{Guid.NewGuid():N}", Tenant(c), numero.Id));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, falha.SqlState);
    }

    [Fact]
    public async Task Cada_instancia_tem_exatamente_uma_rota()
    {
        var (c, numero, _) = await MontarAsync("rotaumaporinstancia");

        var rotas = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelsDbContext>().Database
            .SqlQueryRaw<int>("select count(*)::int as \"Value\" from channels.channel_routes where channel_instance_id = {0}", numero.Id)
            .SingleAsync());

        Assert.Equal(1, rotas);
    }

    [Fact]
    public async Task Dois_tenants_registrando_o_mesmo_numero_ao_mesmo_tempo_so_um_vence()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "corridaa");
        var b = await CenarioDeConversas.CriarAsync(banco, "corridab");
        var contaA = await a.CriarContaAsync();
        var contaB = await b.CriarContaAsync();
        var numero = ChannelsTestSupport.Unico("num");

        var resultados = await Task.WhenAll(
            Tentar(() => a.RegistrarNumeroAsync(contaA.Id, numero: numero)),
            Tentar(() => b.RegistrarNumeroAsync(contaB.Id, numero: numero)));

        Assert.Equal(1, resultados.Count(r => r is null));
        Assert.Equal(1, resultados.Count(r => r is ChannelIdentifierInUseException));
        var resolucao = await a.ComoAsync(null, sp => sp.GetRequiredService<ChannelRouteResolver>().ResolveAsync(ChannelProvider.WhatsAppCloud, numero));
        Assert.NotNull(resolucao);
    }

    private static async Task<Exception?> Tentar(Func<Task> acao)
    {
        try
        {
            await acao();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // A unidade padrão precisa estar entre as autorizadas, também para o banco

    [Fact]
    public async Task Banco_recusa_unidade_padrao_que_nao_esta_entre_as_autorizadas()
    {
        var (c, numero, _) = await MontarAsync("padraobanco");

        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        var db = escopo.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        await using var transacao = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            "update channels.channel_instances set default_business_unit_id = {0} where tenant_id = {1} and id = {2}", Guid.NewGuid(), Tenant(c), numero.Id);

        // A chave é adiada: a violação aparece no fim da transação.
        var falha = await Assert.ThrowsAsync<PostgresException>(() => transacao.CommitAsync());

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, falha.SqlState);
        Assert.Contains("fk_channel_instances_default_unit_authorized", falha.ConstraintName ?? falha.MessageText, StringComparison.Ordinal);
        Assert.Null((await c.LerNumeroAsync(numero.Id))!.DefaultBusinessUnitId);
    }

    [Fact]
    public async Task Banco_aceita_unidade_padrao_autorizada_e_recusa_apagar_a_autorizacao_da_unidade_padrao()
    {
        var (c, numero, unidade) = await MontarAsync("padraoautorizada");

        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ChannelsDbContext>();
            await using var transacao = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync(
                "update channels.channel_instances set default_business_unit_id = {0} where tenant_id = {1} and id = {2}", unidade, Tenant(c), numero.Id);
            await transacao.CommitAsync();
        }

        Assert.Equal(unidade, (await c.LerNumeroAsync(numero.Id))!.DefaultBusinessUnitId);

        await using var escopoDeApagar = banco.NovoEscopo(c.ContextoDeSistema);
        var dbDeApagar = escopoDeApagar.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        await using var transacaoDeApagar = await dbDeApagar.Database.BeginTransactionAsync();
        await dbDeApagar.Database.ExecuteSqlRawAsync(
            "delete from channels.channel_instance_units where tenant_id = {0} and channel_instance_id = {1} and business_unit_id = {2}", Tenant(c), numero.Id, unidade);
        var falha = await Assert.ThrowsAsync<PostgresException>(() => transacaoDeApagar.CommitAsync());

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, falha.SqlState);
        Assert.Equal(new[] { unidade }, (await c.LerNumeroAsync(numero.Id))!.AuthorizedBusinessUnitIds);
    }

    [Theory]
    [InlineData("update channels.channel_instances set status = 9 where tenant_id = {0} and id = {1}", "ck_channel_instances_status")]
    [InlineData("update channels.channel_instances set version = 0 where tenant_id = {0} and id = {1}", "ck_channel_instances_version")]
    [InlineData("update channels.channel_instances set default_locale = 'pt_BR' where tenant_id = {0} and id = {1}", "ck_channel_instances_default_locale")]
    [InlineData("update channels.channel_instances set default_department_id = gen_random_uuid() where tenant_id = {0} and id = {1}", "ck_channel_instances_default_department")]
    [InlineData("update channels.channel_instances set provider = 2 where tenant_id = {0} and id = {1}", "ck_channel_instances_provider")]
    public async Task Restricoes_de_dominio_recusam_valores_invalidos_vindos_de_qualquer_caminho(string comando, string restricao)
    {
        var (c, numero, _) = await MontarAsync("restricoes");

        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        var db = escopo.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var falha = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(comando, Tenant(c), numero.Id));

        Assert.Equal((PostgresErrorCodes.CheckViolation, restricao), (falha.SqlState, falha.ConstraintName));
    }

    // Isolamento por tenant no armazenamento

    [Fact]
    public async Task O_contexto_nao_grava_entidade_de_outro_tenant()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "isolagravaa");
        var b = await CenarioDeConversas.CriarAsync(banco, "isolagravab");

        await using var escopo = banco.NovoEscopo(b.ContextoDeSistema);
        var db = escopo.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        db.Accounts.Add(ChannelAccount.Create(a.Tenant.TenantId, a.OrganizacaoId, ChannelProvider.WhatsAppCloud, ChannelsTestSupport.Unico("waba"), "Conta forjada", DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task As_consultas_do_modulo_so_enxergam_o_tenant_do_contexto_e_sem_contexto_falham_fechadas()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "isolaleia");
        var b = await CenarioDeConversas.CriarAsync(banco, "isolaleib");
        var contaA = await a.CriarContaAsync();
        var numeroA = await a.RegistrarNumeroAsync(contaA.Id);
        await b.NovoNumeroAsync();

        var visiveisParaB = await b.ComoAsync(b.ContextoDeSistema, sp => sp.GetRequiredService<ChannelsDbContext>().Instances.Select(i => i.Id).ToListAsync());

        Assert.DoesNotContain(new ChannelInstanceId(numeroA.Id), visiveisParaB);
        Assert.Single(visiveisParaB);
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => a.ComoAsync(null, sp => sp.GetRequiredService<ChannelsDbContext>().Instances.ToListAsync()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => a.ComoAsync(null, sp => sp.GetRequiredService<ChannelsDbContext>().InstanceUnits.ToListAsync()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => a.ComoAsync(null, sp => sp.GetRequiredService<ChannelsDbContext>().Secrets.ToListAsync()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => a.ComoAsync(null, sp => sp.GetRequiredService<ChannelsDbContext>().Accounts.ToListAsync()));
    }

    [Fact]
    public async Task Nao_ha_chave_estrangeira_para_outros_esquemas()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "semfkcruzada");

        var cruzadas = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelsDbContext>().Database
            .SqlQueryRaw<string>(
                @"select (conrelid::regclass)::text || ' -> ' || (confrelid::regclass)::text as ""Value""
                  from pg_constraint
                  where contype = 'f' and connamespace = 'channels'::regnamespace and confrelid::regclass::text not like 'channels.%'")
            .ToListAsync());

        Assert.Empty(cruzadas);
    }
}
