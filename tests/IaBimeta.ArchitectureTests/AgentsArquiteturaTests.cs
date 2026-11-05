using System.Text.RegularExpressions;

namespace IaBimeta.ArchitectureTests;

/// <summary>
/// As regras do módulo Agents (ADR 0017 e ADR 0007; BOUNDARIES.md): estrutura (Core e Infrastructure, sem Contracts porque nenhum módulo o consome, PRJ-2), arestas só as da matriz, o handler que só
/// grava e nunca chama o modelo (ADR 0017 G.1), nenhum nome de provedor no módulo, a falha de envio que não dispara IA (ARQ-24) e a ausência de qualquer chave para aceitar resposta defasada (ADR 0007).
/// Cada regra negativa tem entrada real: o conjunto de arquivos varridos não pode ser vazio.
/// </summary>
public class AgentsArquiteturaTests
{
    private static IEnumerable<string> ArquivosDoAgents()
    {
        var pasta = Path.Combine(RepositorioDeProjetos.Raiz, "backend", "modules", "agents");
        var arquivos = Directory.EnumerateFiles(pasta, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(arquivos.Count >= 15, "A varredura precisa ter os arquivos do módulo para provar a regra negativa.");
        return arquivos;
    }

    /// <summary>O código sem comentários (as regras valem para o código que roda, não para a explicação do que ele NÃO faz).</summary>
    private static string Codigo(string arquivo)
    {
        var texto = File.ReadAllText(arquivo);
        texto = Regex.Replace(texto, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(texto, @"//.*$", string.Empty, RegexOptions.Multiline);
    }

    [Fact]
    public void O_Agents_tem_Core_e_Infrastructure_sem_Contracts_e_so_as_referencias_permitidas()
    {
        var projetos = RepositorioDeProjetos.LerProjetos();
        var doModulo = projetos.Where(p => p.Nome.StartsWith("IaBimeta.Agents.", StringComparison.Ordinal) && !p.Nome.EndsWith("Tests", StringComparison.Ordinal)).ToList();

        // PRJ-2: sem Contracts, porque nenhum módulo ou adaptador consome o Agents. A Presentation nasce com os endpoints de definição (C2.3).
        Assert.Equal(new[] { "IaBimeta.Agents.Core", "IaBimeta.Agents.Infrastructure" }, doModulo.Select(p => p.Nome).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        var permitidas = new Dictionary<string, string[]>
        {
            ["IaBimeta.Agents.Core"] = new[]
            {
                "IaBimeta.SharedKernel", "IaBimeta.AiGateway.Contracts", "IaBimeta.Conversations.Contracts", "IaBimeta.ContactCenter.Contracts", "IaBimeta.Audit.Contracts",
            },
            ["IaBimeta.Agents.Infrastructure"] = new[] { "IaBimeta.Messaging", "IaBimeta.SharedKernel", "IaBimeta.Conversations.Contracts", "IaBimeta.Agents.Core" },
        };
        foreach (var projeto in doModulo)
        {
            Assert.All(projeto.Referencias, r => Assert.True(permitidas[projeto.Nome].Contains(r), $"{projeto.Nome} -> {r}"));
        }

        // Nenhum projeto de módulo referencia a Infrastructure do Agents: só os hosts e os testes.
        var infra = Assert.Single(doModulo, p => p.Nome == "IaBimeta.Agents.Infrastructure");
        Assert.Equal("IaBimeta.Agents.Infrastructure", infra.Nome);
        Assert.DoesNotContain(
            projetos.Where(p => !p.Nome.StartsWith("IaBimeta.Agents.", StringComparison.Ordinal) && p.Nome.Contains(".Core") || p.Nome.EndsWith(".Contracts", StringComparison.Ordinal)),
            p => p.Referencias.Contains("IaBimeta.Agents.Core") || p.Referencias.Contains("IaBimeta.Agents.Infrastructure"));
    }

    [Fact]
    public void O_handler_do_evento_so_grava_e_o_agendador_nunca_chama_o_modelo_o_Conversations_nem_o_ContactCenter()
    {
        var arquivos = ArquivosDoAgents().ToDictionary(a => Path.GetFileName(a)!, a => Codigo(a));
        var handler = arquivos["ConversationEventHandlers.cs"];
        var agendador = arquivos["TurnScheduler.cs"];

        // ADR 0017, G.1: o relay entrega em lote; uma chamada de IA (ou de outro módulo) no handler bloquearia todos os tenants.
        foreach (var codigo in new[] { handler, agendador })
        {
            foreach (var proibido in new[] { "IAiGateway", "GenerateAsync", "IHandoffRequests", "RequestHandoffAsync", "ITurnCommands", "BeginTurnAsync", "RegisterAiReplyAsync", "HttpClient" })
            {
                Assert.DoesNotContain(proibido, codigo);
            }
        }

        // A regra tem entrada real: o executor (TurnRunner) é quem chama o modelo, o Conversations e o ContactCenter.
        var runner = arquivos["TurnRunner.cs"];
        Assert.Contains("GenerateAsync", runner);
        Assert.Contains("BeginTurnAsync", runner);
        Assert.Contains("RequestHandoffAsync", runner);
    }

    [Fact]
    public void O_Agents_nao_conhece_nenhum_provedor_de_IA_pelo_nome()
    {
        foreach (var arquivo in ArquivosDoAgents())
        {
            var codigo = Codigo(arquivo);
            foreach (var nome in new[] { "openai", "anthropic", "gpt-", "claude-", "gemini", "mistral" })
            {
                Assert.False(codigo.Contains(nome, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(arquivo)} cita o provedor '{nome}': o Agents só conhece o contrato do AiGateway.");
            }
        }
    }

    [Fact]
    public void ARQ_24_O_Agents_nao_referencia_a_falha_de_envio_nem_o_reenvio_e_nao_cria_turno_por_eles()
    {
        foreach (var arquivo in ArquivosDoAgents())
        {
            var codigo = Codigo(arquivo);
            foreach (var proibido in new[] { "OutboundMessageFailed", "OutboundMessageRequested", "RequestResend", "RecordDeliveryStatus" })
            {
                Assert.False(codigo.Contains(proibido, StringComparison.Ordinal), $"{Path.GetFileName(arquivo)} referencia {proibido}: falha de envio nunca dispara IA (ARQ-24, I10).");
            }
        }
    }

    [Fact]
    public void Nao_existe_chave_nem_codigo_para_aceitar_resposta_defasada_no_Agents()
    {
        foreach (var arquivo in ArquivosDoAgents())
        {
            var codigo = Codigo(arquivo);
            foreach (var proibido in new[] { "StaleReplyMode", "allowStaleInput", "AllowStaleInput", "StaleNotAllowed", "StaleReplyAccepted" })
            {
                Assert.False(codigo.Contains(proibido, StringComparison.Ordinal), $"{Path.GetFileName(arquivo)} contém {proibido}: no MVP a resposta defasada é SEMPRE recusada, sem chave nem código preparatório (ADR 0007).");
            }
        }
    }

    [Fact]
    public void So_o_Agents_cria_o_RequestHandoffCommand_e_ninguem_no_Agents_muda_o_modo_de_atendimento_direto()
    {
        var arquivos = ArquivosDoAgents().Select(a => (Nome: Path.GetFileName(a), Codigo: Codigo(a))).ToList();

        // A IA decide transferir pelo contrato do ContactCenter; quem muda o modo é o Conversations (TransitionHandlingMode), pelo executor do ContactCenter.
        Assert.Contains(arquivos, a => a.Nome == "TurnRunner.cs" && a.Codigo.Contains("new RequestHandoffCommand(", StringComparison.Ordinal));
        Assert.DoesNotContain(arquivos, a => a.Codigo.Contains("TransitionHandlingMode", StringComparison.Ordinal) || a.Codigo.Contains("TransferSessionCommand", StringComparison.Ordinal));
    }
}
