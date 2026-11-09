namespace IaBimeta.Channels.Contracts;

/// <summary>O provedor do canal. Só entra um valor novo com o adaptador correspondente.</summary>
public enum ChannelProvider
{
    /// <summary>WhatsApp Business Platform (Cloud API da Meta).</summary>
    WhatsAppCloud = 1,
}

/// <summary>
/// Situação operacional de uma instância de canal. Só <see cref="Active"/> recebe e envia: o Receiver e o Sender recusam as
/// demais. A mudança de situação é auditada.
/// </summary>
public enum ChannelStatus
{
    /// <summary>Cadastrada e ainda não ativada: falta configurar (por exemplo, as unidades autorizadas).</summary>
    Inactive = 1,

    Active = 2,

    /// <summary>Pausada por decisão de um administrador; as associações e a configuração ficam como estão.</summary>
    Suspended = 3,
}

/// <summary>A categoria do modelo de mensagem no provedor (a Meta a define na aprovação e cobra por ela).</summary>
public enum TemplateCategory
{
    Marketing = 1,

    Utility = 2,

    /// <summary>Código de uso único. NÃO é suportada pelo envio de modelos do MVP (o botão de copiar código tem parâmetro próprio): o cadastro a recusa.</summary>
    Authentication = 3,
}

/// <summary>
/// A situação do modelo NO PROVEDOR, como o administrador a registrou (o Channels não consulta a Meta). Só <see cref="Approved"/> pode ser enviado. Um modelo pausado ou desativado pela Meta (erros 132015 e 132016
/// no envio) é marcado aqui pelo próprio Sender, para o atendente deixar de vê-lo.
/// </summary>
public enum TemplateStatus
{
    Approved = 1,

    Pending = 2,

    Rejected = 3,

    Paused = 4,

    Disabled = 5,
}
