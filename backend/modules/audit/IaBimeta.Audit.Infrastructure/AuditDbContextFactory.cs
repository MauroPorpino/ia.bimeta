using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IaBimeta.Audit.Infrastructure;

/// <summary>
/// Usada só pelas ferramentas do EF (<c>dotnet ef migrations add</c>). Gerar uma migration não abre conexão: a
/// string abaixo não aponta para nenhum banco real e nenhum segredo é necessário.
/// </summary>
public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=design_time_only",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AuditDbContext.Schema))
            .Options;

        return new AuditDbContext(options, new RequestContextHolder());
    }
}
