using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IaBimeta.ContactCenter.Infrastructure;

internal static class PostgresErrors
{
    /// <summary>A gravação violou o índice único informado (SQLSTATE 23505).</summary>
    public static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && postgres.ConstraintName == constraintName;
}
