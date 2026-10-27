using Microsoft.Extensions.Configuration;

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

    private static string Resolve()
    {
        var doAmbiente = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(doAmbiente))
        {
            return doAmbiente;
        }

        var configuracao = new ConfigurationBuilder()
            .AddUserSecrets(typeof(TestDatabase).Assembly, optional: true)
            .Build();
        var segredo = configuracao[SecretKey];
        if (!string.IsNullOrWhiteSpace(segredo))
        {
            return segredo;
        }

        throw new InvalidOperationException(
            $"PostgreSQL de teste não configurado. Defina a variável de ambiente {EnvironmentVariable} " +
            $"ou o segredo de usuário '{SecretKey}' (dotnet user-secrets set ... --project tests/IaBimeta.IntegrationTests).");
    }
}
