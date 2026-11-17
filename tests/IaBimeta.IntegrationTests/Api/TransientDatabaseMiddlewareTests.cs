using IaBimeta.Api.RequestContexts;
using Npgsql;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// E1-R, terceira rodada: o que o host traduz em 503 e o que continua sendo 500. SÓ a falha em que o banco comprovadamente não executou o comando; falha de programação e resultado ambíguo nunca viram 503.
/// </summary>
public sealed class TransientDatabaseMiddlewareTests
{
    private static PostgresException Postgres(string estado) => new("msg", "ERROR", "ERROR", estado);

    [Theory]
    [InlineData("40P01")]
    [InlineData("40001")]
    [InlineData("55P03")]
    [InlineData("53300")]
    [InlineData("57P03")]
    public void Falha_transitoria_do_banco_e_reconhecida_diretamente_embrulhada_ou_agregada(string estado)
    {
        Assert.True(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(Postgres(estado)));
        Assert.True(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(new InvalidOperationException("embrulho", Postgres(estado))));
        Assert.True(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(new AggregateException(Postgres(estado))));
    }

    [Fact]
    public void O_pool_local_esgotado_do_Npgsql_e_reconhecido_mesmo_embrulhado_pelo_EF_e_o_tempo_de_comando_nao()
    {
        var pool = new NpgsqlException("The connection pool has been exhausted, either raise Max Pool Size or Timeout", new TimeoutException("The operation has timed out."));
        Assert.True(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(pool));
        Assert.True(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(new InvalidOperationException("An exception has been raised that is likely due to a transient failure.", pool)));

        // Tempo esgotado de um comando em andamento: o resultado é ambíguo (pode ter executado).
        Assert.False(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(new NpgsqlException("Exception while reading from stream", new TimeoutException("Timeout during reading attempt"))));
    }

    [Theory]
    [InlineData("23505")] // violação de restrição única: erro do dado, não contenção
    [InlineData("42P01")] // tabela inexistente: erro de programação ou de esquema
    [InlineData("22001")]
    public void Erro_de_dado_ou_de_programacao_do_banco_nao_vira_503(string estado)
    {
        Assert.False(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(Postgres(estado)));
    }

    [Fact]
    public void Falha_de_programacao_e_resultado_ambiguo_nunca_viram_503()
    {
        Assert.False(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(new InvalidOperationException("bug")));
        Assert.False(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(new NullReferenceException()));
        Assert.False(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(new TimeoutException("qualquer outro tempo esgotado")));
        Assert.False(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(new NpgsqlException("queda no meio do comando", new IOException()))); // ambíguo: o comando pode ter executado
        Assert.False(TransientDatabaseMiddleware.IsDefinitelyNotExecuted(new AggregateException(new InvalidOperationException("bug"), Postgres("40P01"))));
    }
}
