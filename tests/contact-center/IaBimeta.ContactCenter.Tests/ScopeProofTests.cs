using IaBimeta.ContactCenter.Domain;

namespace IaBimeta.ContactCenter.Tests;

/// <summary>
/// As cinco provas de identidade (<see cref="ScopeProof"/>) são funções puras: o que decidem é o que toda leitura, projeção, reconciliação, fronteira e execução
/// privilegiada usa. Aqui cada uma é provada contra um oráculo escrito à parte, em TODAS as combinações de {conhecido igual, conhecido diferente, desconhecido}, e a
/// regra comum é afirmada: o desconhecido NUNCA é "igual".
/// </summary>
public sealed class ScopeProofTests
{
    private static readonly Guid Unidade = Guid.NewGuid();
    private static readonly Guid OutraUnidade = Guid.NewGuid();
    private static readonly Guid Setor = Guid.NewGuid();

    private static PendingOperation Pendente(PendingKind tipo, HandlingBasis? basis, Guid? unidade = null, Guid? usuario = null) =>
        new(tipo, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), usuario, unidade, null, null, null, "teste", DateTimeOffset.UnixEpoch, basis);

    // 1. Identidade do atendimento

    [Fact]
    public void Identidade_do_atendimento_da_sessao_so_e_igual_com_periodo_conhecido_igual_em_atendimento_humano()
    {
        foreach (long? doItem in new long?[] { null, 1, 2 })
        {
            foreach (var humano in new[] { true, false })
            {
                foreach (var daSessao in new long[] { 1, 2 })
                {
                    var esperado = doItem is null
                        ? ProofVerdict.Unknown
                        : humano && doItem == daSessao ? ProofVerdict.Same : ProofVerdict.Other;
                    Assert.Equal(esperado, ScopeProof.AttendanceOfSession(doItem, humano, daSessao));
                }
            }
        }
    }

    [Fact]
    public void Identidade_do_atendimento_do_evento_exige_os_dois_periodos_conhecidos()
    {
        foreach (long? doItem in new long?[] { null, 1, 2 })
        {
            foreach (long? doEvento in new long?[] { null, 1, 2 })
            {
                var esperado = doItem is null || doEvento is null
                    ? ProofVerdict.Unknown
                    : doItem == doEvento ? ProofVerdict.Same : ProofVerdict.Other;
                Assert.Equal(esperado, ScopeProof.AttendanceOfEvent(doItem, doEvento));
            }
        }
    }

    // 2. Continuidade da colocação

    [Fact]
    public void Continuidade_da_colocacao_da_sessao_prende_a_geracao_e_nao_so_a_unidade()
    {
        foreach (long? doItem in new long?[] { null, 3, 5 })
        {
            foreach (var daSessao in new long[] { 3, 5 })
            {
                foreach (var mesmaUnidade in new[] { true, false })
                {
                    var unidadeDaSessao = mesmaUnidade ? Unidade : OutraUnidade;
                    var esperado = doItem is null
                        ? ProofVerdict.Unknown
                        : doItem == daSessao && mesmaUnidade ? ProofVerdict.Same : ProofVerdict.Other;
                    Assert.Equal(esperado, ScopeProof.PlacementOfSession(doItem, Unidade, Setor, daSessao, unidadeDaSessao, Setor));
                }
            }
        }

        // A → B → A: a unidade e o setor são de novo os do item, e a geração não é: outra colocação.
        Assert.Equal(ProofVerdict.Other, ScopeProof.PlacementOfSession(3, Unidade, Setor, 5, Unidade, Setor));
    }

    [Fact]
    public void Continuidade_da_colocacao_do_evento_exige_as_duas_geracoes_conhecidas()
    {
        foreach (long? doItem in new long?[] { null, 3, 5 })
        {
            foreach (long? deOrigem in new long?[] { null, 3, 5 })
            {
                var esperado = doItem is null || deOrigem is null
                    ? ProofVerdict.Unknown
                    : doItem == deOrigem ? ProofVerdict.Same : ProofVerdict.Other;
                Assert.Equal(esperado, ScopeProof.PlacementOfEvent(doItem, deOrigem));
            }
        }
    }

    // 3. Origem da fronteira

    [Fact]
    public void A_origem_do_limite_guardado_so_e_valida_no_modelo_certificado_e_na_geracao_do_item()
    {
        Assert.Equal(BoundaryOrigin.None, ScopeProof.StoredLimit(null, null, QueueItemBoundaryModel.Certified, 3));

        // Sem o modelo certificado (registro anterior), ou sem origem, ou sem a geração do item: desconhecida, nunca válida.
        Assert.Equal(BoundaryOrigin.Unknown, ScopeProof.StoredLimit(5, 3, QueueItemBoundaryModel.Legacy, 3));
        Assert.Equal(BoundaryOrigin.Unknown, ScopeProof.StoredLimit(5, null, QueueItemBoundaryModel.Certified, 3));
        Assert.Equal(BoundaryOrigin.Unknown, ScopeProof.StoredLimit(5, 3, QueueItemBoundaryModel.Certified, null));
        Assert.Equal(BoundaryOrigin.Unknown, ScopeProof.StoredLimit(5, null, QueueItemBoundaryModel.Legacy, null));

        Assert.Equal(BoundaryOrigin.Valid, ScopeProof.StoredLimit(5, 3, QueueItemBoundaryModel.Certified, 3));
        Assert.Equal(BoundaryOrigin.BeforeBindingChange, ScopeProof.StoredLimit(5, 2, QueueItemBoundaryModel.Certified, 3));
        Assert.Equal(BoundaryOrigin.Invalidated, ScopeProof.StoredLimit(5, 4, QueueItemBoundaryModel.Certified, 3));
    }

    [Fact]
    public void A_fronteira_nova_so_vale_se_a_origem_e_a_geracao_do_item()
    {
        Assert.Equal(BoundaryOrigin.Valid, ScopeProof.CandidateLimit(3, 3));
        Assert.Equal(BoundaryOrigin.OtherGeneration, ScopeProof.CandidateLimit(5, 3));
        Assert.Equal(BoundaryOrigin.Unknown, ScopeProof.CandidateLimit(null, 3));
        Assert.Equal(BoundaryOrigin.Unknown, ScopeProof.CandidateLimit(3, null));
        Assert.Equal(BoundaryOrigin.Unknown, ScopeProof.CandidateLimit(null, null));
    }

    // 4. Identidade da operação

    [Fact]
    public void A_operacao_e_propria_pelo_identificador_nunca_pelo_destino_e_a_transicao_derivada_tambem_e()
    {
        var pendente = Pendente(PendingKind.TransferQueue, new HandlingBasis(1, null, false, 3), Unidade, Guid.NewGuid());

        Assert.Equal(OperationVerdict.Own, ScopeProof.TransitionOwner(pendente, pendente.TransitionId));
        Assert.Equal(OperationVerdict.Own, ScopeProof.TransitionOwner(pendente, ScopeProof.DerivedTransitionId(pendente.TransitionId)));
        Assert.Equal(OperationVerdict.Foreign, ScopeProof.TransitionOwner(pendente, Guid.NewGuid()));
        Assert.Equal(OperationVerdict.Foreign, ScopeProof.TransitionOwner(pendente, Guid.Empty));
        Assert.Equal(OperationVerdict.Foreign, ScopeProof.TransitionOwner(null, pendente.TransitionId));

        Assert.Equal(OperationVerdict.Own, ScopeProof.TransferOwner(pendente, pendente.TransferId));
        Assert.Equal(OperationVerdict.Foreign, ScopeProof.TransferOwner(pendente, Guid.NewGuid())); // outro encaminhamento, mesmo para o mesmo destino
        Assert.Equal(OperationVerdict.Foreign, ScopeProof.TransferOwner(pendente, pendente.TransitionId)); // o identificador do pedido não é o do encaminhamento
        Assert.Equal(OperationVerdict.Foreign, ScopeProof.TransferOwner(null, pendente.TransferId));
    }

    // 5. Completude da base

    [Fact]
    public void A_base_so_esta_completa_com_o_estado_de_atendimento_e_a_geracao_da_colocacao()
    {
        var semBase = ScopeProof.BaseOf(Pendente(PendingKind.Assign, null));
        Assert.False(semBase.IsComplete);
        Assert.Equal(BaseGaps.NoBase | BaseGaps.NoPlacementEpoch, semBase.Gaps);

        var semGeracao = ScopeProof.BaseOf(Pendente(PendingKind.Assign, new HandlingBasis(1, null)));
        Assert.False(semGeracao.IsComplete);
        Assert.Equal(BaseGaps.NoPlacementEpoch, semGeracao.Gaps);

        var completa = ScopeProof.BaseOf(Pendente(PendingKind.Assign, new HandlingBasis(1, null, false, 3)));
        Assert.True(completa.IsComplete);
        Assert.Equal(BaseGaps.None, completa.Gaps);
    }

    [Fact]
    public void A_geracao_e_o_periodo_esperados_derivam_da_base_e_nao_do_estado_de_agora()
    {
        // A geração: a da autorização, mais uma se a operação muda a unidade.
        Assert.Equal(3L, ScopeProof.ExpectedEpoch(Pendente(PendingKind.Assign, new HandlingBasis(1, null, false, 3))));
        Assert.Equal(4L, ScopeProof.ExpectedEpoch(Pendente(PendingKind.TransferQueue, new HandlingBasis(1, null, false, 3), Unidade)));
        Assert.Null(ScopeProof.ExpectedEpoch(Pendente(PendingKind.Assign, new HandlingBasis(1, null))));
        Assert.Null(ScopeProof.ExpectedEpoch(Pendente(PendingKind.Assign, null)));

        // O período: entrar em humano vindo da IA abre o seguinte; já em atendimento humano, o mesmo; a devolução à IA mantém o período que acabou.
        Assert.Equal(1L, ScopeProof.ExpectedPeriod(Pendente(PendingKind.Enter, new HandlingBasis(0, null, true, 3))));
        Assert.Equal(5L, ScopeProof.ExpectedPeriod(Pendente(PendingKind.Enter, new HandlingBasis(4, null, true, 3))));
        Assert.Equal(2L, ScopeProof.ExpectedPeriod(Pendente(PendingKind.Enter, new HandlingBasis(2, null, false, 3))));
        Assert.Equal(2L, ScopeProof.ExpectedPeriod(Pendente(PendingKind.Assign, new HandlingBasis(2, null, false, 3))));
        Assert.Equal(2L, ScopeProof.ExpectedPeriod(Pendente(PendingKind.ReturnToAi, new HandlingBasis(2, null, false, 3))));
        Assert.Null(ScopeProof.ExpectedPeriod(Pendente(PendingKind.Enter, null)));
    }

    // A regra comum

    [Fact]
    public void O_desconhecido_nunca_e_igual_e_combinar_provas_so_da_igual_se_todas_forem()
    {
        Assert.Equal(ProofVerdict.Unknown, default(ProofVerdict)); // o valor padrão é o desconhecido: falha segura
        Assert.Equal(OperationVerdict.Foreign, default(OperationVerdict));

        var todos = new[] { ProofVerdict.Same, ProofVerdict.Other, ProofVerdict.Unknown };
        foreach (var a in todos)
        {
            foreach (var b in todos)
            {
                var esperado = a == ProofVerdict.Other || b == ProofVerdict.Other
                    ? ProofVerdict.Other
                    : a == ProofVerdict.Same && b == ProofVerdict.Same ? ProofVerdict.Same : ProofVerdict.Unknown;
                Assert.Equal(esperado, ScopeProof.All(a, b));
            }
        }

        Assert.NotEqual(ProofVerdict.Same, ScopeProof.All(ProofVerdict.Same, ProofVerdict.Unknown));
    }
}
