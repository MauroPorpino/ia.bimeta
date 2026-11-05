using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IaBimeta.Agents.Infrastructure;

/// <summary>
/// Usada só pelas ferramentas do EF (<c>dotnet ef migrations add</c>). Gerar uma migration não abre conexão: a string abaixo não aponta para nenhum banco real e nenhum segredo é necessário.
/// </summary>
public sealed class AgentsDbContextFactory : IDesignTimeDbContextFactory<AgentsDbContext>
{
    public AgentsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AgentsDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=design_time_only",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AgentsDbContext.Schema))
            .Options;

        return new AgentsDbContext(options, new RequestContextHolder());
    }
}
