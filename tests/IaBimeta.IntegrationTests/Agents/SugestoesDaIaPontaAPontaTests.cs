using System.Text.Json;
using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// As sugestoes da IA local (ADR 0034) ponta a ponta: uma pergunta real sem resposta no FAQ vira lacuna, o administrador pede a sugestao (a IA local e simulada), o texto vai a ela SEM dados pessoais, a sugestao nasce
/// pendente SEM publicar nada, e so a decisao humana a leva ao RASCUNHO, com a resposta escrita por uma pessoa e a resposta direta desligada. PostgreSQL, Conversations e Agents reais.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SugestoesDaIaPontaAPontaTests(PostgresFixture banco)
{
    private const string Pergunta = "Posso tomar cafe antes da coleta? meu email e ana@exemplo.com";

    private static RequestContext AdminQueLe(CenarioDeAgentes a) =>
        PostgresFixture.ContextoDeUsuario(a.Tenant, Guid.NewGuid(), new PermissionGrant(Permission.TenantSettingsManage, null), new PermissionGrant(Permission.ConversationRead, null));

    private async Task<(CenarioDeAgentes A, Guid Lacuna)> LacunaAsync(string prefixo)
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, prefixo);
        var entrada = await a.EntrarAsync("mauro", Pergunta);
        var atendente = Guid.NewGuid();
        var turno = Guid.NewGuid();
        var inicio = await a.C.IniciarTurnoAsync(entrada.SessionId, turno, 1);
        Assert.Equal(RegisterReplyOutcome.Accepted, (await a.C.ResponderAsync(entrada.SessionId, turno, inicio, entrada.ConversationId, "Resposta da IA.")).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await a.C.TransicionarAsync(entrada.SessionId, HandlingMode.HumanAssisted, atendente, (await a.C.SessaoAsync(entrada.SessionId))!.StateVersion)).Outcome);
        var s = (await a.C.SessaoAsync(entrada.SessionId))!;
        await a.C.ComoAsync(
            a.C.ContextoDeSistema,
            sp => sp.GetRequiredService<IHandlingCommands>().RegisterHumanReplyAsync(new RegisterHumanReplyCommand(
                entrada.SessionId, Guid.NewGuid(), atendente, new ReplyPrecondition(s.HandlingPeriod, s.PlacementEpoch, ResponderMatch.Exact, atendente), s.StateVersion, 0,
                new ReplyContent("Pode tomar cafe sem acucar. Ligue (81) 99999-1234 se tiver duvida.", null, null))));

        var mensagem = (await a.C.HistoricoAsync(entrada.SessionId))!.Items.Single(m => m.Direction == MessageDirection.Inbound);
        await a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            await sp.GetRequiredService<IKnowledgeGapRecorder>().RecordAsync(
                new KnowledgeGapCandidate(a.Tenant.TenantId, Guid.NewGuid(), entrada.SessionId, entrada.ConversationId, mensagem.Id, FaqMatcher.Normalize(Pergunta), KnowledgeGapKind.NoMatch, DateTimeOffset.UtcNow), default);
            return 0;
        });
        var lacuna = (await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().KnowledgeGaps.AsNoTracking().ToListAsync())).Single();
        return (a, lacuna.Id);
    }

    private static string NovoFaq(string pergunta, string resposta = "") =>
        JsonSerializer.Serialize(new { action = "new_faq", question = pergunta, aliases = new[] { "cafe antes do exame" }, answer = resposta, targetQuestion = "", rationale = "duvida recorrente" });

    [Fact]
    public async Task Fluxo_completo_gerar_revisar_e_aceitar_vai_so_ao_rascunho_com_a_resposta_direta_desligada_e_sem_dado_pessoal_no_modelo()
    {
        var (a, lacuna) = await LacunaAsync("sugfluxo");
        var publicadaAntes = (await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetPublishedAsync()))!.Number;
        lock (banco.IaLocal.Pedidos)
        {
            banco.IaLocal.Pedidos.Clear();
        }

        banco.IaLocal.Falha = null;
        banco.IaLocal.Resposta = _ => NovoFaq("Posso tomar cafe antes do exame?", "Pode tomar cafe sem acucar.");
        var admin = AdminQueLe(a);

        var geracao = await a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().GenerateForGapAsync(lacuna));
        var sugestao = Assert.Single(geracao!.Suggestions);

        // O que foi ao modelo: sem e-mail nem telefone, como dado.
        LocalGenerationRequest pedido;
        lock (banco.IaLocal.Pedidos)
        {
            pedido = Assert.Single(banco.IaLocal.Pedidos.ToList());
        }

        Assert.DoesNotContain("ana@exemplo.com", pedido.UserContent);
        Assert.DoesNotContain("99999-1234", pedido.UserContent);
        Assert.Contains("[email]", pedido.UserContent);
        Assert.Contains("<dados>", pedido.UserContent);

        // Gerar nao publica nem cria rascunho, e a lacuna continua aberta.
        Assert.Equal(FaqSuggestionStatus.Pending, sugestao.Status);
        Assert.Equal(publicadaAntes, (await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetPublishedAsync()))!.Number);
        Assert.Null(await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetDraftAsync()));
        Assert.Equal(KnowledgeGapStatus.Open, (await a.C.ComoAsync(admin, sp => sp.GetRequiredService<KnowledgeGapService>().GetAsync(lacuna)))!.Gap.Status);

        // A decisao humana leva ao RASCUNHO.
        var aceita = await a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().ApproveAsync(sugestao.Id));
        Assert.Equal(FaqSuggestionStatus.Approved, aceita!.Status);
        var rascunho = (await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetDraftAsync()))!;
        var entrada = rascunho.Content.Faq.Single(f => f.Question == "Posso tomar cafe antes do exame?");
        Assert.False(entrada.AutoReply);
        Assert.Equal(publicadaAntes, (await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetPublishedAsync()))!.Number); // nada publicado
        Assert.Equal(KnowledgeGapStatus.Promoted, (await a.C.ComoAsync(admin, sp => sp.GetRequiredService<KnowledgeGapService>().GetAsync(lacuna)))!.Gap.Status);
    }

    [Fact]
    public async Task Sugestao_sem_resposta_humana_nao_pode_ser_aceita_ate_uma_pessoa_escrever_a_resposta()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "sugsemresp");
        var entrada = await a.EntrarAsync("mauro", "Voces aceitam cartao de credito?");
        var mensagem = (await a.C.HistoricoAsync(entrada.SessionId))!.Items.Single();
        await a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            await sp.GetRequiredService<IKnowledgeGapRecorder>().RecordAsync(
                new KnowledgeGapCandidate(a.Tenant.TenantId, Guid.NewGuid(), entrada.SessionId, entrada.ConversationId, mensagem.Id, "voces aceitam cartao de credito", KnowledgeGapKind.NoMatch, DateTimeOffset.UtcNow), default);
            return 0;
        });
        var lacuna = (await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().KnowledgeGaps.AsNoTracking().ToListAsync())).Single().Id;
        banco.IaLocal.Falha = null;
        banco.IaLocal.Resposta = _ => NovoFaq("Aceitam cartao de credito?", "Sim, todas as bandeiras (inventada pelo modelo).");
        var admin = AdminQueLe(a);

        var sugestao = Assert.Single((await a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().GenerateForGapAsync(lacuna)))!.Suggestions);
        Assert.Null(sugestao.Payload.Answer); // a resposta do modelo foi descartada

        var ex = await Assert.ThrowsAsync<FaqSuggestionConflictException>(() => a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().ApproveAsync(sugestao.Id)));
        Assert.Equal("answer-required", ex.Code);

        await a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().EditAsync(sugestao.Id, new FaqSuggestionEdit(null, "Aceitamos cartao de credito e debito.", null)));
        Assert.Equal(FaqSuggestionStatus.Approved, (await a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().ApproveAsync(sugestao.Id)))!.Status);
    }

    [Fact]
    public async Task IA_local_fora_do_ar_nao_grava_nada_e_a_lacuna_continua_revisavel_e_duas_geracoes_nao_duplicam()
    {
        var (a, lacuna) = await LacunaAsync("sugfora");
        var admin = AdminQueLe(a);
        banco.IaLocal.Falha = new LocalGenerationUnavailableException("timeout", "x");
        var ex = await Assert.ThrowsAsync<FaqSuggestionUnavailableException>(() => a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().GenerateForGapAsync(lacuna)));
        Assert.Equal("timeout", ex.Code);
        Assert.Equal(0, (await a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().ListAsync(null, null, 10, 0))).Total);

        banco.IaLocal.Falha = null;
        banco.IaLocal.Resposta = _ => NovoFaq("Posso tomar cafe antes do exame?");
        var primeira = await a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().GenerateForGapAsync(lacuna));
        var segunda = await a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().GenerateForGapAsync(lacuna));
        Assert.Equal(primeira!.Suggestions[0].Id, segunda!.Suggestions[0].Id);
        Assert.Equal(1, (await a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().ListAsync(null, null, 10, 0))).Total);
    }

    [Fact]
    public async Task Duas_aprovacoes_ao_mesmo_tempo_aplicam_o_efeito_uma_so_vez()
    {
        var (a, lacuna) = await LacunaAsync("sugconc");
        banco.IaLocal.Falha = null;
        banco.IaLocal.Resposta = _ => NovoFaq("Posso tomar cafe antes do exame?", "Pode tomar cafe sem acucar.");
        var admin = AdminQueLe(a);
        var sugestao = Assert.Single((await a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().GenerateForGapAsync(lacuna)))!.Suggestions);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await a.C.ComoAsync(admin, sp => sp.GetRequiredService<FaqSuggestionService>().ApproveAsync(sugestao.Id));
                return true;
            }
            catch (Exception ex) when (ex is FaqSuggestionConflictException or KnowledgeGapConflictException or AgentDefinitionConflictException)
            {
                return false;
            }
        }));

        Assert.Equal(1, resultados.Count(r => r));
        var rascunho = (await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetDraftAsync()))!;
        Assert.Single(rascunho.Content.Faq, f => f.Question == "Posso tomar cafe antes do exame?"); // a entrada nao foi duplicada
    }
}
