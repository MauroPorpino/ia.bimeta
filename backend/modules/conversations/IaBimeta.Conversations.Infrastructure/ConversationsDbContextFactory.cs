using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// Usada só pelas ferramentas do EF (<c>dotnet ef migrations add</c>). Gerar uma migration não abre conexão: a
/// string abaixo não aponta para nenhum banco real e nenhum segredo é necessário.
/// </summary>
public sealed class ConversationsDbContextFactory : IDesignTimeDbContextFactory<ConversationsDbContext>
{
    public ConversationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ConversationsDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=design_time_only",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ConversationsDbContext.Schema))
            .Options;

        return new ConversationsDbContext(options, new RequestContextHolder());
    }
}
