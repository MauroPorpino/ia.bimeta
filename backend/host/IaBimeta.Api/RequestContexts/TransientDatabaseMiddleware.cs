using Npgsql;

namespace IaBimeta.Api.RequestContexts;

/// <summary>
/// Rede de segurança do host para a saturação do PostgreSQL (E1-R, terceira rodada). Só os filtros de endpoint do Identity traduziam contenção do banco em 503; a autenticação da sessão (feita no
/// <see cref="RequestContextMiddleware"/>, antes de qualquer endpoint) e os demais acessos ao banco ficavam de fora e viravam 500. Este middleware cobre a requisição inteira.
/// SÓ traduz falhas em que o banco comprovadamente NÃO executou o comando (deadlock, falha de serialização, trava que passou do <c>lock_timeout</c>, conexões esgotadas, banco ainda não aceitando conexões, ou o pool local
/// esgotado): repetir é seguro. Qualquer outra exceção (erro de programação, violação de restrição, resultado ambíguo como queda de conexão no meio do comando) segue o caminho normal e continua sendo 500.
/// </summary>
internal sealed class TransientDatabaseMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TransientDatabaseMiddleware> _logger;

    public TransientDatabaseMiddleware(RequestDelegate next, ILogger<TransientDatabaseMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext http)
    {
        try
        {
            await _next(http);
        }
        catch (Exception ex) when (IsDefinitelyNotExecuted(ex) && !http.Response.HasStarted && !http.RequestAborted.IsCancellationRequested)
        {
            // Sem o texto da exceção (pode trazer nome de host, banco ou usuário): só a classe do erro.
            _logger.LogWarning("Banco ocupado ou indisponível por instantes ({Classe}); respondendo 503 com Retry-After.", Classify(ex));
            http.Response.Clear();
            http.Response.Headers.RetryAfter = "1";
            await Results.Problem(
                title: "Ocupado",
                detail: "O serviço está ocupado. Tente novamente em instantes.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?> { ["code"] = "service-busy" }).ExecuteAsync(http);
        }
    }

    /// <summary>Se a exceção (ou alguma interna) é uma falha transitória do banco em que o comando não chegou a executar.</summary>
    internal static bool IsDefinitelyNotExecuted(Exception exception)
    {
        for (var atual = exception; atual is not null; atual = atual.InnerException)
        {
            if (atual is AggregateException agregada)
            {
                return agregada.InnerExceptions.Count > 0 && agregada.InnerExceptions.All(IsDefinitelyNotExecuted);
            }

            if (IsTransientHere(atual))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTransientHere(Exception exception) =>
        exception switch
        {
            PostgresException p => p.SqlState is "40P01" or "40001" or "55P03" or "53300" or "57P03",
            // O pool local esgotado (nenhuma conexão foi sequer obtida): o Npgsql lança NpgsqlException com esta mensagem fixa (em inglês). O tempo esgotado de um comando em andamento também traz um TimeoutException interno, mas é ambíguo e NÃO conta.
            NpgsqlException n => n.Message.Contains("connection pool has been exhausted", StringComparison.Ordinal),
            _ => false,
        };

    private static string Classify(Exception exception)
    {
        for (var atual = exception; atual is not null; atual = atual.InnerException)
        {
            if (atual is PostgresException p)
            {
                return "SQLSTATE " + p.SqlState;
            }
        }

        return "pool esgotado";
    }
}
