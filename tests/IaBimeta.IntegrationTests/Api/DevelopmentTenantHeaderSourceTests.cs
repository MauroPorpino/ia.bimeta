using System.Net;
using IaBimeta.Api.RequestContexts;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;
using Microsoft.AspNetCore.Http;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// As travas da fonte de desenvolvimento que não dá para exercitar por uma conexão real (origem não loopback e
/// proxy). O diretório é um dublê: aqui só se testa a decisão da fonte, não o isolamento de dados.
/// </summary>
public sealed class DevelopmentTenantHeaderSourceTests
{
    private static readonly TenantInfo Ativo = new(new TenantId(Guid.NewGuid()), "lab-dev", "Lab", TenantStatus.Active);

    private sealed class DiretorioDeUmTenant(TenantInfo? tenant) : ITenantDirectory
    {
        public Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenant is not null && tenant.Slug == slug ? tenant : null);

        public Task<TenantInfo?> FindByIdAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenant is not null && tenant.TenantId == tenantId ? tenant : null);
    }

    private static DefaultHttpContext Requisicao(string? origem, string? slug = "lab-dev", bool comProxy = false)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = origem is null ? null : IPAddress.Parse(origem);
        if (slug is not null)
        {
            http.Request.Headers["X-Dev-Tenant"] = slug;
        }

        if (comProxy)
        {
            http.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        }

        return http;
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Origem_loopback_com_tenant_ativo_recebe_contexto_marcado_como_desenvolvimento(string origem)
    {
        var fonte = new DevelopmentTenantHeaderSource(new DiretorioDeUmTenant(Ativo));

        var contexto = await fonte.ResolveAsync(Requisicao(origem), CancellationToken.None);

        Assert.NotNull(contexto);
        Assert.Equal(Ativo.TenantId, contexto.TenantId);
        Assert.Equal("development-header", contexto.Origin);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.0.20")]
    [InlineData("203.0.113.9")]
    [InlineData("2001:db8::1")]
    public async Task Origem_que_nao_e_loopback_nunca_recebe_contexto(string origem)
    {
        var fonte = new DevelopmentTenantHeaderSource(new DiretorioDeUmTenant(Ativo));

        Assert.Null(await fonte.ResolveAsync(Requisicao(origem), CancellationToken.None));
    }

    [Fact]
    public async Task Origem_desconhecida_nunca_recebe_contexto()
    {
        var fonte = new DevelopmentTenantHeaderSource(new DiretorioDeUmTenant(Ativo));

        Assert.Null(await fonte.ResolveAsync(Requisicao(origem: null), CancellationToken.None));
    }

    [Fact]
    public async Task Requisicao_com_proxy_nunca_recebe_contexto_mesmo_vinda_de_loopback()
    {
        var fonte = new DevelopmentTenantHeaderSource(new DiretorioDeUmTenant(Ativo));

        Assert.Null(await fonte.ResolveAsync(Requisicao("127.0.0.1", comProxy: true), CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("outro-tenant")]
    public async Task Cabecalho_ausente_vazio_ou_de_tenant_desconhecido_nao_recebe_contexto(string? slug)
    {
        var fonte = new DevelopmentTenantHeaderSource(new DiretorioDeUmTenant(Ativo));

        Assert.Null(await fonte.ResolveAsync(Requisicao("127.0.0.1", slug), CancellationToken.None));
    }

    [Fact]
    public async Task Tenant_suspenso_nao_recebe_contexto()
    {
        var suspenso = Ativo with { Status = TenantStatus.Suspended };
        var fonte = new DevelopmentTenantHeaderSource(new DiretorioDeUmTenant(suspenso));

        Assert.Null(await fonte.ResolveAsync(Requisicao("127.0.0.1"), CancellationToken.None));
    }
}
