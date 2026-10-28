using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IaBimeta.ContactCenter.Infrastructure;

/// <summary>
/// Usada só pelas ferramentas do EF (<c>dotnet ef migrations add</c>). Gerar uma migration não abre conexão: a
/// string abaixo não aponta para nenhum banco real e nenhum segredo é necessário.
/// </summary>
public sealed class ContactCenterDbContextFactory : IDesignTimeDbContextFactory<ContactCenterDbContext>
{
    public ContactCenterDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ContactCenterDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=design_time_only",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ContactCenterDbContext.Schema))
            .Options;

        return new ContactCenterDbContext(options, new RequestContextHolder());
    }
}
