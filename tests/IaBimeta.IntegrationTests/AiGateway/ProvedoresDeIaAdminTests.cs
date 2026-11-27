using System.Net;
using System.Text.Json;
using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.AiGateway;

/// <summary>
/// A Etapa 05 no backend: os casos de uso da tela de provedores de IA (<see cref="AiProviderAdminService"/>) com PostgreSQL real, o AiGateway real e os adaptadores REAIS da OpenAI, da Anthropic e do Ollama conversando com
/// servidores HTTP SIMULADOS. Nenhuma rede, nenhuma chave real, nenhum modelo real: as credenciais são canários sintéticos.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProvedoresDeIaAdminTests(PostgresFixture banco)
{
    private static readonly Permission[] Todas = [Permission.AiSettingsRead, Permission.AiSettingsManage, Permission.AiCredentialManage, Permission.AiConnectivityTest, Permission.AiGenerationTest];

    private const string ModeloOpenAi = PostgresFixture.ModeloOpenAiDeTeste;
    private const string ModeloAnthropic = PostgresFixture.ModeloAnthropicDeTeste;
    private const string ModeloOllama = PostgresFixture.ModeloOllamaDeTeste;

    private static RequestContext Como(CenarioDeAgentes a, params Permission[] permissoes) =>
        PostgresFixture.ContextoDeUsuario(a.Tenant, Guid.NewGuid(), permissoes.Select(p => new PermissionGrant(p, null)).ToArray());

    private static Task<T> Executar<T>(CenarioDeAgentes a, RequestContext contexto, Func<AiProviderAdminService, Task<T>> acao) =>
        a.C.ComoAsync(contexto, sp => acao(sp.GetRequiredService<AiProviderAdminService>()));

    private static async Task Executar(CenarioDeAgentes a, RequestContext contexto, Func<AiProviderAdminService, Task> acao) =>
        await a.C.ComoAsync(contexto, async sp =>
        {
            await acao(sp.GetRequiredService<AiProviderAdminService>());
            return 0;
        });

    private static string Chave() => $"sk-CANARIO-{Guid.NewGuid():N}";

    private static Task<List<AiRequest>> Ledger(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.AsNoTracking().OrderBy(r => r.StartedAt).ToListAsync());

    private static Task<List<string>> Auditoria(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AiGatewayDbContext>().Database
            .SqlQueryRaw<string>("select operation as \"Value\" from audit.audit_events where tenant_id = {0} and operation like 'ai-gateway.%' order by occurred_at", a.Tenant.TenantId.Value).ToListAsync());

    private static Task MudarSituacaoAsync(CenarioDeAgentes a, bool suspender) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            var repo = sp.GetRequiredService<IaBimeta.Tenancy.Application.ITenantRepository>();
            var tenant = (await repo.FindByIdAsync(a.Tenant.TenantId, default))!;
            var agora = a.Relogio.GetUtcNow();
            await repo.SaveStatusChangeAsync(suspender ? tenant.SuspendWithReason("suspensao de teste", null, agora) : tenant.ReactivateWithReason("reativacao de teste", null, agora), default);
            return 0;
        });

    private static Task<CenarioDeAgentes> NovoAsync(PostgresFixture banco, string prefixo) => CenarioDeAgentes.CriarAsync(banco, prefixo, configurarIA: false, publicarAgente: false);

    // Respostas simuladas dos três provedores

    private static HttpResponseMessage ModeloOpenAiOk(string id = ModeloOpenAi) =>
        ServidorOpenAiSimulado.Json(HttpStatusCode.OK, $"{{\"id\":\"{id}\",\"object\":\"model\",\"created\":1,\"owned_by\":\"openai\"}}");

    private static HttpResponseMessage ModeloAnthropicOk(string ciclo = "active") =>
        ServidorAnthropicSimulado.Json(HttpStatusCode.OK, $"{{\"type\":\"model\",\"id\":\"{ModeloAnthropic}\",\"display_name\":\"Haiku\",\"lifecycle\":\"{ciclo}\",\"capabilities\":{{\"structured_outputs\":{{\"supported\":true}}}}}}");

    private void RoteiroDaOpenAi(string chave, bool geracao = true, HttpStatusCode modelos = HttpStatusCode.OK) =>
        banco.ServidorOpenAi.Definir(chave, (c, _) => Task.FromResult(c.Url.Contains("/models/", StringComparison.Ordinal)
            ? (modelos == HttpStatusCode.OK ? ModeloOpenAiOk() : ServidorOpenAiSimulado.Json(modelos, ServidorOpenAiSimulado.Erro("x")))
            : geracao ? ServidorOpenAiSimulado.Sucesso("OK") : ServidorOpenAiSimulado.Json(HttpStatusCode.InternalServerError, ServidorOpenAiSimulado.Erro("x"))));

    private void RoteiroDaAnthropic(string chave, bool geracao = true, HttpStatusCode modelos = HttpStatusCode.OK) =>
        banco.ServidorAnthropic.Definir(chave, (c, _) => Task.FromResult(c.Url.Contains("/models/", StringComparison.Ordinal)
            ? (modelos == HttpStatusCode.OK ? ModeloAnthropicOk() : ServidorAnthropicSimulado.Json(modelos, ServidorAnthropicSimulado.Erro("authentication_error")))
            : geracao ? ServidorAnthropicSimulado.Sucesso("OK") : ServidorAnthropicSimulado.Json(HttpStatusCode.InternalServerError, ServidorAnthropicSimulado.Erro("api_error"))));

    private void ServidorOllamaOk(bool instalado = true, string capacidades = "\"completion\",\"thinking\"")
    {
        banco.ServidorOllama.Padrao = (c, _) => Task.FromResult(new Uri(c.Url).AbsolutePath switch
        {
            "/api/version" => ServidorOllamaSimulado.Json(HttpStatusCode.OK, "{\"version\":\"0.0.0-teste\"}"),
            "/api/tags" => ServidorOllamaSimulado.Json(HttpStatusCode.OK, instalado ? "{\"models\":[{\"name\":\"qwen3:4b\"}]}" : "{\"models\":[]}"),
            "/api/show" => ServidorOllamaSimulado.Json(HttpStatusCode.OK, $"{{\"capabilities\":[{capacidades}],\"model_info\":{{\"x.context_length\":40960}}}}"),
            "/api/chat" => ServidorOllamaSimulado.Sucesso("OK", entrada: 40, saida: 2),
            _ => ServidorOllamaSimulado.Json(HttpStatusCode.NotFound, "{}"),
        });
    }

    private static AiProviderView Provedor(AiSettingsView v, string id) => v.Providers.Single(p => p.ProviderId == id);

    // Consulta e isolamento

    [Fact]
    public async Task A_consulta_lista_os_tres_provedores_com_modelos_autorizados_sem_inventar_estados_positivos()
    {
        var a = await NovoAsync(banco, "pia1");
        var v = await Executar(a, Como(a, Permission.AiSettingsRead), s => s.GetSettingsAsync());

        Assert.False(v.Configured);
        Assert.Contains("no-default", v.Warnings);
        var openai = Provedor(v, "openai");
        var anthropic = Provedor(v, "anthropic");
        var ollama = Provedor(v, "ollama");
        Assert.All(new[] { openai, anthropic }, p => Assert.True(p.Implemented && p.Authorized && p.Credential.Required && !p.Credential.Present && p.Supply == "Byok" && p.ConnectivityCheckSupported));
        Assert.True(ollama.Implemented && ollama.Authorized && !ollama.Credential.Required && ollama.Supply == "Infrastructure");
        Assert.Contains(openai.Models, m => m.ModelId == ModeloOpenAi && m.ContextWindowTokens > 0 && m.MaxOutputTokens > 0);

        // Nenhum diagnóstico existe: nada aparece como verificado ou testado.
        Assert.All(v.Providers.SelectMany(p => p.Models), m => Assert.True(m.Connectivity is null && m.Generation is null && !m.IsDefault));
        Assert.All(v.Providers, p => Assert.False(p.IsDefault));
    }

    [Fact]
    public async Task A_configuracao_de_uma_empresa_nunca_aparece_na_outra_e_o_valor_da_chave_nunca_sai()
    {
        var a = await NovoAsync(banco, "pia2a");
        var b = await NovoAsync(banco, "pia2b");
        var chaveA = Chave();
        await Executar(a, Como(a, Todas), s => s.SetCredentialAsync("anthropic", chaveA));

        var deA = await Executar(a, Como(a, Todas), s => s.GetSettingsAsync());
        var deB = await Executar(b, Como(b, Todas), s => s.GetSettingsAsync());

        Assert.True(Provedor(deA, "anthropic").Credential.Present);
        Assert.Matches("^[0-9a-f]{12}$", Provedor(deA, "anthropic").Credential.Fingerprint!);
        Assert.False(Provedor(deB, "anthropic").Credential.Present);
        Assert.DoesNotContain(chaveA, JsonSerializer.Serialize(deA));
        Assert.DoesNotContain(chaveA, JsonSerializer.Serialize(deB));
    }

    [Fact]
    public async Task Cada_operacao_exige_a_permissao_especifica_no_servidor_e_quem_so_le_nao_altera_nem_testa()
    {
        var a = await NovoAsync(banco, "pia3");
        var semNada = Como(a);
        var atendente = Como(a, Permission.ConversationRead, Permission.ConversationManage, Permission.QueueRead);
        var soLeitura = Como(a, Permission.AiSettingsRead);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Executar(a, semNada, s => s.GetSettingsAsync()));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Executar(a, atendente, s => s.GetSettingsAsync()));
        Assert.NotNull(await Executar(a, soLeitura, s => s.GetSettingsAsync()));

        await Assert.ThrowsAsync<AccessDeniedException>(() => Executar(a, soLeitura, s => s.SetCredentialAsync("anthropic", Chave())));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Executar(a, soLeitura, s => s.RevokeCredentialAsync("anthropic")));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Executar(a, soLeitura, s => s.SetDefaultAsync("ollama", ModeloOllama)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Executar(a, soLeitura, s => s.TestConnectivityAsync("ollama", null)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Executar(a, soLeitura, s => s.TestGenerationAsync("ollama", ModeloOllama, true)));

        // A permissão de credencial não lê a configuração, e a de teste de conectividade não testa geração.
        await Executar(a, Como(a, Permission.AiCredentialManage), s => s.SetCredentialAsync("anthropic", Chave()));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Executar(a, Como(a, Permission.AiCredentialManage), s => s.GetSettingsAsync()));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Executar(a, Como(a, Permission.AiConnectivityTest), s => s.TestGenerationAsync("ollama", ModeloOllama, true)));

        // As recusas ficam na auditoria (resultado Denied), sem provedor, modelo nem dado.
        var recusas = await Auditoria(a);
        Assert.Contains("ai-gateway.settings.read", recusas);
        Assert.Contains("ai-gateway.credential.revoke", recusas);
        Assert.Contains("ai-gateway.connectivity.test", recusas);
        Assert.Contains("ai-gateway.generation.test", recusas);

        // A permissão ampla de configurar o tenant (que já abrangia a IA) continua valendo.
        Assert.NotNull(await Executar(a, Como(a, Permission.TenantSettingsManage), s => s.GetSettingsAsync()));
    }

    // Credenciais

    [Fact]
    public async Task Cadastro_substituicao_e_remocao_alteram_a_impressao_nao_expoem_a_chave_e_invalidam_os_diagnosticos()
    {
        var a = await NovoAsync(banco, "pia4");
        var gestor = Como(a, Todas);
        var chave1 = Chave();
        var chave2 = Chave();
        RoteiroDaAnthropic(chave1);

        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", chave1));
        var primeira = Provedor(await Executar(a, gestor, s => s.GetSettingsAsync()), "anthropic").Credential;
        Assert.True(primeira.Present);
        await Executar(a, gestor, s => s.TestConnectivityAsync("anthropic", ModeloAnthropic));
        Assert.NotNull(Provedor(await Executar(a, gestor, s => s.GetSettingsAsync()), "anthropic").Models.Single(m => m.ModelId == ModeloAnthropic).Connectivity);

        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", chave2));
        var depois = await Executar(a, gestor, s => s.GetSettingsAsync());
        Assert.NotEqual(primeira.Fingerprint, Provedor(depois, "anthropic").Credential.Fingerprint);
        Assert.Null(Provedor(depois, "anthropic").Models.Single(m => m.ModelId == ModeloAnthropic).Connectivity); // credencial nova: o diagnóstico anterior não vale mais

        var remocao = await Executar(a, gestor, s => s.RevokeCredentialAsync("anthropic"));
        Assert.True(remocao.Revoked);
        Assert.False(Provedor(await Executar(a, gestor, s => s.GetSettingsAsync()), "anthropic").Credential.Present);
        Assert.False((await Executar(a, gestor, s => s.RevokeCredentialAsync("anthropic"))).Revoked); // idempotente

        // Nenhum valor de chave em nenhuma tabela do esquema nem na auditoria.
        foreach (var segredo in new[] { chave1, chave2 })
        {
            var ocorrencias = await a.C.ComoAsync(gestor, sp => sp.GetRequiredService<AiGatewayDbContext>().Database
                .SqlQueryRaw<int>(
                    "select count(*)::int as \"Value\" from (select to_jsonb(x)::text as t from ai_gateway.ai_credentials x union all select to_jsonb(x)::text from ai_gateway.ai_tenant_settings x " +
                    "union all select to_jsonb(x)::text from ai_gateway.ai_requests x union all select to_jsonb(x)::text from audit.audit_events x) f where t like {0}", "%" + segredo + "%")
                .SingleAsync());
            Assert.Equal(0, ocorrencias);
        }

        // A ordem não é afirmada: o relógio da fixture é controlado e as entradas podem ter o mesmo instante.
        Assert.Equal(["ai-gateway.connectivity.test", "ai-gateway.credential.revoke", "ai-gateway.credential.set", "ai-gateway.credential.set"], (await Auditoria(a)).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task OpenAI_e_Anthropic_mantem_credenciais_simultaneas_sem_mudar_o_padrao_e_o_Ollama_nao_aceita_chave()
    {
        var a = await NovoAsync(banco, "pia5");
        var gestor = Como(a, Todas);
        await Executar(a, gestor, s => s.SetCredentialAsync("openai", Chave()));
        await Executar(a, gestor, s => s.SetDefaultAsync("openai", ModeloOpenAi));
        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", Chave()));

        var v = await Executar(a, gestor, s => s.GetSettingsAsync());
        Assert.True(Provedor(v, "openai").Credential.Present && Provedor(v, "anthropic").Credential.Present);
        Assert.Equal(("openai", ModeloOpenAi), (v.DefaultProviderId, v.DefaultModelId)); // cadastrar a segunda credencial não mudou o padrão

        var recusa = await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.SetCredentialAsync("ollama", Chave())));
        Assert.Equal("credential-not-used", recusa.Code);
        Assert.Equal("provider-not-implemented", (await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.SetCredentialAsync("inexistente", Chave())))).Code);
    }

    // Padrão

    [Fact]
    public async Task A_selecao_do_padrao_valida_adaptador_catalogo_e_credencial_no_servidor_e_distingue_elegivel_de_testado()
    {
        var a = await NovoAsync(banco, "pia6");
        var gestor = Como(a, Todas);

        Assert.Equal("provider-not-implemented", (await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.SetDefaultAsync("sem-adaptador", "x")))).Code);
        Assert.Equal("model-not-authorized", (await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.SetDefaultAsync("openai", "modelo-que-nao-esta-no-catalogo")))).Code);
        Assert.Equal("credential-required", (await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.SetDefaultAsync("anthropic", ModeloAnthropic)))).Code);
        Assert.Equal("credential-required", (await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.SetDefaultAsync("openai", ModeloOpenAi)))).Code);

        // O Ollama não exige credencial: salva sem chave, e a resposta diz que nada foi verificado nem testado (elegível, não testada).
        var resultado = await Executar(a, gestor, s => s.SetDefaultAsync("ollama", ModeloOllama));
        Assert.Contains("connectivity-not-verified", resultado.Warnings);
        Assert.Contains("generation-not-tested", resultado.Warnings);
        var v = await Executar(a, gestor, s => s.GetSettingsAsync());
        Assert.Equal(("ollama", ModeloOllama), (v.DefaultProviderId, v.DefaultModelId));
        Assert.True(Provedor(v, "ollama").IsDefault && Provedor(v, "ollama").Models.Single().IsDefault);
        Assert.Empty(await Ledger(a)); // salvar nunca gera chamada paga
    }

    [Fact]
    public async Task Um_diagnostico_fresco_que_diz_que_o_modelo_nao_serve_a_conversa_recusa_a_escolha_e_um_negativo_ou_inconclusivo_nunca_confirma_disponibilidade()
    {
        var a = await NovoAsync(banco, "pia7");
        var gestor = Como(a, Todas);
        ServidorOllamaOk(capacidades: "\"embedding\"");
        var teste = await Executar(a, gestor, s => s.TestConnectivityAsync("ollama", ModeloOllama));
        Assert.Equal("ModelUnavailable", teste.Result!.State);
        Assert.False(teste.Result.ConversationCapable!.Value);
        Assert.Equal("model-not-conversational", (await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.SetDefaultAsync("ollama", ModeloOllama)))).Code);

        // Não instalado: permite salvar (a instalação é da infraestrutura), mas avisa e NUNCA aparece como verificado.
        ServidorOllamaOk(instalado: false);
        var naoInstalado = await Executar(a, gestor, s => s.TestConnectivityAsync("ollama", ModeloOllama));
        Assert.Equal("ModelUnavailable", naoInstalado.Result!.State);
        var salvo = await Executar(a, gestor, s => s.SetDefaultAsync("ollama", ModeloOllama));
        Assert.Contains("model-unavailable-per-last-check", salvo.Warnings);
        banco.ServidorOllama.Padrao = null;
    }

    // Diagnósticos

    [Fact]
    public async Task O_teste_de_conectividade_nao_gera_texto_nao_cria_consumo_nem_altera_o_padrao_e_o_resultado_leva_data()
    {
        var a = await NovoAsync(banco, "pia8");
        var gestor = Como(a, Todas);
        var openai = Chave();
        var anthropic = Chave();
        RoteiroDaOpenAi(openai);
        RoteiroDaAnthropic(anthropic);
        await Executar(a, gestor, s => s.SetCredentialAsync("openai", openai));
        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", anthropic));
        await Executar(a, gestor, s => s.SetDefaultAsync("openai", ModeloOpenAi));

        var t1 = await Executar(a, gestor, s => s.TestConnectivityAsync("anthropic", ModeloAnthropic));
        var t2 = await Executar(a, gestor, s => s.TestConnectivityAsync("openai", ModeloOpenAi));

        Assert.Equal("Verified", t1.Result!.State);
        Assert.Equal("Verified", t2.Result!.State);
        Assert.True(t1.Result.Reachable == true && t1.Result.Authenticated == true && t1.Result.ModelAvailable == true);
        Assert.Null(t1.Result.ConversationCapable); // a Anthropic não declara "serve à conversa": nada é inventado
        Assert.True(t1.Result.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.False(t1.Result.Stale);

        // Só a verificação do modelo (GET /models/…), nunca a geração; nada no ledger; o padrão continua o mesmo.
        Assert.All(banco.ServidorAnthropic.DaCredencial(anthropic), c => Assert.Contains("/models/", c.Url));
        Assert.All(banco.ServidorOpenAi.DaCredencial(openai), c => Assert.Contains("/models/", c.Url));
        Assert.Empty(await Ledger(a));
        var v = await Executar(a, gestor, s => s.GetSettingsAsync());
        Assert.Equal(("openai", ModeloOpenAi), (v.DefaultProviderId, v.DefaultModelId));
        Assert.Equal("Verified", Provedor(v, "openai").Models.Single(m => m.ModelId == ModeloOpenAi).Connectivity!.State);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AuthenticationFailed")]
    [InlineData(HttpStatusCode.NotFound, "ModelUnavailable")]
    [InlineData(HttpStatusCode.Forbidden, "Inconclusive")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Inconclusive")]
    public async Task Falha_de_autenticacao_modelo_inexistente_e_resposta_inconclusiva_nunca_viram_verificado(HttpStatusCode status, string estado)
    {
        var a = await NovoAsync(banco, "pia9");
        var gestor = Como(a, Todas);
        var chave = Chave();
        RoteiroDaAnthropic(chave, modelos: status);
        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", chave));
        var r = await Executar(a, gestor, s => s.TestConnectivityAsync("anthropic", ModeloAnthropic));
        Assert.Equal(estado, r.Result!.State);
        Assert.NotEqual("Verified", r.Result.State);
    }

    [Fact]
    public async Task Servidor_fora_do_ar_e_provedor_sem_credencial_sao_tratados_sem_vazar_o_endereco()
    {
        var a = await NovoAsync(banco, "pia10");
        var gestor = Como(a, Todas);
        Assert.Equal("credential-required", (await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.TestConnectivityAsync("openai", ModeloOpenAi)))).Code);

        banco.ServidorOllama.Padrao = (_, _) => throw new HttpRequestException("conexão recusada em http://10.9.8.7:11434");
        var r = await Executar(a, gestor, s => s.TestConnectivityAsync("ollama", ModeloOllama));
        banco.ServidorOllama.Padrao = null;
        Assert.Equal("Unreachable", r.Result!.State);
        Assert.DoesNotContain("10.9.8.7", JsonSerializer.Serialize(r));
    }

    [Fact]
    public async Task O_teste_de_geracao_exige_confirmacao_passa_pelo_AiGateway_registra_no_ledger_com_finalidade_diagnostica_e_nao_toca_o_padrao()
    {
        var a = await NovoAsync(banco, "pia11");
        var gestor = Como(a, Todas);
        var chave = Chave();
        RoteiroDaAnthropic(chave);
        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", chave));
        ServidorOllamaOk();
        await Executar(a, gestor, s => s.SetDefaultAsync("ollama", ModeloOllama));

        Assert.Equal("confirmation-required", (await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.TestGenerationAsync("anthropic", ModeloAnthropic, confirmed: false)))).Code);
        Assert.Empty(banco.ServidorAnthropic.DaCredencial(chave)); // sem confirmação, nada é chamado

        var r = await Executar(a, gestor, s => s.TestGenerationAsync("anthropic", ModeloAnthropic, confirmed: true));

        Assert.True(r.Succeeded);
        Assert.Equal("Succeeded", r.Result.State);
        Assert.Equal("OK", r.Result.ReplyPreview);
        var linha = Assert.Single(await Ledger(a));
        Assert.Equal((AiRequestState.Confirmed, "anthropic", ModeloAnthropic), (linha.State, linha.ProviderId, linha.ModelId));
        Assert.StartsWith("diagnostic:", linha.CorrelationId);
        Assert.Equal(r.AiRequestId, linha.AiRequestId);

        // O pedido levou só a mensagem sintética do sistema, com o teto de saída reduzido, e nunca a credencial no corpo.
        var http = Assert.Single(banco.ServidorAnthropic.DaCredencial(chave));
        Assert.Equal(AiGatewayService.DiagnosticUserMessage, (string)http.Json["messages"]![0]!["content"]!);
        Assert.Equal(AiGatewayService.DiagnosticMaxOutputTokens, (int)http.Json["max_tokens"]!);
        Assert.DoesNotContain(chave, http.Corpo);

        // O padrão continua o Ollama, e o resultado do teste aparece só no modelo testado.
        var v = await Executar(a, gestor, s => s.GetSettingsAsync());
        Assert.Equal(("ollama", ModeloOllama), (v.DefaultProviderId, v.DefaultModelId));
        Assert.Equal("Succeeded", Provedor(v, "anthropic").Models.Single(m => m.ModelId == ModeloAnthropic).Generation!.State);
        banco.ServidorOllama.Padrao = null;
    }

    [Fact]
    public async Task O_teste_de_geracao_do_Ollama_nao_usa_credencial_e_falha_do_provedor_nao_tem_fallback()
    {
        var a = await NovoAsync(banco, "pia12");
        var gestor = Como(a, Todas);
        var chave = Chave();
        RoteiroDaAnthropic(chave, geracao: false);
        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", chave));

        var falha = await Executar(a, gestor, s => s.TestGenerationAsync("anthropic", ModeloAnthropic, true));
        Assert.False(falha.Succeeded);
        Assert.Equal("Failed", falha.Result.State);
        Assert.Single(banco.ServidorAnthropic.DaCredencial(chave)); // sem nova tentativa
        Assert.DoesNotContain(banco.ServidorOpenAi.Capturas, c => c.Corpo.Contains(AiGatewayService.DiagnosticUserMessage, StringComparison.Ordinal)); // nenhum fallback para outro provedor
        Assert.Equal(AiUsageCertainty.Unknown, (await Ledger(a)).Single().UsageCertainty);

        ServidorOllamaOk();
        var ollama = await Executar(a, gestor, s => s.TestGenerationAsync("ollama", ModeloOllama, true));
        banco.ServidorOllama.Padrao = null;
        Assert.True(ollama.Succeeded);
        var linhas = await Ledger(a);
        Assert.Equal(("ollama", 40, 2), (linhas.Last().ProviderId, linhas.Last().InputTokens, linhas.Last().OutputTokens));
        Assert.Equal(0m, linhas.Last().EstimatedCost); // ausência de tarifa por token, não de custo operacional
    }

    [Fact]
    public async Task Empresa_suspensa_nao_testa_nada_nem_chama_o_provedor_e_a_reativacao_nao_reprocessa()
    {
        var a = await NovoAsync(banco, "pia13");
        var gestor = Como(a, Todas);
        var chave = Chave();
        RoteiroDaAnthropic(chave);
        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", chave));
        await MudarSituacaoAsync(a, true);

        Assert.Equal("tenant-suspended", (await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.TestConnectivityAsync("anthropic", ModeloAnthropic)))).Code);
        Assert.Equal("tenant-suspended", (await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.TestGenerationAsync("anthropic", ModeloAnthropic, true)))).Code);
        Assert.Empty(banco.ServidorAnthropic.DaCredencial(chave));
        Assert.Empty(await Ledger(a));

        await MudarSituacaoAsync(a, false);
        Assert.Empty(banco.ServidorAnthropic.DaCredencial(chave)); // nada é refeito sozinho
        Assert.True((await Executar(a, gestor, s => s.TestConnectivityAsync("anthropic", ModeloAnthropic))).Supported);
    }

    // Concorrência e abuso

    [Fact]
    public async Task Um_teste_iniciado_com_uma_credencial_substituida_durante_o_teste_nao_a_marca_como_validada()
    {
        var a = await NovoAsync(banco, "pia14");
        var gestor = Como(a, Todas);
        var antiga = Chave();
        var nova = Chave();
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        banco.ServidorAnthropic.Definir(antiga, async (_, ct) =>
        {
            await liberar.Task.WaitAsync(ct);
            return ModeloAnthropicOk();
        });
        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", antiga));

        var teste = Executar(a, gestor, s => s.TestConnectivityAsync("anthropic", ModeloAnthropic));
        while (banco.ServidorAnthropic.DaCredencial(antiga).Count == 0)
        {
            await Task.Delay(20);
        }

        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", nova)); // trocada DURANTE o teste
        liberar.SetResult();
        var resultado = await teste;

        Assert.True(resultado.Superseded);
        var v = await Executar(a, gestor, s => s.GetSettingsAsync());
        Assert.Null(Provedor(v, "anthropic").Models.Single(m => m.ModelId == ModeloAnthropic).Connectivity); // nada validado para a credencial nova
    }

    [Fact]
    public async Task Dois_testes_do_mesmo_provedor_ao_mesmo_tempo_recusam_o_segundo_e_o_excesso_de_geracoes_e_limitado()
    {
        var a = await NovoAsync(banco, "pia15");
        var gestor = Como(a, Todas);
        var chave = Chave();
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        banco.ServidorAnthropic.Definir(chave, async (c, ct) =>
        {
            if (c.Url.Contains("/models/", StringComparison.Ordinal))
            {
                await liberar.Task.WaitAsync(ct);
                return ModeloAnthropicOk();
            }

            return ServidorAnthropicSimulado.Sucesso("OK");
        });
        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", chave));

        var primeiro = Executar(a, gestor, s => s.TestConnectivityAsync("anthropic", ModeloAnthropic));
        while (banco.ServidorAnthropic.DaCredencial(chave).Count == 0)
        {
            await Task.Delay(20);
        }

        Assert.Equal("test-in-progress", (await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.TestConnectivityAsync("anthropic", ModeloAnthropic)))).Code);
        liberar.SetResult();
        await primeiro;

        // Geração: no máximo 5 por janela por empresa; a 6ª é recusada com tempo de espera, sem chamar o provedor.
        for (var i = 0; i < 5; i++)
        {
            Assert.True((await Executar(a, gestor, s => s.TestGenerationAsync("anthropic", ModeloAnthropic, true))).Succeeded);
        }

        var excesso = await Assert.ThrowsAsync<AiAdminRefusedException>(() => Executar(a, gestor, s => s.TestGenerationAsync("anthropic", ModeloAnthropic, true)));
        Assert.Equal((AiAdminRefusalKind.TooManyRequests, "rate-limited"), (excesso.Kind, excesso.Code));
        Assert.True(excesso.RetryAfter > TimeSpan.Zero);
        Assert.Equal(5, (await Ledger(a)).Count);
    }

    // Revogação da credencial do padrão

    [Fact]
    public async Task Revogar_a_credencial_do_provedor_padrao_e_permitido_o_atendimento_falha_fechado_e_nao_ha_fallback()
    {
        var a = await NovoAsync(banco, "pia16");
        var gestor = Como(a, Todas);
        var chave = Chave();
        RoteiroDaAnthropic(chave);
        await Executar(a, gestor, s => s.SetCredentialAsync("anthropic", chave));
        await Executar(a, gestor, s => s.SetDefaultAsync("anthropic", ModeloAnthropic));

        var revogacao = await Executar(a, gestor, s => s.RevokeCredentialAsync("anthropic"));

        Assert.True(revogacao.Revoked);
        Assert.True(revogacao.WasDefaultProvider);
        var v = await Executar(a, gestor, s => s.GetSettingsAsync());
        Assert.Contains("default-credential-missing", v.Warnings);
        Assert.Equal("anthropic", v.DefaultProviderId); // o padrão não foi trocado em silêncio

        // Uma geração do atendimento (contexto de sistema) falha fechada: sem credencial, sem chamar nenhum provedor.
        var unica = $"mensagem-unica-{Guid.NewGuid():N}";
        var antes = banco.ServidorAnthropic.DaCredencial(chave).Count;
        var resultado = await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<IAiGateway>().GenerateAsync(new AiGenerationRequest("c1", "sistema", [new AiMessage(AiMessageRole.User, unica)], 100, TimeSpan.FromSeconds(5))));
        Assert.Equal(AiFailureCodes.CredentialMissing, resultado.Failure!.Code);
        Assert.Equal(antes, banco.ServidorAnthropic.DaCredencial(chave).Count);
        Assert.DoesNotContain(banco.ServidorAnthropic.Capturas, c => c.Corpo.Contains(unica, StringComparison.Ordinal));
        Assert.DoesNotContain(banco.ServidorOpenAi.Capturas, c => c.Corpo.Contains(unica, StringComparison.Ordinal));
    }
}


