using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IaBimeta.Agents.Infrastructure;

internal static class PostgresErrors
{
    /// <summary>A gravação violou algum índice único (SQLSTATE 23505): outra operação gravou primeiro.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
