using System.Net;
using System.Net.Http.Json;

namespace IaBimeta.IntegrationTests.Api;

internal sealed class RelogioManual : TimeProvider
{
    private DateTimeOffset _agora = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _agora;

    public void Avancar(TimeSpan intervalo) => _agora += intervalo;
}

internal sealed record LoginDto(string Token, DateTimeOffset ExpiresAt);

internal sealed record OrganizacaoDto(Guid Id, string Name, DateTimeOffset CreatedAt);

internal sealed record UnidadeDto(Guid Id, Guid OrganizationId, string Name, string Kind, DateTimeOffset CreatedAt);

internal sealed record SetorDto(Guid Id, Guid BusinessUnitId, string Name, DateTimeOffset CreatedAt);

internal sealed record UsuarioDto(Guid Id, string Email, string DisplayName, DateTimeOffset CreatedAt);

internal sealed record AcessoDto(Guid UserId, string Email, string DisplayName, string Role, bool TenantWide);

internal sealed record ConcessaoDto(Guid? OrganizationId, string Role);

internal sealed record EuDto(Guid Id, string Email, string DisplayName, List<ConcessaoDto> Grants);

internal sealed record EventoDto(Guid Id, DateTimeOffset OccurredAt, Guid? ActorUserId, string ActorOrigin, string Operation, string ResourceType, string? ResourceId, string Outcome);

internal static class ApiTestSupport
{
    public static async Task<string> LoginAsync(this ApiServer api, UsuarioCriado usuario)
    {
        using var resposta = await api.Client.PostAsJsonAsync("/auth/login", new { tenant = usuario.TenantSlug, email = usuario.Email, password = usuario.Senha });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<LoginDto>())!.Token;
    }

    public static async Task<HttpResponseMessage> EnviarAsync(this ApiServer api, string? token, HttpMethod metodo, string caminho, object? corpo = null)
    {
        using var requisicao = api.RequisicaoDe(token, metodo, caminho, corpo is null ? null : JsonContent.Create(corpo));
        return await api.Client.SendAsync(requisicao);
    }

    public static async Task<HttpStatusCode> StatusAsync(this ApiServer api, string? token, HttpMethod metodo, string caminho, object? corpo = null)
    {
        using var resposta = await api.EnviarAsync(token, metodo, caminho, corpo);
        return resposta.StatusCode;
    }

    public static async Task<T> LerAsync<T>(this ApiServer api, string? token, string caminho)
    {
        using var resposta = await api.EnviarAsync(token, HttpMethod.Get, caminho);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<T>())!;
    }

    public static async Task<T> CriarAsync<T>(this ApiServer api, string? token, string caminho, object corpo)
    {
        using var resposta = await api.EnviarAsync(token, HttpMethod.Post, caminho, corpo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<T>())!;
    }
}
