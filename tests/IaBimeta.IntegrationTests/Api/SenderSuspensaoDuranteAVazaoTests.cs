using System.Net;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// P1-04 (revisão do Codex, Etapa 02): o Sender consultava o estado do tenant uma única vez, no início, e podia esperar pelo controle de vazão e iniciar a chamada externa depois que a empresa foi suspensa. Agora reconsulta
/// o estado PERSISTIDO depois das esperas, antes de registrar a tentativa e de chamar o provedor. Api e Worker reais, PostgreSQL real, Graph API simulada; a espera é segurada de forma DETERMINÍSTICA por um decorador do
/// balde de vazão (sem atrasos arbitrários).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SenderSuspensaoDuranteAVazaoTests(PostgresFixture banco)
{
    private const string Cliente = "5511999990071";

    /// <summary>Segura o Sender no ponto da vazão (a primeira ficha pedida) até o teste liberar.</summary>
    private sealed class ControleDeVazao
    {
        private int _armado;

        public TaskCompletionSource Chegou { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Liberar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Armar() => Interlocked.Exchange(ref _armado, 1);

        public bool TentarSegurar() => Interlocked.CompareExchange(ref _armado, 0, 1) == 1;
    }

    private sealed class BaldeQueEspera(IAccountSendStateStore interno, ControleDeVazao controle) : IAccountSendStateStore
    {
        public async Task<SendAcquireResult> TryAcquireAsync(Guid accountId, CancellationToken cancellationToken)
        {
            if (controle.TentarSegurar())
            {
                controle.Chegou.TrySetResult();
                await controle.Liberar.Task.WaitAsync(cancellationToken);
            }

            return await interno.TryAcquireAsync(accountId, cancellationToken);
        }

        public Task ApplyCooldownAsync(Guid accountId, TimeSpan duration, CancellationToken cancellationToken) => interno.ApplyCooldownAsync(accountId, duration, cancellationToken);

        public Task<bool> PauseAsync(Guid accountId, string reason, CancellationToken cancellationToken) => interno.PauseAsync(accountId, reason, cancellationToken);

        public Task<bool> ResumeAsync(Guid accountId, CancellationToken cancellationToken) => interno.ResumeAsync(accountId, cancellationToken);

        public Task SetThroughputAsync(Guid accountId, int? perSecond, CancellationToken cancellationToken) => interno.SetThroughputAsync(accountId, perSecond, cancellationToken);

        public Task<AccountSendState?> FindAsync(Guid accountId, CancellationToken cancellationToken) => interno.FindAsync(accountId, cancellationToken);
    }

    private static Action<IServiceCollection> Decorar(ControleDeVazao controle) => servicos =>
    {
        var original = servicos.Last(d => d.ServiceType == typeof(IAccountSendStateStore));
        servicos.Remove(original);
        servicos.Add(new ServiceDescriptor(
            typeof(IAccountSendStateStore),
            sp => new BaldeQueEspera(
                (IAccountSendStateStore)(original.ImplementationFactory is { } fabrica ? fabrica(sp) : ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)),
                controle),
            original.Lifetime));
    };

    private async Task<EnsaioDeWhatsApp> IniciarAsync(ControleDeVazao controle)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, "sndvazao", servicosDoWorker: Decorar(controle));
        await e.Ia.CadastrarChaveAsync();
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder("Ola, tudo bem?"), 150, 0, 25, "req_vazao")));
        return e;
    }

    private static Task SuspenderAsync(EnsaioDeWhatsApp e, string motivo) => AlterarAsync(e, (tenant, agora) => tenant.SuspendWithReason(motivo, null, agora));

    private static Task ReativarAsync(EnsaioDeWhatsApp e, string motivo) => AlterarAsync(e, (tenant, agora) => tenant.ReactivateWithReason(motivo, null, agora));

    private static Task AlterarAsync(EnsaioDeWhatsApp e, Func<IaBimeta.Tenancy.Domain.Tenant, DateTimeOffset, IaBimeta.Tenancy.Domain.TenantStatusChange> mudar) =>
        e.Ia.ComoSistemaAsync(async sp =>
        {
            var tenantId = sp.GetRequiredService<IRequestContextAccessor>().GetRequired().TenantId;
            var repo = sp.GetRequiredService<ITenantRepository>();
            var tenant = (await repo.FindByIdAsync(tenantId, default))!;
            await repo.SaveStatusChangeAsync(mudar(tenant, DateTimeOffset.UtcNow), default);
            return true;
        });

    private static Task<List<OutboxMessage>> PedidosDeEnvioAsync(EnsaioDeWhatsApp e) =>
        e.Ia.ComoSistemaAsync(async sp =>
        {
            var tenant = sp.GetRequiredService<IRequestContextAccessor>().GetRequired().TenantId.Value;
            return await sp.GetRequiredService<ConversationsDbContext>().Set<OutboxMessage>().AsNoTracking()
                .Where(o => o.TenantId == tenant && o.EventType == "conversations.outbound-message-requested").ToListAsync();
        });

    private static async Task AguardarAsync(Task tarefa, string descricao)
    {
        try
        {
            await tarefa.WaitAsync(TimeSpan.FromSeconds(40));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Tempo esgotado: {descricao}");
        }
    }

    [Fact]
    public async Task Tenant_suspenso_durante_a_espera_da_vazao_nao_chama_o_provedor_e_o_pedido_e_adiado_sem_gastar_tentativa()
    {
        var controle = new ControleDeVazao();
        await using var e = await IniciarAsync(controle);
        controle.Armar();

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.VAZAO001", Cliente, "Vazao", "Oi")));
        await AguardarAsync(controle.Chegou.Task, "o Sender deveria chegar ao controle de vazão (tenant ativo)");

        // O Sender passou da primeira verificação (tenant ativo) e está ESPERANDO a vazão; a empresa é suspensa agora.
        await SuspenderAsync(e, "suspensao durante a espera");
        controle.Liberar.SetResult();

        // O pedido é ADIADO (Defer, sem gastar tentativa) pela verificação final; nada chega ao provedor.
        await e.EsperarAsync(
            async () => (await PedidosDeEnvioAsync(e)).Any(o => o.LastError == "tenant-suspended"),
            "a verificação final deveria adiar o pedido por suspensão");
        var pedido = Assert.Single(await PedidosDeEnvioAsync(e));
        Assert.Equal(0, pedido.Attempts);
        Assert.NotEqual(OutboxState.Dead, pedido.State);
        Assert.Empty(e.Graph.Capturas);
    }

    [Fact]
    public async Task Suspensao_seguida_de_reativacao_durante_a_espera_nao_envia_o_pedido_antigo_e_ele_falha_de_forma_visivel()
    {
        var controle = new ControleDeVazao();
        await using var e = await IniciarAsync(controle);
        controle.Armar();

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.VAZAO002", Cliente, "VazaoDois", "Oi")));
        await AguardarAsync(controle.Chegou.Task, "o Sender deveria chegar ao controle de vazão (tenant ativo)");
        var sessao = await e.EsperarSessaoAsync("VazaoDois");

        await SuspenderAsync(e, "suspensao curta");
        await ReativarAsync(e, "reativacao antes do fim da espera");
        controle.Liberar.SetResult();

        // O pedido nasceu ANTES da reativação: não sai em bloco. Falha definitiva e visível; nenhuma chamada ao provedor.
        await e.EsperarEntregaAsync(sessao, "Failed");
        Assert.Empty(e.Graph.Capturas);
        Assert.Single(await PedidosDeEnvioAsync(e));
    }

    // ------------------------------------------------------------ 2.ª revisão do Codex (P1-04): a verificação FINAL de admissão, depois das gravações e antes do adaptador

    private static Task<Guid> TenantDoEnsaioAsync(EnsaioDeWhatsApp e) =>
        e.Ia.ComoSistemaAsync(sp => Task.FromResult(sp.GetRequiredService<IRequestContextAccessor>().GetRequired().TenantId.Value));

    private static Task<List<IaBimeta.Channels.Domain.OutboundMessage>> DespachosAsync(EnsaioDeWhatsApp e) =>
        e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<IaBimeta.Channels.Infrastructure.ChannelsDbContext>().OutboundMessages.AsNoTracking().ToListAsync());

    /// <summary>
    /// Leva o Sender até a gravação da tentativa e a SEGURA ali, de forma determinística: (1) o Sender passa pela verificação anterior (empresa ativa) e para no controle de vazão; (2) uma trava de linha no despacho é tomada
    /// pelo teste; (3) a vazão é liberada e o Sender passa a esperar a trava na gravação da tentativa (confirmado em pg_stat_activity). Devolve a transação que segura a trava.
    /// </summary>
    private async Task<(Npgsql.NpgsqlConnection Conexao, Npgsql.NpgsqlTransaction Transacao)> SegurarNaGravacaoDaTentativaAsync(EnsaioDeWhatsApp e, ControleDeVazao controle, string wamid, string nome)
    {
        controle.Armar();
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto(wamid, Cliente, nome, "Oi")));
        await AguardarAsync(controle.Chegou.Task, "o Sender deveria chegar ao controle de vazão (empresa ativa)");

        var tenant = await TenantDoEnsaioAsync(e);
        var conexao = new Npgsql.NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        var transacao = await conexao.BeginTransactionAsync();
        await using (var trava = new Npgsql.NpgsqlCommand("SELECT 1 FROM channels.outbound_messages WHERE tenant_id = @t FOR UPDATE", conexao, transacao))
        {
            trava.Parameters.AddWithValue("t", tenant);
            Assert.NotNull(await trava.ExecuteScalarAsync()); // a intenção já está gravada: a trava pega a linha do despacho
        }

        controle.Liberar.SetResult();
        await using var observador = new Npgsql.NpgsqlConnection(banco.ConnectionString);
        await observador.OpenAsync();
        var esperando = false;
        for (var i = 0; i < 400 && !esperando; i++)
        {
            await using var consulta = new Npgsql.NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query ILIKE '%outbound_messages%'", observador);
            esperando = Convert.ToInt32(await consulta.ExecuteScalarAsync()) > 0;
            if (!esperando)
            {
                await Task.Delay(50);
            }
        }

        Assert.True(esperando, "o Sender deveria ficar esperando a gravação da tentativa");
        return (conexao, transacao);
    }

    [Fact]
    public async Task Suspensao_durante_a_gravacao_da_tentativa_e_barrada_pela_verificacao_final_e_nenhuma_tentativa_inexistente_e_contabilizada()
    {
        var controle = new ControleDeVazao();
        await using var e = await IniciarAsync(controle);
        var (conexao, transacao) = await SegurarNaGravacaoDaTentativaAsync(e, controle, "wamid.ADMIT001", "Admissao");
        await using var _ = conexao;
        await using var __ = transacao;

        // A empresa é suspensa enquanto a gravação espera; a gravação é liberada.
        await SuspenderAsync(e, "suspensao durante a gravacao");
        await transacao.RollbackAsync();

        // A verificação final impede a chamada ao adaptador: o pedido é adiado e a tentativa que não houve não conta.
        await e.EsperarAsync(
            async () => (await PedidosDeEnvioAsync(e)).Any(o => o.LastError == "tenant-suspended"),
            "a verificação final deveria adiar o pedido por suspensão");
        Assert.Empty(e.Graph.Capturas);
        var despacho = Assert.Single(await DespachosAsync(e));
        Assert.Equal(0, despacho.Attempts);
        Assert.Equal(IaBimeta.Channels.Domain.OutboundState.Dispatching, despacho.State); // a intenção: nunca "enviado" nem "resultado incerto"
        Assert.False(despacho.MayHaveReachedProvider);
        Assert.Null(despacho.ProviderMessageId);
    }

    [Fact]
    public async Task Suspensao_seguida_de_reativacao_durante_a_gravacao_da_tentativa_nao_envia_o_pedido_antigo_e_ele_falha_de_forma_visivel_sem_tentativa()
    {
        var controle = new ControleDeVazao();
        await using var e = await IniciarAsync(controle);
        var (conexao, transacao) = await SegurarNaGravacaoDaTentativaAsync(e, controle, "wamid.ADMIT002", "AdmissaoDois");
        await using var _ = conexao;
        await using var __ = transacao;
        var sessao = await e.EsperarSessaoAsync("AdmissaoDois");

        await SuspenderAsync(e, "suspensao curta");
        await ReativarAsync(e, "reativacao durante a gravacao");
        await transacao.RollbackAsync();

        await e.EsperarEntregaAsync(sessao, "Failed");
        Assert.Empty(e.Graph.Capturas);
        var despacho = Assert.Single(await DespachosAsync(e));
        Assert.Equal((IaBimeta.Channels.Domain.OutboundState.Failed, 0, "tenant-suspended"), (despacho.State, despacho.Attempts, despacho.ErrorCode));
    }
}
