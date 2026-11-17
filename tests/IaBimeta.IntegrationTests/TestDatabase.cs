using Microsoft.Extensions.Configuration;
using Npgsql;

namespace IaBimeta.IntegrationTests;

/// <summary>
/// Localiza o PostgreSQL de TESTE. A string de conexão nunca fica no repositório: vem da variável de ambiente
/// <c>IABIMETA_TEST_CONNECTION</c> ou do segredo de usuário <c>ConnectionStrings:TenancyTests</c>.
/// Sem banco configurado os testes FALHAM (não são ignorados): isolamento não se declara sem PostgreSQL real.
/// </summary>
internal static class TestDatabase
{
    public const string EnvironmentVariable = "IABIMETA_TEST_CONNECTION";
    public const string SecretKey = "ConnectionStrings:TenancyTests";

    private static readonly Lazy<string> Resolved = new(Resolve);

    public static string ConnectionString => Resolved.Value;

    /// <summary>
    /// Teto de conexões por pool nos testes. O padrão do Npgsql (100) é igual ao <c>max_connections</c> do PostgreSQL de teste:
    /// uma rajada de requisições simultâneas abre conexões até esgotar o servidor e as ociosas seguram as vagas por minutos,
    /// derrubando os testes seguintes. Com o teto, a rajada espera na fila do pool (Timeout) em vez de tomar o servidor.
    /// O teto vale por pool (o do EF, o das conexões diretas dos testes e o do processo filho do Worker); <c>max_connections</c> não é alterado.
    /// </summary>
    private const int MaximumPoolSize = 40;

    private static string ComTeto(string conexao)
    {
        var construtor = new NpgsqlConnectionStringBuilder(conexao);
        if (!conexao.Contains("Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            construtor.MaxPoolSize = MaximumPoolSize;
        }

        return construtor.ConnectionString;
    }

    private static string Resolve()
    {
        var doAmbiente = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(doAmbiente))
        {
            return ComTeto(doAmbiente);
        }

        var configuracao = new ConfigurationBuilder()
            .AddUserSecrets(typeof(TestDatabase).Assembly, optional: true)
            .Build();
        var segredo = configuracao[SecretKey];
        if (!string.IsNullOrWhiteSpace(segredo))
        {
            return ComTeto(segredo);
        }

        throw new InvalidOperationException(
            $"PostgreSQL de teste não configurado. Defina a variável de ambiente {EnvironmentVariable} " +
            $"ou o segredo de usuário '{SecretKey}' (dotnet user-secrets set ... --project tests/IaBimeta.IntegrationTests).");
    }
}
