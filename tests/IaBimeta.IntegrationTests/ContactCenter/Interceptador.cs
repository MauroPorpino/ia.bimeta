using System.Reflection;
using System.Runtime.ExceptionServices;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// Envolve uma porta (interface) para o teste intercalar outra operação em um ponto EXATO de uma chamada: antes ou depois de um método,
/// na n-ésima vez em que ele é chamado, ou no lugar dele. Serve a provar corridas de forma determinística (a outra operação roda
/// inteira, em outro escopo, entre a leitura e a gravação de quem está sendo testado), sem depender de sorte de agendamento.
/// </summary>
internal class Interceptador<T> : DispatchProxy
    where T : class
{
    private static readonly MethodInfo EsperarGenerico =
        typeof(Interceptador<T>).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Single(x => x.Name == nameof(EsperarAsync) && x.IsGenericMethodDefinition);

    private readonly object _trava = new();
    private readonly List<Gancho> _ganchos = new();
    private readonly Dictionary<string, int> _chamadas = new();
    private readonly Dictionary<string, Func<object?[], object?>> _substituicoes = new();
    private T _alvo = null!;

    private sealed record Gancho(string Metodo, bool Antes, int Chamada, Func<Task> Acao);

    public static (T Porta, Interceptador<T> Controle) Envolver(T alvo)
    {
        var porta = Create<T, Interceptador<T>>();
        var controle = (Interceptador<T>)(object)porta;
        controle._alvo = alvo;
        return (porta, controle);
    }

    /// <summary>Quantas vezes <paramref name="metodo"/> foi chamado por este interceptador (inclusive as chamadas que um gancho ou uma substituição atendeu).</summary>
    public int Chamadas(string metodo)
    {
        lock (_trava)
        {
            return _chamadas.GetValueOrDefault(metodo);
        }
    }

    /// <summary>Roda <paramref name="acao"/> logo ANTES da chamada de número <paramref name="chamada"/> (a partir de 1) de <paramref name="metodo"/>.</summary>
    public Interceptador<T> Antes(string metodo, Func<Task> acao, int chamada = 1)
    {
        _ganchos.Add(new Gancho(metodo, true, chamada, acao));
        return this;
    }

    /// <summary>Roda <paramref name="acao"/> logo DEPOIS de a chamada de número <paramref name="chamada"/> de <paramref name="metodo"/> terminar, antes de devolver ao chamador.</summary>
    public Interceptador<T> Depois(string metodo, Func<Task> acao, int chamada = 1)
    {
        _ganchos.Add(new Gancho(metodo, false, chamada, acao));
        return this;
    }

    /// <summary>Faz <paramref name="metodo"/> devolver <paramref name="resultado"/> (por argumentos) em vez de chamar o alvo.</summary>
    public Interceptador<T> Substituir(string metodo, Func<object?[], object?> resultado)
    {
        _substituicoes[metodo] = resultado;
        return this;
    }

    protected override object? Invoke(MethodInfo? metodo, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(metodo);
        args ??= Array.Empty<object?>();

        int numero;
        lock (_trava)
        {
            _chamadas[metodo.Name] = numero = _chamadas.GetValueOrDefault(metodo.Name) + 1;
        }

        if (_substituicoes.TryGetValue(metodo.Name, out var substituicao))
        {
            return substituicao(args);
        }

        var antes = _ganchos.Where(g => g.Metodo == metodo.Name && g.Antes && g.Chamada == numero).Select(g => g.Acao).ToList();
        var depois = _ganchos.Where(g => g.Metodo == metodo.Name && !g.Antes && g.Chamada == numero).Select(g => g.Acao).ToList();
        if (antes.Count == 0 && depois.Count == 0)
        {
            return Chamar(metodo, args);
        }

        Func<Task>? aAntes = antes.Count == 0 ? null : async () => { foreach (var a in antes) { await a(); } };
        Func<Task>? aDepois = depois.Count == 0 ? null : async () => { foreach (var a in depois) { await a(); } };

        if (metodo.ReturnType == typeof(Task))
        {
            return EsperarAsync(aAntes, () => Chamar(metodo, args), aDepois);
        }

        if (metodo.ReturnType.IsGenericType && metodo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return EsperarGenerico.MakeGenericMethod(metodo.ReturnType.GetGenericArguments()[0]).Invoke(null, new object?[] { aAntes, (Func<object?>)(() => Chamar(metodo, args)), aDepois });
        }

        throw new NotSupportedException($"O método {metodo.Name} não devolve Task: não dá para intercalar nele.");
    }

    private object? Chamar(MethodInfo metodo, object?[] args)
    {
        try
        {
            return metodo.Invoke(_alvo, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static async Task EsperarAsync(Func<Task>? antes, Func<object?> chamada, Func<Task>? depois)
    {
        if (antes is not null)
        {
            await antes();
        }

        await (Task)chamada()!;
        if (depois is not null)
        {
            await depois();
        }
    }

    private static async Task<TResultado> EsperarAsync<TResultado>(Func<Task>? antes, Func<object?> chamada, Func<Task>? depois)
    {
        if (antes is not null)
        {
            await antes();
        }

        var resultado = await (Task<TResultado>)chamada()!;
        if (depois is not null)
        {
            await depois();
        }

        return resultado;
    }
}
