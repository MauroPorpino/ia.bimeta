using System.Security.Cryptography;
using System.Text;
using IaBimeta.Identity.Application;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Tests;

/// <summary>E1-R: peças puras do endurecimento do Embedded (formato dos tokens de sessão, limite de taxa por identidade e escopo da sessão embutida).</summary>
public sealed class EmbeddedHardeningTests
{
    [Fact]
    public void O_token_embutido_tem_prefixo_proprio_e_hash_com_separacao_de_dominio()
    {
        var (token, hash) = SessionTokens.GenerateEmbedded();

        Assert.StartsWith("ibe_", token);
        Assert.Equal(4 + 43, token.Length);
        Assert.True(SessionTokens.TryHash(token, out var apresentado, out var embutido));
        Assert.True(embutido);
        Assert.Equal(hash, apresentado);

        // O SHA-256 SIMPLES do token (o que uma versão antiga da API calcularia) não é o hash guardado.
        Assert.NotEqual(SHA256.HashData(Encoding.UTF8.GetBytes(token)), hash);
        Assert.NotEqual(SHA256.HashData(Encoding.UTF8.GetBytes("ibs_" + token[4..])), hash);
    }

    [Fact]
    public void O_token_interativo_continua_com_o_formato_e_o_hash_de_sempre_e_nao_vira_embutido()
    {
        var (token, hash) = SessionTokens.Generate();

        Assert.StartsWith("ibs_", token);
        Assert.True(SessionTokens.TryHash(token, out var apresentado, out var embutido));
        Assert.False(embutido);
        Assert.Equal(hash, apresentado);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), hash);

        // Trocar o prefixo muda o formato E o hash: nunca o mesmo token lido como o outro tipo.
        Assert.True(SessionTokens.TryHash("ibe_" + token[4..], out var comoEmbutido, out _));
        Assert.NotEqual(hash, comoEmbutido);
    }

    [Theory]
    [InlineData("ibx_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("ibe_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("ibe_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("ibe_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!")]
    [InlineData("IBE_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("")]
    [InlineData(null)]
    public void Token_fora_do_formato_nao_chega_ao_banco(string? token)
    {
        Assert.False(SessionTokens.TryHash(token, out _, out _));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(50, false)]
    [InlineData(100, true)]
    [InlineData(5000, true)]
    [InlineData(30000, true)]
    [InlineData(30001, false)]
    public void O_tempo_maximo_de_espera_por_trava_tem_piso_e_teto_e_o_padrao_vale(int milissegundos, bool valido)
    {
        // Sem teto de espera, as requisições enfileiradas na mesma linha seguram uma conexão cada e esgotam o pool.
        new IdentityOptions().ValidateEmbedded();
        var opcoes = new IdentityOptions { EmbeddedLockTimeout = TimeSpan.FromMilliseconds(milissegundos) };

        if (valido)
        {
            opcoes.ValidateEmbedded();
        }
        else
        {
            Assert.Throws<InvalidOperationException>(opcoes.ValidateEmbedded);
        }
    }

    private sealed class Relogio : TimeProvider
    {
        public DateTimeOffset Agora { get; set; } = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Agora;
    }

    private static IntegrationRateLimiter Limitador(Relogio relogio, int porChave = 3, int porConexao = 5) =>
        new(relogio, new IdentityOptions { IntegrationPermitsPerKey = porChave, IntegrationPermitsPerConnection = porConexao, IntegrationRateWindow = TimeSpan.FromMinutes(1) });

    [Fact]
    public void So_as_falhas_gastam_a_cota_da_chave_e_a_janela_vira()
    {
        var relogio = new Relogio();
        var limitador = Limitador(relogio);
        var chave = Guid.NewGuid();

        for (var i = 0; i < 20; i++)
        {
            limitador.ThrowIfKeyThrottled(chave); // consultar não gasta
        }

        for (var i = 0; i < 3; i++)
        {
            limitador.RecordKeyFailure(chave);
        }

        var excecao = Assert.Throws<EmbeddedRateLimitedException>(() => limitador.ThrowIfKeyThrottled(chave));
        Assert.True(excecao.RetryAfter > TimeSpan.Zero && excecao.RetryAfter <= TimeSpan.FromMinutes(1));
        limitador.ThrowIfKeyThrottled(Guid.NewGuid()); // outra chave não é afetada

        relogio.Agora = relogio.Agora.AddMinutes(1).AddSeconds(1);
        limitador.ThrowIfKeyThrottled(chave); // a janela virou
    }

    [Fact]
    public void A_cota_da_conexao_conta_as_chamadas_autenticadas_e_nao_depende_da_chave()
    {
        var relogio = new Relogio();
        var limitador = Limitador(relogio, porConexao: 5);
        var conexao = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            limitador.CheckConnection(conexao);
        }

        Assert.Throws<EmbeddedRateLimitedException>(() => limitador.CheckConnection(conexao));
        limitador.CheckConnection(Guid.NewGuid()); // outra conexão tem a própria cota
    }

    [Fact]
    public void O_escopo_embutido_limita_organizacao_e_modo_e_nunca_atinge_sessao_interativa_nem_contexto_de_sistema()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var comoSistema = new RequestContext(tenant, "teste");
        var interativo = new RequestContext(tenant, "teste", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<PermissionGrant>()));
        var embutido = new RequestContext(tenant, "teste", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<PermissionGrant>(), new EmbeddedScope(orgA, CustomerOnly: false)));
        var soCliente = new RequestContext(tenant, "teste", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<PermissionGrant>(), new EmbeddedScope(orgA, CustomerOnly: true)));

        Assert.False(comoSistema.IsOutsideEmbeddedScope(orgB));
        Assert.False(interativo.IsOutsideEmbeddedScope(orgB));
        Assert.False(embutido.IsOutsideEmbeddedScope(orgA));
        Assert.True(embutido.IsOutsideEmbeddedScope(orgB));
        Assert.True(embutido.IsOutsideEmbeddedScope(null)); // sem organização conhecida, fora (nega por padrão)
        Assert.True(soCliente.IsOutsideEmbeddedScope(orgA)); // CustomerOnly: nada, nem a própria organização
        Assert.True(soCliente.IsOutsideEmbeddedScope(orgB));
    }
}
