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
