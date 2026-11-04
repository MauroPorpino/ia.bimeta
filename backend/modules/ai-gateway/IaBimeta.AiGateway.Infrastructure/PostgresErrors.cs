using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IaBimeta.AiGateway.Infrastructure;

internal static class PostgresErrors
{
    /// <summary>A gravação violou algum índice único (SQLSTATE 23505): outra operação gravou primeiro.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    // As classes de SQLSTATE que são falha de INFRAESTRUTURA: conexão (08), reversão de transação por deadlock ou serialização (40), recursos esgotados (53), intervenção do operador, como
    // o desligamento do servidor (57) e erro de sistema (58).
    private static readonly string[] InfrastructureClasses = { "08", "40", "53", "57", "58" };

    /// <summary>
    /// A falha é de INFRAESTRUTURA (conexão, tempo esgotado, deadlock, serialização, banco em recuperação) e não de integridade ou de programação: uma violação de restrição (classe 23), um
    /// dado inválido (22) ou um SQL inválido (42) são DEFEITO de quem chamou e nunca são mascarados como "indisponível".
    /// </summary>
    public static bool IsInfrastructureFailure(Exception exception) =>
        exception switch
        {
            PostgresException postgres => InfrastructureClasses.Any(classe => postgres.SqlState.StartsWith(classe, StringComparison.Ordinal)),
            NpgsqlException or TimeoutException or IOException or System.Net.Sockets.SocketException => true,
            _ => false,
        };
}
