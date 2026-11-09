using IaBimeta.Api.Operations;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O texto da economia do FAQ direto (ADR 0029), sem banco: a contagem de chamadas evitadas é MEDIDA e exata; tokens e custo são ESTIMADOS (acertos × a média por chamada confirmada do mesmo tenant) e rotulados
/// como estimativa, com a base. Sem chamada confirmada não há número inventado. Cada empresa só vê os próprios números.
/// </summary>
public sealed class EconomiaDoFaqRelatorioTests
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    private static IReadOnlyList<(Guid? Tenant, string Message)> Descrever(
        IReadOnlyList<FaqTurnCounts> contagens, IReadOnlyList<ModelCallAverage> medias, params Guid[] obrigatorios) =>
        FaqSavingsReport.Describe(contagens, medias, obrigatorios);

    [Fact]
    public void Os_acertos_sao_contados_exatos_e_a_estimativa_e_acertos_vezes_a_media_por_chamada_com_a_base_a_vista()
    {
        // 3 acertos; 2 chamadas confirmadas: (1.000 + 200) e (2.000 + 400) tokens = 3.600 no total, média 1.800; custo 0,002 + 0,004 = 0,006 USD, média 0,003.
        var linhas = Descrever(
            new[] { new FaqTurnCounts(A, 3, 1) },
            new[] { new ModelCallAverage(A, 2, 3600, "USD", 2, 0.006m) });

        Assert.Equal(2, linhas.Count);
        Assert.All(linhas, l => Assert.Equal(A, l.Tenant));
        Assert.Equal(
            "nos últimos 7 d: 3 turno(s) respondido(s) direto pelo FAQ, SEM chamar o modelo (chamadas evitadas; contagem exata); o modelo respondeu 1 turno(s); 75,0% dos turnos respondidos pela IA saíram do FAQ.",
            linhas[0].Message);
        Assert.Contains("ESTIMATIVA (não medida): ~5.400 tokens e ~0,0090 USD", linhas[1].Message); // 3 x 1.800 e 3 x 0,003
        Assert.Contains("custo estimado pela tabela de preços configurada, não é fatura", linhas[1].Message);
        Assert.Contains("3 chamada(s) evitada(s)", linhas[1].Message);
        Assert.Contains("média de 1.800 tokens por chamada confirmada deste tenant (2 chamada(s) na janela)", linhas[1].Message);
    }

    [Fact]
    public void Sem_chamada_confirmada_na_janela_nao_ha_media_e_nenhum_numero_e_inventado()
    {
        var linhas = Descrever(new[] { new FaqTurnCounts(A, 4, 0) }, Array.Empty<ModelCallAverage>());

        Assert.Equal(2, linhas.Count);
        Assert.Contains("4 turno(s) respondido(s) direto pelo FAQ", linhas[0].Message);
        Assert.Contains("100,0% dos turnos respondidos pela IA saíram do FAQ", linhas[0].Message);
        Assert.Contains("sem chamada confirmada ao modelo deste tenant na janela, não há média para estimar (nenhum número foi inventado)", linhas[1].Message);
        Assert.DoesNotContain("~", linhas[1].Message);
    }

    [Fact]
    public void Sem_custo_na_tabela_de_precos_a_estimativa_de_tokens_sai_e_o_custo_diz_que_nao_ha()
    {
        var linhas = Descrever(new[] { new FaqTurnCounts(A, 2, 1) }, new[] { new ModelCallAverage(A, 1, 1300, null, 0, 0m) });

        Assert.Contains("~2.600 tokens", linhas[1].Message);
        Assert.Contains("sem custo estimado: a tabela de preços não informou custo", linhas[1].Message);
        Assert.DoesNotContain("USD", linhas[1].Message);
    }

    [Fact]
    public void Empresa_pedida_sem_acertos_aparece_com_a_contagem_zero_e_sem_estimativa()
    {
        var linhas = Descrever(new[] { new FaqTurnCounts(A, 0, 5) }, new[] { new ModelCallAverage(A, 5, 6500, "USD", 5, 0.05m) }, A);

        var unica = Assert.Single(linhas);
        Assert.Contains("0 turno(s) respondido(s) direto pelo FAQ", unica.Message);
        Assert.Contains("o modelo respondeu 5 turno(s); 0,0% dos turnos respondidos pela IA saíram do FAQ", unica.Message);
        Assert.DoesNotContain("ESTIMATIVA", unica.Message); // nada foi evitado: nada a estimar
    }

    [Fact]
    public void Empresa_pedida_sem_nenhum_turno_aparece_zerada_e_sem_dividir_por_zero()
    {
        var linhas = Descrever(Array.Empty<FaqTurnCounts>(), Array.Empty<ModelCallAverage>(), B);

        var unica = Assert.Single(linhas);
        Assert.Equal(B, unica.Tenant);
        Assert.Contains("0 turno(s) respondido(s) direto pelo FAQ", unica.Message);
        Assert.Contains("0,0% dos turnos respondidos pela IA saíram do FAQ", unica.Message);
    }

    [Fact]
    public void Cada_empresa_usa_a_propria_media_nunca_a_da_outra_e_a_sem_acerto_nao_aparece_no_relatorio_geral()
    {
        var linhas = Descrever(
            new[] { new FaqTurnCounts(A, 10, 0), new FaqTurnCounts(B, 0, 20) },
            new[] { new ModelCallAverage(A, 1, 1000, "USD", 1, 0.001m), new ModelCallAverage(B, 20, 200000, "USD", 20, 1m) });

        Assert.All(linhas, l => Assert.Equal(A, l.Tenant)); // B não teve acerto: fora do relatório geral
        Assert.Contains("~10.000 tokens e ~0,0100 USD", linhas[1].Message); // 10 x a média de A (1.000 tokens, 0,001), e não a de B (10.000 tokens, 0,05)
    }

    [Fact]
    public void Duas_empresas_com_acerto_geram_linhas_separadas_cada_uma_com_o_proprio_tenant()
    {
        var linhas = Descrever(
            new[] { new FaqTurnCounts(A, 2, 2), new FaqTurnCounts(B, 1, 0) },
            new[] { new ModelCallAverage(A, 2, 2000, "USD", 2, 0.002m) });

        Assert.Equal(new Guid?[] { A, A, B, B }, linhas.Select(l => l.Tenant));
        Assert.Contains("~2.000 tokens", linhas[1].Message);
        Assert.Contains("não há média para estimar", linhas[3].Message); // B não tem chamadas: sem base, sem número
    }

    [Fact]
    public void O_texto_nunca_traz_conteudo_de_conversa_so_numeros_e_rotulos()
    {
        var linhas = Descrever(new[] { new FaqTurnCounts(A, 1, 1) }, new[] { new ModelCallAverage(A, 1, 1000, "USD", 1, 0.001m) });

        Assert.All(linhas, l => Assert.DoesNotMatch("(?i)pergunta:|resposta:|horário de atendimento", l.Message));
    }
}
