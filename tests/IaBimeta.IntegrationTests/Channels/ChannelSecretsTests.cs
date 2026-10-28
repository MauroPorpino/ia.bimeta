using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Domain;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.Channels.Presentation;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Channels;

/// <summary>
/// As credenciais do canal: guardadas cifradas (AES-256-GCM, chave fora do banco), referenciadas por identificador, só de
/// escrita pela API e ausentes de respostas, logs e auditoria. Valores sintéticos: nenhuma credencial real em teste.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelSecretsTests(PostgresFixture banco)
{
    private const string ValorSintetico = "EAAG-VALOR-SINTETICO-0123456789-abcdef";

    private static string NovaChave() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(ChannelSecretOptions.KeySizeBytes));

    private static AesGcmSecretProtector Protetor(string ativa = "k1", params (string Id, string Chave)[] chaves)
    {
        var opcoes = new ChannelSecretOptions { ActiveKeyId = ativa };
        foreach (var (id, chave) in chaves.Length > 0 ? chaves : new[] { ("k1", NovaChave()) })
        {
            opcoes.Keys[id] = chave;
        }

        return new AesGcmSecretProtector(opcoes);
    }

    private static SecretBinding Vinculo(ChannelSecretKind papel = ChannelSecretKind.AccessToken) => new(new TenantId(Guid.NewGuid()), Guid.NewGuid(), papel);

    // A cifra

    [Fact]
    public void Cifra_e_decifra_de_volta_o_valor_inclusive_com_acentos()
    {
        var protetor = Protetor();
        var vinculo = Vinculo();

        foreach (var valor in new[] { ValorSintetico, "x", "çãõ-é-€-日本語-🔑" })
        {
            var cifrado = protetor.Protect(valor, vinculo);
            Assert.Equal(valor, protetor.Unprotect(cifrado, vinculo));
        }
    }

    [Fact]
    public void O_texto_cifrado_nao_contem_o_valor_nem_o_valor_em_base64()
    {
        var cifrado = Protetor().Protect(ValorSintetico, Vinculo());

        Assert.DoesNotContain(ValorSintetico, cifrado.Ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(ValorSintetico)), cifrado.Ciphertext, StringComparison.Ordinal);
        Assert.StartsWith("v1:", cifrado.Ciphertext, StringComparison.Ordinal);
    }

    [Fact]
    public void O_mesmo_valor_cifrado_duas_vezes_gera_textos_diferentes_nonce_aleatorio()
    {
        var protetor = Protetor();
        var vinculo = Vinculo();

        Assert.NotEqual(protetor.Protect(ValorSintetico, vinculo).Ciphertext, protetor.Protect(ValorSintetico, vinculo).Ciphertext);
    }

    [Fact]
    public void O_texto_cifrado_nao_abre_sob_outro_tenant_outra_referencia_ou_outro_papel()
    {
        var protetor = Protetor();
        var vinculo = Vinculo(ChannelSecretKind.AppSecret);
        var cifrado = protetor.Protect(ValorSintetico, vinculo);

        Assert.Throws<AuthenticationTagMismatchException>(() => protetor.Unprotect(cifrado, vinculo with { TenantId = new TenantId(Guid.NewGuid()) }));
        Assert.Throws<AuthenticationTagMismatchException>(() => protetor.Unprotect(cifrado, vinculo with { SecretId = Guid.NewGuid() }));
        Assert.Throws<AuthenticationTagMismatchException>(() => protetor.Unprotect(cifrado, vinculo with { Kind = ChannelSecretKind.AccessToken }));
        Assert.Equal(ValorSintetico, protetor.Unprotect(cifrado, vinculo));
    }

    [Fact]
    public void Texto_cifrado_adulterado_e_detectado()
    {
        var protetor = Protetor();
        var vinculo = Vinculo();
        var cifrado = protetor.Protect(ValorSintetico, vinculo);
        var bruto = Convert.FromBase64String(cifrado.Ciphertext["v1:".Length..]);

        foreach (var posicao in new[] { 0, 12, bruto.Length - 1 }) // nonce, tag e texto
        {
            var adulterado = (byte[])bruto.Clone();
            adulterado[posicao] ^= 0x01;

            Assert.Throws<AuthenticationTagMismatchException>(() =>
                protetor.Unprotect(cifrado with { Ciphertext = "v1:" + Convert.ToBase64String(adulterado) }, vinculo));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("v2:AAAA")]
    [InlineData("v1:nao-e-base64!")]
    [InlineData("v1:AAAA")]
    public void Formato_desconhecido_ou_truncado_e_recusado(string texto)
    {
        var protetor = Protetor();

        Assert.ThrowsAny<CryptographicException>(() => protetor.Unprotect(new ProtectedSecret("k1", texto), Vinculo()));
    }

    [Fact]
    public void A_rotacao_de_chave_cifra_com_a_nova_e_continua_abrindo_o_que_a_antiga_cifrou()
    {
        var k1 = NovaChave();
        var k2 = NovaChave();
        var vinculo = Vinculo();
        var antiga = Protetor("k1", ("k1", k1)).Protect(ValorSintetico, vinculo);

        var comAsDuas = Protetor("k2", ("k1", k1), ("k2", k2));
        var nova = comAsDuas.Protect(ValorSintetico, vinculo);

        Assert.Equal(("k1", "k2"), (antiga.KeyId, nova.KeyId));
        Assert.Equal(ValorSintetico, comAsDuas.Unprotect(antiga, vinculo));
        Assert.Equal(ValorSintetico, comAsDuas.Unprotect(nova, vinculo));
        Assert.Throws<ChannelSecretsNotConfiguredException>(() => Protetor("k2", ("k2", k2)).Unprotect(antiga, vinculo));
    }

    [Fact]
    public void Sem_chave_ativa_ou_com_chave_invalida_a_cifra_falha_fechada_sem_revelar_a_chave()
    {
        var vinculo = Vinculo();
        var semAtiva = new AesGcmSecretProtector(new ChannelSecretOptions());
        var ativaInexistente = Protetor("k9", ("k1", NovaChave()));
        const string chaveCurta = "Q2hhdmUtY3VydGEtZGUtdGVzdGU="; // base64 de uma chave que não tem 32 bytes
        var tamanhoErrado = Protetor("k1", ("k1", chaveCurta));
        var naoBase64 = Protetor("k1", ("k1", "isto não é base64 de jeito nenhum"));

        Assert.Throws<ChannelSecretsNotConfiguredException>(() => semAtiva.Protect(ValorSintetico, vinculo));
        Assert.Throws<ChannelSecretsNotConfiguredException>(() => ativaInexistente.Protect(ValorSintetico, vinculo));
        var curta = Assert.Throws<ChannelSecretsNotConfiguredException>(() => tamanhoErrado.Protect(ValorSintetico, vinculo));
        var invalida = Assert.Throws<ChannelSecretsNotConfiguredException>(() => naoBase64.Protect(ValorSintetico, vinculo));
        Assert.DoesNotContain(chaveCurta, curta.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("isto não é base64", invalida.Message, StringComparison.Ordinal);
    }

    // Os casos de uso

    private async Task<(CenarioDeConversas Cenario, ChannelAccountView Conta, RequestContext Admin)> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var admin = c.Administrador();
        var conta = await c.CriarContaAsync(admin);
        return (c, conta, admin);
    }

    private static Task<ChannelSecretView> Definir(CenarioDeConversas c, RequestContext contexto, Guid conta, ChannelSecretKind papel, string valor) =>
        c.ComoAsync(contexto, sp => sp.GetRequiredService<ChannelAccountService>().SetSecretAsync(conta, papel, valor));

    [Fact]
    public async Task Definir_a_credencial_devolve_so_os_metadados_e_a_conta_lista_a_referencia_sem_o_valor()
    {
        var (c, conta, admin) = await MontarAsync("segredometa");

        var definida = await Definir(c, admin, conta.Id, ChannelSecretKind.AccessToken, ValorSintetico);
        var lida = await c.ComoAsync(admin, sp => sp.GetRequiredService<ChannelAccountService>().GetAsync(conta.Id));
        var lista = await c.ComoAsync(admin, sp => sp.GetRequiredService<ChannelAccountService>().ListAsync());

        Assert.Equal((ChannelSecretKind.AccessToken, 1), (definida.Kind, definida.Version));
        Assert.NotEqual(Guid.Empty, definida.Reference);
        // O banco guarda microssegundos e o relógio do .NET, centenas de nanossegundos: o instante é o mesmo até o milissegundo.
        foreach (var segredo in new[] { Assert.Single(lida!.Secrets), Assert.Single(Assert.Single(lista).Secrets) })
        {
            Assert.Equal((definida.Kind, definida.Reference, definida.Version), (segredo.Kind, segredo.Reference, segredo.Version));
            Assert.Equal(definida.UpdatedAt, segredo.UpdatedAt, TimeSpan.FromMilliseconds(1));
        }

        foreach (var serializado in new[] { JsonSerializer.Serialize(definida), JsonSerializer.Serialize(lida), JsonSerializer.Serialize(lista) })
        {
            Assert.DoesNotContain(ValorSintetico, serializado, StringComparison.Ordinal);
            Assert.DoesNotContain("ciphertext", serializado, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("v1:", serializado, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task O_banco_guarda_so_o_texto_cifrado_e_ele_abre_com_a_chave_para_o_vinculo_certo()
    {
        var (c, conta, admin) = await MontarAsync("segredobanco");
        var definida = await Definir(c, admin, conta.Id, ChannelSecretKind.AppSecret, ValorSintetico);

        var linha = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelsDbContext>().Secrets.AsNoTracking().SingleAsync(s => s.Id == definida.Reference));
        var bruto = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelsDbContext>().Database
            .SqlQueryRaw<string>("select key_id || '|' || ciphertext as \"Value\" from channels.channel_secrets where id = {0}", definida.Reference).SingleAsync());
        var protetor = await c.ComoAsync(c.ContextoDeSistema, sp => Task.FromResult(sp.GetRequiredService<ISecretProtector>()));

        Assert.Equal(PostgresFixture.IdDaChaveDeTeste, linha.KeyId);
        Assert.DoesNotContain(ValorSintetico, bruto, StringComparison.Ordinal);
        Assert.Equal(ValorSintetico, protetor.Unprotect(new ProtectedSecret(linha.KeyId, linha.Ciphertext), new SecretBinding(c.Tenant.TenantId, linha.Id, linha.Kind)));
        Assert.ThrowsAny<CryptographicException>(() =>
            protetor.Unprotect(new ProtectedSecret(linha.KeyId, linha.Ciphertext), new SecretBinding(new TenantId(Guid.NewGuid()), linha.Id, linha.Kind)));
    }

    [Fact]
    public async Task Definir_de_novo_o_mesmo_papel_gira_o_valor_e_mantem_a_referencia()
    {
        var (c, conta, admin) = await MontarAsync("segredogira");
        var primeira = await Definir(c, admin, conta.Id, ChannelSecretKind.AccessToken, ValorSintetico);
        var antes = await Cifrado(c, primeira.Reference);

        var segunda = await Definir(c, admin, conta.Id, ChannelSecretKind.AccessToken, "OUTRO-VALOR-SINTETICO-9876543210");
        var depois = await Cifrado(c, segunda.Reference);

        Assert.Equal(primeira.Reference, segunda.Reference);
        Assert.Equal((1, 2), (primeira.Version, segunda.Version));
        Assert.NotEqual(antes, depois);
        Assert.Single(Assert.Single(await c.ComoAsync(admin, sp => sp.GetRequiredService<ChannelAccountService>().ListAsync())).Secrets);
        var protetor = await c.ComoAsync(c.ContextoDeSistema, sp => Task.FromResult(sp.GetRequiredService<ISecretProtector>()));
        Assert.Equal("OUTRO-VALOR-SINTETICO-9876543210", protetor.Unprotect(new ProtectedSecret(PostgresFixture.IdDaChaveDeTeste, depois), new SecretBinding(c.Tenant.TenantId, segunda.Reference, ChannelSecretKind.AccessToken)));
    }

    private static Task<string> Cifrado(CenarioDeConversas c, Guid referencia) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelsDbContext>().Secrets.AsNoTracking().Where(s => s.Id == referencia).Select(s => s.Ciphertext).SingleAsync());

    [Fact]
    public async Task Cada_papel_tem_a_propria_referencia_e_a_conta_lista_todos_em_ordem()
    {
        var (c, conta, admin) = await MontarAsync("segredopapeis");

        var token = await Definir(c, admin, conta.Id, ChannelSecretKind.AccessToken, "valor-sintetico-1");
        var app = await Definir(c, admin, conta.Id, ChannelSecretKind.AppSecret, "valor-sintetico-2");
        var verificacao = await Definir(c, admin, conta.Id, ChannelSecretKind.WebhookVerifyToken, "valor-sintetico-3");

        Assert.Equal(3, new[] { token.Reference, app.Reference, verificacao.Reference }.Distinct().Count());
        var segredos = (await c.ComoAsync(admin, sp => sp.GetRequiredService<ChannelAccountService>().GetAsync(conta.Id)))!.Secrets;
        Assert.Equal(new[] { ChannelSecretKind.AccessToken, ChannelSecretKind.AppSecret, ChannelSecretKind.WebhookVerifyToken }, segredos.Select(s => s.Kind));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" com-espaco-na-ponta")]
    [InlineData("com-quebra\nde-linha")]
    public async Task Valor_invalido_e_recusado_sem_gravar_e_sem_reproduzir_o_valor(string valor)
    {
        var (c, conta, admin) = await MontarAsync("segredoinvalido");

        var erro = await Assert.ThrowsAsync<ChannelsValidationException>(() => Definir(c, admin, conta.Id, ChannelSecretKind.AccessToken, valor));

        Assert.DoesNotContain("quebra", erro.Message, StringComparison.Ordinal);
        Assert.Empty((await c.ComoAsync(admin, sp => sp.GetRequiredService<ChannelAccountService>().GetAsync(conta.Id)))!.Secrets);
    }

    [Fact]
    public async Task Papel_inexistente_e_recusado()
    {
        var (c, conta, admin) = await MontarAsync("segredopapelinvalido");

        await Assert.ThrowsAsync<ChannelsValidationException>(() => Definir(c, admin, conta.Id, (ChannelSecretKind)99, ValorSintetico));
    }

    [Fact]
    public async Task A_auditoria_registra_que_a_credencial_mudou_e_qual_papel_mas_nunca_o_valor()
    {
        var (c, conta, admin) = await MontarAsync("segredoauditoria");

        await Definir(c, admin, conta.Id, ChannelSecretKind.AccessToken, ValorSintetico);
        await Definir(c, admin, conta.Id, ChannelSecretKind.AccessToken, "OUTRO-VALOR-SINTETICO-9876543210");

        var eventos = await c.AuditoriaAsync("channels.secret.set", $"{conta.Id}:AccessToken");
        Assert.Equal(2, eventos.Count);
        Assert.All(eventos, e => Assert.Equal(admin.Principal!.UserId, e.ActorUserId));
        var tudo = JsonSerializer.Serialize(await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<Audit.Application.AuditQueryService>().ListAsync(200)));
        Assert.DoesNotContain(ValorSintetico, tudo, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTRO-VALOR", tudo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Definir_o_mesmo_papel_ao_mesmo_tempo_nunca_cria_duas_credenciais()
    {
        var (c, conta, admin) = await MontarAsync("segredocorrida");

        var resultados = await Task.WhenAll(
            Tentar(() => Definir(c, admin, conta.Id, ChannelSecretKind.AccessToken, "valor-sintetico-a")),
            Tentar(() => Definir(c, admin, conta.Id, ChannelSecretKind.AccessToken, "valor-sintetico-b")));

        Assert.All(resultados, r => Assert.True(r is null or ChannelsConflictException, r?.ToString()));
        var segredo = Assert.Single((await c.ComoAsync(admin, sp => sp.GetRequiredService<ChannelAccountService>().GetAsync(conta.Id)))!.Secrets);
        Assert.Equal(ChannelSecretKind.AccessToken, segredo.Kind);
    }

    private static async Task<Exception?> Tentar(Func<Task> acao)
    {
        try
        {
            await acao();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void O_pedido_de_credencial_nao_expoe_o_valor_no_texto_nem_em_log()
    {
        var pedido = new SetChannelSecretRequest(ValorSintetico);

        Assert.DoesNotContain(ValorSintetico, pedido.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ValorSintetico, $"{pedido}", StringComparison.Ordinal);
    }

    [Fact]
    public void Nenhuma_visao_do_modulo_tem_propriedade_para_o_valor_da_credencial()
    {
        var nomes = new[] { typeof(ChannelSecretView), typeof(ChannelAccountView), typeof(ChannelInstanceView) }
            .SelectMany(t => t.GetProperties().Select(p => p.Name))
            .ToList();

        Assert.DoesNotContain(nomes, n => n.Contains("Value", StringComparison.OrdinalIgnoreCase) || n.Contains("Ciphertext", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Plaintext", StringComparison.OrdinalIgnoreCase) || n.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }
}
