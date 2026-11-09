using System.Text.Json;
using IaBimeta.Audit.Contracts;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.Channels.Domain;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Channels;

/// <summary>
/// O cadastro dos modelos de mensagem (templates) aprovados na Meta (ADR 0027), com PostgreSQL real: a associação à conta CERTA (isolamento entre tenants, contas e organizações), o cadastro idempotente que
/// atualiza o que a Meta mudou, a permissão de gerir e a auditoria, o que o atendimento enxerga (só modelo aprovado e da conta do número) e as garantias do próprio banco.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelTemplatesTests(PostgresFixture banco)
{
    private const string Resultado = """{"name":"resultado_pronto","language":"pt_BR","category":"UTILITY","status":"APPROVED","id":"1","components":[{"type":"BODY","text":"Olá {{1}}!"}]}""";

    private static JsonElement Json(string texto) => JsonDocument.Parse(texto).RootElement.Clone();

    private static Task<ChannelTemplateRegistration> RegistrarAsync(CenarioDeConversas c, RequestContext? contexto, Guid conta, string json) =>
        c.ComoAsync(contexto, sp => sp.GetRequiredService<ChannelTemplateService>().RegisterAsync(conta, Json(json)));

    private static Task<IReadOnlyList<ChannelTemplateView>?> ListarAsync(CenarioDeConversas c, RequestContext? contexto, Guid conta) =>
        c.ComoAsync(contexto, sp => sp.GetRequiredService<ChannelTemplateService>().ListAsync(conta));

    private static Task<ChannelTemplateView> MudarSituacaoAsync(CenarioDeConversas c, RequestContext? contexto, Guid conta, Guid modelo, TemplateStatus situacao) =>
        c.ComoAsync(contexto, sp => sp.GetRequiredService<ChannelTemplateService>().SetStatusAsync(conta, modelo, situacao));

    private static Task<ChannelReplyOptions?> OpcoesAsync(CenarioDeConversas c, RequestContext? contexto, Guid instancia) =>
        c.ComoAsync(contexto, sp => sp.GetRequiredService<IChannelTemplates>().GetReplyOptionsAsync(new ChannelInstanceId(instancia)));

    private static Task<TemplatePreparation> PrepararAsync(CenarioDeConversas c, RequestContext? contexto, Guid instancia, Guid modelo, params (string, string)[] parametros) =>
        c.ComoAsync(contexto, sp => sp.GetRequiredService<IChannelTemplates>().PrepareAsync(
            new ChannelInstanceId(instancia), modelo, parametros.ToDictionary(p => p.Item1, p => p.Item2)));

    private async Task<(CenarioDeConversas C, ChannelAccountView Conta, ChannelInstanceView Numero, RequestContext Admin)> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var conta = await c.CriarContaAsync();
        var numero = await c.RegistrarNumeroAsync(conta.Id);
        return (c, conta, numero, c.Administrador());
    }

    // O cadastro

    [Fact]
    public async Task O_administrador_associa_o_modelo_a_conta_repetir_nao_muda_nada_e_o_que_a_Meta_mudou_e_atualizado()
    {
        var (c, conta, _, admin) = await MontarAsync("modcadastro");

        var primeira = await RegistrarAsync(c, admin, conta.Id, Resultado);
        var criado = Assert.Single(primeira.Created);
        Assert.Equal(("resultado_pronto", "pt_BR", TemplateStatus.Approved, TemplateCategory.Utility, conta.Id), (criado.Name, criado.Language, criado.Status, criado.Category, criado.AccountId));
        Assert.Equal(new[] { "body.1" }, criado.Parameters.Select(p => p.Key));

        // O mesmo JSON de novo: nada muda e o identificador continua o mesmo.
        var igual = Assert.Single((await RegistrarAsync(c, admin, conta.Id, Resultado)).Unchanged);
        Assert.Equal(criado.Id, igual.Id);
        Assert.InRange((igual.UpdatedAt - criado.UpdatedAt).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1)); // o banco guarda microssegundos: não houve nova escrita

        // A Meta mudou o texto, a categoria e a situação (pausou o modelo): o mesmo registro é atualizado.
        var atualizado = Assert.Single((await RegistrarAsync(
            c, admin, conta.Id,
            """{"name":"resultado_pronto","language":"pt_BR","category":"MARKETING","status":"PAUSED","id":"1","components":[{"type":"BODY","text":"Olá {{1}}, tudo bem? Exame {{2}}."}]}""")).Updated);
        Assert.Equal((criado.Id, TemplateStatus.Paused, TemplateCategory.Marketing), (atualizado.Id, atualizado.Status, atualizado.Category));
        Assert.Equal(new[] { "body.1", "body.2" }, atualizado.Parameters.Select(p => p.Key));
        Assert.Single((await ListarAsync(c, admin, conta.Id))!);

        // Outro idioma é outro modelo; o mesmo nome em outra conta do mesmo tenant também.
        await RegistrarAsync(c, admin, conta.Id, Resultado.Replace("pt_BR", "en_US"));
        var outraConta = await c.CriarContaAsync();
        await RegistrarAsync(c, admin, outraConta.Id, Resultado);
        Assert.Equal(new[] { "pt_BR", "en_US" }.Order(), (await ListarAsync(c, admin, conta.Id))!.Select(m => m.Language).Order());
        Assert.Single((await ListarAsync(c, admin, outraConta.Id))!);

        // Cada associação e cada mudança de situação ficam na trilha de auditoria (sem o texto do modelo).
        Assert.True((await c.AuditoriaAsync("channels.template.register", conta.Id.ToString())).Count >= 4);
    }

    [Fact]
    public async Task Modelo_que_o_MVP_nao_sabe_enviar_e_recusado_com_o_motivo_e_nenhum_dos_demais_e_perdido()
    {
        var (c, conta, _, admin) = await MontarAsync("modrecusa");
        var pagina = $$"""{"data":[{{Resultado}},{"name":"com_imagem","language":"pt_BR","category":"MARKETING","status":"APPROVED","components":[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Oi"}]}]}""";

        var registro = await RegistrarAsync(c, admin, conta.Id, pagina);

        Assert.Single(registro.Created);
        Assert.Contains("IMAGE", Assert.Single(registro.Rejected));
        Assert.Single((await ListarAsync(c, admin, conta.Id))!);

        // Quando NADA é aceito, a operação falha com o motivo (e fica na auditoria como falha).
        var falha = await Assert.ThrowsAsync<ChannelsValidationException>(() => RegistrarAsync(
            c, admin, conta.Id, """{"name":"so_imagem","language":"pt_BR","category":"MARKETING","status":"APPROVED","components":[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Oi"}]}"""));
        Assert.Contains("IMAGE", falha.Message);
        await Assert.ThrowsAsync<ChannelsValidationException>(() => RegistrarAsync(c, admin, conta.Id, "[]"));
        await Assert.ThrowsAsync<ChannelsValidationException>(() => RegistrarAsync(c, admin, conta.Id, "\"texto\""));
        await Assert.ThrowsAsync<ChannelsValidationException>(() => RegistrarAsync(
            c, admin, conta.Id, "[" + string.Join(",", Enumerable.Repeat(Resultado, ChannelTemplateService.MaxTemplatesPerRegistration + 1)) + "]"));
    }

    // Quem pode o quê

    [Fact]
    public async Task So_quem_gere_os_canais_associa_e_muda_a_situacao_o_leitor_lista_e_a_negacao_e_auditada()
    {
        var (c, conta, _, admin) = await MontarAsync("modpermissao");
        var modelo = Assert.Single((await RegistrarAsync(c, admin, conta.Id, Resultado)).Created);
        var leitor = c.Leitor();

        await Assert.ThrowsAsync<AccessDeniedException>(() => RegistrarAsync(c, leitor, conta.Id, Resultado));
        await Assert.ThrowsAsync<AccessDeniedException>(() => MudarSituacaoAsync(c, leitor, conta.Id, modelo.Id, TemplateStatus.Disabled));
        Assert.Single((await ListarAsync(c, leitor, conta.Id))!);
        Assert.Equal(TemplateStatus.Approved, (await ListarAsync(c, admin, conta.Id))!.Single().Status);
        Assert.Contains(await c.AuditoriaAsync("channels.template.register", conta.Id.ToString()), e => e.Outcome == AuditOutcome.Denied && e.ActorUserId == leitor.Principal!.UserId);
        Assert.Contains(await c.AuditoriaAsync("channels.template.status", modelo.Id.ToString()), e => e.Outcome == AuditOutcome.Denied);

        // Sem contexto de tenant, falha fechada.
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => RegistrarAsync(c, null, conta.Id, Resultado));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => ListarAsync(c, null, conta.Id));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => OpcoesAsync(c, null, Guid.NewGuid()));
    }

    [Fact]
    public async Task Sem_leitura_na_organizacao_da_conta_ela_e_o_modelo_respondem_como_inexistentes()
    {
        var (c, conta, _, admin) = await MontarAsync("modorganizacao");
        var modelo = Assert.Single((await RegistrarAsync(c, admin, conta.Id, Resultado)).Created);
        var outraOrganizacao = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var deOutra = c.Administrador(outraOrganizacao.Id);

        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => RegistrarAsync(c, deOutra, conta.Id, Resultado));
        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => MudarSituacaoAsync(c, deOutra, conta.Id, modelo.Id, TemplateStatus.Disabled));
        Assert.Null(await ListarAsync(c, deOutra, conta.Id));
        Assert.Equal(TemplateStatus.Approved, (await ListarAsync(c, admin, conta.Id))!.Single().Status);
    }

    [Fact]
    public async Task O_modelo_de_um_tenant_nunca_aparece_nem_e_usado_por_outro()
    {
        var mauro = await CenarioDeConversas.CriarAsync(banco, "modmauro");
        var pedro = await CenarioDeConversas.CriarAsync(banco, "modpedro");
        var contaDoMauro = await mauro.CriarContaAsync();
        var numeroDoMauro = await mauro.RegistrarNumeroAsync(contaDoMauro.Id);
        var contaDoPedro = await pedro.CriarContaAsync();
        var numeroDoPedro = await pedro.RegistrarNumeroAsync(contaDoPedro.Id);
        var adminDoMauro = mauro.Administrador();
        var adminDoPedro = pedro.Administrador();
        var doTenantDoPedro = pedro.Usuario(null, Permission.OrganizationRead, Permission.ChannelRead, Permission.ChannelManage);
        var modeloDoMauro = Assert.Single((await RegistrarAsync(mauro, adminDoMauro, contaDoMauro.Id, Resultado)).Created);

        // O Pedro não associa, não lista nem muda a situação do modelo do Mauro (nem com a permissão de escopo do tenant dele).
        foreach (var contexto in new[] { adminDoPedro, doTenantDoPedro })
        {
            await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => RegistrarAsync(pedro, contexto, contaDoMauro.Id, Resultado));
            await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => MudarSituacaoAsync(pedro, contexto, contaDoMauro.Id, modeloDoMauro.Id, TemplateStatus.Disabled));
            Assert.Null(await ListarAsync(pedro, contexto, contaDoMauro.Id));
        }

        // A consulta do atendimento: o número do Mauro não existe para o Pedro, e o modelo do Mauro não serve ao número do Pedro.
        Assert.Null(await OpcoesAsync(pedro, pedro.ContextoDeSistema, numeroDoMauro.Id));
        Assert.Equal(ChannelTemplateQueries.TemplateNotFound, (await PrepararAsync(pedro, pedro.ContextoDeSistema, numeroDoMauro.Id, modeloDoMauro.Id, ("body.1", "Pedro"))).FailureCode);
        Assert.Equal(ChannelTemplateQueries.TemplateNotFound, (await PrepararAsync(pedro, pedro.ContextoDeSistema, numeroDoPedro.Id, modeloDoMauro.Id, ("body.1", "Pedro"))).FailureCode);
        Assert.Empty((await OpcoesAsync(pedro, pedro.ContextoDeSistema, numeroDoPedro.Id))!.Templates);

        // O mesmo nome e idioma em cada tenant são modelos independentes.
        var modeloDoPedro = Assert.Single((await RegistrarAsync(pedro, adminDoPedro, contaDoPedro.Id, Resultado)).Created);
        Assert.NotEqual(modeloDoMauro.Id, modeloDoPedro.Id);
        Assert.Equal(new[] { modeloDoMauro.Id }, (await OpcoesAsync(mauro, mauro.ContextoDeSistema, numeroDoMauro.Id))!.Templates.Select(t => t.Id));
        Assert.Equal(new[] { modeloDoPedro.Id }, (await OpcoesAsync(pedro, pedro.ContextoDeSistema, numeroDoPedro.Id))!.Templates.Select(t => t.Id));
    }

    // O que o atendimento enxerga

    [Fact]
    public async Task O_numero_so_oferece_e_prepara_modelos_aprovados_da_conta_dele()
    {
        var (c, conta, numero, admin) = await MontarAsync("modoferta");
        var outraConta = await c.CriarContaAsync();
        var aprovado = Assert.Single((await RegistrarAsync(c, admin, conta.Id, Resultado)).Created);
        var pendente = Assert.Single((await RegistrarAsync(c, admin, conta.Id, Resultado.Replace("resultado_pronto", "em_analise").Replace("APPROVED", "PENDING"))).Created);
        var daOutraConta = Assert.Single((await RegistrarAsync(c, admin, outraConta.Id, Resultado.Replace("resultado_pronto", "da_outra"))).Created);

        var opcoes = (await OpcoesAsync(c, c.ContextoDeSistema, numero.Id))!;

        // Sem adaptador de provedor neste cenário, não há janela conhecida; só o modelo aprovado da conta é oferecido, com a estrutura de parâmetros.
        Assert.Null(opcoes.ReplyWindow);
        Assert.Equal(new[] { aprovado.Id }, opcoes.Templates.Select(t => t.Id));
        Assert.Equal(("body.1", "body"), (opcoes.Templates[0].Parameters[0].Key, opcoes.Templates[0].Parameters[0].Component));

        var pronto = await PrepararAsync(c, c.ContextoDeSistema, numero.Id, aprovado.Id, ("body.1", "Mauro"));
        Assert.True(pronto.IsReady);
        Assert.Equal("Olá Mauro!", pronto.PreviewText);
        Assert.Equal(aprovado.Id.ToString("D"), pronto.ChannelData!["tpl.id"]);

        Assert.Equal(ChannelTemplateQueries.TemplateNotSendable, (await PrepararAsync(c, c.ContextoDeSistema, numero.Id, pendente.Id)).FailureCode);
        Assert.Equal(ChannelTemplateQueries.TemplateNotFound, (await PrepararAsync(c, c.ContextoDeSistema, numero.Id, daOutraConta.Id, ("body.1", "x"))).FailureCode);
        Assert.Equal(ChannelTemplateQueries.TemplateNotFound, (await PrepararAsync(c, c.ContextoDeSistema, numero.Id, Guid.NewGuid())).FailureCode);
        Assert.Equal(ChannelTemplateQueries.TemplateNotFound, (await PrepararAsync(c, c.ContextoDeSistema, Guid.NewGuid(), aprovado.Id)).FailureCode); // número inexistente

        // Parâmetros inválidos voltam como lista de erros e não como falha do modelo.
        var invalido = await PrepararAsync(c, c.ContextoDeSistema, numero.Id, aprovado.Id);
        Assert.False(invalido.IsReady);
        Assert.Null(invalido.FailureCode);
        Assert.Equal(("body.1", "required"), (invalido.Errors.Single().Key, invalido.Errors.Single().Code));

        // Pausado pela administração (ou pela Meta), o modelo sai da oferta e deixa de ser preparado; reaprovado, volta.
        await MudarSituacaoAsync(c, admin, conta.Id, aprovado.Id, TemplateStatus.Paused);
        Assert.Empty((await OpcoesAsync(c, c.ContextoDeSistema, numero.Id))!.Templates);
        Assert.Equal(ChannelTemplateQueries.TemplateNotSendable, (await PrepararAsync(c, c.ContextoDeSistema, numero.Id, aprovado.Id, ("body.1", "Mauro"))).FailureCode);
        await MudarSituacaoAsync(c, admin, conta.Id, aprovado.Id, TemplateStatus.Approved);
        Assert.Single((await OpcoesAsync(c, c.ContextoDeSistema, numero.Id))!.Templates);
        Assert.NotEmpty(await c.AuditoriaAsync("channels.template.status", aprovado.Id.ToString()));
    }

    // As garantias do banco

    [Fact]
    public async Task Banco_recusa_modelo_ligado_a_conta_de_outro_tenant_categoria_de_autenticacao_situacao_invalida_e_duplicata()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "modbancoa");
        var b = await CenarioDeConversas.CriarAsync(banco, "modbancob");
        var contaA = await a.CriarContaAsync();
        var contaB = await b.CriarContaAsync();
        await RegistrarAsync(a, a.Administrador(), contaA.Id, Resultado);

        const string Insercao =
            "insert into channels.channel_templates (id, tenant_id, account_id, name, language, category, status, body_text, parameters, created_at, updated_at) " +
            "values ({0}, {1}, {2}, {3}, 'pt_BR', {4}, {5}, 'Oi', '[]'::jsonb, now(), now())";

        async Task<string?> TentarAsync(CenarioDeConversas dono, Guid tenant, Guid conta, string nome, short categoria, short situacao)
        {
            await using var escopo = banco.NovoEscopo(dono.ContextoDeSistema);
            var db = escopo.ServiceProvider.GetRequiredService<ChannelsDbContext>();
            var falha = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(Insercao, Guid.NewGuid(), tenant, conta, nome, categoria, situacao));
            return falha.SqlState + ":" + (falha.ConstraintName ?? string.Empty);
        }

        // O banco recusa o modelo do tenant B ligado à conta do tenant A (chave estrangeira composta com o tenant).
        Assert.StartsWith(PostgresErrorCodes.ForeignKeyViolation, await TentarAsync(b, b.Tenant.TenantId.Value, contaA.Id, "x", 2, 1));
        Assert.StartsWith(PostgresErrorCodes.ForeignKeyViolation, await TentarAsync(a, a.Tenant.TenantId.Value, Guid.NewGuid(), "x", 2, 1));
        Assert.Equal($"{PostgresErrorCodes.CheckViolation}:ck_channel_templates_category", await TentarAsync(b, b.Tenant.TenantId.Value, contaB.Id, "autenticacao", 3, 1));
        Assert.Equal($"{PostgresErrorCodes.CheckViolation}:ck_channel_templates_status", await TentarAsync(b, b.Tenant.TenantId.Value, contaB.Id, "situacao", 2, 9));

        // Duplicata de conta, nome e idioma.
        Assert.Equal($"{PostgresErrorCodes.UniqueViolation}:ux_channel_templates_tenant_id_account_id_name_language", await TentarAsync(a, a.Tenant.TenantId.Value, contaA.Id, "resultado_pronto", 2, 1));
    }

    [Fact]
    public async Task O_contexto_de_um_tenant_nao_grava_modelo_de_outro()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "modgravaa");
        var b = await CenarioDeConversas.CriarAsync(banco, "modgravab");
        var contaA = await a.CriarContaAsync();
        var intruso = ChannelTemplate.Create(
            a.Tenant.TenantId, contaA.Id, "intruso", "pt_BR", TemplateCategory.Utility, TemplateStatus.Approved, null, null, "Oi", null, Array.Empty<TemplateParameterSpec>(), DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => b.ComoAsync(b.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ChannelsDbContext>();
            db.Templates.Add(intruso);
            return await db.SaveChangesAsync();
        }));
    }
}
