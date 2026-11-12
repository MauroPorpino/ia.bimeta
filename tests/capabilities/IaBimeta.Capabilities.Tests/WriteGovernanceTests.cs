using IaBimeta.Audit.Contracts;
using IaBimeta.Capabilities.Application;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;
using IaBimeta.Capabilities.Tests.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Tests;

/// <summary>Escrita: idempotência, confirmação humana, ambiguidade (nunca reenviar) e concorrência.</summary>
public class WriteGovernanceTests
{
    private const string Cancelar = Harness.Cancelamento;
    private static readonly object Valores = new { numero = 55, motivo = "Paciente desistiu" };

    [Fact]
    public async Task Escrita_sem_chave_de_idempotencia_valida_e_recusada_antes_de_qualquer_registro()
    {
        var h = new Harness();
        h.SeedTypical();

        foreach (var chave in new string?[] { null, "", "curta", "tem espaco na chave 123", new string('a', 101), "chave;com;simbolos" })
        {
            var r = await h.Engine.ExecuteAsync(new CapabilityInvocation(Cancelar, Harness.P(Valores), CapabilityCaller.Operator, chave));
            Assert.Equal("idempotency-required", r.Error!.Code);
        }

        Assert.Empty(h.Rows.Executions);
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task Escrita_pede_confirmacao_por_padrao_e_nada_e_enviado_antes_dela()
    {
        var h = new Harness();
        h.SeedTypical();

        var r = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores));

        Assert.Equal(CapabilityExecutionStatus.AwaitingConfirmation, r.Status);
        Assert.NotNull(r.ConfirmationExpiresAt);
        Assert.Equal(h.Clock.GetUtcNow().AddMinutes(h.Options.ConfirmationTtlMinutes), r.ConfirmationExpiresAt);
        Assert.Empty(h.Connector.Calls);
        var registro = Assert.Single(h.Rows.Executions);
        Assert.NotNull(registro.PendingCiphertext);
        Assert.DoesNotContain("Paciente desistiu", registro.PendingCiphertext, StringComparison.Ordinal);
        Assert.DoesNotContain("Paciente desistiu", h.Everything(), StringComparison.Ordinal);
        Assert.Contains(h.Audit.Entries, e => e.Operation == "capabilities.execution.request");
    }

    [Fact]
    public async Task Confirmar_executa_uma_vez_apaga_os_parametros_guardados_e_registra_quem_confirmou()
    {
        var h = new Harness();
        h.SeedTypical();
        var pendente = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores));
        h.Connector.Script = _ => ScriptedConnector.Ok("""{"protocolo":"P-123"}""");

        var r = await h.Engine.ConfirmAsync(pendente.ExecutionId);

        Assert.Equal(CapabilityExecutionStatus.Succeeded, r.Status);
        Assert.Equal("""{"protocolo":"P-123"}""", r.Output!.Value.GetRawText());
        var chamada = Assert.Single(h.Connector.Calls);
        Assert.Equal(55L, chamada.Values["numero"]);
        Assert.Equal("Paciente desistiu", chamada.Values["motivo"]);
        Assert.Equal("chave-0000001", chamada.IdempotencyKey);
        Assert.True(chamada.IsWrite);
        var registro = h.Rows.Executions.Single();
        Assert.Null(registro.PendingCiphertext);
        Assert.Equal(h.UserId, registro.ConfirmedBy);
        Assert.Equal(CapabilityExecutionStatus.Succeeded, registro.Status);
    }

    [Fact]
    public async Task Duas_confirmacoes_so_uma_executa()
    {
        var h = new Harness();
        h.SeedTypical();
        var pendente = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores));

        var primeira = await h.Engine.ConfirmAsync(pendente.ExecutionId);
        var segunda = await h.Engine.ConfirmAsync(pendente.ExecutionId);

        Assert.Equal(CapabilityExecutionStatus.Succeeded, primeira.Status);
        Assert.Equal("not-pending", segunda.Error!.Code);
        Assert.Single(h.Connector.Calls);
    }

    [Fact]
    public async Task Confirmacao_vencida_cancela_e_nao_envia()
    {
        var h = new Harness();
        h.SeedTypical();
        var pendente = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores));
        h.Clock.Advance(TimeSpan.FromMinutes(h.Options.ConfirmationTtlMinutes + 1));

        var r = await h.Engine.ConfirmAsync(pendente.ExecutionId);

        Assert.Equal("confirmation-expired", r.Error!.Code);
        var registro = h.Rows.Executions.Single();
        Assert.Equal(CapabilityExecutionStatus.Cancelled, registro.Status);
        Assert.Null(registro.PendingCiphertext);
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task Operacao_aposentada_ou_desligada_entre_o_pedido_e_a_confirmacao_nao_ressuscita()
    {
        var h = new Harness();
        var (conexao, _, escrita) = h.SeedTypical();
        var pendente = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores));
        escrita.Retire(h.Clock.GetUtcNow());

        Assert.Equal("definition-changed", (await h.Engine.ConfirmAsync(pendente.ExecutionId)).Error!.Code);

        var h2 = new Harness();
        var (c2, _, _) = h2.SeedTypical();
        var pendente2 = await h2.Engine.ExecuteAsync(h2.Write(Cancelar, Valores));
        c2.SetEnabled(false, h2.Clock.GetUtcNow());
        Assert.Equal("definition-changed", (await h2.Engine.ConfirmAsync(pendente2.ExecutionId)).Error!.Code);
        Assert.Empty(h.Connector.Calls);
        Assert.Empty(h2.Connector.Calls);
        Assert.NotNull(conexao);
    }

    [Fact]
    public async Task Cancelar_impede_a_confirmacao_depois()
    {
        var h = new Harness();
        h.SeedTypical();
        var pendente = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores));

        var cancelada = await h.Engine.CancelAsync(pendente.ExecutionId);
        var depois = await h.Engine.ConfirmAsync(pendente.ExecutionId);

        Assert.Equal(CapabilityExecutionStatus.Cancelled, cancelada.Status);
        Assert.Equal("not-pending", depois.Error!.Code);
        Assert.Empty(h.Connector.Calls);
        Assert.Null(h.Rows.Executions.Single().PendingCiphertext);
    }

    [Fact]
    public async Task Um_contexto_de_sistema_nunca_confirma_nem_cancela_so_uma_pessoa_com_permissao_de_escrita()
    {
        var h = new Harness();
        h.SeedTypical(callers: CapabilityCaller.Operator | CapabilityCaller.Workflow);
        var agente = h.As(TestContexts.System());
        var pendente = await agente.Engine.ExecuteAsync(agente.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));
        Assert.Equal(CapabilityExecutionStatus.AwaitingConfirmation, pendente.Status);

        await Assert.ThrowsAsync<AccessDeniedException>(() => agente.Engine.ConfirmAsync(pendente.ExecutionId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => agente.Engine.CancelAsync(pendente.ExecutionId));

        var semEscrita = h.As(TestContexts.User(TestContexts.TenantA, Permission.IntegrationExecute, Permission.IntegrationRead));
        await Assert.ThrowsAsync<AccessDeniedException>(() => semEscrita.Engine.ConfirmAsync(pendente.ExecutionId));
        Assert.Empty(h.Connector.Calls);
        Assert.Contains(h.Audit.Entries, e => e.Operation == "capabilities.execution.confirm" && e.Outcome == AuditOutcome.Denied);

        Assert.Equal(CapabilityExecutionStatus.Succeeded, (await h.Engine.ConfirmAsync(pendente.ExecutionId)).Status);
    }

    [Fact]
    public async Task A_confirmacao_vale_so_para_a_organizacao_da_conexao()
    {
        var h = new Harness(TestContexts.User(TestContexts.TenantA, TestContexts.AllIntegration));
        h.SeedTypical(TestContexts.OrgA);
        var pendente = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores));
        var deOutraOrg = h.As(TestContexts.User(TestContexts.TenantA, new PermissionGrant(Permission.IntegrationExecuteWrite, TestContexts.OrgB)));

        await Assert.ThrowsAsync<AccessDeniedException>(() => deOutraOrg.Engine.ConfirmAsync(pendente.ExecutionId));
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task Sem_chave_de_cifra_uma_escrita_pendente_nao_e_aceita()
    {
        var h = new Harness(secretsConfigured: false);
        h.SeedTypical();

        var r = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores));

        Assert.Equal("secret-unavailable", r.Error!.Code);
        Assert.Empty(h.Rows.Executions);
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task Parametros_pendentes_adulterados_nunca_viram_chamada()
    {
        var h = new Harness();
        h.SeedTypical();
        var pendente = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores));
        var outra = await h.Engine.ExecuteAsync(h.Write(Cancelar, new { numero = 99, motivo = "outro" }, "chave-0000002"));
        var a = h.Rows.Executions.Single(e => e.Id == pendente.ExecutionId);
        var b = h.Rows.Executions.Single(e => e.Id == outra.ExecutionId);

        // Troca o texto cifrado de uma execução pela da outra: o dado autenticado inclui o id da execução, então não abre.
        typeof(CapabilityExecution).GetProperty(nameof(CapabilityExecution.PendingCiphertext))!.SetValue(a, b.PendingCiphertext);

        var r = await h.Engine.ConfirmAsync(pendente.ExecutionId);

        Assert.Equal(CapabilityExecutionStatus.Failed, r.Status);
        Assert.Equal("secret-unavailable", r.Error!.Code);
        Assert.Empty(h.Connector.Calls);
    }

    // ------------------------------------------------------------ idempotência

    private static Harness NoConfirmation(out CapabilityDefinition escrita, IdempotencySupport support = IdempotencySupport.Proven)
    {
        var h = new Harness(TestContexts.System());
        var conexao = h.SeedConnection();
        escrita = h.SeedDefinition(conexao, Cancelar, Specs.Write(support), callers: CapabilityCaller.Workflow, requiresConfirmation: false);
        return h;
    }

    [Fact]
    public async Task A_mesma_chave_e_os_mesmos_parametros_devolvem_o_resultado_registrado_sem_nova_chamada()
    {
        var h = NoConfirmation(out _);

        var primeira = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));
        var segunda = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));

        Assert.Equal(CapabilityExecutionStatus.Succeeded, primeira.Status);
        Assert.False(primeira.Replayed);
        Assert.Equal(CapabilityExecutionStatus.Succeeded, segunda.Status);
        Assert.True(segunda.Replayed);
        Assert.Equal(primeira.ExecutionId, segunda.ExecutionId);
        Assert.Single(h.Connector.Calls);
        Assert.Single(h.Rows.Executions);
    }

    [Fact]
    public async Task A_mesma_chave_com_outros_parametros_ou_outra_operacao_e_recusada()
    {
        var h = NoConfirmation(out _);
        await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));

        var outros = await h.Engine.ExecuteAsync(h.Write(Cancelar, new { numero = 56, motivo = "Paciente desistiu" }, caller: CapabilityCaller.Workflow));

        Assert.Equal(CapabilityExecutionStatus.Rejected, outros.Status);
        Assert.Equal("idempotency-conflict", outros.Error!.Code);
        Assert.Single(h.Connector.Calls);
    }

    [Fact]
    public async Task A_mesma_chave_em_tenants_diferentes_e_independente()
    {
        var a = NoConfirmation(out _);
        var b = a.As(TestContexts.System(TestContexts.TenantB));
        var conexaoB = b.SeedConnection();
        b.SeedDefinition(conexaoB, Cancelar, Specs.Write(), callers: CapabilityCaller.Workflow, requiresConfirmation: false);

        var ra = await a.Engine.ExecuteAsync(a.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));
        var rb = await b.Engine.ExecuteAsync(b.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));

        Assert.False(rb.Replayed);
        Assert.NotEqual(ra.ExecutionId, rb.ExecutionId);
        Assert.Equal(2, a.Connector.Calls.Count);
    }

    [Fact]
    public async Task Chamadas_simultaneas_com_a_mesma_chave_executam_exatamente_uma_vez()
    {
        var h = NoConfirmation(out _);
        h.Connector.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var chamadas = Enumerable.Range(0, 12).Select(_ => Task.Run(() => h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow)))).ToList();
        await Task.Delay(300);
        h.Connector.Gate.SetResult();
        var resultados = await Task.WhenAll(chamadas);

        Assert.Single(h.Connector.Calls);
        Assert.Single(h.Rows.Executions);
        Assert.Single(resultados, r => !r.Replayed);
        Assert.All(resultados.Where(r => r.Replayed), r => Assert.True(r.Status is CapabilityExecutionStatus.InProgress or CapabilityExecutionStatus.Succeeded));
        Assert.Contains(resultados, r => r.Replayed && r.Status == CapabilityExecutionStatus.InProgress);
    }

    [Fact]
    public async Task Execucao_em_curso_alem_do_prazo_vira_ambigua_e_nunca_e_reenviada()
    {
        var h = NoConfirmation(out _);
        h.Connector.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primeira = Task.Run(() => h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow)));
        await Task.Delay(200);

        var dentroDoPrazo = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));
        h.Clock.Advance(TimeSpan.FromSeconds(h.Options.RunningLeaseSeconds + 1));
        var vencida = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));
        h.Connector.Gate.SetResult();
        await primeira;

        Assert.Equal(CapabilityExecutionStatus.InProgress, dentroDoPrazo.Status);
        Assert.Equal("in-progress", dentroDoPrazo.Error!.Code);
        Assert.Equal(CapabilityExecutionStatus.Unknown, vencida.Status);
        Assert.Equal("outcome-unknown", vencida.Error!.Code);
        Assert.Single(h.Connector.Calls);
    }

    // ------------------------------------------------------------ ambiguidade e retentativa

    [Theory]
    [InlineData(ConnectorOutcome.Timeout, null)]
    [InlineData(ConnectorOutcome.Unavailable, 503)]
    [InlineData(ConnectorOutcome.Redirected, 302)]
    [InlineData(ConnectorOutcome.TooLarge, 200)]
    [InlineData(ConnectorOutcome.InvalidResponse, 200)]
    public async Task Escrita_sem_idempotencia_comprovada_que_pode_ter_executado_vira_resultado_ambiguo_e_nunca_e_reenviada(ConnectorOutcome desfecho, int? status)
    {
        var h = NoConfirmation(out _, IdempotencySupport.None);
        h.Connector.Script = _ => ScriptedConnector.Fail(desfecho, status);

        var r = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));
        var repetida = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));

        Assert.Equal(CapabilityExecutionStatus.Unknown, r.Status);
        Assert.Equal("outcome-unknown", r.Error!.Code);
        Assert.Equal(CapabilityExecutionStatus.Unknown, repetida.Status);
        Assert.True(repetida.Replayed);
        Assert.Single(h.Connector.Calls);
    }

    [Fact]
    public async Task Erro_de_conexao_antes_do_envio_numa_escrita_e_falha_definitiva_sem_ambiguidade()
    {
        var h = NoConfirmation(out _, IdempotencySupport.None);
        h.Connector.Script = _ => ScriptedConnector.Fail(ConnectorOutcome.NetworkError, null, mayHaveExecuted: false);

        var r = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));

        Assert.Equal(CapabilityExecutionStatus.Failed, r.Status);
        Assert.Equal("unavailable", r.Error!.Code);
    }

    [Fact]
    public async Task Escrita_recusada_pelo_sistema_externo_e_falha_definitiva()
    {
        var h = NoConfirmation(out _, IdempotencySupport.None);
        h.Connector.Script = _ => ScriptedConnector.Fail(ConnectorOutcome.Rejected, 422, mayHaveExecuted: false);

        var r = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));

        Assert.Equal(CapabilityExecutionStatus.Failed, r.Status);
        Assert.Equal("upstream-rejected", r.Error!.Code);
        Assert.Equal(422, r.Error.UpstreamStatus);
    }

    [Fact]
    public async Task Escrita_comprovada_com_5xx_esgotado_e_falha_retentavel_e_com_timeout_e_ambigua()
    {
        var h = NoConfirmation(out _);
        h.Connector.Script = _ => ScriptedConnector.Fail(ConnectorOutcome.Unavailable, 503);
        var r = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, "chave-0000001", CapabilityCaller.Workflow));
        Assert.Equal(CapabilityExecutionStatus.Failed, r.Status);
        Assert.True(r.Error!.Retryable);

        h.Connector.Script = _ => ScriptedConnector.Fail(ConnectorOutcome.Timeout);
        var t = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, "chave-0000002", CapabilityCaller.Workflow));
        Assert.Equal(CapabilityExecutionStatus.Unknown, t.Status);
    }

    [Fact]
    public async Task Resposta_2xx_sem_o_formato_esperado_numa_escrita_e_ambigua_mas_sem_campos_esperados_basta_o_2xx()
    {
        var h = NoConfirmation(out _);
        h.Connector.Script = _ => ScriptedConnector.Ok("""{"nada":1}""");
        var r = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));
        Assert.Equal(CapabilityExecutionStatus.Unknown, r.Status);

        var h2 = new Harness(TestContexts.System());
        var conexao = h2.SeedConnection();
        h2.SeedDefinition(conexao, "sem.resposta", Specs.Write() with { Response = [] }, callers: CapabilityCaller.Workflow, requiresConfirmation: false);
        h2.Connector.Script = _ => ScriptedConnector.Ok("{}");
        var ok = await h2.Engine.ExecuteAsync(h2.Write("sem.resposta", Valores, caller: CapabilityCaller.Workflow));
        Assert.Equal(CapabilityExecutionStatus.Succeeded, ok.Status);
    }

    [Fact]
    public async Task Defeito_do_conector_e_cancelamento_no_meio_de_uma_escrita_viram_ambiguidade()
    {
        var h = NoConfirmation(out _);
        h.Connector.Script = _ => throw new InvalidOperationException("quebrou");
        var r = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, "chave-0000001", CapabilityCaller.Workflow));
        Assert.Equal(CapabilityExecutionStatus.Unknown, r.Status);

        h.Connector.Script = _ => throw new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        h.Connector.Script = _ => throw new OperationCanceledException(cts.Token);
        var c = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, "chave-0000002", CapabilityCaller.Workflow), cts.Token);
        Assert.Equal(CapabilityExecutionStatus.Unknown, c.Status);
    }

    [Theory]
    [InlineData(IdempotencySupport.Proven, 3, true, 3)]
    [InlineData(IdempotencySupport.None, 1, false, 1)]
    [InlineData(IdempotencySupport.Reconcilable, 1, false, 1)]
    public async Task So_a_escrita_comprovada_recebe_permissao_de_repetir_e_a_leitura_sempre(IdempotencySupport suporte, int tentativas, bool repete, int esperado)
    {
        var h = new Harness(TestContexts.System());
        var conexao = h.SeedConnection();
        h.SeedDefinition(conexao, Cancelar, Specs.Write(suporte, tentativas), callers: CapabilityCaller.Workflow, requiresConfirmation: false);

        await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));

        var chamada = Assert.Single(h.Connector.Calls);
        Assert.Equal(repete, chamada.AllowRetries);
        Assert.Equal(esperado, chamada.MaxAttempts);
    }

    [Fact]
    public async Task Escrita_sem_confirmacao_exigida_executa_direto_para_a_origem_liberada()
    {
        var h = NoConfirmation(out _);

        var r = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores, caller: CapabilityCaller.Workflow));

        Assert.Equal(CapabilityExecutionStatus.Succeeded, r.Status);
        Assert.Null(h.Rows.Single().PendingCiphertext);
    }

    // ------------------------------------------------------------ limites

    [Fact]
    public async Task Limite_por_conexao_por_minuto_recusa_e_a_janela_desliza()
    {
        var h = new Harness(options: new CapabilitiesOptions { PerConnectionPerMinute = 3, RetryBaseDelayMs = 0, RetryMaxDelayMs = 0 });
        h.SeedTypical();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(CapabilityExecutionStatus.Succeeded, (await h.Engine.ExecuteAsync(h.Read(Harness.Pedido, new { numero = i + 1 }))).Status);
        }

        var recusada = await h.Engine.ExecuteAsync(h.Read(Harness.Pedido, new { numero = 9 }));
        Assert.Equal("rate-limited", recusada.Error!.Code);
        Assert.True(recusada.Error.Retryable);
        Assert.Equal(3, h.Connector.Calls.Count);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(CapabilityExecutionStatus.Succeeded, (await h.Engine.ExecuteAsync(h.Read(Harness.Pedido, new { numero = 9 }))).Status);
    }

    [Fact]
    public async Task Limite_por_tenant_e_de_escrita_valem_e_nao_vazam_entre_tenants()
    {
        var a = new Harness(options: new CapabilitiesOptions { PerTenantPerMinute = 2, WritesPerTenantPerMinute = 1 });
        a.SeedTypical();
        await a.Engine.ExecuteAsync(a.Read(Harness.Pedido, new { numero = 1 }));
        await a.Engine.ExecuteAsync(a.Write(Cancelar, Valores, "chave-0000001"));

        Assert.Equal("rate-limited", (await a.Engine.ExecuteAsync(a.Read(Harness.Pedido, new { numero = 2 }))).Error!.Code);

        var b = a.As(TestContexts.User(TestContexts.TenantB, TestContexts.AllIntegration));
        b.SeedTypical();
        Assert.Equal(CapabilityExecutionStatus.Succeeded, (await b.Engine.ExecuteAsync(b.Read(Harness.Pedido, new { numero = 2 }))).Status);

        var c = new Harness(options: new CapabilitiesOptions { WritesPerTenantPerMinute = 1 });
        c.SeedTypical();
        await c.Engine.ExecuteAsync(c.Write(Cancelar, Valores, "chave-0000001"));
        Assert.Equal("rate-limited", (await c.Engine.ExecuteAsync(c.Write(Cancelar, Valores, "chave-0000002"))).Error!.Code);
        Assert.Equal(CapabilityExecutionStatus.Succeeded, (await c.Engine.ExecuteAsync(c.Read(Harness.Pedido, new { numero = 1 }))).Status);
    }

    [Fact]
    public async Task Limite_de_chamadas_simultaneas_por_conexao()
    {
        var h = new Harness(options: new CapabilitiesOptions { PerConnectionMaxInFlight = 2 });
        h.SeedTypical();
        h.Connector.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var emCurso = Enumerable.Range(1, 2).Select(i => Task.Run(() => h.Engine.ExecuteAsync(h.Read(Harness.Pedido, new { numero = i })))).ToList();
        await Task.Delay(300);
        var excedente = await h.Engine.ExecuteAsync(h.Read(Harness.Pedido, new { numero = 3 }));
        h.Connector.Gate.SetResult();
        await Task.WhenAll(emCurso);

        Assert.Equal("rate-limited", excedente.Error!.Code);
        Assert.Equal(2, h.Connector.Calls.Count);
    }

    // ------------------------------------------------------------ trilha

    [Fact]
    public async Task Toda_etapa_deixa_trilha_sem_valores_e_o_historico_nao_traz_parametros()
    {
        var h = new Harness();
        h.SeedTypical();
        var pendente = await h.Engine.ExecuteAsync(h.Write(Cancelar, Valores));
        await h.Engine.ConfirmAsync(pendente.ExecutionId);
        await h.Engine.ExecuteAsync(h.Read(Harness.Pedido, new { numero = 12345 }));
        await h.Engine.ExecuteAsync(h.Read("inexistente", new { numero = 1 }));

        var operacoes = h.Audit.Entries.Select(e => e.Operation).ToList();
        Assert.Contains("capabilities.execution.request", operacoes);
        Assert.Contains("capabilities.execution.confirm", operacoes);
        Assert.Contains("capabilities.execute", operacoes);
        Assert.DoesNotContain(h.Audit.Entries, e => e.ResourceId is { } id && !Guid.TryParse(id, out _));

        var pagina = await h.History.ListAsync(null, null, null, null, 50, 0);
        Assert.Equal(2, pagina.Total);
        var texto = System.Text.Json.JsonSerializer.Serialize(pagina);
        Assert.DoesNotContain("Paciente desistiu", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", texto, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(ExecutionView).GetProperties(), p => p.Name.Contains("Param", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Pending", StringComparison.OrdinalIgnoreCase));
    }
}

internal static class HarnessRowsExtensions
{
    public static CapabilityExecution Single(this SharedRows rows) => rows.Executions.Single();
}
