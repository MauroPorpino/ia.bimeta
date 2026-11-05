using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Conversations.Contracts;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// O isolamento entre tenants, a concorrência entre executores e o reconciliador, com PostgreSQL real: dois tenants com o mesmo nome de contato nunca se misturam (turno, definição do agente, credencial
/// e consumo), dois executores nunca levam o mesmo turno, o teto de turnos simultâneos do tenant vale no banco, e a sessão com mensagens pendentes mas sem turno (evento perdido) é recuperada.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AgentsIsolationAndConcurrencyTests(PostgresFixture banco)
{
    [Fact]
    public async Task Dois_tenants_com_o_mesmo_contato_nunca_se_misturam_e_o_executor_do_Worker_atende_os_dois()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agisoa");
        var b = await CenarioDeAgentes.CriarAsync(banco, "agisob");
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Resposta do tenant A"));
        b.Roteiro = _ => b.Sucesso(CenarioDeAgentes.Responder("Resposta do tenant B"));

        // O MESMO nome de contato nos dois tenants: são contatos, sessões e turnos diferentes.
        var deA = await a.EntrarAsync("mauro", "Pergunta do tenant A");
        var deB = await b.EntrarAsync("mauro", "Pergunta do tenant B");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        // O executor REAL do Worker abre um contexto de sistema por tenant. O ciclo é limitado em lote, então repete até os dois responderem.
        var executor = a.ExecutorDoWorker();
        for (var i = 0; i < 5 && (await a.RespostasDaIAAsync(deA.SessionId)).Count + (await b.RespostasDaIAAsync(deB.SessionId)).Count < 2; i++)
        {
            await executor.RunOnceAsync(default);
        }

        Assert.Equal("Resposta do tenant A", Assert.Single(await a.RespostasDaIAAsync(deA.SessionId)).Text);
        Assert.Equal("Resposta do tenant B", Assert.Single(await b.RespostasDaIAAsync(deB.SessionId)).Text);

        // Cada pedido ao modelo tem a instrução e a credencial do PRÓPRIO tenant, e nada do outro.
        var pedidoA = Assert.Single(a.PedidosAoModelo);
        var pedidoB = Assert.Single(b.PedidosAoModelo);
        Assert.Contains(a.CanarioDasInstrucoes, pedidoA.SystemInstruction);
        Assert.DoesNotContain(b.CanarioDasInstrucoes, pedidoA.SystemInstruction);
        Assert.Contains(b.CanarioDasInstrucoes, pedidoB.SystemInstruction);
        Assert.DoesNotContain(a.CanarioDasInstrucoes, pedidoB.SystemInstruction);
        Assert.Equal(a.Canario, pedidoA.Credential.Secret);
        Assert.Equal(b.Canario, pedidoB.Credential.Secret);
        Assert.DoesNotContain("tenant B", string.Join(" ", pedidoA.Messages.Select(m => m.Text)));
        Assert.DoesNotContain("tenant A", string.Join(" ", pedidoB.Messages.Select(m => m.Text)));
    }

    [Fact]
    public async Task Um_tenant_nao_enxerga_nem_executa_o_turno_de_outro()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agiso2a");
        var b = await CenarioDeAgentes.CriarAsync(banco, "agiso2b");
        var deB = await b.EntrarAsync("pedro", "Pergunta");
        await a.RelayAsync();
        var turnoDeB = await b.UnicoTurnoAsync(deB.SessionId);

        // O contexto do tenant A não encontra o turno do tenant B: não o executa, e a lista de A só tem turnos de A.
        var tentativa = await a.RodarTurnoAsync(turnoDeB.Id);

        Assert.Equal((TurnRunOutcome.Skipped, "not-running"), (tentativa.Outcome, tentativa.Code));
        Assert.All(await a.TurnosAsync(), t => Assert.Equal(a.Tenant.TenantId, t.TenantId));
        Assert.DoesNotContain(await a.TurnosAsync(), t => t.SessionId == deB.SessionId);
        Assert.Equal(AiTurnState.Scheduled, (await b.UnicoTurnoAsync(deB.SessionId)).State);
    }

    [Fact]
    public async Task Varios_executores_ao_mesmo_tempo_nunca_levam_o_mesmo_turno_e_o_tenant_nunca_passa_do_teto_de_turnos_simultaneos()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agpar");
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("ok"));
        var sessoes = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            sessoes.Add((await a.EntrarAsync($"contato-{i}", $"Pergunta {i}")).SessionId);
        }

        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        // Quatro executores reivindicam ao mesmo tempo: ninguém leva o mesmo turno, e o total respeita o teto do tenant (MaxConcurrentTurnsPerTenant = 4), contado no banco.
        var lotes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => a.ReivindicarAsync(8)));
        var primeiraOnda = lotes.SelectMany(l => l).ToList();
        Assert.Equal(primeiraOnda.Count, primeiraOnda.Distinct().Count());
        Assert.Equal(4, primeiraOnda.Count);

        // Com o teto cheio, uma nova reivindicação não leva nada; terminados os quatro, os dois que sobraram saem.
        Assert.Empty(await a.ReivindicarAsync(8));
        var primeiros = await Task.WhenAll(primeiraOnda.Select(id => a.RodarTurnoAsync(id)));
        Assert.All(primeiros, r => Assert.Equal(TurnRunOutcome.Replied, r.Outcome));
        var segundaOnda = await a.ExecutarEmParaleloAsync();
        Assert.Equal(2, segundaOnda.Count);

        foreach (var sessao in sessoes)
        {
            Assert.Single(await a.RespostasDaIAAsync(sessao)); // exatamente uma resposta por atendimento
        }

        Assert.Equal(6, a.ChamadasAoModelo);
    }

    [Fact]
    public async Task Reconciliador_cria_o_turno_da_sessao_com_mensagem_pendente_e_sem_turno_aberto_e_ignora_a_que_esta_com_humano()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agrecon");
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Resposta"));

        // Uma primeira conversa normal (o tenant passa a ter turnos), depois a segunda mensagem do cliente chega e o evento dela se PERDE (o relay não roda).
        var entrada = await a.EntrarAsync("mauro", "Primeira");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        await a.EntrarAsync("mauro", "Segunda, cujo evento se perdeu");
        var comHumano = await a.EntrarAsync("pedro", "Oi", modoInicial: HandlingMode.HumanAssisted);

        a.Avancar(TimeSpan.FromSeconds(60));
        Assert.Empty(await a.ExecutarAsync()); // sem turno, nada a executar

        var criados = await a.ReconciliadorDoWorker().RunOnceAsync(default);

        Assert.True(criados >= 1);
        var turnos = await a.TurnosAsync(entrada.SessionId);
        Assert.Equal(2, turnos.Count);
        Assert.Equal(AiTurnState.Scheduled, turnos[1].State);
        Assert.Empty(await a.TurnosAsync(comHumano.SessionId)); // a sessão com humano não ganha turno

        // O turno órfão está devido já e é respondido pela IA, uma vez.
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal(2, (await a.RespostasDaIAAsync(entrada.SessionId)).Count);

        // Rodar o reconciliador de novo não cria turno sem pendência.
        a.Avancar(TimeSpan.FromSeconds(60));
        await a.ReconciliadorDoWorker().RunOnceAsync(default);
        Assert.Equal(2, (await a.TurnosAsync(entrada.SessionId)).Count);
    }
}
