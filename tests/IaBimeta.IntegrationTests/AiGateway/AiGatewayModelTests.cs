using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace IaBimeta.IntegrationTests.AiGateway;

/// <summary>
/// O MODELO do EF do AiGateway (migração 15): chaves compostas por tenant, índices únicos parciais, restrições e a migração (gatilho do ledger e <c>Down</c> que não destrói dado). NÃO usa
/// banco: constrói o contexto sem conexão (como a fábrica de tempo de design) e lê os metadados e os arquivos.
/// </summary>
public sealed class AiGatewayModelTests
{
    private static IModel Modelo()
    {
        using var contexto = new AiGatewayDbContextFactory().CreateDbContext(Array.Empty<string>());
        return contexto.GetService<IDesignTimeModel>().Model;
    }

    private static IEntityType Entidade<T>() => Modelo().FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} não está no modelo.");

    [Theory]
    [InlineData(typeof(AiTenantSettings), "ai_tenant_settings")]
    [InlineData(typeof(StoredAiCredential), "ai_credentials")]
    [InlineData(typeof(AiRequest), "ai_requests")]
    [InlineData(typeof(AiUsageWindow), "usage_windows")]
    public void Toda_tabela_do_esquema_tem_tenant_id_e_a_chave_composta_comeca_por_ele(Type tipo, string tabela)
    {
        var entidade = Modelo().FindEntityType(tipo)!;

        Assert.Equal(tabela, entidade.GetTableName());
        Assert.Equal("ai_gateway", entidade.GetSchema());
        var chave = entidade.FindPrimaryKey()!;
        Assert.Equal("tenant_id", chave.Properties[0].GetColumnName());
        Assert.True(chave.Properties.Count >= 2);
        Assert.False(chave.Properties[0].IsNullable);
    }

    [Fact]
    public void Uma_credencial_ativa_por_tenant_e_provedor_e_uma_configuracao_por_tenant()
    {
        var ativa = Assert.Single(Entidade<StoredAiCredential>().GetIndexes(), i => i.GetDatabaseName() == "ux_ai_credentials_active");
        Assert.True(ativa.IsUnique);
        Assert.Equal(new[] { "tenant_id", "provider_id" }, ativa.Properties.Select(p => p.GetColumnName()));
        Assert.Equal("status = 1", ativa.GetFilter());

        var unica = Assert.Single(Entidade<AiTenantSettings>().GetIndexes(), i => i.GetDatabaseName() == "ux_ai_tenant_settings_tenant");
        Assert.True(unica.IsUnique);
        Assert.Equal(new[] { "tenant_id" }, unica.Properties.Select(p => p.GetColumnName()));
    }

    [Fact]
    public void As_vagas_abertas_tem_indice_parcial_proprio_e_a_correlacao_so_e_indexada_quando_existe()
    {
        var indices = Entidade<AiRequest>().GetIndexes().ToDictionary(i => i.GetDatabaseName() ?? string.Empty);

        Assert.Equal("state = 1", indices["ix_ai_requests_open"].GetFilter());
        Assert.Equal(new[] { "tenant_id", "lease_expires_at" }, indices["ix_ai_requests_open"].Properties.Select(p => p.GetColumnName()));
        Assert.Equal("correlation_id IS NOT NULL", indices["ix_ai_requests_tenant_id_correlation_id"].GetFilter());
    }

    [Fact]
    public void As_restricoes_do_esquema_existem_com_os_nomes_esperados()
    {
        string[] Nomes<T>() => Entidade<T>().GetCheckConstraints().Select(c => c.Name ?? string.Empty).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "ck_ai_tenant_settings_limits", "ck_ai_tenant_settings_source", "ck_ai_tenant_settings_status", "ck_ai_tenant_settings_version" }, Nomes<AiTenantSettings>());
        Assert.Equal(new[] { "ck_ai_credentials_fingerprint", "ck_ai_credentials_shape", "ck_ai_credentials_status", "ck_ai_credentials_version" }, Nomes<StoredAiCredential>());
        Assert.Equal(
            new[] { "ck_ai_requests_applied", "ck_ai_requests_applied_amounts", "ck_ai_requests_cache", "ck_ai_requests_lease", "ck_ai_requests_outcome", "ck_ai_requests_state", "ck_ai_requests_tokens" },
            Nomes<AiRequest>());
        Assert.Equal(new[] { "ck_usage_windows_counters", "ck_usage_windows_span" }, Nomes<AiUsageWindow>());
    }

    [Fact]
    public void So_a_origem_BYOK_e_aceita_pelo_banco_e_a_revogada_nao_guarda_o_texto_cifrado()
    {
        var configuracao = Entidade<AiTenantSettings>().GetCheckConstraints().ToDictionary(c => c.Name ?? string.Empty, c => c.Sql ?? string.Empty);
        var credencial = Entidade<StoredAiCredential>().GetCheckConstraints().ToDictionary(c => c.Name ?? string.Empty, c => c.Sql ?? string.Empty);

        Assert.Equal("credential_source = 1", configuracao["ck_ai_tenant_settings_source"]);
        Assert.Contains("status = 2 AND ciphertext IS NULL AND key_id IS NULL AND revoked_at IS NOT NULL", credencial["ck_ai_credentials_shape"]);
        Assert.Contains("status = 1 AND ciphertext IS NOT NULL AND key_id IS NOT NULL AND revoked_at IS NULL", credencial["ck_ai_credentials_shape"]);
    }

    [Fact]
    public void O_ledger_nao_guarda_prompt_resposta_nem_credencial_nenhuma_coluna_os_sugere()
    {
        var colunas = Entidade<AiRequest>().GetProperties().Select(p => p.GetColumnName()).ToList();

        Assert.DoesNotContain(colunas, c => new[] { "prompt", "response", "completion", "secret", "credential", "api_key", "message", "text", "content" }.Any(n => c.Contains(n, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("estimated_input_tokens", colunas); // a estimativa fica ao lado do real, para calibrar
        Assert.Contains("usage_certainty", colunas);
    }

    [Fact]
    public void Toda_restricao_do_esquema_trata_o_nulo_de_forma_explicita_um_CHECK_so_recusa_FALSE()
    {
        // As colunas ANULÁVEIS de cada tabela (lidas do modelo) só aparecem em comparação junto de IS NULL / IS NOT NULL, nunca soltas (o resultado NULL passaria).
        var modelo = Modelo();
        var verificadas = 0;
        foreach (var tipo in new[] { typeof(AiTenantSettings), typeof(StoredAiCredential), typeof(AiRequest), typeof(AiUsageWindow) })
        {
            var entidade = modelo.FindEntityType(tipo)!;
            var anulaveis = entidade.GetProperties().Where(p => p.IsNullable).Select(p => p.GetColumnName()).ToList();
            foreach (var restricao in entidade.GetCheckConstraints())
            {
                var sql = restricao.Sql ?? string.Empty;
                verificadas++;
                foreach (var coluna in anulaveis.Where(c => System.Text.RegularExpressions.Regex.IsMatch(sql, $@"\b{c}\b")))
                {
                    var tratada = sql.Contains($"{coluna} IS NULL", StringComparison.Ordinal) || sql.Contains($"{coluna} IS NOT NULL", StringComparison.Ordinal);
                    Assert.True(tratada, $"{restricao.Name}: a coluna anulável {coluna} aparece sem IS NULL / IS NOT NULL.");
                }
            }
        }

        Assert.True(verificadas >= 15, "O teste precisa ter restrições para verificar.");
    }

    // A migração

    private static string LerMigracao()
    {
        var raiz = ProcurarRaiz();
        var pasta = Path.Combine(raiz, "backend", "modules", "ai-gateway", "IaBimeta.AiGateway.Infrastructure", "Migrations");
        return File.ReadAllText(Assert.Single(Directory.GetFiles(pasta, "*_AiGatewayInitial.cs")));
    }

    private static string ProcurarRaiz()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "docs", "architecture", "module-dependencies.json")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }

    [Fact]
    public void A_migracao_cria_o_gatilho_que_so_permite_as_transicoes_validas_do_ledger()
    {
        var migracao = LerMigracao();

        Assert.Contains("CREATE FUNCTION ai_gateway.ai_requests_guard()", migracao);
        Assert.Contains("CREATE TRIGGER ai_requests_guard BEFORE UPDATE ON ai_gateway.ai_requests", migracao);
        Assert.Contains("(OLD.state = 1 AND NEW.state IN (2, 3)) OR (OLD.state = 3 AND NEW.state = 2)", migracao);
        Assert.Contains("USING ERRCODE = '23514'", migracao);
    }

    [Fact]
    public void O_Down_da_migracao_verifica_a_perda_de_dado_antes_de_qualquer_remocao_e_recusa_sem_alterar_nada()
    {
        var migracao = LerMigracao();
        var down = migracao[migracao.IndexOf("protected override void Down(", StringComparison.Ordinal)..];

        var verificacao = down.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal);
        var primeiraRemocao = new[] { "DROP TRIGGER", "DROP FUNCTION", "DropTable(" }.Select(op => down.IndexOf(op, StringComparison.Ordinal)).Where(p => p >= 0).Min();

        Assert.True(verificacao >= 0 && verificacao < primeiraRemocao, "O Down precisa verificar, e abortar, antes de remover qualquer coisa.");
        Assert.Contains("ai_gateway.ai_credentials", down);
        Assert.Contains("ai_gateway.ai_tenant_settings", down);
        Assert.Contains("ai_gateway.ai_requests", down);
        Assert.DoesNotContain("CASCADE", down);
    }

    [Fact]
    public void A_migracao_so_toca_o_esquema_ai_gateway_e_nao_cria_chave_estrangeira_para_outro_modulo()
    {
        var migracao = LerMigracao();

        Assert.DoesNotContain("ForeignKey", migracao);
        Assert.DoesNotContain("AddForeignKey", migracao);
        Assert.DoesNotMatch("schema: \"(?!ai_gateway\")", migracao);
        foreach (var outro in new[] { "conversations.", "contact_center.", "channels.", "tenancy.", "identity.", "audit." })
        {
            Assert.DoesNotContain(outro, migracao);
        }
    }
}
