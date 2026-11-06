using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.Api.Provisioning;
using IaBimeta.Identity.Domain;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// Troca de senha pelo próprio usuário, redefinição por token de uso único e revogação de sessões, pela API real com
/// PostgreSQL: o mesmo mecanismo de autenticação (sessões revogáveis, PBKDF2, bloqueio por tentativas), sem um segundo sistema.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SenhaESessoesApiTests(PostgresFixture banco)
{
    private const string NovaSenha = "Outra-Senha-Longa-456!";
    private const string MensagemDaRecusa = "O token de redefinição é inválido ou expirou.";

    private sealed record Cenario(TenantInfo Tenant, UsuarioCriado Admin, OrganizationView Organizacao, UsuarioCriado Usuario);

    private sealed record EmissaoDto(string Token, DateTimeOffset ExpiresAt);

    private sealed record RevogadasDto(int Revoked);

    private sealed record ProblemaDto(string? Title, int Status, string? Detail);

    private async Task<Cenario> MontarAsync(string prefixo)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        var organizacao = await banco.CriarOrganizacaoAsync(tenant, "Organização de teste");
        var usuario = await banco.CriarUsuarioAsync(tenant, organizacao.Id, Role.User);
        return new Cenario(tenant, admin, organizacao, usuario);
    }

    private static Dictionary<string, string?> Configuracao(Action<Dictionary<string, string?>>? ajustar = null)
    {
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        configuracao["Identity:LoginRateLimit:PermitLimit"] = "1000"; // o limite por origem não é o assunto destes testes
        ajustar?.Invoke(configuracao);
        return configuracao;
    }

    private static Task<HttpResponseMessage> TrocarAsync(ApiServer api, string token, string? atual, string? nova) =>
        api.EnviarAsync(token, HttpMethod.Put, "/auth/me/password", new { currentPassword = atual, newPassword = nova });

    private static Task<HttpResponseMessage> EmitirAsync(ApiServer api, string token, Cenario c, Guid? usuario = null) =>
        api.EnviarAsync(token, HttpMethod.Post, $"/organizations/{c.Organizacao.Id}/users/{usuario ?? c.Usuario.Id}/password-reset");

    private static async Task<string> EmitirOkAsync(ApiServer api, string token, Cenario c)
    {
        using var resposta = await EmitirAsync(api, token, c);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<EmissaoDto>())!.Token;
    }

    private static Task<HttpResponseMessage> UsarAsync(ApiServer api, string tenant, string? email, string? token, string? novaSenha) =>
        api.Client.PostAsJsonAsync("/auth/password-reset/redeem", new { tenant, email, token, newPassword = novaSenha });

    private static Task<HttpResponseMessage> EntrarAsync(ApiServer api, UsuarioCriado usuario, string senha) =>
        api.Client.PostAsJsonAsync("/auth/login", new { tenant = usuario.TenantSlug, email = usuario.Email, password = senha });

    private static async Task<ProblemaDto> ProblemaAsync(HttpResponseMessage resposta) =>
        (await resposta.Content.ReadFromJsonAsync<ProblemaDto>())!;

    private static async Task<List<EventoDto>> EventosAsync(ApiServer api, string token, string operacao) =>
        await api.LerAsync<List<EventoDto>>(token, $"/audit-events?take=200&operation={operacao}");

    // Trocar a própria senha

    [Fact]
    public async Task Trocar_a_propria_senha_exige_a_atual_revoga_as_outras_sessoes_e_mantem_a_que_trocou()
    {
        var c = await MontarAsync("troca");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var adminToken = await api.LoginAsync(c.Admin);
        var atual = await api.LoginAsync(c.Usuario);
        var outra = await api.LoginAsync(c.Usuario);

        using var troca = await TrocarAsync(api, atual, c.Usuario.Senha, NovaSenha);

        Assert.Equal(HttpStatusCode.NoContent, troca.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(atual, HttpMethod.Get, "/auth/me")); // a sessão em uso continua
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(outra, HttpMethod.Get, "/auth/me")); // as outras caem
        using (var antiga = await EntrarAsync(api, c.Usuario, c.Usuario.Senha))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, antiga.StatusCode);
        }

        using (var nova = await EntrarAsync(api, c.Usuario, NovaSenha))
        {
            Assert.Equal(HttpStatusCode.OK, nova.StatusCode);
        }

        var trilha = await EventosAsync(api, adminToken, "identity.password.change");
        var evento = Assert.Single(trilha);
        Assert.Equal("Success", evento.Outcome);
        Assert.Equal(c.Usuario.Id, evento.ActorUserId);
        Assert.DoesNotContain(NovaSenha, JsonSerializer.Serialize(trilha));
    }

    [Fact]
    public async Task Senha_atual_errada_conta_no_bloqueio_do_login_e_a_certa_tambem_responde_429()
    {
        var c = await MontarAsync("atual");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var token = await api.LoginAsync(c.Usuario);

        for (var i = 0; i < 5; i++)
        {
            using var errada = await TrocarAsync(api, token, "senha-errada-123", NovaSenha);
            Assert.Equal(HttpStatusCode.BadRequest, errada.StatusCode);
        }

        using var certa = await TrocarAsync(api, token, c.Usuario.Senha, NovaSenha);
        Assert.Equal(HttpStatusCode.TooManyRequests, certa.StatusCode);
        Assert.True(certa.Headers.Contains("Retry-After"));
        using var login = await EntrarAsync(api, c.Usuario, c.Usuario.Senha);
        Assert.Equal(HttpStatusCode.TooManyRequests, login.StatusCode); // é o mesmo bloqueio do login
    }

    [Fact]
    public async Task Nova_senha_fora_da_politica_ou_igual_a_atual_e_recusada_sem_bloquear_ninguem()
    {
        var c = await MontarAsync("politica");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var token = await api.LoginAsync(c.Usuario);

        for (var i = 0; i < 7; i++)
        {
            using var curta = await TrocarAsync(api, token, c.Usuario.Senha, "curta");
            Assert.Equal(HttpStatusCode.BadRequest, curta.StatusCode);
        }

        using var igual = await TrocarAsync(api, token, c.Usuario.Senha, c.Usuario.Senha);
        using var vazia = await TrocarAsync(api, token, c.Usuario.Senha, null);
        Assert.Equal(HttpStatusCode.BadRequest, igual.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, vazia.StatusCode);

        using var certa = await TrocarAsync(api, token, c.Usuario.Senha, NovaSenha); // nada disso virou tentativa de adivinhar a senha
        Assert.Equal(HttpStatusCode.NoContent, certa.StatusCode);
    }

    [Fact]
    public async Task Sem_sessao_nenhuma_das_rotas_novas_autenticadas_responde()
    {
        var c = await MontarAsync("anonimo");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());

        using var troca = await TrocarAsync(api, "ibs_invalido", c.Usuario.Senha, NovaSenha);
        using var emissao = await EmitirAsync(api, "ibs_invalido", c);
        using var revogacao = await api.EnviarAsync(null, HttpMethod.Post, $"/organizations/{c.Organizacao.Id}/users/{c.Usuario.Id}/sessions/revoke");

        Assert.Equal(HttpStatusCode.Unauthorized, troca.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, emissao.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revogacao.StatusCode);
    }

    // Redefinição por token

    [Fact]
    public async Task O_administrador_emite_o_usuario_redefine_as_sessoes_caem_e_o_token_nao_vale_de_novo()
    {
        var c = await MontarAsync("fluxo");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var adminToken = await api.LoginAsync(c.Admin);
        var s1 = await api.LoginAsync(c.Usuario);
        var s2 = await api.LoginAsync(c.Usuario);

        using var emissao = await EmitirAsync(api, adminToken, c);
        Assert.Equal(HttpStatusCode.OK, emissao.StatusCode);
        Assert.True(emissao.Headers.CacheControl?.NoStore);
        var emitido = (await emissao.Content.ReadFromJsonAsync<EmissaoDto>())!;
        Assert.StartsWith("ibr_", emitido.Token);
        Assert.InRange(emitido.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));

        using (var uso = await UsarAsync(api, c.Usuario.TenantSlug, c.Usuario.Email, emitido.Token, NovaSenha))
        {
            Assert.Equal(HttpStatusCode.NoContent, uso.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(s1, HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(s2, HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(adminToken, HttpMethod.Get, "/auth/me")); // só as sessões de quem redefiniu
        using (var antiga = await EntrarAsync(api, c.Usuario, c.Usuario.Senha))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, antiga.StatusCode);
        }

        using (var nova = await EntrarAsync(api, c.Usuario, NovaSenha))
        {
            Assert.Equal(HttpStatusCode.OK, nova.StatusCode);
        }

        using var reuso = await UsarAsync(api, c.Usuario.TenantSlug, c.Usuario.Email, emitido.Token, "Mais-Uma-Senha-789!");
        Assert.Equal(HttpStatusCode.BadRequest, reuso.StatusCode);
        Assert.Equal(MensagemDaRecusa, (await ProblemaAsync(reuso)).Detail);

        var emissoes = await EventosAsync(api, adminToken, "identity.password.reset.issue");
        Assert.Equal(c.Admin.Id, Assert.Single(emissoes).ActorUserId);
        var usos = await EventosAsync(api, adminToken, "identity.password.reset.redeem");
        Assert.Contains(usos, e => e.Outcome == "Success" && e.ActorUserId == c.Usuario.Id);
    }

    [Fact]
    public async Task O_token_expira_pela_configuracao_e_um_novo_substitui_o_anterior()
    {
        var c = await MontarAsync("expira");
        var relogio = new RelogioManual();
        await using var api = await ApiServer.StartAsync("Production", Configuracao(k => k["Identity:PasswordResetLifetime"] = "00:05:00"), relogio);
        var adminToken = await api.LoginAsync(c.Admin);
        var vencido = await EmitirOkAsync(api, adminToken, c);

        relogio.Avancar(TimeSpan.FromMinutes(6));

        using (var tarde = await UsarAsync(api, c.Usuario.TenantSlug, c.Usuario.Email, vencido, NovaSenha))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tarde.StatusCode);
        }

        var primeiro = await EmitirOkAsync(api, adminToken, c);
        var segundo = await EmitirOkAsync(api, adminToken, c); // o segundo revoga o primeiro, que ainda não foi usado
        using (var substituido = await UsarAsync(api, c.Usuario.TenantSlug, c.Usuario.Email, primeiro, NovaSenha))
        {
            Assert.Equal(HttpStatusCode.BadRequest, substituido.StatusCode);
        }

        using var valido = await UsarAsync(api, c.Usuario.TenantSlug, c.Usuario.Email, segundo, NovaSenha);
        Assert.Equal(HttpStatusCode.NoContent, valido.StatusCode);
    }

    [Fact]
    public async Task Toda_recusa_tem_a_mesma_resposta_exista_o_tenant_o_usuario_ou_o_token_ou_nao()
    {
        var c = await MontarAsync("enumera");
        var outro = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.User);
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var adminToken = await api.LoginAsync(c.Admin);
        var valido = await EmitirOkAsync(api, adminToken, c);
        var formatoCerto = "ibr_" + new string('A', 43);

        var tentativas = new (string? Tenant, string? Email, string? Token)[]
        {
            (c.Tenant.Slug, c.Usuario.Email, formatoCerto), // usuário existe, token errado
            (c.Tenant.Slug, c.Usuario.Email, "lixo"), // não é token nosso
            (c.Tenant.Slug, "ninguem@teste.local", valido), // e-mail inexistente
            (c.Tenant.Slug, outro.Email, valido), // token certo, para outra pessoa
            ("tenant-que-nao-existe", c.Usuario.Email, valido),
            (c.Tenant.Slug, c.Usuario.Email, null),
            (null, null, null),
            (c.Tenant.Slug, "isto-nao-e-email", valido),
        };

        ProblemaDto? referencia = null;
        foreach (var (tenant, email, token) in tentativas)
        {
            using var resposta = await UsarAsync(api, tenant!, email, token, NovaSenha);
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            var problema = await ProblemaAsync(resposta);
            referencia ??= problema;
            Assert.Equal(referencia, problema);
            Assert.Equal(MensagemDaRecusa, problema.Detail);
        }

        // Nenhuma dessas tentativas gastou o token verdadeiro.
        using var uso = await UsarAsync(api, c.Usuario.TenantSlug, c.Usuario.Email, valido, NovaSenha);
        Assert.Equal(HttpStatusCode.NoContent, uso.StatusCode);
    }

    [Fact]
    public async Task Tentativas_demais_bloqueiam_por_e_mail_com_a_mesma_resposta_e_sem_bloquear_o_login()
    {
        var c = await MontarAsync("tentativas");
        var relogio = new RelogioManual();
        await using var api = await ApiServer.StartAsync("Production", Configuracao(), relogio);
        var adminToken = await api.LoginAsync(c.Admin);
        var valido = await EmitirOkAsync(api, adminToken, c);
        var errado = "ibr_" + new string('B', 43);

        foreach (var email in new[] { c.Usuario.Email, "fantasma@teste.local" })
        {
            for (var i = 0; i < 5; i++)
            {
                using var recusada = await UsarAsync(api, c.Tenant.Slug, email, errado, NovaSenha);
                Assert.Equal(HttpStatusCode.BadRequest, recusada.StatusCode);
            }
        }

        using var existente = await UsarAsync(api, c.Tenant.Slug, c.Usuario.Email, valido, NovaSenha); // até o token certo espera
        using var inexistente = await UsarAsync(api, c.Tenant.Slug, "fantasma@teste.local", valido, NovaSenha);
        Assert.Equal(HttpStatusCode.TooManyRequests, existente.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, inexistente.StatusCode);
        Assert.Equal(await existente.Content.ReadAsStringAsync(), await inexistente.Content.ReadAsStringAsync());
        Assert.True(existente.Headers.Contains("Retry-After"));

        using (var login = await EntrarAsync(api, c.Usuario, c.Usuario.Senha)) // chaves separadas: o login do dono não é bloqueado por quem tenta adivinhar o token
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        relogio.Avancar(TimeSpan.FromMinutes(16));
        using var depois = await UsarAsync(api, c.Tenant.Slug, c.Usuario.Email, valido, NovaSenha);
        Assert.Equal(HttpStatusCode.NoContent, depois.StatusCode);
    }

    [Fact]
    public async Task Token_de_um_tenant_nao_vale_no_outro_mesmo_com_o_mesmo_e_mail()
    {
        var a = await MontarAsync("ta");
        var b = await MontarAsync("tb");
        var mesmoEmailEmB = await banco.CriarUsuarioAsync(b.Tenant, b.Organizacao.Id, Role.User, a.Usuario.Email, "Senha-Do-Tenant-B-1!");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var adminA = await api.LoginAsync(a.Admin);
        var tokenDeA = await EmitirOkAsync(api, adminA, a);

        using (var emB = await UsarAsync(api, b.Tenant.Slug, a.Usuario.Email, tokenDeA, NovaSenha))
        {
            Assert.Equal(HttpStatusCode.BadRequest, emB.StatusCode);
        }

        using (var dePessoaB = await EntrarAsync(api, mesmoEmailEmB, mesmoEmailEmB.Senha))
        {
            Assert.Equal(HttpStatusCode.OK, dePessoaB.StatusCode); // a senha de B continua a dele
        }

        using var emA = await UsarAsync(api, a.Tenant.Slug, a.Usuario.Email, tokenDeA, NovaSenha);
        Assert.Equal(HttpStatusCode.NoContent, emA.StatusCode); // e o token de A segue valendo em A
    }

    [Fact]
    public async Task Usos_simultaneos_do_mesmo_token_tem_um_unico_vencedor()
    {
        var c = await MontarAsync("corrida");
        await using var api = await ApiServer.StartAsync("Production", Configuracao(k => k["Identity:MaxFailedLogins"] = "100"));
        var adminToken = await api.LoginAsync(c.Admin);
        var token = await EmitirOkAsync(api, adminToken, c);

        var senhas = Enumerable.Range(0, 8).Select(i => $"Senha-Concorrente-{i}-Longa!").ToArray();
        var respostas = await Task.WhenAll(senhas.Select(async s =>
        {
            using var resposta = await UsarAsync(api, c.Tenant.Slug, c.Usuario.Email, token, s);
            return (Senha: s, resposta.StatusCode);
        }));

        var vencedores = respostas.Where(r => r.StatusCode == HttpStatusCode.NoContent).ToList();
        Assert.Single(vencedores);
        Assert.All(respostas.Where(r => r.StatusCode != HttpStatusCode.NoContent), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));

        // A senha que vale é a do vencedor, e só ela.
        using var doVencedor = await EntrarAsync(api, c.Usuario, vencedores.Single().Senha);
        Assert.Equal(HttpStatusCode.OK, doVencedor.StatusCode);
        foreach (var perdedora in senhas.Where(s => s != vencedores.Single().Senha))
        {
            using var recusada = await EntrarAsync(api, c.Usuario, perdedora);
            Assert.Equal(HttpStatusCode.Unauthorized, recusada.StatusCode);
        }
    }

    [Fact]
    public async Task Trocar_a_propria_senha_invalida_o_token_de_redefinicao_pendente()
    {
        var c = await MontarAsync("pendente");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var adminToken = await api.LoginAsync(c.Admin);
        var token = await EmitirOkAsync(api, adminToken, c);
        var sessao = await api.LoginAsync(c.Usuario);
        using (var troca = await TrocarAsync(api, sessao, c.Usuario.Senha, NovaSenha))
        {
            Assert.Equal(HttpStatusCode.NoContent, troca.StatusCode);
        }

        using var uso = await UsarAsync(api, c.Tenant.Slug, c.Usuario.Email, token, "Mais-Uma-Senha-789!");

        Assert.Equal(HttpStatusCode.BadRequest, uso.StatusCode);
    }

    // Emissão controlada pelo operador (sem serviço de e-mail)

    [Fact]
    public async Task O_comando_do_operador_emite_o_token_que_funciona_e_na_trilha_fica_so_a_emissao()
    {
        var c = await MontarAsync("operador");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var adminToken = await api.LoginAsync(c.Admin);
        var sessao = await api.LoginAsync(c.Usuario);
        var saida = new StringWriter();

        Assert.Equal(0, await ResetPasswordCommand.IssueAsync(api.Services, c.Tenant.Slug, c.Usuario.Email, saida));

        var token = Regex.Match(saida.ToString(), "ibr_[A-Za-z0-9_-]{43}").Value;
        Assert.NotEmpty(token);
        using (var uso = await UsarAsync(api, c.Tenant.Slug, c.Usuario.Email, token, NovaSenha))
        {
            Assert.Equal(HttpStatusCode.NoContent, uso.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(sessao, HttpMethod.Get, "/auth/me"));
        var emissoes = await EventosAsync(api, adminToken, "identity.password.reset.issue");
        var evento = Assert.Single(emissoes);
        Assert.Equal("operator-cli", evento.ActorOrigin);
        Assert.Null(evento.ActorUserId);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(emissoes));
    }

    [Fact]
    public async Task O_comando_do_operador_recusa_empresa_ou_usuario_inexistente_e_uso_invalido_sem_emitir_nada()
    {
        var c = await MontarAsync("operador2");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());

        var semEmpresa = new StringWriter();
        var semUsuario = new StringWriter();
        Assert.Equal(1, await ResetPasswordCommand.IssueAsync(api.Services, "empresa-que-nao-existe", c.Usuario.Email, semEmpresa));
        Assert.Equal(1, await ResetPasswordCommand.IssueAsync(api.Services, c.Tenant.Slug, "ninguem@teste.local", semUsuario));
        Assert.Equal(2, await ResetPasswordCommand.RunAsync(["--bogus"], new StringWriter()));
        Assert.Equal(2, await ResetPasswordCommand.RunAsync([], new StringWriter()));
        Assert.Equal(2, await ResetPasswordCommand.RunAsync(["--tenant", c.Tenant.Slug], new StringWriter()));

        Assert.DoesNotContain("ibr_", semEmpresa.ToString() + semUsuario);
        var adminToken = await api.LoginAsync(c.Admin);
        Assert.Empty(await EventosAsync(api, adminToken, "identity.password.reset.issue"));
    }

    // Quem pode emitir e revogar

    [Theory]
    [InlineData(Role.User)]
    [InlineData(Role.Manager)]
    public async Task Papeis_sem_gestao_de_usuarios_nao_emitem_nem_revogam_e_a_negacao_entra_na_trilha(Role papel)
    {
        var c = await MontarAsync("papel");
        var quem = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, papel);
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var adminToken = await api.LoginAsync(c.Admin);
        var token = await api.LoginAsync(quem);

        using var emissao = await EmitirAsync(api, token, c);
        using var revogacao = await api.EnviarAsync(token, HttpMethod.Post, $"/organizations/{c.Organizacao.Id}/users/{c.Usuario.Id}/sessions/revoke");

        Assert.Equal(HttpStatusCode.Forbidden, emissao.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revogacao.StatusCode);
        Assert.DoesNotContain("ibr_", await emissao.Content.ReadAsStringAsync());
        Assert.Contains(await EventosAsync(api, adminToken, "identity.password.reset.issue"), e => e.Outcome == "Denied" && e.ActorUserId == quem.Id);
    }

    [Fact]
    public async Task Administrador_de_outra_organizacao_nao_alcanca_o_usuario_e_a_resposta_e_404()
    {
        var c = await MontarAsync("outraorg");
        var outraOrganizacao = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var adminDaOutra = await banco.CriarUsuarioAsync(c.Tenant, outraOrganizacao.Id, Role.OrganizationAdmin);
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var token = await api.LoginAsync(adminDaOutra);

        using var emissao = await EmitirAsync(api, token, c); // pela rota da organização que ele não enxerga
        using var revogacao = await api.EnviarAsync(token, HttpMethod.Post, $"/organizations/{c.Organizacao.Id}/users/{c.Usuario.Id}/sessions/revoke");
        using var daOrganizacaoDele = await api.EnviarAsync(token, HttpMethod.Post, $"/organizations/{outraOrganizacao.Id}/users/{c.Usuario.Id}/password-reset"); // e pela dele, o usuário não pertence

        Assert.Equal(HttpStatusCode.NotFound, emissao.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revogacao.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, daOrganizacaoDele.StatusCode);
    }

    [Fact]
    public async Task Quem_administra_so_uma_organizacao_nao_age_sobre_quem_administra_o_tenant_inteiro()
    {
        var c = await MontarAsync("escopo");
        var adminDaOrganizacao = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.OrganizationAdmin);
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var tokenDoDaOrganizacao = await api.LoginAsync(adminDaOrganizacao);
        var tokenDoDoTenant = await api.LoginAsync(c.Admin);

        using var emissao = await EmitirAsync(api, tokenDoDaOrganizacao, c, c.Admin.Id);
        using var revogacao = await api.EnviarAsync(tokenDoDaOrganizacao, HttpMethod.Post, $"/organizations/{c.Organizacao.Id}/users/{c.Admin.Id}/sessions/revoke");

        Assert.Equal(HttpStatusCode.Forbidden, emissao.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revogacao.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(tokenDoDoTenant, HttpMethod.Get, "/auth/me")); // a sessão do alvo não caiu

        // O contrário vale: quem administra o tenant redefine quem administra a organização.
        using var deCimaParaBaixo = await EmitirAsync(api, tokenDoDoTenant, c, adminDaOrganizacao.Id);
        Assert.Equal(HttpStatusCode.OK, deCimaParaBaixo.StatusCode);
    }

    [Fact]
    public async Task O_administrador_revoga_todas_as_sessoes_do_usuario_e_a_propria_continua()
    {
        var c = await MontarAsync("revoga");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var adminToken = await api.LoginAsync(c.Admin);
        var s1 = await api.LoginAsync(c.Usuario);
        var s2 = await api.LoginAsync(c.Usuario);

        using var resposta = await api.EnviarAsync(adminToken, HttpMethod.Post, $"/organizations/{c.Organizacao.Id}/users/{c.Usuario.Id}/sessions/revoke");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(2, (await resposta.Content.ReadFromJsonAsync<RevogadasDto>())!.Revoked);
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(s1, HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(s2, HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(adminToken, HttpMethod.Get, "/auth/me"));
        using (var denovo = await EntrarAsync(api, c.Usuario, c.Usuario.Senha)) // a senha não mudou: ele só precisa entrar de novo
        {
            Assert.Equal(HttpStatusCode.OK, denovo.StatusCode);
        }

        Assert.Single(await EventosAsync(api, adminToken, "identity.session.revoke-all"));
    }

    // Segredos fora de logs, trilha e banco

    [Fact]
    public async Task Token_e_senhas_nao_aparecem_nos_logs_na_trilha_nem_no_banco_em_claro()
    {
        var c = await MontarAsync("segredo");
        var logs = new CapturaDeLogs();
        await using var api = await ApiServer.StartAsync(
            "Production",
            Configuracao(k => k["Logging:LogLevel:Default"] = "Trace"),
            servicos: servicos => servicos.AddSingleton<ILoggerProvider>(logs));
        var adminToken = await api.LoginAsync(c.Admin);
        var token = await EmitirOkAsync(api, adminToken, c);
        using (var recusada = await UsarAsync(api, c.Tenant.Slug, c.Usuario.Email, "ibr_" + new string('C', 43), NovaSenha))
        {
            Assert.Equal(HttpStatusCode.BadRequest, recusada.StatusCode);
        }

        using (var uso = await UsarAsync(api, c.Tenant.Slug, c.Usuario.Email, token, NovaSenha))
        {
            Assert.Equal(HttpStatusCode.NoContent, uso.StatusCode);
        }

        var sessao = await api.LoginAsync(new UsuarioCriado(c.Usuario.Id, c.Usuario.Email, NovaSenha, c.Usuario.TenantSlug));
        using (var troca = await TrocarAsync(api, sessao, NovaSenha, "Terceira-Senha-Longa-9!"))
        {
            Assert.Equal(HttpStatusCode.NoContent, troca.StatusCode);
        }

        string[] segredos = [token, token[4..], NovaSenha, "Terceira-Senha-Longa-9!", c.Usuario.Senha];
        var todosOsLogs = logs.Texto();
        Assert.NotEmpty(todosOsLogs); // a captura funcionou: a ausência abaixo tem valor
        var trilha = JsonSerializer.Serialize(await api.LerAsync<List<EventoDto>>(adminToken, "/audit-events?take=200"));
        foreach (var segredo in segredos)
        {
            Assert.DoesNotContain(segredo, todosOsLogs);
            Assert.DoesNotContain(segredo, trilha);
        }

        // No banco só existe o SHA-256 do token: nem o token, nem o texto base64url dele.
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand("SELECT token_hash FROM identity.password_reset_tokens WHERE tenant_id = @t AND user_id = @u", conexao);
        comando.Parameters.AddWithValue("t", c.Tenant.TenantId.Value);
        comando.Parameters.AddWithValue("u", c.Usuario.Id);
        var guardado = (byte[])(await comando.ExecuteScalarAsync())!;
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), guardado);
        Assert.NotEqual(Encoding.UTF8.GetBytes(token), guardado);
    }

    private sealed class CapturaDeLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _linhas = new();

        public ILogger CreateLogger(string categoria) => new Registro(_linhas);

        public string Texto() => string.Join('\n', _linhas);

        public void Dispose()
        {
        }

        private sealed class Registro(ConcurrentQueue<string> linhas) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel nivel) => true;

            public void Log<TState>(LogLevel nivel, EventId evento, TState estado, Exception? erro, Func<TState, Exception?, string> formatador) =>
                linhas.Enqueue(formatador(estado, erro) + (erro is null ? string.Empty : "\n" + erro));
        }
    }
}
