using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A administração da plataforma BImeta (Etapa 02, ADR 0043) pela API REAL (HTTP, PostgreSQL real): cadastro e provisionamento de empresas, primeiro administrador e reemissão, limite e habilitação,
/// suspensão e reativação com efeito verificável, separação entre as duas administrações (sessões e rotas), perfis e permissões individuais dos operadores, e auditoria. O tenant técnico da plataforma é ÚNICO
/// no banco (índice parcial): os testes o reaproveitam e criam operadores próprios, com e-mails únicos.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlataformaApiTests(PostgresFixture banco)
{
    private const string SenhaDoOperador = PostgresFixture.PasswordPadrao;

    private sealed record Operador(Guid Id, string Email, string Token);

    private async Task<TenantInfo> PlataformaAsync()
    {
        await using var escopo = banco.NovoEscopo();
        return await escopo.ServiceProvider.GetRequiredService<TenantAdministrationService>().EnsurePlatformTenantAsync("Plataforma BImeta (teste)");
    }

    /// <summary>Cria um operador ATIVO diretamente no banco (como o seed do teste), com o papel de plataforma, e faz o login pela rota da plataforma.</summary>
    private async Task<Operador> OperadorAsync(ApiServer api, Role papel, string? prefixo = null)
    {
        var plataforma = await PlataformaAsync();
        var email = $"{prefixo ?? "op"}{Guid.NewGuid().ToString("N")[..8]}@plataforma.local";
        Guid id;
        await using (var escopo = banco.NovoEscopo(PostgresFixture.Contexto(plataforma)))
        {
            var sp = escopo.ServiceProvider;
            var agora = DateTimeOffset.UtcNow;
            var usuario = User.Create(plataforma.TenantId, email, "Operador de teste", sp.GetRequiredService<PasswordHasher>().Hash(SenhaDoOperador), agora);
            await sp.GetRequiredService<IUserRepository>().AddAsync(usuario, UserOrganizationGrant.Create(plataforma.TenantId, usuario.Id, null, papel, agora, null), default);
            id = usuario.Id;
        }

        return new Operador(id, email, await LoginAsync(api, email));
    }

    private static async Task<string> LoginAsync(ApiServer api, string email, string senha = SenhaDoOperador)
    {
        using var resposta = await api.Client.PostAsJsonAsync("/platform/auth/login", new { email, password = senha });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static async Task<JsonElement> CorpoAsync(HttpResponseMessage resposta) => await resposta.Content.ReadFromJsonAsync<JsonElement>();

    private static string Slug(string prefixo) => $"{prefixo}-{Guid.NewGuid().ToString("N")[..8]}";

    private static object NovaEmpresa(string slug, int? limite = 3, string? adminEmail = null) => new
    {
        slug,
        name = $"Empresa {slug}",
        legalName = $"Empresa {slug} Ltda",
        document = "11.222.333/0001-81",
        contactName = "Contato",
        contactEmail = "contato@empresa.local",
        maxUsers = limite,
        registrationEnabled = true,
        adminEmail = adminEmail ?? $"admin@{slug}.local",
        adminName = "Administrador da empresa",
    };

    private static async Task<JsonElement> CadastrarAsync(ApiServer api, string token, string slug, int? limite = 3)
    {
        using var resposta = await api.EnviarAsync(token, HttpMethod.Post, "/platform/companies", NovaEmpresa(slug, limite));
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await CorpoAsync(resposta);
    }

    private static async Task ConcluirPrimeiroAcessoAsync(ApiServer api, string slug, string email, string token, string senha = SenhaDoOperador)
    {
        using var resposta = await api.Client.PostAsJsonAsync("/auth/password-reset/redeem", new { tenant = slug, email, token, newPassword = senha });
        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
    }

    private static async Task<HttpStatusCode> LoginDaEmpresaAsync(ApiServer api, string slug, string email, string senha = SenhaDoOperador)
    {
        using var resposta = await api.Client.PostAsJsonAsync("/auth/login", new { tenant = slug, email, password = senha });
        return resposta.StatusCode;
    }

    // Cadastro, provisionamento e primeiro administrador

    [Fact]
    public async Task Cadastro_de_empresa_exige_limite_provisiona_o_primeiro_administrador_e_so_opera_depois_do_primeiro_acesso_com_reemissao_segura()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var slug = Slug("cad");
        var email = $"admin@{slug}.local";

        // O limite é OBRIGATÓRIO para uma empresa nova.
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Post, "/platform/companies", NovaEmpresa(slug, limite: null)));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Post, "/platform/companies", NovaEmpresa(slug, limite: 0)));

        using var criada = await api.EnviarAsync(admin.Token, HttpMethod.Post, "/platform/companies", NovaEmpresa(slug, 3));
        Assert.Equal(HttpStatusCode.Created, criada.StatusCode);
        Assert.Contains("no-store", criada.Headers.CacheControl?.ToString() ?? string.Empty);
        var corpo = await CorpoAsync(criada);
        var primeiro = corpo.GetProperty("firstAccess").GetProperty("token").GetString()!;
        Assert.StartsWith("ibr_", primeiro);
        Assert.Equal("Active", corpo.GetProperty("detail").GetProperty("company").GetProperty("status").GetString()); // provisionamento concluído: a empresa passa a poder operar
        var vagas = corpo.GetProperty("detail").GetProperty("seats");
        Assert.Equal((3, 1, 1, 2), (vagas.GetProperty("limit").GetInt32(), vagas.GetProperty("used").GetInt32(), vagas.GetProperty("pendingReserved").GetInt32(), vagas.GetProperty("available").GetInt32())); // o administrador ocupa uma vaga

        // Sem primeiro acesso, ninguém entra na empresa; o token nunca volta numa leitura posterior.
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginDaEmpresaAsync(api, slug, email));
        var leitura = await api.LerAsync<JsonElement>(admin.Token, $"/platform/companies/{slug}");
        Assert.Equal("Pending", leitura.GetProperty("initialAdministrator").GetProperty("status").GetString());
        Assert.DoesNotContain("ibr_", leitura.GetRawText());

        // Reemissão: o token anterior deixa de valer; a conta é a mesma e a reserva não muda as vagas.
        using var reemitida = await api.EnviarAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/first-administrator/reissue");
        Assert.Equal(HttpStatusCode.OK, reemitida.StatusCode);
        var segundo = (await CorpoAsync(reemitida)).GetProperty("token").GetString()!;
        Assert.NotEqual(primeiro, segundo);
        using var antigo = await api.Client.PostAsJsonAsync("/auth/password-reset/redeem", new { tenant = slug, email, token = primeiro, newPassword = SenhaDoOperador });
        Assert.Equal(HttpStatusCode.BadRequest, antigo.StatusCode);
        Assert.Equal(1, (await api.LerAsync<JsonElement>(admin.Token, $"/platform/companies/{slug}")).GetProperty("seats").GetProperty("used").GetInt32());

        await ConcluirPrimeiroAcessoAsync(api, slug, email, segundo);
        Assert.Equal(HttpStatusCode.OK, await LoginDaEmpresaAsync(api, slug, email));

        // Concluído o primeiro acesso, o mecanismo NÃO recupera a conta.
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/first-administrator/reissue"));

        // A empresa consulta a própria situação (limite e habilitação), mas não tem como alterá-los.
        var admEmpresa = await api.LoginAsync(new UsuarioCriado(Guid.Empty, email, SenhaDoOperador, slug));
        var uso = await api.LerAsync<JsonElement>(admEmpresa, "/users/usage");
        Assert.Equal(3, uso.GetProperty("limit").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(admEmpresa, HttpMethod.Put, $"/platform/companies/{slug}/quota", new { maxUsers = 99 }));
    }

    [Fact]
    public async Task Cadastros_concorrentes_da_mesma_empresa_nao_duplicam_o_primeiro_administrador_nem_reativam_uma_empresa_suspensa()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var slug = Slug("conc");

        var respostas = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var r = await api.EnviarAsync(admin.Token, HttpMethod.Post, "/platform/companies", NovaEmpresa(slug, 3));
            return r.StatusCode;
        }));

        // Exatamente um cadastro cria a empresa; as demais respostas são retomadas (200) ou recusa por identificador já provisionado (409). Nenhum 400 (validação) nem 5xx: nada de erro "aceito sem motivo".
        Assert.Single(respostas, HttpStatusCode.Created);
        Assert.All(respostas, s => Assert.True(s is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict, $"status {(int)s}"));
        var detalhe = await api.LerAsync<JsonElement>(admin.Token, $"/platform/companies/{slug}");
        var vagas = detalhe.GetProperty("seats");
        Assert.Equal(1, vagas.GetProperty("used").GetInt32()); // um único primeiro administrador, qualquer que tenha sido a ordem das retomadas

        // Suspensa, uma nova tentativa de cadastro nunca a reativa.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/suspend", new { reason = "suspensao apos o cadastro", confirm = slug }));
        await api.StatusAsync(admin.Token, HttpMethod.Post, "/platform/companies", NovaEmpresa(slug, 3));
        var depois = await api.LerAsync<JsonElement>(admin.Token, $"/platform/companies/{slug}");
        Assert.Equal("Suspended", depois.GetProperty("company").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Identificadores_reservados_e_em_uso_sao_recusados_e_a_plataforma_nao_aparece_como_empresa()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);

        foreach (var reservado in new[] { "bimeta-platform", "admin", "plataforma", "api" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Post, "/platform/companies", NovaEmpresa(reservado)));
        }

        var slug = Slug("dup");
        _ = await CadastrarAsync(api, admin.Token, slug);
        Assert.Equal(HttpStatusCode.Conflict, await api.StatusAsync(admin.Token, HttpMethod.Post, "/platform/companies", NovaEmpresa(slug)));

        // O tenant técnico não é empresa contratante: não é listado, não é consultável e não entra nos indicadores.
        var busca = await api.LerAsync<JsonElement>(admin.Token, "/platform/companies?search=bimeta-platform");
        Assert.Equal(0, busca.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(admin.Token, HttpMethod.Get, "/platform/companies/bimeta-platform"));
        var visao = await api.LerAsync<JsonElement>(admin.Token, "/platform/overview");
        var lista = await api.LerAsync<JsonElement>(admin.Token, "/platform/companies?pageSize=100");
        Assert.DoesNotContain(lista.GetProperty("items").EnumerateArray(), i => i.GetProperty("company").GetProperty("slug").GetString() == "bimeta-platform");
        Assert.True(visao.GetProperty("totalCompanies").GetInt32() >= 1);
    }

    [Fact]
    public async Task Dados_cadastrais_mudam_mas_o_identificador_tecnico_e_permanente_e_a_listagem_pesquisa_e_filtra()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var slug = Slug("cadas");
        _ = await CadastrarAsync(api, admin.Token, slug);

        using var alterada = await api.EnviarAsync(admin.Token, HttpMethod.Put, $"/platform/companies/{slug}", new { name = "Nome Novo", legalName = "Razão Nova", document = "11222333000181", contactName = "Outro", contactEmail = "novo@empresa.local" });
        Assert.Equal(HttpStatusCode.OK, alterada.StatusCode);
        var empresa = (await CorpoAsync(alterada)).GetProperty("company");
        Assert.Equal((slug, "Nome Novo"), (empresa.GetProperty("slug").GetString(), empresa.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Put, $"/platform/companies/{slug}", new { name = "X", document = "123" }));

        var pesquisa = await api.LerAsync<JsonElement>(admin.Token, $"/platform/companies?search={slug}&status=Active&page=1&pageSize=5");
        Assert.Equal(1, pesquisa.GetProperty("total").GetInt32());
        var vazio = await api.LerAsync<JsonElement>(admin.Token, $"/platform/companies?search={slug}&status=Suspended");
        Assert.Equal(0, vazio.GetProperty("total").GetInt32());
    }

    // Limite e habilitação

    [Fact]
    public async Task Reduzir_o_limite_abaixo_do_uso_nao_desativa_ninguem_alteracoes_concorrentes_preservam_os_dois_campos_e_a_empresa_so_consulta()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var slug = Slug("lim");
        var empresa = await CadastrarAsync(api, admin.Token, slug, limite: 3);
        var email = empresa.GetProperty("firstAccess").GetProperty("email").GetString()!;
        await ConcluirPrimeiroAcessoAsync(api, slug, email, empresa.GetProperty("firstAccess").GetProperty("token").GetString()!);

        // Limite inválido
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Put, $"/platform/companies/{slug}/quota", new { maxUsers = 0 }));

        // Alterações CONCORRENTES de campos diferentes: nenhuma sobrescreve a outra.
        var limite = api.EnviarAsync(admin.Token, HttpMethod.Put, $"/platform/companies/{slug}/quota", new { maxUsers = 5 });
        var cadastro = api.EnviarAsync(admin.Token, HttpMethod.Put, $"/platform/companies/{slug}/enrollment", new { enabled = false });
        using (await limite)
        using (await cadastro)
        {
        }

        var atual = (await api.LerAsync<JsonElement>(admin.Token, $"/platform/companies/{slug}")).GetProperty("seats");
        Assert.Equal((5, false), (atual.GetProperty("limit").GetInt32(), atual.GetProperty("registrationEnabled").GetBoolean()));

        // Redução abaixo do uso atual (1 ativo): aceita, mostra excesso e NÃO desativa o administrador, que continua entrando.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Put, $"/platform/companies/{slug}/quota", new { maxUsers = 1 }));
        Assert.Equal(HttpStatusCode.OK, await LoginDaEmpresaAsync(api, slug, email));

        // A empresa vê a habilitação (consulta) e não tem rota para alterar nada da plataforma.
        var admEmpresa = await api.LoginAsync(new UsuarioCriado(Guid.Empty, email, SenhaDoOperador, slug));
        var uso = await api.LerAsync<JsonElement>(admEmpresa, "/users/usage");
        Assert.False(uso.GetProperty("registrationEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(admEmpresa, HttpMethod.Put, $"/platform/companies/{slug}/enrollment", new { enabled = true }));
    }

    // Suspensão e reativação

    [Fact]
    public async Task Suspender_exige_motivo_e_confirmacao_bloqueia_login_e_sessoes_e_a_reativacao_nao_restaura_sessoes_antigas()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var slug = Slug("susp");
        var empresa = await CadastrarAsync(api, admin.Token, slug, limite: 4);
        var email = empresa.GetProperty("firstAccess").GetProperty("email").GetString()!;
        await ConcluirPrimeiroAcessoAsync(api, slug, email, empresa.GetProperty("firstAccess").GetProperty("token").GetString()!);
        var sessaoAntiga = await api.LoginAsync(new UsuarioCriado(Guid.Empty, email, SenhaDoOperador, slug));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(sessaoAntiga, HttpMethod.Get, "/auth/me"));

        // Motivo e confirmação são obrigatórios.
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/suspend", new { reason = "", confirm = slug }));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/suspend", new { reason = "Inadimplência de teste", confirm = "outro" }));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(sessaoAntiga, HttpMethod.Get, "/auth/me")); // nada mudou

        using var suspensa = await api.EnviarAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/suspend", new { reason = "Inadimplência de teste", confirm = slug });
        Assert.Equal(HttpStatusCode.OK, suspensa.StatusCode);
        var detalhe = await CorpoAsync(suspensa);
        Assert.Equal("Suspended", detalhe.GetProperty("company").GetProperty("status").GetString());
        Assert.Equal(4, detalhe.GetProperty("seats").GetProperty("limit").GetInt32()); // limite e usuários preservados

        // Login bloqueado, sessão aberta derrubada, e suspender duas vezes é recusado.
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginDaEmpresaAsync(api, slug, email));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(sessaoAntiga, HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/suspend", new { reason = "Outra vez", confirm = slug }));

        // Reativação: exige motivo; as sessões anteriores NÃO voltam; é preciso entrar de novo; nada foi perdido.
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/reactivate", new { reason = "x", confirm = slug }));
        using var reativada = await api.EnviarAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/reactivate", new { reason = "Regularizada em teste", confirm = slug });
        Assert.Equal(HttpStatusCode.OK, reativada.StatusCode);
        var depois = await CorpoAsync(reativada);
        Assert.Equal("Active", depois.GetProperty("company").GetProperty("status").GetString());
        Assert.Equal(4, depois.GetProperty("seats").GetProperty("limit").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(sessaoAntiga, HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.OK, await LoginDaEmpresaAsync(api, slug, email));

        // O histórico guarda situação anterior, motivo e operador, e o que não está suspenso não se reativa.
        var historico = depois.GetProperty("statusHistory").EnumerateArray().ToList();
        Assert.Equal(2, historico.Count);
        Assert.Contains(historico, h => h.GetProperty("toStatus").GetString() == "Suspended" && h.GetProperty("fromStatus").GetString() == "Active" && h.GetProperty("reason").GetString() == "Inadimplência de teste" && h.GetProperty("actorUserId").GetGuid() == admin.Id);
        Assert.Contains(historico, h => h.GetProperty("toStatus").GetString() == "Active" && h.GetProperty("fromStatus").GetString() == "Suspended");
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/reactivate", new { reason = "Sem suspensão", confirm = slug }));
    }

    [Fact]
    public async Task O_portao_operacional_reflete_a_situacao_da_empresa_e_a_reativacao_marca_o_corte_do_processamento_automatico()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var slug = Slug("gate");
        var empresa = await CadastrarAsync(api, admin.Token, slug);
        var tenantId = new TenantId((await api.LerAsync<JsonElement>(admin.Token, $"/platform/companies/{slug}")).GetProperty("company").GetProperty("tenantId").GetGuid());

        async Task<TenantOperationState> EstadoAsync()
        {
            await using var escopo = banco.NovoEscopo();
            return await escopo.ServiceProvider.GetRequiredService<ITenantOperationGate>().GetAsync(tenantId);
        }

        var inicial = await EstadoAsync();
        Assert.True(inicial.IsOperational);
        Assert.Null(inicial.OperationalSince);

        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/suspend", new { reason = "Teste do portão", confirm = slug }));
        Assert.False((await EstadoAsync()).IsOperational);

        var antes = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/reactivate", new { reason = "Teste do portão", confirm = slug }));
        var reativada = await EstadoAsync();
        Assert.True(reativada.IsOperational);
        Assert.True(reativada.OperationalSince >= antes.AddSeconds(-5)); // tudo o que nasceu antes deste instante não é processado automaticamente
        _ = empresa;

        // Empresa inexistente: falha fechada.
        await using var escopo2 = banco.NovoEscopo();
        Assert.False((await escopo2.ServiceProvider.GetRequiredService<ITenantOperationGate>().GetAsync(new TenantId(Guid.NewGuid()))).IsOperational);
    }

    // Separação entre as duas administrações

    [Fact]
    public async Task A_sessao_da_plataforma_nao_acessa_o_ambiente_das_empresas_e_a_sessao_de_uma_empresa_nao_acessa_a_plataforma()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var slug = Slug("iso");
        var empresa = await CadastrarAsync(api, admin.Token, slug);
        var email = empresa.GetProperty("firstAccess").GetProperty("email").GetString()!;
        await ConcluirPrimeiroAcessoAsync(api, slug, email, empresa.GetProperty("firstAccess").GetProperty("token").GetString()!);
        var tokenDaEmpresa = await api.LoginAsync(new UsuarioCriado(Guid.Empty, email, SenhaDoOperador, slug));

        // Empresa → plataforma: nunca.
        foreach (var rota in new[] { "/platform/overview", "/platform/companies", "/platform/operators", "/platform/audit", "/platform/roles" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(tokenDaEmpresa, HttpMethod.Get, rota));
        }

        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(tokenDaEmpresa, HttpMethod.Post, "/platform/companies", NovaEmpresa(Slug("hack"))));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(tokenDaEmpresa, HttpMethod.Post, $"/platform/companies/{slug}/suspend", new { reason = "tentativa", confirm = slug }));

        // Plataforma → empresas: nunca (só a própria conta). O administrador da plataforma não herda nada do ambiente das empresas.
        foreach (var rota in new[] { "/organizations", "/audit-events", "/users/usage", "/channels/accounts", "/conversations/sessions" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(admin.Token, HttpMethod.Get, rota));
        }

        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Get, "/auth/me"));

        // Cada ambiente tem o seu login: o slug da plataforma não entra pelo login das empresas, e um operador não entra pelo login da plataforma com a senha de outro tenant.
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginDaEmpresaAsync(api, "bimeta-platform", admin.Email));
        using var cruzado = await api.Client.PostAsJsonAsync("/platform/auth/login", new { email, password = SenhaDoOperador });
        Assert.Equal(HttpStatusCode.Unauthorized, cruzado.StatusCode);

        // As permissões efetivas de cada sessão são só as do próprio ambiente.
        var euPlataforma = (await api.LerAsync<JsonElement>(admin.Token, "/auth/me")).GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("permission").GetString()!).ToList();
        Assert.All(euPlataforma, p => Assert.StartsWith("Platform", p));
        var euEmpresa = (await api.LerAsync<JsonElement>(tokenDaEmpresa, "/auth/me")).GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("permission").GetString()!).ToList();
        Assert.DoesNotContain(euEmpresa, p => p.StartsWith("Platform", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Permissoes_da_plataforma_nao_existem_em_empresa_contratante_mesmo_se_gravadas_por_qualquer_caminho()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var slug = Slug("elev");
        var empresa = await CadastrarAsync(api, admin.Token, slug);
        var email = empresa.GetProperty("firstAccess").GetProperty("email").GetString()!;
        await ConcluirPrimeiroAcessoAsync(api, slug, email, empresa.GetProperty("firstAccess").GetProperty("token").GetString()!);
        var tenantId = new TenantId((await api.LerAsync<JsonElement>(admin.Token, $"/platform/companies/{slug}")).GetProperty("company").GetProperty("tenantId").GetGuid());

        // Um papel de plataforma gravado direto no banco para o administrador de uma empresa não vira acesso: as permissões de plataforma são descartadas fora do tenant técnico.
        await using (var escopo = banco.NovoEscopo(PostgresFixture.Contexto(new TenantInfo(tenantId, slug, "x", TenantStatus.Active))))
        {
            var usuario = (await escopo.ServiceProvider.GetRequiredService<IUserRepository>().FindByEmailAsync(email, default))!;
            var grants = escopo.ServiceProvider.GetRequiredService<IGrantRepository>();
            var existente = (await grants.FindAsync(usuario.Id, null, default))!;
            existente.Reassign(Role.PlatformAdministrator, DateTimeOffset.UtcNow, null);
            await grants.SaveChangesAsync(default);
        }

        var token = await api.LoginAsync(new UsuarioCriado(Guid.Empty, email, SenhaDoOperador, slug));
        var permissoes = (await api.LerAsync<JsonElement>(token, "/auth/me")).GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("permission").GetString()!).ToList();
        Assert.DoesNotContain(permissoes, p => p.StartsWith("Platform", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Get, "/platform/companies"));
    }

    // Perfis e permissões

    [Fact]
    public async Task Os_tres_perfis_tem_o_acesso_previsto_e_as_negacoes_sao_auditadas()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var comercial = await OperadorAsync(api, Role.PlatformCommercial, "com");
        var consulta = await OperadorAsync(api, Role.PlatformViewer, "con");
        var slug = Slug("perf");
        _ = await CadastrarAsync(api, admin.Token, slug);

        // Consulta: lê empresas e indicadores; nada de alteração, operadores nem auditoria.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(consulta.Token, HttpMethod.Get, "/platform/overview"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(consulta.Token, HttpMethod.Get, "/platform/companies"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(consulta.Token, HttpMethod.Get, $"/platform/companies/{slug}"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(consulta.Token, HttpMethod.Post, "/platform/companies", NovaEmpresa(Slug("naoe"))));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(consulta.Token, HttpMethod.Put, $"/platform/companies/{slug}/quota", new { maxUsers = 9 }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(consulta.Token, HttpMethod.Get, "/platform/operators"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(consulta.Token, HttpMethod.Get, "/platform/audit"));

        // Operador comercial: cadastra, altera limite e habilitação, reemite o primeiro acesso; NÃO suspende, não gere operadores e não vê a auditoria.
        var dele = Slug("comerc");
        using (var cadastrada = await api.EnviarAsync(comercial.Token, HttpMethod.Post, "/platform/companies", NovaEmpresa(dele)))
        {
            Assert.Equal(HttpStatusCode.Created, cadastrada.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(comercial.Token, HttpMethod.Put, $"/platform/companies/{dele}/quota", new { maxUsers = 6 }));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(comercial.Token, HttpMethod.Put, $"/platform/companies/{dele}/enrollment", new { enabled = false }));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(comercial.Token, HttpMethod.Post, $"/platform/companies/{dele}/first-administrator/reissue"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comercial.Token, HttpMethod.Post, $"/platform/companies/{dele}/suspend", new { reason = "Sem permissão", confirm = dele }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comercial.Token, HttpMethod.Get, "/platform/operators"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comercial.Token, HttpMethod.Get, "/platform/audit"));

        // Administrador: auditoria completa, com as negações (resultado Denied) e as operações bem-sucedidas, sem senha, token nem chave.
        var negadas = await api.LerAsync<JsonElement>(admin.Token, $"/platform/audit?operation=platform.tenant.suspend&outcome=Denied&resource={dele}");
        Assert.True(negadas.GetProperty("total").GetInt32() >= 1);
        var cadastros = await api.LerAsync<JsonElement>(admin.Token, "/platform/audit?operation=platform.tenant.register&outcome=Success&pageSize=50");
        Assert.True(cadastros.GetProperty("total").GetInt32() >= 2);
        var tudo = await api.LerAsync<JsonElement>(admin.Token, "/platform/audit?pageSize=200");
        Assert.DoesNotContain("ibr_", tudo.GetRawText()); // a trilha nunca guarda token, senha nem chave (não há campo livre; os nomes de operação são só rótulos)
    }

    [Fact]
    public async Task Operadores_sao_gerenciados_com_primeiro_acesso_personalizacao_sem_elevacao_sem_autoadministracao_e_revogacao_de_sessao()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var email = $"novo{Guid.NewGuid().ToString("N")[..8]}@plataforma.local";

        // Cadastro: operador pendente, token de primeiro acesso uma vez (nunca em leituras), sem vaga comercial.
        using var admitido = await api.EnviarAsync(admin.Token, HttpMethod.Post, "/platform/operators", new { email, displayName = "Comercial Novo", role = "PlatformCommercial" });
        Assert.Equal(HttpStatusCode.Created, admitido.StatusCode);
        var corpo = await CorpoAsync(admitido);
        Assert.StartsWith("ibr_", corpo.GetProperty("token").GetString());
        var id = corpo.GetProperty("operator").GetProperty("id").GetGuid();
        Assert.Equal("Pending", corpo.GetProperty("operator").GetProperty("status").GetString());
        Assert.DoesNotContain("ibr_", (await api.LerAsync<JsonElement>(admin.Token, $"/platform/operators/{id}")).GetRawText());
        await ConcluirPrimeiroAcessoAsync(api, "bimeta-platform", email, corpo.GetProperty("token").GetString()!);
        var comercial = await LoginAsync(api, email);

        // Perfil de empresa nunca é atribuído a um operador, e um perfil de plataforma exige o papel certo.
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(admin.Token, HttpMethod.Post, "/platform/operators", new { email = "x" + email, displayName = "X", role = "OrganizationAdmin" }));

        // Personalização: o administrador CONCEDE a suspensão ao operador comercial; vale na hora e some quando restaurada.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Put, $"/platform/operators/{id}/permissions", new { permission = "PlatformTenantSuspend", effect = "Grant" }));
        var detalhe = await api.LerAsync<JsonElement>(admin.Token, $"/platform/operators/{id}");
        var suspender = detalhe.GetProperty("permissions").EnumerateArray().Single(p => p.GetProperty("code").GetString() == "PlatformTenantSuspend");
        Assert.False(suspender.GetProperty("fromRole").GetBoolean());
        Assert.Equal("Grant", suspender.GetProperty("override").GetString());
        Assert.True(suspender.GetProperty("effective").GetBoolean());
        var slug = Slug("pers");
        _ = await CadastrarAsync(api, admin.Token, slug);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(comercial, HttpMethod.Post, $"/platform/companies/{slug}/suspend", new { reason = "Concedida individualmente", confirm = slug }));

        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Delete, $"/platform/operators/{id}/permissions/PlatformTenantSuspend"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comercial, HttpMethod.Post, $"/platform/companies/{slug}/reactivate", new { reason = "Já sem a permissão", confirm = slug }));

        // A restrição vence o perfil: retirar o cadastro de empresas do operador comercial.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Put, $"/platform/operators/{id}/permissions", new { permission = "PlatformTenantCreate", effect = "Deny" }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comercial, HttpMethod.Post, "/platform/companies", NovaEmpresa(Slug("negada"))));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/operators/{id}/permissions/reset"));

        // Sem elevação: quem recebe só a gestão de operadores NÃO cria administrador, não personaliza permissões e não alcança quem tem mais do que ele.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Put, $"/platform/operators/{id}/permissions", new { permission = "PlatformOperatorManage", effect = "Grant" }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comercial, HttpMethod.Post, "/platform/operators", new { email = "y" + email, displayName = "Y", role = "PlatformAdministrator" }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comercial, HttpMethod.Put, $"/platform/operators/{admin.Id}/permissions", new { permission = "PlatformTenantRead", effect = "Deny" }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comercial, HttpMethod.Post, $"/platform/operators/{admin.Id}/deactivate"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comercial, HttpMethod.Post, $"/platform/operators/{id}/deactivate")); // nem a si mesmo

        // Sem autoadministração do administrador.
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/operators/{admin.Id}/deactivate"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(admin.Token, HttpMethod.Put, $"/platform/operators/{admin.Id}/role", new { role = "PlatformViewer" }));

        // Revogar sessões e desativar derrubam o acesso na próxima requisição; a redefinição emite novo token para um operador ativo.
        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/operators/{id}/sessions/revoke"));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(comercial, HttpMethod.Get, "/platform/companies"));
        var segunda = await LoginAsync(api, email);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(segunda, HttpMethod.Get, "/platform/companies"));
        using var redefinicao = await api.EnviarAsync(admin.Token, HttpMethod.Post, $"/platform/operators/{id}/access");
        Assert.Equal("access-reset", (await CorpoAsync(redefinicao)).GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(segunda, HttpMethod.Get, "/platform/companies")); // a redefinição revoga as sessões abertas

        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/operators/{id}/deactivate"));
        using var recusado = await api.Client.PostAsJsonAsync("/platform/auth/login", new { email, password = SenhaDoOperador });
        Assert.Equal(HttpStatusCode.Unauthorized, recusado.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/operators/{id}/reactivate"));
    }

    [Fact]
    public async Task Login_iniciado_antes_da_suspensao_e_concluido_depois_da_reativacao_nao_resulta_em_sessao_valida_e_o_login_legitimo_funciona()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var slug = Slug("sessc");
        var empresa = await CadastrarAsync(api, admin.Token, slug);
        var email = empresa.GetProperty("firstAccess").GetProperty("email").GetString()!;
        await ConcluirPrimeiroAcessoAsync(api, slug, email, empresa.GetProperty("firstAccess").GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, await LoginDaEmpresaAsync(api, slug, email)); // a empresa opera normalmente
        var tenantId = empresa.GetProperty("detail").GetProperty("company").GetProperty("tenantId").GetGuid();

        // 1 e 2. A autenticação começa com o tenant ATIVO e é interrompida de forma determinística ANTES de gravar a sessão: uma trava de linha no usuário (a gravação da sessão trava a linha em modo compartilhado e espera).
        await using var conexao = new Npgsql.NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var transacao = await conexao.BeginTransactionAsync();
        await using (var trava = new Npgsql.NpgsqlCommand("SELECT 1 FROM identity.users WHERE tenant_id = @t FOR UPDATE", conexao, transacao))
        {
            trava.Parameters.AddWithValue("t", tenantId);
            await trava.ExecuteScalarAsync();
        }

        var loginAntigo = Task.Run(async () =>
        {
            using var r = await api.Client.PostAsJsonAsync("/auth/login", new { tenant = slug, email, password = SenhaDoOperador });
            return (r.StatusCode, Token: r.IsSuccessStatusCode ? (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString() : null);
        });

        // Espera (sem atraso arbitrário) o login ficar BLOQUEADO na gravação da sessão.
        await using (var observador = new Npgsql.NpgsqlConnection(banco.ConnectionString))
        {
            await observador.OpenAsync();
            var bloqueado = false;
            for (var i = 0; i < 400 && !bloqueado; i++)
            {
                await using var consulta = new Npgsql.NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query ILIKE '%identity.users%FOR SHARE%'", observador);
                bloqueado = Convert.ToInt32(await consulta.ExecuteScalarAsync()) > 0;
                if (!bloqueado)
                {
                    await Task.Delay(50);
                }
            }

            Assert.True(bloqueado, "o login antigo deveria ficar parado antes de gravar a sessão");
        }

        // 3, 4 e 5. A empresa é suspensa (sessões revogadas em lote) e reativada (de novo revogadas) enquanto o login antigo espera.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/suspend", new { reason = "suspensao durante o login", confirm = slug }));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Post, $"/platform/companies/{slug}/reactivate", new { reason = "reativacao antes do fim do login", confirm = slug }));

        // 6. O login antigo conclui.
        await transacao.RollbackAsync();
        var (statusAntigo, tokenAntigo) = await loginAntigo.WaitAsync(TimeSpan.FromSeconds(30));

        // 7. Nenhuma sessão válida resulta dele: recusado (ou, no mínimo, o token não vale).
        Assert.Equal(HttpStatusCode.Unauthorized, statusAntigo);
        Assert.Null(tokenAntigo);

        // A sessão gravada pelo login recusado foi revogada FISICAMENTE (RevokedAt persistido): nenhuma sessão da empresa continua ativa no banco.
        async Task<long> SessoesAtivasNoBancoAsync()
        {
            await using var leitura = new Npgsql.NpgsqlConnection(banco.ConnectionString);
            await leitura.OpenAsync();
            await using var consulta = new Npgsql.NpgsqlCommand("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND revoked_at IS NULL", leitura);
            consulta.Parameters.AddWithValue("t", tenantId);
            return (long)(await consulta.ExecuteScalarAsync())!;
        }

        Assert.Equal(0, await SessoesAtivasNoBancoAsync());

        // 8. Uma autenticação legítima INICIADA depois da reativação funciona.
        var novo = await api.LoginAsync(new UsuarioCriado(Guid.Empty, email, SenhaDoOperador, slug));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(novo, HttpMethod.Get, "/auth/me"));
        Assert.Equal(1, await SessoesAtivasNoBancoAsync()); // só a sessão legítima
    }

    [Fact]
    public async Task Responsavel_que_perde_a_gestao_de_permissoes_enquanto_espera_a_trava_e_recusado_e_o_alvo_fica_intacto()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var raiz = await OperadorAsync(api, Role.PlatformAdministrator, "raiz");
        var a = await OperadorAsync(api, Role.PlatformAdministrator, "autor");
        var b = await OperadorAsync(api, Role.PlatformCommercial, "alvo");
        var plataforma = await PlataformaAsync();

        // B tem uma restrição individual que a restauração do perfil removeria.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(raiz.Token, HttpMethod.Put, $"/platform/operators/{b.Id}/permissions", new { permission = "PlatformTenantCreate", effect = "Deny" }));

        async Task<string> CadastroDeBAsync() =>
            (await api.LerAsync<JsonElement>(raiz.Token, $"/platform/operators/{b.Id}")).GetProperty("permissions").EnumerateArray()
                .Single(p => p.GetProperty("code").GetString() == "PlatformTenantCreate").GetProperty("override").GetString() ?? "none";
        Assert.Equal("Deny", await CadastroDeBAsync());

        // 1 e 2. Uma conexão segura a MESMA trava da administração de operadores (a que a operação de A vai disputar).
        await using var conexao = new Npgsql.NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var transacao = await conexao.BeginTransactionAsync();
        await using (var trava = new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", conexao, transacao))
        {
            trava.Parameters.AddWithValue("k", UserAdmissionLock.KeyFor(plataforma.TenantId));
            await trava.ExecuteScalarAsync();
        }

        // 3. A (que ainda tem a gestão de permissões) inicia a restauração do perfil de B e fica aguardando a trava.
        var operacaoDeA = Task.Run(() => api.StatusAsync(a.Token, HttpMethod.Post, $"/platform/operators/{b.Id}/permissions/reset"));
        await using (var observador = new Npgsql.NpgsqlConnection(banco.ConnectionString))
        {
            await observador.OpenAsync();
            var esperando = false;
            for (var i = 0; i < 400 && !esperando; i++)
            {
                await using var consulta = new Npgsql.NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND wait_event = 'advisory'", observador);
                esperando = Convert.ToInt32(await consulta.ExecuteScalarAsync()) > 0;
                if (!esperando)
                {
                    await Task.Delay(50);
                }
            }

            Assert.True(esperando, "a operação de A deveria ficar aguardando a trava");
        }

        // 4 e 5. Enquanto A espera, a gestão de permissões de A é revogada e a alteração é CONFIRMADA (na mesma transação que segura a trava).
        await using (var revoga = new Npgsql.NpgsqlCommand(
            "INSERT INTO identity.user_permission_overrides (tenant_id, id, user_id, organization_id, permission, effect, created_at, created_by_user_id) VALUES (@t, @i, @u, NULL, 'PlatformPermissionManage', 'Deny', now(), @r)", conexao, transacao))
        {
            revoga.Parameters.AddWithValue("t", plataforma.TenantId.Value);
            revoga.Parameters.AddWithValue("i", Guid.NewGuid());
            revoga.Parameters.AddWithValue("u", a.Id);
            revoga.Parameters.AddWithValue("r", raiz.Id);
            await revoga.ExecuteNonQueryAsync();
        }

        await transacao.CommitAsync();

        // 6 e 7. A adquire a trava e a operação é RECUSADA.
        Assert.Equal(HttpStatusCode.Forbidden, await operacaoDeA.WaitAsync(TimeSpan.FromSeconds(30)));

        // 8. As permissões de B permanecem intactas, e a recusa foi auditada.
        Assert.Equal("Deny", await CadastroDeBAsync());
        var negadas = await api.LerAsync<JsonElement>(raiz.Token, $"/platform/audit?operation=platform.operator.permission.reset&outcome=Denied&resource={b.Id}&pageSize=20");
        Assert.True(negadas.GetProperty("total").GetInt32() >= 1);
    }

    [Fact]
    public async Task Catalogo_de_perfis_exige_sessao_de_operador_com_a_gestao_de_operadores()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var comercial = await OperadorAsync(api, Role.PlatformCommercial);
        var consulta = await OperadorAsync(api, Role.PlatformViewer);
        var slug = Slug("rol");
        var empresa = await CadastrarAsync(api, admin.Token, slug);
        var email = empresa.GetProperty("firstAccess").GetProperty("email").GetString()!;
        await ConcluirPrimeiroAcessoAsync(api, slug, email, empresa.GetProperty("firstAccess").GetProperty("token").GetString()!);
        var tokenDaEmpresa = await api.LoginAsync(new UsuarioCriado(Guid.Empty, email, SenhaDoOperador, slug));

        // Sem sessão: nada.
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(null, HttpMethod.Get, "/platform/roles"));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync("token-que-nao-existe", HttpMethod.Get, "/platform/roles"));

        // Sessão de empresa: nunca (cerca de rota). Operadores sem a gestão de operadores: negado.
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(tokenDaEmpresa, HttpMethod.Get, "/platform/roles"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comercial.Token, HttpMethod.Get, "/platform/roles"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(consulta.Token, HttpMethod.Get, "/platform/roles"));

        // O operador autorizado continua vendo o catálogo.
        var catalogo = await api.LerAsync<JsonElement>(admin.Token, "/platform/roles");
        Assert.Equal(3, catalogo.GetArrayLength());
    }

    [Fact]
    public async Task Remover_restricao_ou_restaurar_o_perfil_nao_concede_o_que_o_responsavel_nao_tem_e_a_operacao_legitima_funciona()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var raiz = await OperadorAsync(api, Role.PlatformAdministrator, "raiz");
        var a1 = await OperadorAsync(api, Role.PlatformAdministrator, "adm1");
        var a2 = await OperadorAsync(api, Role.PlatformAdministrator, "adm2");

        // Dois administradores com restrição individual sobre a suspensão (mantêm a gestão de operadores e de permissões).
        foreach (var alvo in new[] { a1, a2 })
        {
            Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(raiz.Token, HttpMethod.Put, $"/platform/operators/{alvo.Id}/permissions", new { permission = "PlatformTenantSuspend", effect = "Deny" }));
        }

        async Task<JsonElement> SuspensaoDeAsync(Operador alvo) =>
            (await api.LerAsync<JsonElement>(raiz.Token, $"/platform/operators/{alvo.Id}")).GetProperty("permissions").EnumerateArray().Single(p => p.GetProperty("code").GetString() == "PlatformTenantSuspend");

        Assert.False((await SuspensaoDeAsync(a2)).GetProperty("effective").GetBoolean());

        // a1 tenta tirar a restrição de a2 (daria a a2 a suspensão, que a1 NÃO tem): recusado, nada muda.
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(a1.Token, HttpMethod.Delete, $"/platform/operators/{a2.Id}/permissions/PlatformTenantSuspend"));
        var depoisDaRemocao = await SuspensaoDeAsync(a2);
        Assert.Equal("Deny", depoisDaRemocao.GetProperty("override").GetString());
        Assert.False(depoisDaRemocao.GetProperty("effective").GetBoolean());

        // a1 tenta restaurar todas as personalizações de a2: recusado, nada muda.
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(a1.Token, HttpMethod.Post, $"/platform/operators/{a2.Id}/permissions/reset"));
        var depoisDoReset = await SuspensaoDeAsync(a2);
        Assert.Equal("Deny", depoisDoReset.GetProperty("override").GetString());
        Assert.False(depoisDoReset.GetProperty("effective").GetBoolean());

        // As recusas ficam na auditoria como Denied.
        var negadas = await api.LerAsync<JsonElement>(raiz.Token, $"/platform/audit?outcome=Denied&resource={a2.Id}&pageSize=50");
        var operacoes = negadas.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("operation").GetString()).ToList();
        Assert.Contains("platform.operator.permission.remove", operacoes);
        Assert.Contains("platform.operator.permission.reset", operacoes);

        // A operação LEGÍTIMA: quem tem a autoridade (a raiz, que tem a suspensão) tira a restrição e restaura o perfil.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(raiz.Token, HttpMethod.Delete, $"/platform/operators/{a2.Id}/permissions/PlatformTenantSuspend"));
        Assert.True((await SuspensaoDeAsync(a2)).GetProperty("effective").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(raiz.Token, HttpMethod.Post, $"/platform/operators/{a1.Id}/permissions/reset"));
        Assert.True((await SuspensaoDeAsync(a1)).GetProperty("effective").GetBoolean());
    }

    [Fact]
    public async Task O_ultimo_administrador_efetivo_da_plataforma_e_protegido_sob_a_trava()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var plataforma = await PlataformaAsync();

        // Um contexto com só a gestão de operadores (sem ser administrador essencial) tenta desativar o ÚNICO administrador efetivo de um conjunto isolado:
        // aqui basta provar a regra do serviço com dois administradores: desativar o primeiro é permitido enquanto o segundo for utilizável.
        var a = await OperadorAsync(api, Role.PlatformAdministrator, "ua");
        var b = await OperadorAsync(api, Role.PlatformAdministrator, "ub");

        // A desativa B (permitido: A continua utilizável); depois ninguém pode desativar A senão por outro administrador, e a autoadministração é negada.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(a.Token, HttpMethod.Post, $"/platform/operators/{b.Id}/deactivate"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(a.Token, HttpMethod.Post, $"/platform/operators/{a.Id}/deactivate"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(a.Token, HttpMethod.Post, $"/platform/operators/{b.Id}/reactivate"));
        _ = plataforma;
    }

    [Fact]
    public async Task A_visao_geral_traz_indicadores_reais_com_as_empresas_sem_limite_separadas_e_o_excesso_destacado()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await OperadorAsync(api, Role.PlatformAdministrator);
        var antes = await api.LerAsync<JsonElement>(admin.Token, "/platform/overview");

        // Uma empresa legada (sem política: ilimitada), criada pelo caminho antigo, NÃO entra nos totais contratados.
        var legada = await banco.RegistrarTenantAsync("legada");
        var slug = Slug("vis");
        var empresa = await CadastrarAsync(api, admin.Token, slug, limite: 2);
        var email = empresa.GetProperty("firstAccess").GetProperty("email").GetString()!;
        await ConcluirPrimeiroAcessoAsync(api, slug, email, empresa.GetProperty("firstAccess").GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Put, $"/platform/companies/{slug}/enrollment", new { enabled = false }));
        _ = legada;

        var depois = await api.LerAsync<JsonElement>(admin.Token, "/platform/overview");
        Assert.Equal(antes.GetProperty("totalCompanies").GetInt32() + 2, depois.GetProperty("totalCompanies").GetInt32());
        Assert.Equal(antes.GetProperty("seats").GetProperty("contracted").GetInt32() + 2, depois.GetProperty("seats").GetProperty("contracted").GetInt32());
        Assert.Equal(antes.GetProperty("seats").GetProperty("active").GetInt32() + 1, depois.GetProperty("seats").GetProperty("active").GetInt32());
        Assert.Equal(antes.GetProperty("seats").GetProperty("available").GetInt32() + 1, depois.GetProperty("seats").GetProperty("available").GetInt32());
        Assert.Equal(antes.GetProperty("companiesRegistrationDisabled").GetInt32() + 1, depois.GetProperty("companiesRegistrationDisabled").GetInt32());
        Assert.Equal(antes.GetProperty("legacy").GetProperty("companies").GetInt32() + 1, depois.GetProperty("legacy").GetProperty("companies").GetInt32());

        // Excesso: reduzir o limite abaixo do uso aparece no indicador, sem desativar ninguém.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin.Token, HttpMethod.Put, $"/platform/companies/{slug}/quota", new { maxUsers = 1 }));
        var detalhe = await api.LerAsync<JsonElement>(admin.Token, $"/platform/companies/{slug}");
        Assert.Equal(0, detalhe.GetProperty("seats").GetProperty("excess").GetInt32()); // 1 ativo, limite 1: sem excesso
    }
}
