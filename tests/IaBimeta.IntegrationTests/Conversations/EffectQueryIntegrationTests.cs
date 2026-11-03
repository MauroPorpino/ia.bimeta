using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// A consulta de efeito do Conversations (<c>GetEffectAsync</c>), com PostgreSQL real: só a transição e o encaminhamento são respondidos, com o comportamento de
/// sempre; <c>HumanReply</c> e <c>SessionClose</c> (reservados para as fatias A4 e A6) e qualquer valor desconhecido são RECUSADOS. Antes da correção, todo tipo
/// diferente de <c>Transfer</c> era tratado como <c>Transition</c>: o identificador de uma transição real consultado como <c>SessionClose</c> respondia
/// <c>Applied</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EffectQueryIntegrationTests(PostgresFixture banco)
{
    private const string Contato = "5511999990001";

    private static Task<EffectView> EfeitoAsync(CenarioDeConversas c, Guid sessao, EffectKind tipo, Guid operacao) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IConversationQueries>().GetEffectAsync(new GetEffectQuery(sessao, tipo, operacao)));

    [Fact]
    public async Task A_transicao_aplicada_continua_respondendo_Applied_com_a_versao_a_fronteira_e_o_periodo_e_o_desconhecido_NotFound()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "efeitotrans");
        var r = await c.EntrarAsync(Contato);
        var transicao = Guid.NewGuid();
        var aplicada = await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, null, 1, transicao);
        Assert.Equal(TransitionOutcome.Applied, aplicada.Outcome);

        var efeito = await EfeitoAsync(c, r.SessionId, EffectKind.Transition, transicao);
        var desconhecido = await EfeitoAsync(c, r.SessionId, EffectKind.Transition, Guid.NewGuid());

        Assert.Equal(EffectOutcome.Applied, efeito.Outcome);
        Assert.Equal(aplicada.StateVersion, efeito.StateVersion);
        Assert.Equal(1L, efeito.MessageSeq);
        Assert.Equal(1L, efeito.HandlingPeriod);
        Assert.Equal(new EffectView(EffectOutcome.NotFound), desconhecido);
    }

    [Fact]
    public async Task O_encaminhamento_aplicado_continua_respondendo_Applied_com_a_versao_e_o_desconhecido_NotFound()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "efeitotransf");
        var filialA = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial A");
        var filialB = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial B");
        var r = await c.EntrarAsync(Contato, "Primeira", unidade: filialA);
        var transferId = Guid.NewGuid();
        var aplicado = await c.EncaminharAsync(r.SessionId, filialB, null, 1, transferId);
        Assert.Equal(TransferOutcome.Applied, aplicado.Outcome);

        var efeito = await EfeitoAsync(c, r.SessionId, EffectKind.Transfer, transferId);
        var desconhecido = await EfeitoAsync(c, r.SessionId, EffectKind.Transfer, Guid.NewGuid());

        Assert.Equal(new EffectView(EffectOutcome.Applied, aplicado.StateVersion), efeito);
        Assert.Equal(new EffectView(EffectOutcome.NotFound), desconhecido);
    }

    [Fact]
    public async Task A_transicao_e_o_encaminhamento_de_outro_tenant_continuam_NotFound()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "efeitoa");
        var outro = await CenarioDeConversas.CriarAsync(banco, "efeitob");
        var filialA = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial A");
        var filialB = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial B");
        var r = await c.EntrarAsync(Contato, "Primeira", unidade: filialA);
        var transicao = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        Assert.Equal(TransferOutcome.Applied, (await c.EncaminharAsync(r.SessionId, filialB, null, 1, transferId)).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, null, 2, transicao)).Outcome);

        Assert.Equal(EffectOutcome.Applied, (await EfeitoAsync(c, r.SessionId, EffectKind.Transition, transicao)).Outcome);
        Assert.Equal(EffectOutcome.Applied, (await EfeitoAsync(c, r.SessionId, EffectKind.Transfer, transferId)).Outcome);
        Assert.Equal(EffectOutcome.NotFound, (await EfeitoAsync(outro, r.SessionId, EffectKind.Transition, transicao)).Outcome);
        Assert.Equal(EffectOutcome.NotFound, (await EfeitoAsync(outro, r.SessionId, EffectKind.Transfer, transferId)).Outcome);
    }

    [Theory]
    [InlineData(EffectKind.HumanReply)]
    [InlineData(EffectKind.SessionClose)]
    public async Task O_identificador_de_uma_transicao_ou_de_um_encaminhamento_reais_nao_e_mais_respondido_como_tipo_reservado(EffectKind reservado)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "efeitoreserva" + reservado.ToString().ToLowerInvariant());
        var filialA = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial A");
        var filialB = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial B");
        var r = await c.EntrarAsync(Contato, "Primeira", unidade: filialA);
        var transicao = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        Assert.Equal(TransferOutcome.Applied, (await c.EncaminharAsync(r.SessionId, filialB, null, 1, transferId)).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, null, 2, transicao)).Outcome);

        // Antes da correção, estas duas consultas respondiam Applied (o tipo reservado caía no ramo da transição).
        var comTransicao = await Assert.ThrowsAsync<ConversationsValidationException>(() => EfeitoAsync(c, r.SessionId, reservado, transicao));
        var comEncaminhamento = await Assert.ThrowsAsync<ConversationsValidationException>(() => EfeitoAsync(c, r.SessionId, reservado, transferId));
        var inexistente = await Assert.ThrowsAsync<ConversationsValidationException>(() => EfeitoAsync(c, r.SessionId, reservado, Guid.NewGuid()));

        Assert.All(new[] { comTransicao, comEncaminhamento, inexistente }, falha => Assert.Contains("ainda não suporta", falha.Message));
    }

    [Fact]
    public async Task A_recusa_do_tipo_reservado_nao_depende_da_sessao_nem_do_tenant_e_nao_revela_existencia()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "efeitoreservaiso");
        var outro = await CenarioDeConversas.CriarAsync(banco, "efeitoreservaoutro");
        var r = await c.EntrarAsync(Contato);

        // Mesma recusa para a sessão que existe, a de outro tenant e a que não existe: nada vaza e nada é lido.
        await Assert.ThrowsAsync<ConversationsValidationException>(() => EfeitoAsync(c, r.SessionId, EffectKind.SessionClose, Guid.NewGuid()));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => EfeitoAsync(outro, r.SessionId, EffectKind.SessionClose, Guid.NewGuid()));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => EfeitoAsync(c, Guid.NewGuid(), EffectKind.HumanReply, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(99)]
    public async Task O_tipo_desconhecido_falha_fechado_tambem_com_banco_e_sessao_reais(int valor)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "efeitodesc" + valor);
        var r = await c.EntrarAsync(Contato);
        var transicao = Guid.NewGuid();
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, null, 1, transicao)).Outcome);

        await Assert.ThrowsAsync<ConversationsValidationException>(() => EfeitoAsync(c, r.SessionId, (EffectKind)valor, transicao));
    }
}
