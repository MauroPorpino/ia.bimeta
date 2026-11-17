using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IaBimeta.Identity.Infrastructure;

internal static class PostgresErrors
{
    /// <summary>A gravação violou o índice único informado (SQLSTATE 23505).</summary>
    public static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && postgres.ConstraintName == constraintName;

    /// <summary>
    /// Contenção transitória do banco, em que nada foi gravado e repetir é seguro: deadlock (40P01), falha de serialização (40001), trava que passou do <c>lock_timeout</c> (55P03), conexões esgotadas no servidor (53300) e o
    /// pool local esgotado (<see cref="TimeoutException"/> do Npgsql ao pedir conexão).
    /// </summary>
    public static bool IsTransientContention(Exception exception) =>
        exception switch
        {
            PostgresException p => IsTransientState(p.SqlState),
            DbUpdateException { InnerException: PostgresException p } => IsTransientState(p.SqlState),
            TimeoutException => true,
            _ => false,
        };

    private static bool IsTransientState(string state) => state is "40P01" or "40001" or "55P03" or "53300";
}
