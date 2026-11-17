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
    /// Contenção transitória do banco em que o comando comprovadamente NÃO executou e repetir é seguro: deadlock (40P01), falha de serialização (40001), trava que passou do <c>lock_timeout</c> (55P03), conexões esgotadas no
    /// servidor (53300), banco ainda não aceitando conexões (57P03) e o pool local esgotado (nenhuma conexão foi obtida). Percorre as exceções internas: o EF embrulha a falha ao abrir a conexão em
    /// <see cref="InvalidOperationException"/>. O tempo esgotado de um comando em andamento NÃO conta: o resultado é ambíguo.
    /// </summary>
    public static bool IsTransientContention(Exception exception)
    {
        for (var atual = exception; atual is not null; atual = atual.InnerException)
        {
            var transitoria = atual switch
            {
                PostgresException p => IsTransientState(p.SqlState),
                // O Npgsql avisa o pool esgotado com esta mensagem (em inglês, fixa); o inner TimeoutException também aparece em tempo de comando, por isso a mensagem decide.
                NpgsqlException n => n.Message.Contains("connection pool has been exhausted", StringComparison.Ordinal),
                _ => false,
            };
            if (transitoria)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTransientState(string state) => state is "40P01" or "40001" or "55P03" or "53300" or "57P03";
}
