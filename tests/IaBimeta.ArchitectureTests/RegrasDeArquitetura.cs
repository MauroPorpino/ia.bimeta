using System.Text.RegularExpressions;

namespace IaBimeta.ArchitectureTests;

internal enum TipoDeProjeto
{
    Host,
    Bloco,
    Adaptador,
    Teste,
    Modulo,
    NaoClassificado,
}

internal sealed record ClasseDoProjeto(TipoDeProjeto Tipo, string Modulo, string Camada);

/// <summary>Um projeto lido do .csproj: o nome (do arquivo), as referências a projetos, os pacotes e o tipo de SDK.</summary>
internal sealed record ProjetoDaSolucao(
    string Nome,
    IReadOnlyList<string> Referencias,
    IReadOnlyList<string> Pacotes,
    IReadOnlyList<string> FrameworksEWeb);

internal sealed record Violacao(string Regra, string Detalhe)
{
    public override string ToString() => $"{Regra}: {Detalhe}";
}

/// <summary>
/// As regras de arquitetura de <c>docs/architecture/BOUNDARIES.md</c> sobre o grafo de projetos, sem biblioteca
/// (o ArchUnitNET só entra depois do spike aprovado). Estão separadas dos testes para poderem ser provadas: os testes
/// sintéticos montam grafos com violações e exigem que cada regra as acuse (nenhuma regra passa em conjunto vazio).
/// A matriz <c>module-dependencies.json</c> é a fonte única das arestas permitidas entre módulos (ARQ-06).
/// </summary>
internal static class RegrasDeArquitetura
{
    private static readonly Regex ProjetoDeModulo = new(
        @"^IaBimeta\.(?<modulo>[A-Za-z]+)\.(?<camada>Contracts|Core|Infrastructure|Presentation)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] PacotesDeBanco = { "Microsoft.EntityFrameworkCore", "Npgsql" };

    public static ClasseDoProjeto Classificar(string nome, IReadOnlyCollection<string> modulos)
    {
        if (nome is "IaBimeta.Api" or "IaBimeta.Worker")
        {
            return new ClasseDoProjeto(TipoDeProjeto.Host, string.Empty, string.Empty);
        }

        if (nome is "IaBimeta.SharedKernel" or "IaBimeta.Messaging")
        {
            return new ClasseDoProjeto(TipoDeProjeto.Bloco, string.Empty, string.Empty);
        }

        if (nome.StartsWith("IaBimeta.Adapters.", StringComparison.Ordinal))
        {
            return new ClasseDoProjeto(TipoDeProjeto.Adaptador, string.Empty, string.Empty);
        }

        if (nome.EndsWith("Tests", StringComparison.Ordinal))
        {
            return new ClasseDoProjeto(TipoDeProjeto.Teste, string.Empty, string.Empty);
        }

        var casamento = ProjetoDeModulo.Match(nome);
        if (casamento.Success && modulos.Contains(casamento.Groups["modulo"].Value))
        {
            return new ClasseDoProjeto(TipoDeProjeto.Modulo, casamento.Groups["modulo"].Value, casamento.Groups["camada"].Value);
        }

        return new ClasseDoProjeto(TipoDeProjeto.NaoClassificado, string.Empty, string.Empty);
    }

    public static IReadOnlyList<Violacao> Verificar(IReadOnlyList<ProjetoDaSolucao> projetos, IReadOnlyDictionary<string, string[]> matriz)
    {
        var violacoes = new List<Violacao>();
        var modulos = matriz.Keys.ToList();
        var porNome = projetos.ToDictionary(p => p.Nome);
        var classes = projetos.ToDictionary(p => p.Nome, p => Classificar(p.Nome, modulos));

        foreach (var projeto in projetos.Where(p => classes[p.Nome].Tipo == TipoDeProjeto.NaoClassificado))
        {
            violacoes.Add(new Violacao("CLASSIFICACAO", $"{projeto.Nome} não é módulo, host, bloco, adaptador nem teste"));
        }

        foreach (var projeto in projetos)
        {
            var de = classes[projeto.Nome];

            foreach (var destino in projeto.Referencias)
            {
                if (!porNome.ContainsKey(destino))
                {
                    violacoes.Add(new Violacao("REFERENCIA", $"{projeto.Nome} -> {destino} (projeto inexistente)"));
                    continue;
                }

                var para = classes[destino];
                var aresta = $"{projeto.Nome} -> {destino}";

                // ARQ-04: só hosts (e testes) referenciam Infrastructure, Presentation e adaptadores.
                var destinoRestrito = para.Tipo == TipoDeProjeto.Adaptador
                    || (para.Tipo == TipoDeProjeto.Modulo && para.Camada is "Infrastructure" or "Presentation");
                if (destinoRestrito && de.Tipo is not (TipoDeProjeto.Host or TipoDeProjeto.Teste))
                {
                    violacoes.Add(new Violacao("ARQ-04", aresta));
                }

                // ARQ-05 e ARQ-06: entre módulos, só Contracts, e só as arestas da matriz.
                if (de.Tipo == TipoDeProjeto.Modulo && para.Tipo == TipoDeProjeto.Modulo && de.Modulo != para.Modulo)
                {
                    if (para.Camada != "Contracts")
                    {
                        violacoes.Add(new Violacao("ARQ-05", aresta));
                    }

                    if (!matriz[de.Modulo].Contains(para.Modulo))
                    {
                        violacoes.Add(new Violacao("ARQ-06", aresta));
                    }
                }

                // Contracts só referenciam Contracts e blocos de construção.
                if (de is { Tipo: TipoDeProjeto.Modulo, Camada: "Contracts" } && para is { Tipo: TipoDeProjeto.Modulo } && para.Camada != "Contracts")
                {
                    violacoes.Add(new Violacao("CTR", aresta));
                }

                // DEP-2: adaptadores referenciam só o Contracts do dono.
                if (de.Tipo == TipoDeProjeto.Adaptador && para is { Tipo: TipoDeProjeto.Modulo } && para.Camada != "Contracts")
                {
                    violacoes.Add(new Violacao("DEP-2", aresta));
                }

                // DEP-4 e DEP-6: blocos de construção não referenciam módulos, hosts nem adaptadores.
                if (de.Tipo == TipoDeProjeto.Bloco && para.Tipo is TipoDeProjeto.Modulo or TipoDeProjeto.Host or TipoDeProjeto.Adaptador)
                {
                    violacoes.Add(new Violacao("DEP-4", aresta));
                }

                // O SharedKernel não referencia projeto algum.
                if (projeto.Nome == "IaBimeta.SharedKernel" && para.Tipo != TipoDeProjeto.Teste)
                {
                    violacoes.Add(new Violacao("DEP-4", $"{aresta} (o SharedKernel não referencia projeto algum)"));
                }

                // Ninguém referencia um host, exceto testes.
                if (para.Tipo == TipoDeProjeto.Host && de.Tipo != TipoDeProjeto.Teste)
                {
                    violacoes.Add(new Violacao("HOST", aresta));
                }
            }

            // BOUNDARIES.md §2: Domain, Application e Contracts não conhecem EF, Npgsql nem ASP.NET; Presentation não toca no banco.
            if (de is { Tipo: TipoDeProjeto.Modulo, Camada: "Core" or "Contracts" })
            {
                foreach (var pacote in projeto.Pacotes.Where(EhPacoteDeBanco))
                {
                    violacoes.Add(new Violacao("PKG-1", $"{projeto.Nome} referencia o pacote {pacote}"));
                }

                foreach (var framework in projeto.FrameworksEWeb)
                {
                    violacoes.Add(new Violacao("PKG-1", $"{projeto.Nome} usa {framework} (ASP.NET)"));
                }
            }

            if (de is { Tipo: TipoDeProjeto.Modulo, Camada: "Presentation" })
            {
                foreach (var pacote in projeto.Pacotes.Where(EhPacoteDeBanco))
                {
                    violacoes.Add(new Violacao("PKG-2", $"{projeto.Nome} referencia o pacote {pacote}"));
                }
            }
        }

        var ciclo = EncontrarCiclo(projetos);
        if (ciclo is not null)
        {
            violacoes.Add(new Violacao("CICLO", ciclo));
        }

        return violacoes;
    }

    /// <summary>A matriz precisa ser coerente: toda dependência é um módulo da própria matriz e não há ciclo entre módulos.</summary>
    public static IReadOnlyList<Violacao> VerificarMatriz(IReadOnlyDictionary<string, string[]> matriz)
    {
        var violacoes = new List<Violacao>();

        foreach (var (modulo, dependencias) in matriz)
        {
            foreach (var dependencia in dependencias)
            {
                if (!matriz.ContainsKey(dependencia))
                {
                    violacoes.Add(new Violacao("MATRIZ", $"{modulo} depende de '{dependencia}', que não é um módulo da matriz"));
                }

                if (dependencia == modulo)
                {
                    violacoes.Add(new Violacao("MATRIZ", $"{modulo} depende de si mesmo"));
                }
            }
        }

        var estado = new Dictionary<string, int>();
        foreach (var modulo in matriz.Keys)
        {
            var ciclo = ProcurarCicloNaMatriz(modulo, matriz, estado, new List<string>());
            if (ciclo is not null)
            {
                violacoes.Add(new Violacao("MATRIZ", $"ciclo entre módulos: {ciclo}"));
                break;
            }
        }

        return violacoes;
    }

    private static bool EhPacoteDeBanco(string pacote) =>
        PacotesDeBanco.Any(prefixo => pacote.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase));

    private static string? EncontrarCiclo(IReadOnlyList<ProjetoDaSolucao> projetos)
    {
        var porNome = projetos.ToDictionary(p => p.Nome);
        var estado = new Dictionary<string, int>();

        string? Visitar(string no, List<string> pilha)
        {
            estado[no] = 1;
            pilha.Add(no);

            foreach (var referencia in porNome[no].Referencias.Where(porNome.ContainsKey))
            {
                if (estado.TryGetValue(referencia, out var situacao) && situacao == 1)
                {
                    return string.Join(" -> ", pilha.Append(referencia));
                }

                if (!estado.ContainsKey(referencia) && Visitar(referencia, pilha) is { } achado)
                {
                    return achado;
                }
            }

            pilha.RemoveAt(pilha.Count - 1);
            estado[no] = 2;
            return null;
        }

        foreach (var projeto in projetos.Where(p => !estado.ContainsKey(p.Nome)))
        {
            if (Visitar(projeto.Nome, new List<string>()) is { } ciclo)
            {
                return ciclo;
            }
        }

        return null;
    }

    private static string? ProcurarCicloNaMatriz(string no, IReadOnlyDictionary<string, string[]> matriz, Dictionary<string, int> estado, List<string> pilha)
    {
        if (estado.TryGetValue(no, out var situacao))
        {
            return situacao == 1 ? string.Join(" -> ", pilha.SkipWhile(m => m != no).Append(no)) : null;
        }

        estado[no] = 1;
        pilha.Add(no);

        foreach (var dependencia in matriz[no].Where(matriz.ContainsKey))
        {
            if (ProcurarCicloNaMatriz(dependencia, matriz, estado, pilha) is { } ciclo)
            {
                return ciclo;
            }
        }

        pilha.RemoveAt(pilha.Count - 1);
        estado[no] = 2;
        return null;
    }
}
