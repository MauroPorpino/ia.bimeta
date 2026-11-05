using System.Text.RegularExpressions;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// O MODELO do EF do Agents (migração 16): chaves compostas por tenant, índices únicos parciais (um turno aberto por sessão, um rascunho e uma publicada por tenant), restrições null-safe e a migração
/// (esquema próprio, sem chave estrangeira, <c>Down</c> que não destrói dado). NÃO usa banco: constrói o contexto sem conexão (como a fábrica de tempo de design) e lê os metadados e os arquivos.
/// </summary>
public sealed class AgentsModelTests
{
    private static IModel Modelo()
    {
        using var contexto = new AgentsDbContextFactory().CreateDbContext(Array.Empty<string>());
        return contexto.GetService<IDesignTimeModel>().Model;
    }

    private static IEntityType Entidade<T>() => Modelo().FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} não está no modelo.");

    [Theory]
    [InlineData(typeof(AiTurn), "ai_turns")]
    [InlineData(typeof(AgentDefinitionVersion), "agent_definition_versions")]
    [InlineData(typeof(InboxMessage), "inbox_message")]
    public void Toda_tabela_do_esquema_tem_tenant_id_e_a_chave_composta_comeca_por_ele(Type tipo, string tabela)
    {
        var entidade = Modelo().FindEntityType(tipo)!;

        Assert.Equal(tabela, entidade.GetTableName());
        Assert.Equal("agents", entidade.GetSchema());
        var chave = entidade.FindPrimaryKey()!;
        Assert.Equal("tenant_id", chave.Properties[0].GetColumnName());
        Assert.True(chave.Properties.Count >= 2);
        Assert.False(chave.Properties[0].IsNullable);
    }

    [Fact]
    public void No_maximo_um_turno_aberto_por_sessao_e_a_busca_do_executor_so_olha_os_abertos()
    {
        var indices = Entidade<AiTurn>().GetIndexes().ToDictionary(i => i.GetDatabaseName() ?? string.Empty);

        var unico = indices["ux_ai_turns_open_per_session"];
        Assert.True(unico.IsUnique);
        Assert.Equal(new[] { "tenant_id", "session_id" }, unico.Properties.Select(p => p.GetColumnName()));
        Assert.Equal("state IN (1, 2, 3)", unico.GetFilter());

        var busca = indices["ix_ai_turns_open_state_fire_at"];
        Assert.Equal(new[] { "state", "fire_at" }, busca.Properties.Select(p => p.GetColumnName()));
        Assert.Equal("state IN (1, 2, 3)", busca.GetFilter());
    }

    [Fact]
    public void No_maximo_um_rascunho_e_uma_versao_publicada_por_tenant_sao_dois_indices_distintos_e_o_numero_e_unico()
    {
        // Os dois índices parciais são sobre a MESMA coluna: um defeito já fez o EF fundi-los num só e o rascunho ficar sem a garantia do banco.
        var indices = Entidade<AgentDefinitionVersion>().GetIndexes().ToDictionary(i => i.GetDatabaseName() ?? string.Empty);

        Assert.True(indices.ContainsKey("ux_agent_definition_versions_draft"));
        Assert.True(indices.ContainsKey("ux_agent_definition_versions_published"));
        Assert.Equal("status = 1", indices["ux_agent_definition_versions_draft"].GetFilter());
        Assert.Equal("status = 2", indices["ux_agent_definition_versions_published"].GetFilter());
        Assert.All(new[] { "ux_agent_definition_versions_draft", "ux_agent_definition_versions_published", "ux_agent_definition_versions_number" }, nome => Assert.True(indices[nome].IsUnique));
        Assert.Equal(new[] { "tenant_id", "number" }, indices["ux_agent_definition_versions_number"].Properties.Select(p => p.GetColumnName()));
    }

    [Fact]
    public void As_restricoes_do_esquema_existem_com_os_nomes_esperados()
    {
        string[] Nomes<T>() => Entidade<T>().GetCheckConstraints().Select(c => c.Name ?? string.Empty).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "ck_ai_turns_counters", "ck_ai_turns_ended", "ck_ai_turns_next_attempt", "ck_ai_turns_running", "ck_ai_turns_state" }, Nomes<AiTurn>());
        Assert.Equal(
            new[] { "ck_agent_definition_versions_counters", "ck_agent_definition_versions_instructions", "ck_agent_definition_versions_published", "ck_agent_definition_versions_status" },
            Nomes<AgentDefinitionVersion>());
    }

    [Fact]
    public void A_concorrencia_otimista_do_turno_e_da_definicao_esta_no_modelo_e_a_sequencia_de_lane_existe()
    {
        Assert.True(Entidade<AiTurn>().FindProperty(nameof(AiTurn.Version))!.IsConcurrencyToken);
        Assert.True(Entidade<AgentDefinitionVersion>().FindProperty(nameof(AgentDefinitionVersion.Revision))!.IsConcurrencyToken);
        var sequencia = Modelo().FindSequence(AgentsDbContext.LaneTokenSequence, "agents");
        Assert.NotNull(sequencia);
    }

    [Fact]
    public void O_turno_nao_guarda_prompt_resposta_nem_conteudo_de_mensagem_nenhuma_coluna_os_sugere()
    {
        var colunas = Entidade<AiTurn>().GetProperties().Select(p => p.GetColumnName()).ToList();

        Assert.DoesNotContain(colunas, c => new[] { "prompt", "response", "completion", "secret", "credential", "api_key", "message", "text", "content_text" }.Any(n => c.Contains(n, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("lane_token", colunas);
        Assert.Contains("handoff_reason", colunas);
    }

    [Fact]
    public void Toda_restricao_do_esquema_trata_o_nulo_de_forma_explicita_um_CHECK_so_recusa_FALSE()
    {
        // As colunas ANULÁVEIS de cada tabela (lidas do modelo) só aparecem em comparação junto de IS NULL / IS NOT NULL, nunca soltas (o resultado NULL passaria).
        var modelo = Modelo();
        var verificadas = 0;
        foreach (var tipo in new[] { typeof(AiTurn), typeof(AgentDefinitionVersion) })
        {
            var entidade = modelo.FindEntityType(tipo)!;
            var anulaveis = entidade.GetProperties().Where(p => p.IsNullable).Select(p => p.GetColumnName()).ToList();
            foreach (var restricao in entidade.GetCheckConstraints())
            {
                var sql = restricao.Sql ?? string.Empty;
                verificadas++;
                foreach (var coluna in anulaveis.Where(c => Regex.IsMatch(sql, $@"\b{c}\b")))
                {
                    var tratada = sql.Contains($"{coluna} IS NULL", StringComparison.Ordinal) || sql.Contains($"{coluna} IS NOT NULL", StringComparison.Ordinal);
                    Assert.True(tratada, $"{restricao.Name}: a coluna anulável {coluna} aparece sem IS NULL / IS NOT NULL.");
                }
            }
        }

        Assert.True(verificadas >= 8, "O teste precisa ter restrições para verificar.");
    }

    // A migração

    private static string LerMigracao()
    {
        var raiz = ProcurarRaiz();
        var pasta = Path.Combine(raiz, "backend", "modules", "agents", "IaBimeta.Agents.Infrastructure", "Migrations");
        return File.ReadAllText(Assert.Single(Directory.GetFiles(pasta, "*_AgentsInitial.cs")));
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
    public void A_migracao_cria_os_dois_indices_parciais_do_rascunho_e_da_publicada_e_o_do_turno_aberto()
    {
        var migracao = LerMigracao();

        Assert.Contains("name: \"ux_agent_definition_versions_draft\"", migracao);
        Assert.Contains("name: \"ux_agent_definition_versions_published\"", migracao);
        Assert.Contains("name: \"ux_ai_turns_open_per_session\"", migracao);
        Assert.Contains("filter: \"state IN (1, 2, 3)\"", migracao);
    }

    [Fact]
    public void O_Down_da_migracao_verifica_a_perda_de_dado_antes_de_qualquer_remocao_e_recusa_sem_alterar_nada()
    {
        var migracao = LerMigracao();
        var down = migracao[migracao.IndexOf("protected override void Down(", StringComparison.Ordinal)..];

        var verificacao = down.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal);
        var primeiraRemocao = new[] { "DropTable(", "DropSequence(" }.Select(op => down.IndexOf(op, StringComparison.Ordinal)).Where(p => p >= 0).Min();

        Assert.True(verificacao >= 0 && verificacao < primeiraRemocao, "O Down precisa verificar, e abortar, antes de remover qualquer coisa.");
        Assert.Contains("agents.ai_turns", down);
        Assert.Contains("agents.agent_definition_versions", down);
        Assert.DoesNotContain("CASCADE", down);
    }

    [Fact]
    public void A_migracao_so_toca_o_esquema_agents_e_nao_cria_chave_estrangeira_para_outro_modulo()
    {
        var migracao = LerMigracao();

        Assert.DoesNotContain("ForeignKey", migracao);
        Assert.DoesNotContain("AddForeignKey", migracao);
        Assert.DoesNotMatch("schema: \"(?!agents\")", migracao);
        foreach (var outro in new[] { "conversations.", "contact_center.", "channels.", "tenancy.", "identity.", "audit.", "ai_gateway." })
        {
            Assert.DoesNotContain(outro, migracao);
        }
    }
}
