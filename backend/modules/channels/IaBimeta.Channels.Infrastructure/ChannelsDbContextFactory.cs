using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IaBimeta.Channels.Infrastructure;

/// <summary>
/// Usada só pelas ferramentas do EF (<c>dotnet ef migrations add</c>). Gerar uma migration não abre conexão: a
/// string abaixo não aponta para nenhum banco real e nenhum segredo é necessário.
/// </summary>
public sealed class ChannelsDbContextFactory : IDesignTimeDbContextFactory<ChannelsDbContext>
{
    public ChannelsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChannelsDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=design_time_only",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ChannelsDbContext.Schema))
            .Options;

        return new ChannelsDbContext(options, new RequestContextHolder());
    }
}
