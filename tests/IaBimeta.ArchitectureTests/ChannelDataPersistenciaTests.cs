using System.Text.RegularExpressions;

namespace IaBimeta.ArchitectureTests;

/// <summary>
/// A persistência do bloco opaco de metadados do canal (<c>ChannelData</c>, fatia A3, migração 6 reduzida do ADR 0011) tem três propriedades que o compilador não garante e que
/// estes testes conferem lendo os arquivos de produção: (1) a migração só acrescenta as duas colunas e a restrição que as une, em <c>messages</c> e no esquema do próprio módulo, e a
/// reversão NUNCA descarta dado em silêncio; (2) nada específico de um canal entra no domínio comum nem no que a A3 criou (o que é do canal vive dentro do bloco opaco); (3) a recusa
/// temporária da A1 saiu, e o serviço guarda o bloco. Sem banco, e cada regra negativa tem entrada real (não passa em conjunto vazio).
/// </summary>
public class ChannelDataPersistenciaTests
{
    private static readonly string Conversations = Path.Combine(RepositorioDeProjetos.Raiz, "backend", "modules", "conversations");

    private static string Ler(params string[] relativoAoModulo) => File.ReadAllText(Path.Combine(new[] { Conversations }.Concat(relativoAoModulo).ToArray()));

    private static string SemComentarios(string texto) =>
        string.Join('\n', texto.Split('\n').Where(linha => !linha.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string LerMigracao()
    {
        var pasta = Path.Combine(Conversations, "IaBimeta.Conversations.Infrastructure", "Migrations");
        return File.ReadAllText(Assert.Single(Directory.GetFiles(pasta, "*_MessageChannelData.cs")));
    }

    private static (string Up, string Down) Dividir(string migracao)
    {
        var up = migracao.IndexOf("protected override void Up(", StringComparison.Ordinal);
        var down = migracao.IndexOf("protected override void Down(", StringComparison.Ordinal);
        Assert.True(up >= 0 && down > up, "A migração precisa ter Up e depois Down.");
        return (migracao[up..down], migracao[down..]);
    }

    // A migração

    [Fact]
    public void O_Up_so_acrescenta_as_duas_colunas_e_a_restricao_em_messages_e_nao_altera_nem_remove_nada()
    {
        var (up, _) = Dividir(LerMigracao());

        Assert.Equal(2, Regex.Count(up, @"migrationBuilder\.AddColumn<"));
        Assert.Equal(1, Regex.Count(up, @"migrationBuilder\.AddCheckConstraint\("));
        Assert.Contains("name: \"channel_data\"", up);
        Assert.Contains("name: \"channel_data_version\"", up);
        Assert.Contains("name: \"ck_messages_channel_data\"", up);

        // Nada além disso: nenhuma alteração, remoção, criação de tabela ou de índice, nem SQL solto, e nenhum dado reescrito.
        foreach (var proibido in new[] { "AlterColumn", "DropColumn", "DropTable", "DropIndex", "CreateTable", "CreateIndex", "DropCheckConstraint", "migrationBuilder.Sql(", "UpdateData", "DeleteData", "InsertData" })
        {
            Assert.DoesNotContain(proibido, up);
        }

        // Só a tabela das mensagens, no esquema do módulo (nenhuma tabela de outro módulo).
        Assert.Equal(3, Regex.Count(up, "table: \"messages\""));
        Assert.Equal(3, Regex.Count(up, "schema: \"conversations\""));
        Assert.Equal(0, Regex.Count(up, "table: \"(?!messages\")"));
    }

    [Fact]
    public void O_CHECK_trata_o_nulo_de_forma_explicita_porque_um_CHECK_so_recusa_FALSE_e_um_resultado_nulo_passaria()
    {
        // Defeito que o teste de integração achou: com o objeto JSON preenchido e a versão nula, "versão >= 1" é NULL, o ramo todo é NULL e o PostgreSQL ACEITA a linha. A versão tem de
        // ser comparada com nulo de forma explícita, no modelo, na migração, no snapshot e no Designer.
        const string esperado = "(channel_data IS NULL AND channel_data_version IS NULL) OR (channel_data IS NOT NULL AND channel_data_version IS NOT NULL AND channel_data_version >= 1 AND jsonb_typeof(channel_data) = 'object')";
        var pasta = Path.Combine(Conversations, "IaBimeta.Conversations.Infrastructure");
        var migracoes = Path.Combine(pasta, "Migrations");

        var textos = new Dictionary<string, string>
        {
            ["MessageConfiguration"] = Ler("IaBimeta.Conversations.Infrastructure", "Configurations", "MessageConfiguration.cs"),
            ["migração"] = LerMigracao(),
            ["Designer"] = File.ReadAllText(Assert.Single(Directory.GetFiles(migracoes, "*_MessageChannelData.Designer.cs"))),
            ["snapshot"] = Ler("IaBimeta.Conversations.Infrastructure", "Migrations", "ConversationsDbContextModelSnapshot.cs"),
        };

        foreach (var (nome, texto) in textos)
        {
            Assert.True(texto.Contains(esperado, StringComparison.Ordinal), $"{nome} não tem o CHECK com o nulo tratado de forma explícita.");
        }
    }

    [Fact]
    public void As_colunas_novas_sao_anulaveis_e_sem_valor_padrao_para_nao_reescrever_linhas_nem_quebrar_o_codigo_anterior()
    {
        var (up, _) = Dividir(LerMigracao());

        Assert.Equal(2, Regex.Count(up, "nullable: true"));
        Assert.DoesNotContain("defaultValue", up);
        Assert.DoesNotContain("nullable: false", up);
        Assert.Contains("type: \"jsonb\"", up);
        Assert.Contains("type: \"integer\"", up);
    }

    [Fact]
    public void O_Down_verifica_a_perda_de_dado_antes_de_qualquer_mudanca_e_so_depois_remove_a_restricao_e_as_colunas()
    {
        var (_, down) = Dividir(LerMigracao());

        var verificacao = down.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal);
        var primeiraMudanca = new[] { "DropCheckConstraint(", "DropColumn(" }.Select(op => down.IndexOf(op, StringComparison.Ordinal)).Where(p => p >= 0).Min();

        Assert.True(verificacao >= 0, "O Down precisa verificar, e abortar, antes de remover as colunas.");
        Assert.True(verificacao < primeiraMudanca, "A verificação vem ANTES de qualquer remoção.");
        Assert.Equal(1, Regex.Count(down, "RAISE EXCEPTION"));
        Assert.Contains("channel_data IS NOT NULL", down);
        Assert.Contains("channel_data_version IS NOT NULL", down);
        Assert.Equal(1, Regex.Count(down, @"DropCheckConstraint\("));
        Assert.Equal(2, Regex.Count(down, @"DropColumn\("));

        // O Down não toca em mais nada.
        foreach (var proibido in new[] { "AlterColumn", "DropTable", "DropIndex", "CreateIndex", "DeleteData", "UpdateData" })
        {
            Assert.DoesNotContain(proibido, down);
        }
    }

    [Fact]
    public void O_snapshot_tem_as_duas_colunas_e_a_restricao_e_o_modelo_nao_tem_a_propriedade_reconstruida()
    {
        var snapshot = Ler("IaBimeta.Conversations.Infrastructure", "Migrations", "ConversationsDbContextModelSnapshot.cs");

        Assert.Contains(".HasColumnName(\"channel_data\")", snapshot);
        Assert.Contains(".HasColumnName(\"channel_data_version\")", snapshot);
        Assert.Contains("ck_messages_channel_data", snapshot);
        Assert.DoesNotContain("b.Property<IaBimeta.Conversations.Contracts.ChannelData>(\"ChannelData\")", snapshot);
    }

    // Sem nada específico de canal

    [Fact]
    public void Nada_do_que_a_A3_criou_cita_um_canal_especifico_no_codigo_o_que_e_do_canal_vive_dentro_do_bloco_opaco()
    {
        var arquivos = new[]
        {
            Path.Combine("IaBimeta.Conversations.Infrastructure", "ChannelDataValuesConversion.cs"),
            Path.Combine("IaBimeta.Conversations.Infrastructure", "Configurations", "MessageConfiguration.cs"),
            Path.Combine("IaBimeta.Conversations.Core", "Domain", "Message.cs"),
            Path.Combine("IaBimeta.Conversations.Contracts", "ChannelData.cs"),
        };
        var migracao = SemComentarios(LerMigracao());
        var padrao = new Regex("whatsapp|wamid|webchat|instagram|messenger|telegram|\\bsms\\b|e-?mail|facebook", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var arquivo in arquivos)
        {
            var texto = SemComentarios(Ler(arquivo));
            Assert.True(texto.Length > 0, $"{arquivo} não pode estar vazio: o teste não passa sem entrada.");
            Assert.False(padrao.IsMatch(texto), $"{arquivo} cita um canal específico no código (fora de comentário).");
        }

        Assert.False(padrao.IsMatch(migracao), "A migração cita um canal específico.");
    }

    [Fact]
    public void O_domino_comum_nao_interpreta_chaves_de_canal_nenhuma_chave_literal_e_conhecida_pelo_Conversations()
    {
        // O Conversations só impõe a FORMA do bloco (limites estruturais). Nenhum literal de chave do tipo "media." aparece no código de produção do módulo fora de comentário.
        var modulo = Directory.EnumerateFiles(Conversations, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(modulo);

        var comLiteral = modulo.Where(f => SemComentarios(File.ReadAllText(f)).Contains("\"media.", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();

        Assert.Empty(comLiteral);
    }

    // A recusa temporária da A1 saiu

    [Fact]
    public void A_recusa_temporaria_do_ChannelData_saiu_do_servico_e_a_validacao_e_a_gravacao_continuam()
    {
        var servico = SemComentarios(Ler("IaBimeta.Conversations.Core", "Application", "InboundMessageService.cs"));

        Assert.DoesNotContain("ainda não são suportados", servico);
        Assert.DoesNotContain("não foi registrada", servico);
        Assert.Contains("ConversationLimits.ValidateChannelData(", servico);

        // O bloco validado é entregue à mensagem que será gravada: nada o descarta em silêncio.
        Assert.Matches(@"Message\.CreateInbound\([^;]*command\.ChannelData", Regex.Replace(servico, @"\s+", " "));
    }
}
