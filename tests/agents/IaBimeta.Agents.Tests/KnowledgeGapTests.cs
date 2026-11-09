using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// A lacuna do FAQ (ADR 0030) como domínio: o hash com segredo POR TENANT (mesmo texto, hashes diferentes entre empresas; sem a chave não se confirma um palpite), a validação da chave e a máquina de situação da
/// decisão do administrador (aberta, descartada ou enviada ao rascunho, uma só decisão).
/// </summary>
public sealed class KnowledgeGapTests
{
    private static readonly byte[] ChaveA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] ChaveB = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset T0 = Snapshots.T0;
    private static readonly Guid Admin = Guid.Parse("33333333-0000-0000-0000-0000000000aa");

    // O hash

    [Fact]
    public void O_hash_e_estavel_tem_32_bytes_e_nao_contem_o_texto()
    {
        var hasher = new KnowledgeGapHasher(ChaveA);

        var um = hasher.Hash(TestContexts.TenantA, "preciso levar documento para a coleta");
        var dois = hasher.Hash(TestContexts.TenantA, "preciso levar documento para a coleta");

        Assert.Equal(KnowledgeGapHasher.HashLength, um.Length);
        Assert.Equal(um, dois);
        Assert.DoesNotContain("documento", System.Text.Encoding.UTF8.GetString(um));
    }

    [Fact]
    public void O_mesmo_texto_em_tenants_diferentes_da_hashes_diferentes_e_com_outra_chave_tambem()
    {
        var a = new KnowledgeGapHasher(ChaveA);
        var b = new KnowledgeGapHasher(ChaveB);

        var doTenantA = a.Hash(TestContexts.TenantA, "qual o preco");
        var doTenantB = a.Hash(TestContexts.TenantB, "qual o preco");
        var comOutraChave = b.Hash(TestContexts.TenantA, "qual o preco");

        Assert.NotEqual(doTenantA, doTenantB); // nenhum cruzamento entre empresas, nem por comparação de hashes
        Assert.NotEqual(doTenantA, comOutraChave); // sem a chave não se confirma um palpite
    }

    [Fact]
    public void Textos_diferentes_dao_hashes_diferentes()
    {
        var hasher = new KnowledgeGapHasher(ChaveA);

        Assert.NotEqual(hasher.Hash(TestContexts.TenantA, "exame 2"), hasher.Hash(TestContexts.TenantA, "exame 3"));
    }

    [Fact]
    public void O_hasher_recusa_chave_curta_e_texto_vazio_e_a_chave_original_nao_e_compartilhada()
    {
        Assert.Throws<ArgumentException>(() => new KnowledgeGapHasher(new byte[31]));
        var chave = (byte[])ChaveA.Clone();
        var hasher = new KnowledgeGapHasher(chave);
        var antes = hasher.Hash(TestContexts.TenantA, "x");

        Array.Clear(chave); // alterar o vetor do chamador depois não muda o hasher

        Assert.Equal(antes, hasher.Hash(TestContexts.TenantA, "x"));
        Assert.Throws<ArgumentException>(() => hasher.Hash(TestContexts.TenantA, string.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sem_chave_o_hasher_nao_existe_e_o_registro_fica_desligado(string? vazio)
    {
        Assert.Null(KnowledgeGapHasher.FromBase64(vazio));
    }

    [Fact]
    public void Chave_em_Base64_valida_funciona_e_a_invalida_ou_curta_recusa_sem_ecoar_o_valor()
    {
        Assert.NotNull(KnowledgeGapHasher.FromBase64(Convert.ToBase64String(ChaveA)));

        var invalida = Assert.Throws<FormatException>(() => KnowledgeGapHasher.FromBase64("isto-nao-e-base64!!"));
        var curta = Assert.Throws<FormatException>(() => KnowledgeGapHasher.FromBase64(Convert.ToBase64String(new byte[16])));

        Assert.DoesNotContain("isto-nao-e-base64", invalida.Message);
        Assert.DoesNotContain(Convert.ToBase64String(new byte[16]), curta.Message);
    }

    [Fact]
    public void As_opcoes_do_Agents_recusam_chave_invalida_na_partida_e_aceitam_chave_ausente_ou_valida()
    {
        new AgentsOptions().Validate();
        new AgentsOptions { KnowledgeGapHashKey = Convert.ToBase64String(ChaveA) }.Validate();

        var ruim = Assert.Throws<InvalidOperationException>(() => new AgentsOptions { KnowledgeGapHashKey = "curta" }.Validate());

        Assert.DoesNotContain("curta", ruim.Message.Replace("KnowledgeGapHashKey", string.Empty, StringComparison.Ordinal));
    }

    // A decisão

    private static KnowledgeGap Aberta() => KnowledgeGap.Open(TestContexts.TenantA, new KnowledgeGapHasher(ChaveA).Hash(TestContexts.TenantA, "pergunta"), KnowledgeGapKind.NoMatch, T0);

    [Fact]
    public void A_lacuna_nasce_aberta_com_hash_e_sem_decisao()
    {
        var lacuna = Aberta();

        Assert.Equal((KnowledgeGapStatus.Open, KnowledgeGapKind.NoMatch, 1), (lacuna.Status, lacuna.Kind, lacuna.Revision));
        Assert.Null(lacuna.DecidedAt);
        Assert.Null(lacuna.DecidedBy);
        Assert.Null(lacuna.PromotedVersion);
        Assert.Equal(KnowledgeGapHasher.HashLength, lacuna.QuestionHash.Length);
        Assert.Throws<ArgumentException>(() => KnowledgeGap.Open(TestContexts.TenantA, new byte[5], KnowledgeGapKind.NoMatch, T0));
    }

    [Fact]
    public void Descartar_registra_quem_e_quando_e_repetir_nao_muda_nada()
    {
        var lacuna = Aberta();

        lacuna.Dismiss(T0.AddMinutes(1), Admin);
        var revisao = lacuna.Revision;
        lacuna.Dismiss(T0.AddMinutes(9), Guid.NewGuid());

        Assert.Equal((KnowledgeGapStatus.Dismissed, T0.AddMinutes(1), Admin), (lacuna.Status, lacuna.DecidedAt, lacuna.DecidedBy));
        Assert.Equal(revisao, lacuna.Revision);
    }

    [Fact]
    public void Enviar_ao_rascunho_so_vale_para_lacuna_aberta_e_depois_nao_se_descarta()
    {
        var lacuna = Aberta();

        lacuna.MarkPromoted(T0.AddMinutes(2), Admin);
        lacuna.SetPromotedVersion(3);

        Assert.Equal((KnowledgeGapStatus.Promoted, 3, Admin), (lacuna.Status, lacuna.PromotedVersion, lacuna.DecidedBy));
        Assert.Throws<KnowledgeGapStateException>(() => lacuna.MarkPromoted(T0, Admin));
        Assert.Throws<KnowledgeGapStateException>(() => lacuna.Dismiss(T0, Admin));
    }

    [Fact]
    public void Uma_lacuna_descartada_nao_vai_ao_rascunho_e_a_versao_so_se_registra_em_quem_foi_enviado()
    {
        var descartada = Aberta();
        descartada.Dismiss(T0, Admin);
        var aberta = Aberta();

        Assert.Throws<KnowledgeGapStateException>(() => descartada.MarkPromoted(T0, Admin));
        Assert.Throws<KnowledgeGapStateException>(() => aberta.SetPromotedVersion(1));
        aberta.MarkPromoted(T0, Admin);
        Assert.Throws<KnowledgeGapStateException>(() => aberta.SetPromotedVersion(0));
    }

    [Fact]
    public void A_ocorrencia_guarda_so_referencias()
    {
        var ocorrencia = KnowledgeGapOccurrence.Create(TestContexts.TenantA, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0);

        var campos = typeof(KnowledgeGapOccurrence).GetProperties().Select(p => p.PropertyType).Distinct().ToList();

        Assert.DoesNotContain(typeof(string), campos); // nenhum campo de texto: nem pergunta, nem resposta
        Assert.Equal(TestContexts.TenantA, ocorrencia.TenantId);
    }
}
