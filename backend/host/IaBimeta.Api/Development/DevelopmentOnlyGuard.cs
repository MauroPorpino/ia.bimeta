namespace IaBimeta.Api.Development;

internal static class DevelopmentOnlyGuard
{
    /// <summary>
    /// Lê a seção <c>Tenancy:Development</c>. Se qualquer recurso de desenvolvimento estiver ligado fora do ambiente
    /// Development, a partida falha: mecanismo de teste local nunca pode vazar para produção.
    /// </summary>
    public static DevelopmentTenancyOptions Resolve(IHostEnvironment ambiente, IConfiguration configuracao)
    {
        var opcoes = configuracao.GetSection(DevelopmentTenancyOptions.SectionName).Get<DevelopmentTenancyOptions>()
            ?? new DevelopmentTenancyOptions();

        var algumLigado = opcoes.HeaderEnabled || opcoes.ApplyMigrations || opcoes.SeedTenants.Count > 0;
        if (algumLigado && !ambiente.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"A seção '{DevelopmentTenancyOptions.SectionName}' só pode ser usada no ambiente Development, " +
                $"mas o ambiente atual é '{ambiente.EnvironmentName}'. Remova a configuração de desenvolvimento.");
        }

        return opcoes;
    }
}
