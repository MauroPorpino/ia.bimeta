using System.Text.RegularExpressions;

namespace IaBimeta.ArchitectureTests;

/// <summary>
/// A unicidade da sessão aberta de chat (ADR 0012, seção A; fatia A2) depende de TRÊS textos que o compilador não liga: o filtro do índice no modelo do EF, o alvo e o
/// predicado do <c>ON CONFLICT</c> do SQL cru da criação de sessão e a migração. Se um mudar sem os outros, a criação de sessão passa a falhar (o PostgreSQL recusa um
/// <c>ON CONFLICT</c> cujo predicado o índice não sustenta) ou, pior, o índice deixa de proteger. Estes testes leem os arquivos de produção e conferem que os três
/// dizem o mesmo; não usam banco, e cada regra negativa tem entrada real (não passa em conjunto vazio).
/// </summary>
public class IndiceDeSessaoDeChatTests
{
    private const string IndiceDeChat = "ux_service_sessions_open_chat";
    private const string IndiceAntigo = "ix_service_sessions_one_open_per_contact";
    private const string FiltroEsperado = "status = 1 AND thread_key IS NULL";

    private static readonly string Infraestrutura = Path.Combine(RepositorioDeProjetos.Raiz, "backend", "modules", "conversations", "IaBimeta.Conversations.Infrastructure");

    private static string Ler(string relativoAInfraestrutura) => File.ReadAllText(Path.Combine(Infraestrutura, relativoAInfraestrutura));

    /// <summary>Sem os comentários de linha e de documentação (as palavras de um comentário não são SQL).</summary>
    private static string SemComentarios(string texto) =>
        string.Join('\n', texto.Split('\n').Where(linha => !linha.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Normalizar(string texto) => Regex.Replace(texto, @"\s+", " ");

    private static string LerMigracao()
    {
        var arquivos = Directory.GetFiles(Path.Combine(Infraestrutura, "Migrations"), "*_ChatSessionAssociationAndLimits.cs");
        return File.ReadAllText(Assert.Single(arquivos));
    }

    private static (string Up, string Down) DividirMigracao(string migracao)
    {
        var up = migracao.IndexOf("protected override void Up(", StringComparison.Ordinal);
        var down = migracao.IndexOf("protected override void Down(", StringComparison.Ordinal);
        Assert.True(up >= 0 && down > up, "A migração precisa ter Up e depois Down.");
        return (migracao[up..down], migracao[down..]);
    }

    [Fact]
    public void O_INSERT_da_sessao_repete_o_alvo_e_o_predicado_do_indice_de_chat_num_unico_ON_CONFLICT()
    {
        var armazenamento = Normalizar(SemComentarios(Ler("ConversationStore.cs")));
        var inicio = armazenamento.IndexOf("INSERT INTO conversations.service_sessions", StringComparison.Ordinal);
        Assert.True(inicio >= 0, "O INSERT da sessão não foi encontrado: o teste não pode passar sem entrada.");

        // O SQL é um texto interpolado sem aspas internas: termina na próxima aspa.
        var comando = armazenamento[inicio..armazenamento.IndexOf('"', inicio)];

        Assert.Contains($"ON CONFLICT (tenant_id, contact_id) WHERE {FiltroEsperado} DO NOTHING", comando);
        Assert.Equal(1, Regex.Count(comando, "ON CONFLICT"));
        Assert.Contains("association_mode", comando);
        Assert.Contains("thread_key", comando);
    }

    [Fact]
    public void Nenhum_ON_CONFLICT_do_armazenamento_do_Conversations_e_sem_alvo_e_nenhum_usa_o_predicado_antigo()
    {
        var armazenamento = Normalizar(SemComentarios(Ler("ConversationStore.cs")));

        // Entrada real: os três pontos de criação (contato, sessão e conversa) usam ON CONFLICT com alvo.
        Assert.True(Regex.Count(armazenamento, @"ON CONFLICT \(") >= 3);

        // Sem alvo, o ON CONFLICT engoliria a violação de qualquer outro índice único (ADR 0012, seção A, item 3).
        Assert.DoesNotMatch(@"ON CONFLICT DO NOTHING", armazenamento);

        // O predicado anterior ("WHERE status = 1" sem a chave de thread) não é sustentado pelo índice novo.
        Assert.DoesNotMatch(@"WHERE status = 1 DO NOTHING", armazenamento);
    }

    [Fact]
    public void O_filtro_do_indice_e_o_mesmo_no_modelo_no_SQL_na_migracao_e_no_snapshot()
    {
        var configuracao = Ler(Path.Combine("Configurations", "SessionAndContactConfigurations.cs"));
        var armazenamento = Normalizar(SemComentarios(Ler("ConversationStore.cs")));
        var (up, _) = DividirMigracao(LerMigracao());
        var snapshot = Ler(Path.Combine("Migrations", "ConversationsDbContextModelSnapshot.cs"));

        var noModelo = Regex.Match(configuracao, "OpenChatIndexFilter\\s*=\\s*\"([^\"]+)\"");
        var noSql = Regex.Match(armazenamento, @"ON CONFLICT \(tenant_id, contact_id\) WHERE (.+?) DO NOTHING");
        var naMigracao = Regex.Match(Normalizar(up), "filter: \"([^\"]+)\"");
        Assert.True(noModelo.Success && noSql.Success && naMigracao.Success, "Os três textos precisam existir para serem comparados.");

        Assert.Equal(FiltroEsperado, noModelo.Groups[1].Value);
        Assert.Equal(FiltroEsperado, noSql.Groups[1].Value);
        Assert.Equal(FiltroEsperado, naMigracao.Groups[1].Value);
        Assert.Contains($".HasFilter(\"{FiltroEsperado}\")", snapshot);
    }

    [Fact]
    public void A_migracao_cria_as_colunas_e_o_indice_novo_ANTES_de_remover_o_indice_antigo()
    {
        var (up, _) = DividirMigracao(LerMigracao());

        var colunaDaChave = up.IndexOf("name: \"thread_key\"", StringComparison.Ordinal);
        var restricao = up.IndexOf("ck_service_sessions_association", StringComparison.Ordinal);
        var criaIndice = up.IndexOf($"name: \"{IndiceDeChat}\"", StringComparison.Ordinal);
        var removeIndice = up.IndexOf($"name: \"{IndiceAntigo}\"", StringComparison.Ordinal);

        Assert.True(colunaDaChave >= 0 && restricao >= 0 && criaIndice >= 0 && removeIndice >= 0, "A migração precisa ter as quatro operações.");
        Assert.True(colunaDaChave < criaIndice, "A coluna thread_key precisa existir antes do índice que a usa.");
        Assert.True(restricao < criaIndice, "A restrição de associação é criada antes do índice novo.");
        Assert.True(criaIndice < removeIndice, "O índice novo precisa ser criado ANTES de o antigo ser removido: nunca há sessão aberta de chat sem índice único.");
        Assert.Contains("DropIndex", up[(removeIndice - 80)..removeIndice]);
        Assert.Contains("CreateIndex", up[(criaIndice - 80)..criaIndice]);
    }

    [Fact]
    public void O_Down_verifica_a_perda_de_dado_antes_de_qualquer_mudanca_e_recria_o_indice_antigo_antes_de_remover_o_novo()
    {
        var (_, down) = DividirMigracao(LerMigracao());

        var verificacao = down.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal);
        var primeiraMudanca = new[] { "CreateIndex(", "DropIndex(", "DropCheckConstraint(", "DropColumn(", "AlterColumn<" }
            .Select(operacao => down.IndexOf(operacao, StringComparison.Ordinal))
            .Where(posicao => posicao >= 0)
            .Min();

        Assert.True(verificacao >= 0, "O Down precisa verificar, e abortar, antes de estreitar colunas.");
        Assert.True(verificacao < primeiraMudanca, "As verificações vêm ANTES de qualquer mudança.");

        // As três verificações: referência do contato, identificador do evento e sessão que não é de chat.
        Assert.Equal(3, Regex.Count(down, "RAISE EXCEPTION"));
        Assert.Contains("length(external_ref) > 128", down);
        Assert.Contains("length(provider_event_id) > 200", down);
        Assert.Contains("association_mode <> 1 OR thread_key IS NOT NULL", down);

        var recria = down.IndexOf($"name: \"{IndiceAntigo}\"", StringComparison.Ordinal);
        var remove = down.IndexOf($"name: \"{IndiceDeChat}\"", StringComparison.Ordinal);
        Assert.True(recria >= 0 && remove >= 0 && recria < remove, "No Down, o índice antigo volta ANTES de o novo sair.");
    }

    [Fact]
    public void O_snapshot_tem_o_indice_a_restricao_e_os_limites_novos_e_nao_tem_mais_o_indice_antigo()
    {
        var snapshot = Ler(Path.Combine("Migrations", "ConversationsDbContextModelSnapshot.cs"));

        Assert.Contains($".HasDatabaseName(\"{IndiceDeChat}\")", snapshot);
        Assert.Contains("ck_service_sessions_association", snapshot);
        Assert.Contains("character varying(254)", snapshot);
        Assert.Contains("character varying(512)", snapshot);
        Assert.DoesNotContain(IndiceAntigo, snapshot);
    }

    [Fact]
    public void Nada_do_codigo_de_producao_alem_das_migracoes_antigas_referencia_o_indice_antigo()
    {
        var backend = Path.Combine(RepositorioDeProjetos.Raiz, "backend");
        var separador = Path.DirectorySeparatorChar;
        var arquivos = Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separador}obj{separador}") && !f.Contains($"{separador}bin{separador}"))
            .ToList();

        // Entrada real: a migração nova e a configuração do índice estão no conjunto.
        Assert.Contains(arquivos, f => f.EndsWith("SessionAndContactConfigurations.cs", StringComparison.Ordinal));

        var referencias = arquivos
            .Where(f => !f.Contains($"{separador}Migrations{separador}"))
            .Where(f => { var texto = SemComentarios(File.ReadAllText(f)); return texto.Contains(IndiceAntigo, StringComparison.Ordinal) || texto.Contains("OneOpenPerContactIndex", StringComparison.Ordinal); })
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(referencias);
    }
}
