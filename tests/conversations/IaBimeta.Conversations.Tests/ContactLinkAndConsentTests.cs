using System.Reflection;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// Fundação da E2 do Embedded (ADR 0041, seções 2 e 4), sem banco: a normalização E.164, o vínculo cliente-contato (desvinculação lógica e idempotente) e o consentimento (rastreável e somente de acréscimo). O que o
/// banco garante (restrições, gatilho de acréscimo, isolamento, índices) é provado em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
public class ContactLinkAndConsentTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("+5511999990101", "+5511999990101")]
    [InlineData("+55 (11) 99999-0101", "+5511999990101")]
    [InlineData("005511999990101", "+5511999990101")]
    [InlineData("  +55.11.99999.0101 ", "+5511999990101")]
    public void O_formato_internacional_estrito_aceita_so_com_o_prefixo_explicito(string entrada, string esperado)
    {
        Assert.True(E164PhoneNumber.TryNormalizeInternational(entrada, out var e164));
        Assert.Equal(esperado, e164);
    }

    [Theory]
    [InlineData("5511999990101")] // só dígitos: pode ser nacional
    [InlineData("11999990101")]
    [InlineData("(11) 99999-0101")]
    [InlineData("+55abc")]
    [InlineData("+0511999990101")]
    [InlineData("+1234567")]
    [InlineData("+1234567890123456")]
    [InlineData("++5511999990101")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void O_formato_internacional_estrito_recusa_sem_prefixo_e_o_invalido(string? entrada)
    {
        Assert.False(E164PhoneNumber.TryNormalizeInternational(entrada, out var e164));
        Assert.Equal(string.Empty, e164);
    }

    private static readonly TenantId Tenant = new(Guid.NewGuid());

    // E.164

    [Theory]
    [InlineData("+5511999990000", "+5511999990000")]
    [InlineData("5511999990000", "+5511999990000")]
    [InlineData("005511999990000", "+5511999990000")]
    [InlineData("+55 (11) 99999-0000", "+5511999990000")]
    [InlineData("  +55.11.99999.0000  ", "+5511999990000")]
    [InlineData("+12025550123", "+12025550123")]
    [InlineData("+12345678", "+12345678")]
    [InlineData("+123456789012345", "+123456789012345")]
    public void Telefone_valido_e_normalizado_para_E164(string entrada, string esperado)
    {
        Assert.True(E164PhoneNumber.TryNormalize(entrada, out var e164));
        Assert.Equal(esperado, e164);
        Assert.Equal(esperado, E164PhoneNumber.Normalize(entrada));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("++5511999990000")]
    [InlineData("+0511999990000")]                // zero inicial no código do país
    [InlineData("0511999990000")]                 // vira zero inicial
    [InlineData("+1234567")]                      // 7 dígitos: curto
    [InlineData("+1234567890123456")]             // 16 dígitos: longo
    [InlineData("+55119999a0000")]                // letra
    [InlineData("+55 11 99999-0000 ramal 12")]
    [InlineData("tel:+5511999990000")]
    [InlineData("+55\t11999990000")]              // controle
    [InlineData("+٥٥١١٩٩٩٩٩٠٠٠٠")]                  // dígitos de outro alfabeto
    [InlineData("user@example.com")]
    [InlineData("wa.bsuid.ABC123")]               // referência opaca: nunca é lida como telefone
    public void Entrada_que_nao_e_telefone_e_recusada_sem_adivinhar(string? entrada)
    {
        Assert.False(E164PhoneNumber.TryNormalize(entrada, out var e164));
        Assert.Equal(string.Empty, e164);
        Assert.Throws<ConversationsValidationException>(() => E164PhoneNumber.Normalize(entrada));
    }

    [Fact]
    public void Entrada_enorme_e_recusada_sem_percorrer_tudo()
    {
        Assert.False(E164PhoneNumber.TryNormalize("+" + new string('1', 10_000), out _));
    }

    // Vínculo

    private static ContactExternalLink Vinculo(string cliente = "CLI-1", LinkOrigin origem = LinkOrigin.Integration, ActorKind autor = ActorKind.Integration) =>
        ContactExternalLink.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), cliente, Guid.NewGuid(), origem, autor, Guid.NewGuid(), Agora);

    [Fact]
    public void Vinculo_novo_esta_ativo_e_guarda_a_origem_e_o_autor()
    {
        var conexao = Guid.NewGuid();
        var autor = Guid.NewGuid();
        var v = ContactExternalLink.Create(Tenant, Guid.NewGuid(), conexao, "  CLI-1  ", Guid.NewGuid(), LinkOrigin.Integration, ActorKind.Integration, autor, Agora);

        Assert.True(v.IsActive);
        Assert.Equal("CLI-1", v.CustomerRef);
        Assert.Equal(conexao, v.ConnectionId);
        Assert.Equal(Tenant, v.TenantId);
        Assert.Equal(LinkOrigin.Integration, v.Origin);
        Assert.Equal(ActorKind.Integration, v.LinkedByKind);
        Assert.Equal(autor, v.LinkedById);
        Assert.Equal(Agora, v.LinkedAt);
        Assert.Null(v.UnlinkedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("com\ncontrole")]
    public void Cliente_sem_identificador_valido_e_recusado(string? cliente)
    {
        Assert.Throws<ConversationsValidationException>(() => Vinculo(cliente!));
    }

    [Fact]
    public void O_identificador_do_cliente_tem_no_maximo_128_caracteres()
    {
        Assert.Equal(128, Vinculo(new string('c', 128)).CustomerRef.Length);
        Assert.Throws<ConversationsValidationException>(() => Vinculo(new string('c', 129)));
    }

    [Fact]
    public void A_origem_do_vinculo_precisa_combinar_com_o_autor()
    {
        _ = Vinculo(origem: LinkOrigin.Agent, autor: ActorKind.User);
        Assert.Throws<ConversationsValidationException>(() => Vinculo(origem: LinkOrigin.Integration, autor: ActorKind.User));
        Assert.Throws<ConversationsValidationException>(() => Vinculo(origem: LinkOrigin.Agent, autor: ActorKind.Integration));
        Assert.Throws<ConversationsValidationException>(() => Vinculo(origem: LinkOrigin.Agent, autor: ActorKind.System));
    }

    [Fact]
    public void Vinculo_exige_organizacao_conexao_contato_e_autor()
    {
        Assert.Throws<ConversationsValidationException>(() => ContactExternalLink.Create(Tenant, Guid.Empty, Guid.NewGuid(), "C", Guid.NewGuid(), LinkOrigin.Integration, ActorKind.Integration, Guid.NewGuid(), Agora));
        Assert.Throws<ConversationsValidationException>(() => ContactExternalLink.Create(Tenant, Guid.NewGuid(), Guid.Empty, "C", Guid.NewGuid(), LinkOrigin.Integration, ActorKind.Integration, Guid.NewGuid(), Agora));
        Assert.Throws<ConversationsValidationException>(() => ContactExternalLink.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "C", Guid.Empty, LinkOrigin.Integration, ActorKind.Integration, Guid.NewGuid(), Agora));
        Assert.Throws<ConversationsValidationException>(() => ContactExternalLink.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "C", Guid.NewGuid(), LinkOrigin.Integration, ActorKind.Integration, Guid.Empty, Agora));
    }

    [Fact]
    public void Desvincular_e_logico_idempotente_e_a_primeira_desvinculacao_e_a_que_vale()
    {
        var v = Vinculo();
        var primeiro = Guid.NewGuid();

        Assert.True(v.Unlink(ActorKind.Integration, primeiro, Agora.AddMinutes(1)));
        Assert.False(v.IsActive);
        Assert.Equal(Agora.AddMinutes(1), v.UnlinkedAt);
        Assert.Equal(ActorKind.Integration, v.UnlinkedByKind);
        Assert.Equal(primeiro, v.UnlinkedById);

        // Repetir não altera quem nem quando (sem oráculo e sem reescrever a história).
        Assert.False(v.Unlink(ActorKind.User, Guid.NewGuid(), Agora.AddHours(1)));
        Assert.Equal(Agora.AddMinutes(1), v.UnlinkedAt);
        Assert.Equal(ActorKind.Integration, v.UnlinkedByKind);
        Assert.Equal(primeiro, v.UnlinkedById);
    }

    [Fact]
    public void Desvincular_exige_autor_conexao_ou_usuario()
    {
        Assert.Throws<ConversationsValidationException>(() => Vinculo().Unlink(ActorKind.System, Guid.NewGuid(), Agora));
        Assert.Throws<ConversationsValidationException>(() => Vinculo().Unlink(ActorKind.User, Guid.Empty, Agora));
    }

    // Consentimento

    private static ContactConsent Consentimento(
        string? telefone = "+5511999990000",
        ConsentPurpose finalidade = ConsentPurpose.Marketing,
        ConsentStatus situacao = ConsentStatus.Granted,
        ConsentOrigin origem = ConsentOrigin.IntegrationAsserted,
        string? evidencia = "Formulário de cadastro v3, aceite em 2026-11-10",
        DateTimeOffset? coleta = null,
        ActorKind? autor = null,
        Guid? autorId = null,
        Guid? sessao = null,
        Guid? mensagem = null,
        string? cliente = "CLI-1")
    {
        var tipo = autor ?? origem switch
        {
            ConsentOrigin.IntegrationAsserted => ActorKind.Integration,
            ConsentOrigin.AgentRecorded => ActorKind.User,
            _ => ActorKind.System,
        };
        var id = autorId ?? (tipo == ActorKind.System ? null : Guid.NewGuid());
        return ContactConsent.Create(Tenant, Guid.NewGuid(), telefone, finalidade, situacao, origem, evidencia, coleta ?? Agora.AddDays(-1), tipo, id, cliente, sessao, mensagem, Agora);
    }

    [Fact]
    public void Consentimento_valido_guarda_tudo_e_normaliza_o_telefone()
    {
        var c = Consentimento(telefone: "+55 (11) 99999-0000", evidencia: "  prova  ");

        Assert.Equal("+5511999990000", c.Phone);
        Assert.Equal("prova", c.Evidence);
        Assert.Equal(ConsentPurpose.Marketing, c.Purpose);
        Assert.Equal(ConsentStatus.Granted, c.Status);
        Assert.Equal(ConsentOrigin.IntegrationAsserted, c.Origin);
        Assert.Equal(Agora, c.RecordedAt);
        Assert.Equal(Agora.AddDays(-1), c.CollectedAt);
        Assert.Equal(Tenant, c.TenantId);
        Assert.Equal("CLI-1", c.CustomerRef);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void Sem_evidencia_a_gravacao_e_recusada(string? evidencia)
    {
        Assert.Throws<ConversationsValidationException>(() => Consentimento(evidencia: evidencia));
    }

    [Fact]
    public void A_evidencia_tem_no_maximo_2000_caracteres()
    {
        Assert.Equal(2000, Consentimento(evidencia: new string('e', 2000)).Evidence.Length);
        Assert.Throws<ConversationsValidationException>(() => Consentimento(evidencia: new string('e', 2001)));
    }

    [Fact]
    public void A_data_da_coleta_e_obrigatoria_e_nao_pode_estar_no_futuro_alem_da_tolerancia_de_5_minutos()
    {
        Assert.Throws<ConversationsValidationException>(() => Consentimento(coleta: default(DateTimeOffset)));
        Assert.Throws<ConversationsValidationException>(() => Consentimento(coleta: Agora.AddMinutes(5).AddSeconds(1)));
        Assert.Equal(Agora.AddMinutes(5), Consentimento(coleta: Agora.AddMinutes(5)).CollectedAt);
        Assert.Equal(Agora.AddYears(-3), Consentimento(coleta: Agora.AddYears(-3)).CollectedAt);
    }

    [Fact]
    public void Telefone_fora_do_E164_e_recusado()
    {
        Assert.Throws<ConversationsValidationException>(() => Consentimento(telefone: "wa.bsuid.ABC123"));
        Assert.Throws<ConversationsValidationException>(() => Consentimento(telefone: null));
    }

    [Fact]
    public void Finalidade_situacao_ou_origem_indefinidas_sao_recusadas()
    {
        Assert.Throws<ConversationsValidationException>(() => Consentimento(finalidade: (ConsentPurpose)0));
        Assert.Throws<ConversationsValidationException>(() => Consentimento(finalidade: (ConsentPurpose)3));
        Assert.Throws<ConversationsValidationException>(() => Consentimento(situacao: (ConsentStatus)0));
        Assert.Throws<ConversationsValidationException>(() => Consentimento(origem: (ConsentOrigin)9, autor: ActorKind.Integration, autorId: Guid.NewGuid()));
    }

    [Fact]
    public void A_origem_precisa_combinar_com_o_autor_a_sessao_e_a_mensagem()
    {
        // O sistema externo é uma conexão; a pessoa autorizada é um usuário; a palavra-chave é do sistema e traz a sessão e a mensagem.
        _ = Consentimento(origem: ConsentOrigin.IntegrationAsserted);
        _ = Consentimento(origem: ConsentOrigin.AgentRecorded, situacao: ConsentStatus.Revoked);
        _ = Consentimento(origem: ConsentOrigin.CustomerKeyword, situacao: ConsentStatus.Revoked, sessao: Guid.NewGuid(), mensagem: Guid.NewGuid(), cliente: null);

        Assert.Throws<ConversationsValidationException>(() => Consentimento(origem: ConsentOrigin.IntegrationAsserted, autor: ActorKind.User, autorId: Guid.NewGuid()));
        Assert.Throws<ConversationsValidationException>(() => Consentimento(origem: ConsentOrigin.AgentRecorded, autor: ActorKind.Integration, autorId: Guid.NewGuid()));
        Assert.Throws<ConversationsValidationException>(() => Consentimento(origem: ConsentOrigin.IntegrationAsserted, autorId: Guid.Empty));
        Assert.Throws<ConversationsValidationException>(() => Consentimento(origem: ConsentOrigin.CustomerKeyword, sessao: Guid.NewGuid(), mensagem: null));
        Assert.Throws<ConversationsValidationException>(() => Consentimento(origem: ConsentOrigin.CustomerKeyword, sessao: null, mensagem: Guid.NewGuid()));
        Assert.Throws<ConversationsValidationException>(() => Consentimento(origem: ConsentOrigin.CustomerKeyword, sessao: Guid.NewGuid(), mensagem: Guid.NewGuid(), autor: ActorKind.User, autorId: Guid.NewGuid()));
    }

    [Fact]
    public void A_organizacao_e_obrigatoria()
    {
        Assert.Throws<ConversationsValidationException>(() =>
            ContactConsent.Create(Tenant, Guid.Empty, "+5511999990000", ConsentPurpose.Utility, ConsentStatus.Granted, ConsentOrigin.IntegrationAsserted, "e", Agora, ActorKind.Integration, Guid.NewGuid(), null, null, null, Agora));
    }

    [Fact]
    public void Idempotencia_compara_situacao_e_data_da_coleta_com_a_ultima_linha()
    {
        var c = Consentimento(situacao: ConsentStatus.Granted, coleta: Agora.AddDays(-2));

        Assert.True(c.IsSameAssertionAs(ConsentStatus.Granted, Agora.AddDays(-2)));
        Assert.False(c.IsSameAssertionAs(ConsentStatus.Revoked, Agora.AddDays(-2)));
        Assert.False(c.IsSameAssertionAs(ConsentStatus.Granted, Agora.AddDays(-1)));
    }

    [Fact]
    public void O_consentimento_e_imutavel_nenhuma_propriedade_publica_tem_setter_e_nenhum_metodo_o_altera()
    {
        var tipo = typeof(ContactConsent);

        // Nenhuma propriedade pública com setter público ou interno (o EF usa o construtor privado e os setters privados).
        Assert.All(
            tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => Assert.True(p.SetMethod is null || p.SetMethod.IsPrivate, $"{p.Name} tem setter acessível"));

        // Os únicos métodos públicos de instância são consultas (nenhum "Update", "Revoke", "Change" ou semelhante): mudar a situação é inserir outra linha.
        var metodos = tipo.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).Select(m => m.Name).ToList();
        Assert.Equal(new[] { nameof(ContactConsent.IsSameAssertionAs) }, metodos);
    }

    [Fact]
    public void O_vinculo_so_muda_pelo_metodo_de_desvinculacao()
    {
        var tipo = typeof(ContactExternalLink);

        Assert.All(
            tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => Assert.True(p.SetMethod is null || p.SetMethod.IsPrivate, $"{p.Name} tem setter acessível"));
        var metodos = tipo.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).Select(m => m.Name).ToList();
        Assert.Equal(new[] { nameof(ContactExternalLink.Unlink) }, metodos);
    }
}
