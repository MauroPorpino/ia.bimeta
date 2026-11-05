using System.Collections.Concurrent;
using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Testing;
using IaBimeta.ContactCenter.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// Um tenant com a IA e o agente configurados, para os testes ponta a ponta do Agents (PostgreSQL real, AiGateway real, ContactCenter e Conversations reais; o ÚNICO dublê é o provedor de IA, sem
/// rede e sem chave real). Cada tenant tem uma credencial CANÁRIO própria e um roteiro de respostas próprio: o dublê do provedor despacha pelo canário que o AiGateway resolveu para o tenant, então
/// testes de tenants diferentes nunca se contaminam, mesmo compartilhando o provedor e o banco. O relógio é o dos Agents (o teste decide quando o agrupamento, o recuo e o lease vencem).
/// </summary>
internal sealed class CenarioDeAgentes
{
    private static readonly ConcurrentDictionary<string, Func<AiCompletionRequest, Task<AiCompletionResult>>> Roteiros = new();
    private static int _instalado;

    private CenarioDeAgentes(CenarioDeConversas c, string canario, string canarioDasInstrucoes)
    {
        C = c;
        Canario = canario;
        CanarioDasInstrucoes = canarioDasInstrucoes;
    }

    public CenarioDeConversas C { get; }

    /// <summary>A credencial sintética do tenant: o AiGateway a decifra na hora e a entrega ao provedor, que a usa para saber de QUEM é a chamada.</summary>
    public string Canario { get; }

    /// <summary>Um texto único nas instruções publicadas do tenant: aparece no prompt do tenant e em nenhum outro.</summary>
    public string CanarioDasInstrucoes { get; }

    public TenantInfo Tenant => C.Tenant;

    public FakeAiProvider Provedor => C.Banco.ProvedorDeIA;

    public RelogioDeTeste Relogio => C.Banco.RelogioDosAgentes;

    /// <summary>Um usuário do tenant que gere as configurações (a permissão que o ADR 0017 pede).</summary>
    public RequestContext Admin { get; private set; } = null!;

    public static async Task<CenarioDeAgentes> CriarAsync(
        PostgresFixture banco, string prefixo, bool configurarIA = true, bool publicarAgente = true, string? textoDeAudio = null, bool usarOpenAi = false)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true, agentes: true);
        var cenario = new CenarioDeAgentes(c, $"sk-CANARIO-{Guid.NewGuid():N}", $"CANARIO-INSTRUCOES-{Guid.NewGuid():N}");
        InstalarDespachante(banco.ProvedorDeIA);
        var admin = PostgresFixture.ContextoDeUsuario(c.Tenant, Guid.NewGuid(), new PermissionGrant(Permission.TenantSettingsManage, null));
        cenario.Admin = admin;

        // Sem roteiro definido, o tenant recebe uma resposta simples: um teste que não se importa com o modelo não precisa dele.
        cenario.Roteiro = _ => cenario.Provedor.Succeed(Responder("Posso ajudar com mais alguma coisa?"));

        if (configurarIA)
        {
            await c.ComoAsync(admin, async sp =>
            {
                var configuracao = sp.GetRequiredService<AiTenantConfigurationService>();
                // O tenant escolhe o provedor e o modelo: o dublê (provider-a) ou o adaptador REAL da OpenAI com o servidor HTTP simulado.
                var provedor = usarOpenAi ? PostgresFixture.ProvedorOpenAiDeTeste : PostgresFixture.ProvedorDeIADeTeste;
                await configuracao.SetConfigurationAsync(provedor, usarOpenAi ? PostgresFixture.ModeloOpenAiDeTeste : PostgresFixture.ModeloDeIADeTeste);
                await configuracao.SetCredentialAsync(provedor, cenario.Canario);
                return 0;
            });
        }

        if (publicarAgente)
        {
            await cenario.PublicarAgenteAsync(
                new AgentDefinitionContent(
                    $"Atenda com cortesia. {cenario.CanarioDasInstrucoes}",
                    new[] { new FaqEntry("Preciso de jejum para o hemograma?", "Em geral não; confirme com o atendimento.") },
                    textoDeAudio ?? "Recebemos seu áudio, mas por enquanto só lemos texto. Pode escrever sua dúvida?",
                    "Vou transferir você para um atendente. Um momento, por favor."));
        }

        return cenario;
    }

    /// <summary>O dublê do provedor responde pelo roteiro do TENANT cuja credencial a chamada trouxe. Instalado uma vez por processo.</summary>
    private static void InstalarDespachante(FakeAiProvider provedor)
    {
        if (Interlocked.Exchange(ref _instalado, 1) == 1)
        {
            return;
        }

        provedor.Behavior = (pedido, _) => Roteiros.TryGetValue(pedido.Credential.Secret, out var roteiro)
            ? roteiro(pedido)
            : Task.FromResult(provedor.Succeed(Responder("tenant sem roteiro")));
    }

    /// <summary>O roteiro do tenant: o que o provedor devolve a cada chamada DELE. Pode esperar, agir (chegar mensagem, o humano assumir) e falhar.</summary>
    public Func<AiCompletionRequest, Task<AiCompletionResult>> RoteiroAssincrono
    {
        set => Roteiros[Canario] = value;
    }

    public Func<AiCompletionRequest, AiCompletionResult> Roteiro
    {
        set => Roteiros[Canario] = pedido => Task.FromResult(value(pedido));
    }

    // O JSON da decisão que o modelo devolveria (o mesmo contrato do esquema do Agents).
    public static string Responder(string texto) =>
        "{\"action\":\"reply\",\"reply_text\":" + System.Text.Json.JsonSerializer.Serialize(texto) + ",\"handoff_reason\":\"none\"}";

    public static string Transferir(string motivo) => "{\"action\":\"handoff\",\"reply_text\":\"\",\"handoff_reason\":\"" + motivo + "\"}";

    public AiCompletionResult Sucesso(string json) => Provedor.Succeed(json);

    public AiCompletionResult Falha(AiErrorKind tipo, string? codigo = null, AiUsageCertainty certeza = AiUsageCertainty.Unknown) => Provedor.Fail(tipo, code: codigo, usageCertainty: certeza);

    // Chamadas ao modelo DESTE tenant (o provedor é compartilhado: filtra pela credencial)

    public IReadOnlyList<AiCompletionRequest> PedidosAoModelo => Provedor.Requests.Where(r => r.Credential.Secret == Canario).ToList();

    public int ChamadasAoModelo => PedidosAoModelo.Count;

    // O servidor HTTP simulado da OpenAI (só com o adaptador real)

    /// <summary>O roteiro HTTP do tenant: o que o "servidor da OpenAI" responde a cada requisição que traz a credencial DELE.</summary>
    public Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> RoteiroHttp
    {
        set => C.Banco.ServidorOpenAi.Definir(Canario, value);
    }

    /// <summary>As requisições HTTP que chegaram ao servidor simulado com a credencial DESTE tenant.</summary>
    public IReadOnlyList<CapturaHttp> RequisicoesHttp => C.Banco.ServidorOpenAi.DaCredencial(Canario);

    // Fluxo

    public async Task<IngestInboundMessageResult> EntrarAsync(string contato, string? texto = "Olá", MessageKind tipo = MessageKind.Text, HandlingMode modoInicial = HandlingMode.AiAutonomous) =>
        await C.EntrarAsync(contato, texto, quando: Relogio.GetUtcNow(), tipo: tipo, modoInicial: modoInicial);

    public void Avancar(TimeSpan tempo) => Relogio.Avancar(tempo);

    /// <summary>Roda o relay do outbox do Conversations: entrega os eventos aos handlers (ContactCenter e Agents). O relógio dos Agents decide o que já está elegível.</summary>
    public Task<RelayRunResult> RelayAsync() => C.Banco.ServicosComAgentes.GetRequiredService<OutboxRelay<ConversationsDbContext>>().RunOnceAsync("teste-agentes");

    /// <summary>
    /// Um ciclo do executor, SÓ para este tenant: reivindica os turnos devidos (com o teto de turnos simultâneos do tenant, no banco) e roda cada um em escopo próprio, como o Worker faz. Os testes
    /// que provam o executor do Worker inteiro (vários tenants, vários executores) usam <see cref="ExecutorDoWorker"/>.
    /// </summary>
    public async Task<IReadOnlyList<TurnRunResult>> ExecutarAsync(int max = 8)
    {
        var ids = await ReivindicarAsync(max);
        var resultados = new List<TurnRunResult>();
        foreach (var id in ids)
        {
            resultados.Add(await RodarTurnoAsync(id));
        }

        return resultados;
    }

    /// <summary>Como <see cref="ExecutarAsync"/>, mas os turnos reivindicados rodam em PARALELO (cada um no próprio escopo).</summary>
    public async Task<IReadOnlyList<TurnRunResult>> ExecutarEmParaleloAsync(int max = 8)
    {
        var ids = await ReivindicarAsync(max);
        return await Task.WhenAll(ids.Select(id => RodarTurnoAsync(id)));
    }

    public Task<IReadOnlyList<Guid>> ReivindicarAsync(int max = 8) =>
        C.ComoAsync(C.ContextoDeSistema, async sp => (IReadOnlyList<Guid>)(await sp.GetRequiredService<IAiTurnStore>().ClaimDueAsync("teste-executor", max, default)).Select(t => t.Id).ToList());

    public Task<TurnRunResult> RodarTurnoAsync(Guid turno, CancellationToken cancellationToken = default) =>
        C.ComoAsync(C.ContextoDeSistema, sp => sp.GetRequiredService<TurnRunner>().RunAsync(turno, cancellationToken));

    /// <summary>O executor REAL do Worker (todos os tenants com turno devido). Cria uma instância nova a cada chamada (um dono diferente, como processos diferentes).</summary>
    public AgentsTurnExecutor ExecutorDoWorker() => ActivatorUtilities.CreateInstance<AgentsTurnExecutor>(C.Banco.ServicosComAgentes);

    public AgentsReconciler ReconciliadorDoWorker() => ActivatorUtilities.CreateInstance<AgentsReconciler>(C.Banco.ServicosComAgentes);

    // O agente do tenant

    public Task PublicarAgenteAsync(AgentDefinitionContent conteudo) => C.ComoAsync(Admin, async sp =>
    {
        var servico = sp.GetRequiredService<AgentDefinitionService>();
        await servico.SaveDraftAsync(conteudo);
        await servico.PublishAsync();
        return 0;
    });

    // O que ficou gravado

    public Task<List<AiTurn>> TurnosAsync(Guid? sessao = null) => C.ComoAsync(C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().Turns.AsNoTracking()
        .Where(t => sessao == null || t.SessionId == sessao).OrderBy(t => t.CreatedAt).ThenBy(t => t.FireAt).ToListAsync());

    public async Task<AiTurn> UnicoTurnoAsync(Guid sessao) => Assert.Single(await TurnosAsync(sessao));

    /// <summary>As respostas que a IA (e só ela) mandou na sessão, em ordem.</summary>
    public async Task<List<MessageView>> RespostasDaIAAsync(Guid sessao) =>
        (await C.HistoricoAsync(sessao))!.Items.Where(m => m.Direction == MessageDirection.Outbound && m.Author == MessageAuthor.Ai).OrderBy(m => m.MessageSeq).ToList();

    /// <summary>Uma fila que atende qualquer conversa do tenant (fila e regra de roteamento), para o handoff ter para onde ir.</summary>
    public async Task<Guid> FilaDeAtendimentoAsync()
    {
        var fila = await C.CriarFilaAsync();
        await C.CriarRegraAsync(fila.Id);
        return fila.Id;
    }

    public async Task<IReadOnlyList<QueueItemView>> ItensDaFilaAsync() => await C.ItensAsync();
}
