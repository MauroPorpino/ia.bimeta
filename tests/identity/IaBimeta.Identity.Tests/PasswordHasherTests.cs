using IaBimeta.Identity.Application;

namespace IaBimeta.Identity.Tests;

public class PasswordHasherTests
{
    private static PasswordHasher Hasher(int iteracoes = IdentityOptions.MinimumPasswordHashIterations) =>
        new(new IdentityOptions { PasswordHashIterations = iteracoes });

    [Fact]
    public void O_padrao_de_producao_e_600_mil_iteracoes()
    {
        Assert.Equal(600_000, IdentityOptions.DefaultPasswordHashIterations);
        Assert.Equal(600_000, new IdentityOptions().PasswordHashIterations);
        Assert.Equal(600_000, new PasswordHasher(new IdentityOptions()).Iterations);
    }

    [Fact]
    public void Hash_com_o_padrao_de_producao_verifica_a_senha_certa_e_recusa_a_errada()
    {
        var hasher = new PasswordHasher(new IdentityOptions());

        var hash = hasher.Hash(Senhas.Valida);

        Assert.StartsWith("pbkdf2-sha256$600000$", hash);
        Assert.Equal(PasswordVerification.Success, hasher.Verify(Senhas.Valida, hash));
        Assert.Equal(PasswordVerification.Failed, hasher.Verify(Senhas.Outra, hash));
    }

    [Fact]
    public void O_hash_nao_contem_a_senha_e_tem_o_formato_com_parametros()
    {
        var hash = Hasher().Hash(Senhas.Valida);

        Assert.DoesNotContain(Senhas.Valida, hash);
        var partes = hash.Split('$');
        Assert.Equal(4, partes.Length);
        Assert.Equal("pbkdf2-sha256", partes[0]);
        Assert.Equal("10000", partes[1]);
        Assert.Equal(PasswordHasher.SaltSize, Convert.FromBase64String(partes[2]).Length);
        Assert.Equal(PasswordHasher.HashSize, Convert.FromBase64String(partes[3]).Length);
    }

    [Fact]
    public void Cada_hash_tem_salt_proprio_mesmo_para_a_mesma_senha()
    {
        var hasher = Hasher();

        var primeiro = hasher.Hash(Senhas.Valida);
        var segundo = hasher.Hash(Senhas.Valida);

        Assert.NotEqual(primeiro, segundo);
        Assert.NotEqual(primeiro.Split('$')[2], segundo.Split('$')[2]);
        Assert.Equal(PasswordVerification.Success, hasher.Verify(Senhas.Valida, primeiro));
        Assert.Equal(PasswordVerification.Success, hasher.Verify(Senhas.Valida, segundo));
    }

    [Fact]
    public void Senha_diferente_ou_apenas_com_outra_caixa_e_recusada()
    {
        var hasher = Hasher();
        var hash = hasher.Hash("Senha-Longa-123!");

        Assert.Equal(PasswordVerification.Failed, hasher.Verify("senha-longa-123!", hash));
        Assert.Equal(PasswordVerification.Failed, hasher.Verify("Senha-Longa-123", hash));
        Assert.Equal(PasswordVerification.Failed, hasher.Verify(string.Empty, hash));
    }

    [Fact]
    public void Hash_adulterado_e_recusado()
    {
        var hasher = Hasher();
        var hash = hasher.Hash(Senhas.Valida);
        var partes = hash.Split('$');
        var bytes = Convert.FromBase64String(partes[3]);
        bytes[0] ^= 0x01;

        var adulterado = $"{partes[0]}${partes[1]}${partes[2]}${Convert.ToBase64String(bytes)}";

        Assert.Equal(PasswordVerification.Failed, hasher.Verify(Senhas.Valida, adulterado));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("texto-qualquer")]
    [InlineData("pbkdf2-sha256$10000$abc")]
    [InlineData("outro-algoritmo$10000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$xx$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$10000$nao-e-base64$tambem-nao")]
    [InlineData("pbkdf2-sha256$0$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void Hash_ausente_ou_malformado_nunca_confere(string? armazenado)
    {
        Assert.Equal(PasswordVerification.Failed, Hasher().Verify(Senhas.Valida, armazenado));
    }

    [Fact]
    public void Numero_absurdo_de_iteracoes_no_hash_armazenado_e_recusado_sem_travar()
    {
        var hash = $"pbkdf2-sha256$2000000000$AAAAAAAAAAAAAAAAAAAAAA==${Convert.ToBase64String(new byte[32])}";

        Assert.Equal(PasswordVerification.Failed, Hasher().Verify(Senhas.Valida, hash));
    }

    [Fact]
    public void Hash_com_menos_iteracoes_que_as_atuais_confere_e_pede_regravacao()
    {
        var antigo = Hasher(10_000).Hash(Senhas.Valida);
        var atual = Hasher(20_000);

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, atual.Verify(Senhas.Valida, antigo));
        Assert.Equal(PasswordVerification.Failed, atual.Verify(Senhas.Outra, antigo));
    }

    [Fact]
    public void Hash_com_mais_iteracoes_que_as_atuais_confere_sem_pedir_regravacao()
    {
        var forte = Hasher(30_000).Hash(Senhas.Valida);

        Assert.Equal(PasswordVerification.Success, Hasher(10_000).Verify(Senhas.Valida, forte));
    }

    [Fact]
    public void Iteracoes_abaixo_do_piso_sao_recusadas_na_configuracao()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Hasher(IdentityOptions.MinimumPasswordHashIterations - 1));
    }
}
