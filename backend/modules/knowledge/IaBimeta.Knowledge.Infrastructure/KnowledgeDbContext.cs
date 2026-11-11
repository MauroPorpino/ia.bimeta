using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IaBimeta.Knowledge.Infrastructure;

/// <summary>
/// O contexto do esquema <c>knowledge</c>. Não mapeia entidades: a tabela do índice usa o tipo <c>vector</c> do pgvector e é acessada por SQL parametrizado (<see cref="PostgresSemanticStore"/>); o contexto existe para
/// ter as migrations e o histórico PRÓPRIOS do módulo, como os demais (ADR 0005). A migration cria a extensão <c>vector</c>: o PostgreSQL de destino PRECISA ter o pgvector (a imagem <c>pgvector/pgvector:pg17</c> do Compose tem).
/// </summary>
public sealed class KnowledgeDbContext : DbContext
{
    public const string Schema = "knowledge";

    public KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema(Schema);
}

/// <summary>A fábrica de tempo de design (dotnet ef): só para gerar migrations, sem abrir conexão.</summary>
public sealed class KnowledgeDbContextFactory : IDesignTimeDbContextFactory<KnowledgeDbContext>
{
    public KnowledgeDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only", npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", KnowledgeDbContext.Schema))
            .Options);
}
