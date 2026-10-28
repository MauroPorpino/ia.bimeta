using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Tests;

public sealed class ServiceQueueTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new(Guid.NewGuid());

    private static ServiceQueue Fila(string nome = "Coleta domiciliar", Guid? unidade = null, Guid? setor = null) =>
        ServiceQueue.Create(Tenant, Guid.NewGuid(), unidade, setor, nome, Agora);

    [Fact]
    public void Fila_nova_nasce_ativa_na_versao_1_com_o_nome_aparado_e_a_chave_em_minusculas()
    {
        var fila = Fila("  Coleta Domiciliar  ");

        Assert.Equal(("Coleta Domiciliar", "coleta domiciliar", QueueStatus.Active, 1L), (fila.Name, fila.NameKey, fila.Status, fila.Version));
        Assert.Equal((Agora, Agora), (fila.CreatedAt, fila.UpdatedAt));
        Assert.NotEqual(Guid.Empty, fila.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Nome_vazio_e_recusado(string? nome)
    {
        Assert.Throws<ContactCenterValidationException>(() => Fila(nome!));
    }

    [Fact]
    public void Nome_acima_do_limite_e_recusado_e_o_do_limite_exato_e_aceito()
    {
        Assert.Throws<ContactCenterValidationException>(() => Fila(new string('a', ServiceQueue.NameMaxLength + 1)));
        Assert.Equal(ServiceQueue.NameMaxLength, Fila(new string('a', ServiceQueue.NameMaxLength)).Name.Length);
    }

    [Fact]
    public void A_fila_precisa_de_organizacao()
    {
        Assert.Throws<ContactCenterValidationException>(() => ServiceQueue.Create(Tenant, Guid.Empty, null, null, "Fila", Agora));
    }

    [Fact]
    public void Setor_so_existe_dentro_de_uma_unidade_e_identificador_vazio_e_recusado()
    {
        Assert.Throws<ContactCenterValidationException>(() => Fila(unidade: null, setor: Guid.NewGuid()));
        Assert.Throws<ContactCenterValidationException>(() => Fila(unidade: Guid.Empty));
        Assert.Throws<ContactCenterValidationException>(() => Fila(unidade: Guid.NewGuid(), setor: Guid.Empty));

        var unidade = Guid.NewGuid();
        var setor = Guid.NewGuid();
        var fila = Fila(unidade: unidade, setor: setor);
        Assert.Equal((unidade, setor), (fila.BusinessUnitId, fila.DepartmentId));
    }

    [Fact]
    public void Pausar_e_retomar_avancam_a_versao_so_quando_mudam_alguma_coisa()
    {
        var fila = Fila();
        var depois = Agora.AddMinutes(5);

        Assert.False(fila.Resume(depois));                       // já estava ativa
        Assert.Equal((1L, Agora), (fila.Version, fila.UpdatedAt));

        Assert.True(fila.Pause(depois));
        Assert.Equal((QueueStatus.Paused, 2L, depois), (fila.Status, fila.Version, fila.UpdatedAt));

        Assert.False(fila.Pause(depois.AddMinutes(1)));           // repetir não muda nada
        Assert.Equal((2L, depois), (fila.Version, fila.UpdatedAt));

        Assert.True(fila.Resume(depois.AddMinutes(2)));
        Assert.Equal((QueueStatus.Active, 3L), (fila.Status, fila.Version));
    }

    [Fact]
    public void Atendente_novo_guarda_papel_autor_e_data_e_mudar_para_o_mesmo_papel_nao_muda_nada()
    {
        var fila = Guid.NewGuid();
        var usuario = Guid.NewGuid();
        var autor = Guid.NewGuid();
        var membro = QueueMembership.Create(Tenant, fila, usuario, QueueMemberRole.Agent, autor, Agora);

        Assert.Equal((fila, usuario, QueueMemberRole.Agent, (Guid?)autor, Agora), (membro.QueueId, membro.UserId, membro.Role, membro.AddedBy, membro.AddedAt));
        Assert.False(membro.ChangeRole(QueueMemberRole.Agent));
        Assert.True(membro.ChangeRole(QueueMemberRole.Supervisor));
        Assert.Equal(QueueMemberRole.Supervisor, membro.Role);
    }

    [Fact]
    public void Atendente_exige_fila_usuario_e_papel_validos()
    {
        Assert.Throws<ContactCenterValidationException>(() => QueueMembership.Create(Tenant, Guid.Empty, Guid.NewGuid(), QueueMemberRole.Agent, null, Agora));
        Assert.Throws<ContactCenterValidationException>(() => QueueMembership.Create(Tenant, Guid.NewGuid(), Guid.Empty, QueueMemberRole.Agent, null, Agora));
        Assert.Throws<ContactCenterValidationException>(() => QueueMembership.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), (QueueMemberRole)99, null, Agora));

        var membro = QueueMembership.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), QueueMemberRole.Agent, null, Agora);
        Assert.Throws<ContactCenterValidationException>(() => membro.ChangeRole((QueueMemberRole)99));
        Assert.Null(membro.AddedBy); // operação de sistema: sem autor
    }

    [Fact]
    public void As_visoes_copiam_os_dados_da_fila_e_do_atendente_sem_expor_a_chave_de_nome()
    {
        var fila = Fila("Triagem");
        var membro = QueueMembership.Create(Tenant, fila.Id, Guid.NewGuid(), QueueMemberRole.Supervisor, null, Agora);

        var visaoDaFila = ServiceQueueView.From(fila);
        var visaoDoMembro = QueueMemberView.From(membro);

        Assert.Equal((fila.Id, fila.OrganizationId, "Triagem", QueueStatus.Active, 1L), (visaoDaFila.Id, visaoDaFila.OrganizationId, visaoDaFila.Name, visaoDaFila.Status, visaoDaFila.Version));
        Assert.DoesNotContain(typeof(ServiceQueueView).GetProperties(), p => p.Name == nameof(ServiceQueue.NameKey) || p.Name == nameof(ServiceQueue.TenantId));
        Assert.Equal((fila.Id, membro.UserId, QueueMemberRole.Supervisor), (visaoDoMembro.QueueId, visaoDoMembro.UserId, visaoDoMembro.Role));
    }
}
