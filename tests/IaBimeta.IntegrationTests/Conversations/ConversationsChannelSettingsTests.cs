using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// O que o número (instância de canal) informa na criação da sessão: o idioma padrão do canal, entre a informação do cliente e
/// o padrão do tenant, e o modo inicial de atendimento (AI ou Human; Hybrid reservado). Cada número tem o próprio idioma e a
/// mesma pessoa em dois números mantém conversas independentes. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationsChannelSettingsTests(PostgresFixture banco)
{
    // Idioma padrão do canal

    [Fact]
    public async Task Sem_informacao_do_cliente_vale_o_idioma_padrao_do_numero_e_nao_o_do_tenant()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "idiomacanal");
        await c.DefinirIdiomaPadraoDoTenantAsync("pt-BR");

        var entrada = await c.EntrarAsync("5511999990001", idiomaPadraoDoCanal: "es-ES");

        var sessao = await c.SessaoAsync(entrada.SessionId);
        Assert.Equal((Locale.Parse("es-ES"), LocaleSource.ChannelDefault), (sessao!.EffectiveLocale, sessao.LocaleSource));
        Assert.True(LocaleResolution.IsAssumed(sessao.LocaleSource!.Value));
    }

    [Fact]
    public async Task A_informacao_do_cliente_vence_o_idioma_do_numero_e_sem_nenhum_dos_dois_vale_o_do_tenant()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "idiomaprecedencia");
        await c.DefinirIdiomaPadraoDoTenantAsync("pt-BR");

        var comDica = await c.EntrarAsync("5511999990001", idiomaDoContato: "en-US", idiomaPadraoDoCanal: "es-ES");
        var semNada = await c.EntrarAsync("5511999990002");

        var a = await c.SessaoAsync(comDica.SessionId);
        var b = await c.SessaoAsync(semNada.SessionId);
        Assert.Equal((Locale.Parse("en-US"), LocaleSource.ChannelHint), (a!.EffectiveLocale, a.LocaleSource));
        Assert.Equal((Locale.Parse("pt-BR"), LocaleSource.TenantDefault), (b!.EffectiveLocale, b.LocaleSource));
    }

    [Fact]
    public async Task A_preferencia_guardada_do_contato_vence_o_idioma_do_numero_numa_conversa_nova()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "idiomapreferencia");
        var primeira = await c.EntrarAsync("5511999990001", idiomaPadraoDoCanal: "es-ES");
        await c.DefinirPreferenciaDoContatoAsync(primeira.SessionId, "fr-FR");
        await c.EncerrarSessaoAsync(primeira.SessionId);

        var nova = await c.EntrarAsync("5511999990001", idiomaPadraoDoCanal: "es-ES");

        var sessao = await c.SessaoAsync(nova.SessionId);
        Assert.NotEqual(primeira.SessionId, nova.SessionId);
        Assert.Equal((Locale.Parse("fr-FR"), LocaleSource.ContactPreference), (sessao!.EffectiveLocale, sessao.LocaleSource));
    }

    [Fact]
    public async Task O_idioma_do_numero_so_decide_a_conversa_nova_e_nao_muda_a_que_esta_em_andamento()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "idiomaandamento");
        var primeira = await c.EntrarAsync("5511999990001", idiomaPadraoDoCanal: "es-ES", evento: "evt-1");

        var segunda = await c.EntrarAsync("5511999990001", idiomaPadraoDoCanal: "en-US", evento: "evt-2");

        Assert.Equal(primeira.SessionId, segunda.SessionId);
        Assert.Equal(Locale.Parse("es-ES"), (await c.SessaoAsync(primeira.SessionId))!.EffectiveLocale);
    }

    [Fact]
    public async Task A_mesma_pessoa_em_dois_numeros_com_idiomas_diferentes_tem_conversas_independentes()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "idiomadoisnumeros");
        var numeroEspanhol = ChannelInstanceId.New();
        var numeroIngles = ChannelInstanceId.New();

        var emEspanhol = await c.EntrarAsync("5511999990001", canal: numeroEspanhol, idiomaPadraoDoCanal: "es-ES");
        var emIngles = await c.EntrarAsync("5511999990001", canal: numeroIngles, idiomaPadraoDoCanal: "en-US");
        await c.AlterarIdiomaAsync(emEspanhol.SessionId, "fr-FR", 1);

        Assert.NotEqual(emEspanhol.SessionId, emIngles.SessionId);
        Assert.Equal(Locale.Parse("fr-FR"), (await c.SessaoAsync(emEspanhol.SessionId))!.EffectiveLocale);
        Assert.Equal(Locale.Parse("en-US"), (await c.SessaoAsync(emIngles.SessionId))!.EffectiveLocale);
    }

    [Fact]
    public async Task O_banco_aceita_a_origem_idioma_do_canal_e_continua_recusando_origem_desconhecida()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "idiomaconstraint");
        var entrada = await c.EntrarAsync("5511999990001", idiomaPadraoDoCanal: "es-ES");

        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var falha = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "update conversations.conversations set locale_source = 7 where tenant_id = {0} and session_id = {1}", c.Tenant.TenantId.Value, entrada.SessionId));

        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_conversations_locale"), (falha.SqlState, falha.ConstraintName));
    }

    // Modo inicial de atendimento: AI, Human e Hybrid

    [Fact]
    public async Task Por_padrao_a_sessao_nasce_com_a_IA_autonoma_e_o_turno_de_IA_e_concedido()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "modoia");

        var entrada = await c.EntrarAsync("5511999990001");

        var sessao = await c.SessaoAsync(entrada.SessionId);
        Assert.Equal((HandlingMode.AiAutonomous, (Guid?)null, 1L), (sessao!.HandlingMode, sessao.ResponderRef, sessao.StateVersion));
        Assert.Equal(BeginTurnOutcome.Granted, (await c.IniciarTurnoAsync(entrada.SessionId, Guid.NewGuid())).Outcome);
    }

    [Fact]
    public async Task Numero_so_humano_cria_a_sessao_pendente_para_o_atendente_e_a_IA_nunca_abre_turno()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "modohumano");

        var entrada = await c.EntrarAsync("5511999990001", modoInicial: HandlingMode.HumanAssisted);

        var sessao = await c.SessaoAsync(entrada.SessionId);
        Assert.Equal((HandlingMode.HumanAssisted, (Guid?)null, 1L), (sessao!.HandlingMode, sessao.ResponderRef, sessao.StateVersion));
        Assert.Equal(BeginTurnOutcome.NotAiResponder, (await c.IniciarTurnoAsync(entrada.SessionId, Guid.NewGuid())).Outcome);
    }

    [Fact]
    public async Task A_sessao_humana_pode_voltar_para_a_IA_pelo_caminho_normal_de_transicao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "modohumanovolta");
        var entrada = await c.EntrarAsync("5511999990001", modoInicial: HandlingMode.HumanAssisted);

        var transicao = await c.TransicionarAsync(entrada.SessionId, HandlingMode.AiAutonomous, null, versaoEsperada: 1);

        Assert.Equal(TransitionOutcome.Applied, transicao.Outcome);
        Assert.Equal(BeginTurnOutcome.Granted, (await c.IniciarTurnoAsync(entrada.SessionId, Guid.NewGuid())).Outcome);
    }

    [Fact]
    public async Task O_modo_hibrido_e_o_invalido_sao_recusados_sem_gravar_nada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "modohibrido");
        var leitura = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var antes = (await c.ListarSessoesAsync(leitura)).Items.Count;

        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync("5511999990001", modoInicial: HandlingMode.AiSupervised));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync("5511999990002", modoInicial: (HandlingMode)99));

        Assert.Equal(antes, (await c.ListarSessoesAsync(leitura)).Items.Count);
    }

    [Fact]
    public async Task O_modo_inicial_so_vale_para_a_sessao_nova_uma_sessao_aberta_mantem_o_modo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "modoexistente");
        var primeira = await c.EntrarAsync("5511999990001", evento: "evt-1");

        var segunda = await c.EntrarAsync("5511999990001", evento: "evt-2", modoInicial: HandlingMode.HumanAssisted);

        Assert.Equal(primeira.SessionId, segunda.SessionId);
        Assert.Equal(HandlingMode.AiAutonomous, (await c.SessaoAsync(primeira.SessionId))!.HandlingMode);
    }

    [Fact]
    public void Os_tres_modos_do_produto_correspondem_a_valores_estaveis_do_vocabulario_unico()
    {
        // AI = AiAutonomous, Human = HumanAssisted, Hybrid = AiSupervised (reservado): os números são gravados no banco.
        Assert.Equal((1, 2, 3), ((int)HandlingMode.AiAutonomous, (int)HandlingMode.AiSupervised, (int)HandlingMode.HumanAssisted));
    }
}
