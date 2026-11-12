using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using IaBimeta.Workflows.Infrastructure;

namespace IaBimeta.Workflows.Tests;

public sealed class ActivationRuleTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("a0000000-0000-0000-0000-00000000000a"));
    private static readonly Guid Org = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
    private static readonly DateTimeOffset Agora = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static FlowActivation Nova(Guid? unidade = null, Guid? canal = null, int parcela = 100, int prioridade = 0) =>
        FlowActivation.Create(Tenant, Org, unidade, canal, "fluxo", prioridade, parcela, OnFailurePolicy.Agent, null, Agora);

    [Fact]
    public void A_ativacao_nasce_desligada_e_casa_pela_organizacao_unidade_e_canal()
    {
        var unidade = Guid.NewGuid();
        var canal = Guid.NewGuid();
        var geral = Nova();
        var daUnidade = Nova(unidade);
        var doCanal = Nova(canal: canal);

        Assert.False(geral.Enabled);
        Assert.True(geral.Matches(Org, null, canal));
        Assert.False(geral.Matches(Guid.NewGuid(), null, canal)); // outra organização nunca casa
        Assert.True(daUnidade.Matches(Org, unidade, canal));
        Assert.False(daUnidade.Matches(Org, Guid.NewGuid(), canal));
        Assert.False(daUnidade.Matches(Org, null, canal));
        Assert.True(doCanal.Matches(Org, unidade, canal));
        Assert.False(doCanal.Matches(Org, unidade, Guid.NewGuid()));
        Assert.True(Nova(unidade, canal).Specificity > daUnidade.Specificity);
    }

    [Fact]
    public void A_parcela_e_deterministica_para_o_mesmo_contato_e_proxima_da_porcentagem_no_conjunto()
    {
        var a = Nova(parcela: 30);
        var contato = Guid.NewGuid();
        Assert.Equal(a.InRollout(contato), a.InRollout(contato));

        var dentro = Enumerable.Range(0, 4000).Count(_ => a.InRollout(Guid.NewGuid()));
        Assert.InRange(dentro, 1000, 1400); // 30% de 4000 = 1200, com folga estatística

        Assert.All(Enumerable.Range(0, 50), _ => Assert.True(Nova(parcela: 100).InRollout(Guid.NewGuid())));
        Assert.All(Enumerable.Range(0, 50), _ => Assert.False(Nova(parcela: 0).InRollout(Guid.NewGuid())));
    }

    [Fact]
    public void A_decisao_de_roteamento_so_vai_do_fluxo_para_o_agente_nunca_o_contrario()
    {
        var d = RoutingDecision.Create(Tenant, Guid.NewGuid(), IaBimeta.Workflows.Contracts.AutomationOwner.Flow, null, Agora);
        d.ReleaseToAgent(Agora);
        Assert.Equal(IaBimeta.Workflows.Contracts.AutomationOwner.Agent, d.Owner);
        var revisao = d.Revision;
        d.ReleaseToAgent(Agora.AddMinutes(1)); // idempotente
        Assert.Equal(revisao, d.Revision);
    }

    [Fact]
    public void As_opcoes_do_runtime_nascem_desligadas_e_recusam_limites_fora_da_faixa()
    {
        var opcoes = new WorkflowsOptions();
        Assert.False(opcoes.Enabled);
        opcoes.Validate();
        Assert.Throws<InvalidOperationException>(() => new WorkflowsOptions { MaxRunIterations = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new WorkflowsOptions { CapabilityBudget = TimeSpan.FromMinutes(5) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new WorkflowsOptions { MaxActiveInstancesPerTenant = 0 }.Validate());
    }
}

public sealed class FlowCipherTests
{
    private static readonly TenantId TenantA = new(Guid.Parse("a0000000-0000-0000-0000-00000000000a"));
    private static readonly TenantId TenantB = new(Guid.Parse("b0000000-0000-0000-0000-00000000000b"));

    private static AesGcmFlowCipher Cipher(string? ativa = "k1") => new(new WorkflowsSecretOptions
    {
        ActiveKeyId = ativa,
        Keys = new Dictionary<string, string> { ["k1"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray()), ["k2"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 50)).ToArray()) },
    });

    [Fact]
    public void Cifra_e_decifra_so_no_mesmo_tenant_recurso_e_finalidade()
    {
        var c = Cipher();
        var recurso = Guid.NewGuid();
        var texto = c.Protect("{\"cpf\":\"123.456.789-09\"}", TenantA, recurso, FlowPurposes.Variables);

        Assert.DoesNotContain("123.456", texto.Ciphertext);
        Assert.Equal("{\"cpf\":\"123.456.789-09\"}", c.Unprotect(texto, TenantA, recurso, FlowPurposes.Variables));
        Assert.Throws<WorkflowsUnavailableException>(() => c.Unprotect(texto, TenantB, recurso, FlowPurposes.Variables));
        Assert.Throws<WorkflowsUnavailableException>(() => c.Unprotect(texto, TenantA, Guid.NewGuid(), FlowPurposes.Variables));
        Assert.Throws<WorkflowsUnavailableException>(() => c.Unprotect(texto, TenantA, recurso, FlowPurposes.Effect));
    }

    [Fact]
    public void Texto_adulterado_ou_com_chave_inexistente_nunca_devolve_nada_e_o_nonce_muda_a_cada_cifra()
    {
        var c = Cipher();
        var recurso = Guid.NewGuid();
        var a = c.Protect("x", TenantA, recurso, FlowPurposes.Effect);
        var b = c.Protect("x", TenantA, recurso, FlowPurposes.Effect);
        Assert.NotEqual(a.Ciphertext, b.Ciphertext);

        var adulterado = a with { Ciphertext = a.Ciphertext[..^3] + (a.Ciphertext.EndsWith("AAA") ? "BBB" : "AAA") };
        Assert.Throws<WorkflowsUnavailableException>(() => c.Unprotect(adulterado, TenantA, recurso, FlowPurposes.Effect));
        Assert.Throws<WorkflowsUnavailableException>(() => c.Unprotect(a with { KeyId = "inexistente" }, TenantA, recurso, FlowPurposes.Effect));
        Assert.Throws<WorkflowsUnavailableException>(() => c.Unprotect(new FlowCipherText("k1", "texto-em-claro"), TenantA, recurso, FlowPurposes.Effect));
    }

    [Fact]
    public void A_rotacao_de_chave_mantem_o_texto_antigo_legivel_e_sem_chave_ativa_a_gravacao_falha_fechada()
    {
        var recurso = Guid.NewGuid();
        var antigo = Cipher("k1").Protect("dado", TenantA, recurso, FlowPurposes.Variables);
        Assert.Equal("k1", antigo.KeyId);
        Assert.Equal("dado", Cipher("k2").Unprotect(antigo, TenantA, recurso, FlowPurposes.Variables));
        Assert.Equal("k2", Cipher("k2").Protect("dado", TenantA, recurso, FlowPurposes.Variables).KeyId);

        var semChave = Cipher(ativa: null);
        Assert.False(semChave.IsConfigured);
        Assert.Throws<WorkflowsUnavailableException>(() => semChave.Protect("dado", TenantA, recurso, FlowPurposes.Variables));
        Assert.True(Cipher().IsConfigured);
    }
}
