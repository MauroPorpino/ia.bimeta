using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Tests;

/// <summary>A máquina de estados do item da fila: o que cada operação faz, o que recusa e o que registra no histórico.</summary>
public sealed class QueueItemTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly Guid Sessao = Guid.NewGuid();
    private static readonly Guid Organizacao = Guid.NewGuid();
    private static readonly Guid FilaA = Guid.NewGuid();
    private static readonly Guid FilaB = Guid.NewGuid();
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Beto = Guid.NewGuid();

    private static QueueItem NovoItem(Guid? unidade = null, Guid? setor = null) =>
        QueueItem.Enter(Tenant, Sessao, Organizacao, unidade, setor, FilaA, Guid.NewGuid(), null, "ai-requested", Agora);

    /// <summary>Um item em espera: a entrada já concluída.</summary>
    private static QueueItem ItemEmEspera()
    {
        var item = NovoItem();
        item.CompletePending(2, Agora);
        return item;
    }

    private static QueueItem ItemComAna()
    {
        var item = ItemEmEspera();
        item.BeginAssign(Ana, Guid.NewGuid(), Ana, "claim", Agora);
        item.CompletePending(3, Agora);
        return item;
    }

    [Fact]
    public void A_entrada_nasce_em_espera_na_versao_1_com_a_entrada_pendente()
    {
        var item = NovoItem(Guid.NewGuid());

        Assert.Equal((QueueItemStatus.Waiting, 1L, null), (item.Status, item.Version, item.AssignedUserId));
        Assert.Equal(FilaA, item.QueueId);
        var pendente = Assert.IsType<PendingOperation>(item.Pending);
        Assert.Equal((PendingKind.Enter, FilaA, "ai-requested"), (pendente.Kind, pendente.QueueId, pendente.Reason));
        Assert.True(item.HasPending);
    }

    [Fact]
    public void As_chaves_de_idempotencia_da_operacao_sao_o_proprio_identificador_e_um_identificador_de_encaminhamento_distinto()
    {
        var operacao = Guid.NewGuid();
        var item = QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, operacao, null, "handoff", Agora);

        var pendente = item.Pending!;
        Assert.Equal(operacao, pendente.OperationId);
        Assert.Equal(operacao, pendente.TransitionId);
        Assert.NotEqual(Guid.Empty, pendente.TransferId);
        Assert.NotEqual(operacao, pendente.TransferId);
    }

    [Fact]
    public void Concluir_a_entrada_registra_o_historico_sem_fila_de_origem_e_limpa_a_pendencia()
    {
        var item = NovoItem();
        var operacao = item.Pending!.OperationId;

        var historico = item.CompletePending(7, Agora.AddSeconds(1));

        Assert.False(item.HasPending);
        Assert.Equal((QueueItemStatus.Waiting, 2L), (item.Status, item.Version));
        Assert.Equal((HistoryKind.Entered, null, FilaA, operacao, 7L), (historico.Kind, historico.FromQueueId, historico.ToQueueId, historico.OperationId, historico.StateVersionAfter));
        Assert.Equal((item.Id, Sessao, "ai-requested"), (historico.ItemId, historico.SessionId, historico.Reason));
    }

    [Fact]
    public void Quem_pediu_fica_no_historico_e_o_sistema_aparece_como_nulo()
    {
        var deSistema = QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.NewGuid(), null, "handoff", Agora);
        var deUsuario = QueueItem.Enter(Tenant, Guid.NewGuid(), Organizacao, null, null, FilaA, Guid.NewGuid(), Ana, "handoff", Agora);

        Assert.Null(deSistema.CompletePending(2, Agora).ActorUserId);
        Assert.Equal(Ana, deUsuario.CompletePending(2, Agora).ActorUserId);
    }

    [Fact]
    public void Assumir_uma_conversa_em_espera_atribui_ao_atendente_e_registra_quem_assumiu()
    {
        var item = ItemEmEspera();
        var operacao = Guid.NewGuid();

        item.BeginAssign(Ana, operacao, Ana, "claim", Agora);
        var historico = item.CompletePending(3, Agora.AddSeconds(2));

        Assert.Equal((QueueItemStatus.Assigned, Ana, Agora.AddSeconds(2)), (item.Status, item.AssignedUserId, item.AssignedAt));
        Assert.Equal((HistoryKind.Assigned, null, Ana, Ana, operacao), (historico.Kind, historico.FromUserId, historico.ToUserId, historico.ActorUserId, historico.OperationId));
    }

    [Fact]
    public void Passar_para_outro_atendente_e_uma_reatribuicao_com_origem_e_destino_no_historico()
    {
        var item = ItemComAna();

        item.BeginAssign(Beto, Guid.NewGuid(), Ana, "reassign", Agora);
        var historico = item.CompletePending(4, Agora);

        Assert.Equal((QueueItemStatus.Assigned, Beto), (item.Status, item.AssignedUserId));
        Assert.Equal((HistoryKind.Reassigned, Ana, Beto), (historico.Kind, historico.FromUserId, historico.ToUserId));
    }

    [Fact]
    public void Nao_ha_dois_responsaveis_o_item_tem_uma_coluna_so_e_atribuir_ao_mesmo_atendente_e_recusado()
    {
        var item = ItemComAna();

        Assert.Throws<QueueItemStateException>(() => item.BeginAssign(Ana, Guid.NewGuid(), Ana, "claim", Agora));
        Assert.Equal(Ana, item.AssignedUserId);
        Assert.False(item.HasPending);
    }

    [Fact]
    public void Enquanto_ha_operacao_pendente_nenhuma_outra_comeca()
    {
        var item = ItemEmEspera();
        item.BeginAssign(Ana, Guid.NewGuid(), Ana, "claim", Agora);

        Assert.Throws<QueueItemStateException>(() => item.BeginAssign(Beto, Guid.NewGuid(), Beto, "claim", Agora));
        Assert.Throws<QueueItemStateException>(() => item.BeginReturnToAi(Guid.NewGuid(), null, "return-to-ai", Agora));
        Assert.Throws<QueueItemStateException>(() => item.BeginTransferToQueue(FilaB, null, null, null, Guid.NewGuid(), null, "transfer", Agora));
        Assert.Equal(Ana, item.Pending!.UserId);
    }

    [Fact]
    public void Intencao_de_assumir_nao_atribui_ate_a_conclusao()
    {
        var item = ItemEmEspera();

        item.BeginAssign(Ana, Guid.NewGuid(), Ana, "claim", Agora);

        // Só a conclusão (depois do Conversations) atribui: antes disso o item continua em espera e sem responsável.
        Assert.Equal((QueueItemStatus.Waiting, null), (item.Status, item.AssignedUserId));
        Assert.Equal(PendingKind.Assign, item.Pending!.Kind);
    }

    [Fact]
    public void Devolver_a_fila_so_cabe_em_item_atribuido_e_o_deixa_sem_responsavel()
    {
        Assert.Throws<QueueItemStateException>(() => ItemEmEspera().BeginRelease(Guid.NewGuid(), null, "release", Agora));

        var item = ItemComAna();
        item.BeginRelease(Guid.NewGuid(), Ana, "release", Agora);
        var historico = item.CompletePending(5, Agora);

        Assert.Equal((QueueItemStatus.Waiting, null, null), (item.Status, item.AssignedUserId, item.AssignedAt));
        Assert.Equal((HistoryKind.Released, Ana, null), (historico.Kind, historico.FromUserId, historico.ToUserId));
    }

    [Fact]
    public void Transferir_para_a_mesma_fila_e_recusado()
    {
        Assert.Throws<QueueItemStateException>(() => ItemEmEspera().BeginTransferToQueue(FilaA, null, null, null, Guid.NewGuid(), null, "transfer", Agora));
    }

    [Fact]
    public void Transferir_sem_atendente_de_destino_volta_a_espera_na_fila_nova_e_zera_o_responsavel()
    {
        var item = ItemComAna();

        item.BeginTransferToQueue(FilaB, null, null, null, Guid.NewGuid(), Ana, "transfer", Agora);
        var historico = item.CompletePending(6, Agora);

        Assert.Equal((FilaB, QueueItemStatus.Waiting, null), (item.QueueId, item.Status, item.AssignedUserId));
        Assert.Equal((HistoryKind.TransferredQueue, FilaA, FilaB, Ana, null), (historico.Kind, historico.FromQueueId, historico.ToQueueId, historico.FromUserId, historico.ToUserId));
    }

    [Fact]
    public void Transferir_com_atendente_de_destino_ja_o_atribui()
    {
        var item = ItemComAna();

        item.BeginTransferToQueue(FilaB, Beto, null, null, Guid.NewGuid(), Ana, "transfer", Agora);
        item.CompletePending(6, Agora);

        Assert.Equal((FilaB, QueueItemStatus.Assigned, Beto), (item.QueueId, item.Status, item.AssignedUserId));
    }

    [Fact]
    public void Transferir_para_fila_de_outra_unidade_leva_a_unidade_e_o_setor_junto_so_na_conclusao()
    {
        var unidadeAntiga = Guid.NewGuid();
        var unidadeNova = Guid.NewGuid();
        var setorNovo = Guid.NewGuid();
        var item = QueueItem.Enter(Tenant, Sessao, Organizacao, unidadeAntiga, null, FilaA, Guid.NewGuid(), null, "handoff", Agora);
        item.CompletePending(2, Agora);

        item.BeginTransferToQueue(FilaB, null, unidadeNova, setorNovo, Guid.NewGuid(), null, "transfer", Agora);

        Assert.Equal((unidadeAntiga, (Guid?)null), (item.BusinessUnitId, item.DepartmentId));
        Assert.Equal((unidadeNova, setorNovo), (item.Pending!.BusinessUnitId, item.Pending.DepartmentId));

        item.CompletePending(3, Agora);
        Assert.Equal((unidadeNova, setorNovo), (item.BusinessUnitId, item.DepartmentId));
    }

    [Fact]
    public void Setor_de_destino_sem_unidade_e_recusado()
    {
        Assert.Throws<ContactCenterValidationException>(
            () => ItemEmEspera().BeginTransferToQueue(FilaB, null, null, Guid.NewGuid(), Guid.NewGuid(), null, "transfer", Agora));
    }

    [Fact]
    public void Devolver_a_IA_encerra_o_item_e_nada_mais_comeca_nele()
    {
        var item = ItemComAna();

        item.BeginReturnToAi(Guid.NewGuid(), Ana, "return-to-ai", Agora);
        var historico = item.CompletePending(7, Agora.AddMinutes(1));

        Assert.Equal((QueueItemStatus.Closed, null, "returned-to-ai", Agora.AddMinutes(1)), (item.Status, item.AssignedUserId, item.CloseReason, item.ClosedAt));
        Assert.Equal((HistoryKind.ReturnedToAi, Ana, null), (historico.Kind, historico.FromUserId, historico.ToUserId));
        Assert.Throws<QueueItemStateException>(() => item.BeginAssign(Beto, Guid.NewGuid(), Beto, "claim", Agora));
        Assert.Throws<QueueItemStateException>(() => item.BeginReturnToAi(Guid.NewGuid(), null, "return-to-ai", Agora));
    }

    [Fact]
    public void Desistir_de_uma_atribuicao_devolve_o_item_ao_que_era_sem_historico()
    {
        var item = ItemEmEspera();
        item.BeginAssign(Ana, Guid.NewGuid(), Ana, "claim", Agora);
        var versao = item.Version;

        item.CancelPending("operation-rejected", Agora);

        Assert.Equal((QueueItemStatus.Waiting, null, false), (item.Status, item.AssignedUserId, item.HasPending));
        Assert.Equal(versao + 1, item.Version);
    }

    [Fact]
    public void Desistir_da_entrada_encerra_o_item_porque_a_conversa_nunca_chegou_a_ficar_na_fila()
    {
        var item = NovoItem();

        item.CancelPending("operation-rejected", Agora);

        Assert.Equal((QueueItemStatus.Closed, "operation-rejected", false), (item.Status, item.CloseReason, item.HasPending));
    }

    [Fact]
    public void Sessao_encerrada_fecha_o_item_descarta_a_pendencia_e_so_registra_uma_vez()
    {
        var item = ItemComAna();
        item.BeginRelease(Guid.NewGuid(), Ana, "release", Agora);
        var operacao = Guid.NewGuid();

        var historico = item.CloseBecauseSessionEnded(operacao, "session-closed", Agora);

        Assert.NotNull(historico);
        Assert.Equal((HistoryKind.SessionClosed, Ana, operacao), (historico!.Kind, historico.FromUserId, historico.OperationId));
        Assert.Equal((QueueItemStatus.Closed, null, false), (item.Status, item.AssignedUserId, item.HasPending));
        Assert.Null(item.CloseBecauseSessionEnded(Guid.NewGuid(), "session-closed", Agora));
    }

    [Fact]
    public void Anotar_falha_conta_a_tentativa_guarda_so_o_tipo_e_nao_mexe_na_versao()
    {
        var item = ItemEmEspera();
        item.BeginAssign(Ana, Guid.NewGuid(), Ana, "claim", Agora);
        var versao = item.Version;

        item.RecordAttemptFailure("NpgsqlException", Agora.AddSeconds(5));
        item.RecordAttemptFailure(new string('x', 500), Agora.AddSeconds(6));

        Assert.Equal(2, item.PendingAttempts);
        Assert.Equal(QueueItem.ErrorMaxLength, item.PendingLastError!.Length);
        Assert.Equal(versao, item.Version);
        Assert.Equal(Ana, item.Pending!.UserId);
    }

    [Fact]
    public void Anotar_falha_em_item_sem_pendencia_nao_faz_nada()
    {
        var item = ItemEmEspera();

        item.RecordAttemptFailure("NpgsqlException", Agora);

        Assert.Equal((0, null), (item.PendingAttempts, item.PendingLastError));
    }

    [Fact]
    public void Concluir_sem_pendencia_e_um_erro_de_estado()
    {
        Assert.Throws<QueueItemStateException>(() => ItemEmEspera().CompletePending(1, Agora));
        Assert.Throws<QueueItemStateException>(() => ItemEmEspera().CancelPending("operation-rejected", Agora));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Com Espaco")]
    [InlineData("MAIUSCULA")]
    [InlineData("-comeca-com-hifen")]
    public void Motivo_fora_do_formato_de_codigo_e_recusado_para_nao_carregar_texto_livre(string? motivo)
    {
        Assert.Throws<ContactCenterValidationException>(() => QueueItem.ValidateReason(motivo));
        Assert.Throws<ContactCenterValidationException>(() => QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.NewGuid(), null, motivo!, Agora));
    }

    [Fact]
    public void Motivo_no_formato_de_codigo_e_no_limite_e_aceito_e_acima_do_limite_e_recusado()
    {
        Assert.Equal("ai-processing-failed", QueueItem.ValidateReason("ai-processing-failed"));
        Assert.Equal(QueueItem.ReasonMaxLength, QueueItem.ValidateReason(new string('a', QueueItem.ReasonMaxLength)).Length);
        Assert.Throws<ContactCenterValidationException>(() => QueueItem.ValidateReason(new string('a', QueueItem.ReasonMaxLength + 1)));
    }

    [Fact]
    public void Identificadores_vazios_sao_recusados()
    {
        Assert.Throws<ContactCenterValidationException>(() => QueueItem.Enter(Tenant, Guid.Empty, Organizacao, null, null, FilaA, Guid.NewGuid(), null, "handoff", Agora));
        Assert.Throws<ContactCenterValidationException>(() => QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.Empty, null, "handoff", Agora));
        Assert.Throws<ContactCenterValidationException>(() => ItemEmEspera().BeginAssign(Guid.Empty, Guid.NewGuid(), null, "claim", Agora));
        Assert.Throws<ContactCenterValidationException>(() => ItemEmEspera().BeginAssign(Ana, Guid.Empty, null, "claim", Agora));
    }

    [Fact]
    public void Setor_da_sessao_sem_unidade_e_recusado_na_entrada()
    {
        Assert.Throws<ContactCenterValidationException>(() => QueueItem.Enter(Tenant, Sessao, Organizacao, null, Guid.NewGuid(), FilaA, Guid.NewGuid(), null, "handoff", Agora));
    }

    // Cobertura da fila: quem está em uma fila não ganha acesso a conversas fora do escopo dela.

    [Fact]
    public void Fila_da_organizacao_inteira_cobre_qualquer_unidade_e_setor_dela_e_nada_de_outra_organizacao()
    {
        var fila = ServiceQueue.Create(Tenant, Organizacao, null, null, "Geral", Agora);

        Assert.True(fila.Covers(Organizacao, null, null));
        Assert.True(fila.Covers(Organizacao, Guid.NewGuid(), Guid.NewGuid()));
        Assert.False(fila.Covers(Guid.NewGuid(), null, null));
    }

    [Fact]
    public void Fila_de_unidade_so_cobre_a_unidade_dela_e_nunca_conversa_sem_unidade()
    {
        var unidade = Guid.NewGuid();
        var fila = ServiceQueue.Create(Tenant, Organizacao, unidade, null, "Filial", Agora);

        Assert.True(fila.Covers(Organizacao, unidade, null));
        Assert.True(fila.Covers(Organizacao, unidade, Guid.NewGuid()));
        Assert.False(fila.Covers(Organizacao, Guid.NewGuid(), null));
        Assert.False(fila.Covers(Organizacao, null, null));
    }

    [Fact]
    public void Fila_de_setor_so_cobre_o_setor_dela()
    {
        var unidade = Guid.NewGuid();
        var setor = Guid.NewGuid();
        var fila = ServiceQueue.Create(Tenant, Organizacao, unidade, setor, "Coleta", Agora);

        Assert.True(fila.Covers(Organizacao, unidade, setor));
        Assert.False(fila.Covers(Organizacao, unidade, Guid.NewGuid()));
        Assert.False(fila.Covers(Organizacao, unidade, null));
        Assert.False(fila.Covers(Organizacao, Guid.NewGuid(), setor));
    }

    // A base da autorização (modo, período e responsável) na intenção

    [Fact]
    public void A_entrada_guarda_a_base_de_IA_com_a_intencao_e_a_conclusao_a_limpa()
    {
        var base_ = new HandlingBasis(0, null, InAiMode: true);
        var item = QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: base_);

        Assert.Equal(base_, item.Pending!.Basis);
        Assert.True(item.PendingBasisAiMode);

        item.CompletePending(2, Agora, handlingPeriod: 1);

        Assert.Null(item.Pending);
        Assert.False(item.PendingBasisAiMode);
        Assert.Null(item.PendingBasisPeriod);
    }

    [Fact]
    public void A_base_de_atendimento_humano_continua_como_antes_e_a_de_IA_nunca_tem_responsavel()
    {
        var humano = QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(1, Ana));
        Assert.Equal((new HandlingBasis(1, Ana), false), (humano.Pending!.Basis, humano.PendingBasisAiMode));

        Assert.Throws<ContactCenterValidationException>(
            () => QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(1, Ana, InAiMode: true)));
        Assert.Throws<ContactCenterValidationException>(
            () => QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(-1, null, InAiMode: true)));
        Assert.Throws<ContactCenterValidationException>(
            () => QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(0, Ana)));
        Assert.Throws<ContactCenterValidationException>(
            () => QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(1, Guid.Empty)));
    }

    // A transferência de unidade que o Conversations confirmou só em parte

    private static readonly Guid Matriz = Guid.NewGuid();
    private static readonly Guid Filial = Guid.NewGuid();
    private static readonly Guid SetorDaFilial = Guid.NewGuid();

    /// <summary>A Ana atende, na matriz e na fila A, e pede a transferência para a fila B, da filial (com o setor): a operação está pendente.</summary>
    private static QueueItem ItemComTransferenciaDeUnidadePendente()
    {
        var item = QueueItem.Enter(Tenant, Sessao, Organizacao, Matriz, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(0, null, InAiMode: true));
        item.CompletePending(2, Agora, handlingPeriod: 1);
        item.BeginAssign(Ana, Guid.NewGuid(), Ana, "claim", Agora, basis: new HandlingBasis(1, null));
        item.CompletePending(3, Agora);
        item.BeginTransferToQueue(FilaB, null, Filial, SetorDaFilial, Guid.NewGuid(), Ana, "transfer", Agora, basis: new HandlingBasis(1, Ana));
        return item;
    }

    [Fact]
    public void Concluir_em_parte_leva_o_item_para_a_fila_a_unidade_e_o_setor_de_destino_e_espelha_o_responsavel_de_agora()
    {
        var item = ItemComTransferenciaDeUnidadePendente();
        var operacao = item.Pending!.OperationId;
        var espelho = Guid.NewGuid();

        var historico = item.CompletePartialTransfer(ProofVerdict.Same, Beto, 9, espelho, Agora);

        Assert.Equal((FilaB, Filial, SetorDaFilial, QueueItemStatus.Assigned, Beto), (item.QueueId, item.BusinessUnitId, item.DepartmentId, item.Status, item.AssignedUserId));
        Assert.Null(item.Pending);
        Assert.Equal(9, item.ObservedStateVersion);
        Assert.Equal(2, historico.Count);
        Assert.Equal((HistoryKind.TransferredQueue, operacao, FilaA, FilaB, Ana), (historico[0].Kind, historico[0].OperationId, historico[0].FromQueueId, historico[0].ToQueueId, historico[0].ActorUserId));
        Assert.EndsWith(QueueItem.ResponderChangedSuffix, historico[0].Reason, StringComparison.Ordinal);
        Assert.Equal((HistoryKind.Reassigned, espelho, Ana, Beto, QueueItem.ExternalChangeReason), (historico[1].Kind, historico[1].OperationId, historico[1].FromUserId, historico[1].ToUserId, historico[1].Reason));
    }

    [Fact]
    public void Concluir_em_parte_com_o_mesmo_responsavel_so_move_o_item_e_com_a_conversa_em_espera_o_devolve_a_fila()
    {
        var igual = ItemComTransferenciaDeUnidadePendente();
        var historicoIgual = igual.CompletePartialTransfer(ProofVerdict.Same, Ana, 9, Guid.NewGuid(), Agora);
        Assert.Equal((FilaB, QueueItemStatus.Assigned, Ana), (igual.QueueId, igual.Status, igual.AssignedUserId));
        Assert.Equal(HistoryKind.TransferredQueue, Assert.Single(historicoIgual).Kind);

        var emEspera = ItemComTransferenciaDeUnidadePendente();
        var historicoEspera = emEspera.CompletePartialTransfer(ProofVerdict.Same, null, 9, Guid.NewGuid(), Agora);
        Assert.Equal((FilaB, QueueItemStatus.Waiting, null), (emEspera.QueueId, emEspera.Status, emEspera.AssignedUserId));
        Assert.Equal(new[] { HistoryKind.TransferredQueue, HistoryKind.Released }, historicoEspera.Select(h => h.Kind).ToArray());
    }

    [Fact]
    public void Concluir_em_parte_com_o_atendimento_que_acabou_encerra_o_item_ja_na_fila_de_destino_com_a_fronteira_desconhecida()
    {
        var item = ItemComTransferenciaDeUnidadePendente();

        var historico = item.CompletePartialTransfer(ProofVerdict.Other, null, 9, Guid.NewGuid(), Agora);

        Assert.Equal((FilaB, Filial, QueueItemStatus.Closed, "returned-to-ai", null), (item.QueueId, item.BusinessUnitId, item.Status, item.CloseReason, item.ClosedThroughMessageSeq));
        Assert.Null(item.Pending);
        Assert.Equal(new[] { HistoryKind.TransferredQueue, HistoryKind.ReturnedToAi }, historico.Select(h => h.Kind).ToArray());
    }

    [Fact]
    public void So_a_transferencia_que_inclui_a_unidade_conclui_em_parte_e_acompanhar_o_destino_exige_a_operacao_pendente()
    {
        var semUnidade = ItemComAna();
        semUnidade.BeginTransferToQueue(FilaB, null, null, null, Guid.NewGuid(), Ana, "transfer", Agora, basis: new HandlingBasis(1, Ana));
        Assert.Throws<QueueItemStateException>(() => semUnidade.CompletePartialTransfer(ProofVerdict.Same, Ana, 9, Guid.NewGuid(), Agora));
        Assert.Throws<QueueItemStateException>(() => semUnidade.AdoptConfirmedTransferDestination(Agora, QueueItem.SessionEndedSuffix));

        var liberar = ItemComAna();
        liberar.BeginRelease(Guid.NewGuid(), Ana, "release", Agora, basis: new HandlingBasis(1, Ana));
        Assert.Throws<QueueItemStateException>(() => liberar.CompletePartialTransfer(ProofVerdict.Same, null, 9, Guid.NewGuid(), Agora));

        var semPendencia = ItemComAna();
        Assert.Throws<QueueItemStateException>(() => semPendencia.CompletePartialTransfer(ProofVerdict.Same, Ana, 9, Guid.NewGuid(), Agora));
        Assert.Throws<ContactCenterValidationException>(() => ItemComTransferenciaDeUnidadePendente().CompletePartialTransfer(ProofVerdict.Same, Ana, 9, Guid.Empty, Agora));
    }

    // A geração da colocação (a identidade durável da unidade e do setor) a que o acesso do item está preso

    [Fact]
    public void A_geracao_da_colocacao_vai_na_base_da_operacao_pendente_e_e_validada()
    {
        var item = QueueItem.Enter(Tenant, Sessao, Organizacao, Guid.NewGuid(), null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(0, null, InAiMode: true, PlacementEpoch: 4));
        Assert.Equal(4L, item.Pending!.Basis!.PlacementEpoch);
        Assert.Equal(4L, item.PendingBasisPlacementEpoch);

        Assert.Throws<ContactCenterValidationException>(
            () => QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(0, null, InAiMode: true, PlacementEpoch: 0)));

        item.CompletePending(2, Agora, handlingPeriod: 1, placementEpoch: 4);
        Assert.Null(item.PendingBasisPlacementEpoch);
        Assert.Null(item.Pending);
    }

    [Fact]
    public void Item_novo_sem_geracao_conhecida_nasce_sem_ela_a_entrada_a_prende_e_as_demais_operacoes_a_mantem()
    {
        var item = ItemEmEspera(); // entrada concluída sem a geração (intenção anterior ao campo)
        Assert.Null(item.PlacementEpoch);

        var comGeracao = QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(0, null, InAiMode: true, PlacementEpoch: 7));
        comGeracao.CompletePending(2, Agora, handlingPeriod: 1, placementEpoch: 7);
        Assert.Equal(7L, comGeracao.PlacementEpoch);

        // Atribuir, devolver à fila e as demais operações que não mudam a colocação não a tocam.
        comGeracao.BeginAssign(Ana, Guid.NewGuid(), Ana, "claim", Agora, basis: new HandlingBasis(1, null, PlacementEpoch: 7));
        comGeracao.CompletePending(3, Agora);
        Assert.Equal(7L, comGeracao.PlacementEpoch);

        // A transferência para outra FILA da mesma unidade e setor também não; a que muda a unidade prende a geração de destino.
        comGeracao.BeginTransferToQueue(FilaB, null, null, null, Guid.NewGuid(), Ana, "transfer", Agora, basis: new HandlingBasis(1, Ana, PlacementEpoch: 7));
        comGeracao.CompletePending(4, Agora, placementEpoch: 7);
        Assert.Equal(7L, comGeracao.PlacementEpoch);

        var outra = Guid.NewGuid();
        var trocaUnidade = QueueItem.Enter(Tenant, Sessao, Organizacao, Matriz, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(0, null, InAiMode: true, PlacementEpoch: 2));
        trocaUnidade.CompletePending(2, Agora, handlingPeriod: 1, placementEpoch: 2);
        trocaUnidade.BeginTransferToQueue(FilaB, null, outra, null, Guid.NewGuid(), null, "transfer", Agora, basis: new HandlingBasis(1, null, PlacementEpoch: 2));
        trocaUnidade.CompletePending(5, Agora, placementEpoch: 3); // a da autorização (2) mais uma: a unidade mudou
        Assert.Equal((3L, outra), (trocaUnidade.PlacementEpoch, trocaUnidade.BusinessUnitId));

        // Sem a geração na autorização, a transferência que muda a colocação a deixa DESCONHECIDA (o acesso fecha), e não a antiga.
        var legada = QueueItem.Enter(Tenant, Sessao, Organizacao, Matriz, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(0, null, InAiMode: true, PlacementEpoch: 2));
        legada.CompletePending(2, Agora, handlingPeriod: 1, placementEpoch: 2);
        legada.BeginTransferToQueue(FilaB, null, outra, null, Guid.NewGuid(), null, "transfer", Agora, basis: new HandlingBasis(1, null));
        legada.CompletePending(5, Agora);
        Assert.Null(legada.PlacementEpoch);
    }

    [Fact]
    public void A_conclusao_em_parte_e_o_encerramento_com_a_unidade_aplicada_prendem_a_geracao_de_destino()
    {
        var parcial = ItemComTransferenciaDeUnidadePendente();
        parcial.CompletePartialTransfer(ProofVerdict.Same, Ana, 9, Guid.NewGuid(), Agora, placementEpoch: 5);
        Assert.Equal((FilaB, 5L), (parcial.QueueId, parcial.PlacementEpoch));

        var semGeracao = ItemComTransferenciaDeUnidadePendente();
        semGeracao.CompletePartialTransfer(ProofVerdict.Same, Ana, 9, Guid.NewGuid(), Agora);
        Assert.Null(semGeracao.PlacementEpoch);

        var encerrada = ItemComTransferenciaDeUnidadePendente();
        encerrada.AdoptConfirmedTransferDestination(Agora, QueueItem.SessionEndedSuffix, 6);
        Assert.Equal(6L, encerrada.PlacementEpoch);
    }

    // A projeção adiada (evento externo mais novo com a operação pendente) e o espelho da sessão

    [Fact]
    public void A_marca_de_projecao_adiada_guarda_a_maior_versao_avanca_a_versao_do_item_e_so_pede_alinhamento_depois_da_operacao()
    {
        var item = ItemComTransferenciaDeUnidadePendente();
        var versao = item.Version;

        Assert.False(item.MarkResync(item.ObservedStateVersion, Agora)); // obsoleto: não é mais novo que o observado
        Assert.True(item.MarkResync(20, Agora));
        Assert.Equal((20L, versao + 1), (item.ResyncStateVersion, item.Version));
        Assert.False(item.MarkResync(20, Agora)); // repetido
        Assert.False(item.MarkResync(15, Agora)); // já coberto pela marca
        Assert.True(item.MarkResync(25, Agora));
        Assert.Equal(25L, item.ResyncStateVersion);
        Assert.False(item.NeedsResync); // com a operação pendente, quem converge é a operação

        item.CompletePartialTransfer(ProofVerdict.Same, Beto, 11, Guid.NewGuid(), Agora); // espelha a sessão na versão 11, MENOR que a marca
        Assert.True(item.NeedsResync);

        var historico = item.MirrorSession(ProofVerdict.Same, Ana, 25, Guid.NewGuid(), Agora);
        Assert.Equal(HistoryKind.Reassigned, historico!.Kind);
        Assert.Equal((null, false, Ana), (item.ResyncStateVersion, item.NeedsResync, item.AssignedUserId));
        Assert.Equal(25, item.ObservedStateVersion);
    }

    [Fact]
    public void O_espelho_nunca_sobrescreve_uma_visao_mais_nova_com_uma_mais_antiga_e_fecha_o_item_quando_o_atendimento_acabou()
    {
        var item = ItemComAna();
        Assert.NotNull(item.MirrorSession(ProofVerdict.Same, Beto, 9, Guid.NewGuid(), Agora));
        var versao = item.Version;

        Assert.Null(item.MirrorSession(ProofVerdict.Same, Ana, 8, Guid.NewGuid(), Agora)); // mais antiga que a observada
        Assert.Equal((Beto, versao, 9L), (item.AssignedUserId, item.Version, item.ObservedStateVersion));

        var fim = item.MirrorSession(ProofVerdict.Other, null, 10, Guid.NewGuid(), Agora);
        Assert.Equal(HistoryKind.ReturnedToAi, fim!.Kind);
        Assert.Equal((QueueItemStatus.Closed, null), (item.Status, item.ClosedThroughMessageSeq)); // fronteira desconhecida
    }

    // O limite do histórico depois que a sessão saiu do escopo

    private const long Geracao = 3;

    /// <summary>A Ana atende numa colocação de geração CONHECIDA (a origem que um limite de saída precisa para ser gravado).</summary>
    private static QueueItem ItemComAnaNaGeracao(long geracao)
    {
        var item = QueueItem.Enter(Tenant, Sessao, Organizacao, null, null, FilaA, Guid.NewGuid(), null, "ai-requested", Agora, basis: new HandlingBasis(0, null, InAiMode: true, PlacementEpoch: geracao));
        item.CompletePending(2, Agora, handlingPeriod: 1, placementEpoch: geracao);
        item.BeginAssign(Ana, Guid.NewGuid(), Ana, "claim", Agora, basis: new HandlingBasis(1, null, PlacementEpoch: geracao));
        item.CompletePending(3, Agora);
        return item;
    }

    [Fact]
    public void O_limite_do_historico_so_diminui_tem_origem_comprovada_e_sem_a_sequencia_fica_em_zero()
    {
        var item = ItemComAnaNaGeracao(Geracao);
        Assert.Null(item.HistoryLimitSeq);
        Assert.Equal(QueueItemBoundaryModel.Legacy, item.BoundaryModel);

        Assert.True(item.LeaveScope(5, Geracao, Agora));
        Assert.Equal((5L, QueueItemBoundaryModel.Certified, true), (item.HistoryLimitSeq, item.BoundaryModel, item.HistoryLimitIsValid));
        Assert.Equal(Geracao, item.HistoryLimitOriginEpoch);
        Assert.False(item.LeaveScope(7, Geracao, Agora)); // uma saída posterior não amplia
        Assert.False(item.LeaveScope(5, Geracao, Agora)); // repetida
        Assert.True(item.LeaveScope(3, Geracao, Agora));
        Assert.Equal(3L, item.HistoryLimitSeq);
        Assert.True(item.LeaveScope(null, Geracao, Agora)); // sequência desconhecida: nenhuma mensagem
        Assert.Equal(0L, item.HistoryLimitSeq);
        Assert.False(item.LeaveScope(9, Geracao, Agora));
    }

    [Fact]
    public void O_limite_so_e_gravado_com_a_origem_da_geracao_do_item_nunca_de_outra_geracao_nem_sem_origem()
    {
        var item = ItemComAnaNaGeracao(Geracao);
        Assert.False(item.LeaveScope(5, Geracao + 2, Agora)); // a saída é de OUTRA geração
        Assert.False(item.LeaveScope(5, Geracao - 1, Agora));
        Assert.False(item.LeaveScope(5, null, Agora)); // sem origem (evento anterior ao campo)
        Assert.Null(item.HistoryLimitSeq);
        Assert.Null(item.HistoryLimitOriginEpoch);
        Assert.Equal(QueueItemBoundaryModel.Legacy, item.BoundaryModel);

        var semGeracao = ItemComAna(); // a geração do item é desconhecida: nenhuma origem confere
        Assert.False(semGeracao.LeaveScope(5, Geracao, Agora));
        Assert.Null(semGeracao.HistoryLimitSeq);

        Assert.True(item.LeaveScope(5, Geracao, Agora));
        Assert.False(item.LeaveScope(1, Geracao + 2, Agora)); // a saída de outra geração, depois, não substitui nem amplia a original
        Assert.Equal(5L, item.HistoryLimitSeq);
        Assert.Equal(Geracao, item.HistoryLimitOriginEpoch);
    }

    [Fact]
    public void A_fronteira_de_encerramento_so_autoriza_se_foi_certificada_e_certifica_o_modelo_do_item()
    {
        var semProva = ItemComAnaNaGeracao(Geracao);
        semProva.CloseBecauseSessionEnded(Guid.NewGuid(), "session-closed", Agora, 10); // o caminho que encerrou NÃO comprovou o período e a geração
        Assert.Null(semProva.ClosedThroughMessageSeq);
        Assert.False(semProva.ClosedBoundaryIsCertified);

        var comProva = ItemComAnaNaGeracao(Geracao);
        comProva.CloseBecauseSessionEnded(Guid.NewGuid(), "session-closed", Agora, 10, certifiedBoundary: true);
        Assert.Equal(10L, comProva.ClosedThroughMessageSeq);
        Assert.True(comProva.ClosedBoundaryIsCertified);
        Assert.Equal(QueueItemBoundaryModel.Certified, comProva.BoundaryModel);
    }

    [Theory]
    [InlineData(null, 10L, 10L)]   // nunca saiu do escopo: vale a sequência de quem encerrou
    [InlineData(3L, 10L, 3L)]      // saiu depois da mensagem 3: o encerramento não amplia
    [InlineData(30L, 10L, 10L)]    // o limite maior que o fim não restringe
    [InlineData(3L, null, null)]   // fim desconhecido: fechado
    [InlineData(0L, 10L, null)]    // saiu sem fronteira conhecida: fechado
    public void Os_tres_encerramentos_aplicam_o_limite_do_escopo_a_fronteira_do_historico(long? limite, long? fim, long? esperada)
    {
        QueueItem Com() { var i = ItemComAnaNaGeracao(Geracao); if (limite is { } l) { i.LeaveScope(l == 0 ? null : l, Geracao, Agora); } return i; }

        var porFimDaSessao = Com();
        porFimDaSessao.CloseBecauseSessionEnded(Guid.NewGuid(), "session-closed", Agora, fim, certifiedBoundary: true);
        Assert.Equal(esperada, porFimDaSessao.ClosedThroughMessageSeq);

        var porVoltaParaIa = Com();
        porVoltaParaIa.CloseBecauseReturnedToAiExternally(Guid.NewGuid(), 5, Agora, fim, certifiedBoundary: true);
        Assert.Equal(esperada, porVoltaParaIa.ClosedThroughMessageSeq);

        var pelaOperacao = Com();
        pelaOperacao.BeginReturnToAi(Guid.NewGuid(), Ana, "return", Agora, basis: new HandlingBasis(1, Ana, PlacementEpoch: Geracao));
        pelaOperacao.CompletePending(6, Agora, fim, certifiedBoundary: true);
        Assert.Equal(esperada, pelaOperacao.ClosedThroughMessageSeq);
    }

    [Fact]
    public void Acompanhar_o_destino_sem_concluir_move_o_item_e_deixa_a_intencao_para_quem_a_encerra_no_mesmo_passo()
    {
        var item = ItemComTransferenciaDeUnidadePendente();
        var operacao = item.Pending!.OperationId;

        var historico = item.AdoptConfirmedTransferDestination(Agora, QueueItem.SessionEndedSuffix);

        Assert.Equal((FilaB, Filial, SetorDaFilial), (item.QueueId, item.BusinessUnitId, item.DepartmentId));
        Assert.Equal(operacao, item.Pending!.OperationId);
        Assert.Equal((HistoryKind.TransferredQueue, operacao), (historico.Kind, historico.OperationId));
        Assert.EndsWith(QueueItem.SessionEndedSuffix, historico.Reason, StringComparison.Ordinal);
    }
}
