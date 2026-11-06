namespace IaBimeta.Channels.Application;

/// <summary>
/// O recurso não existe no tenant ou o usuário não pode nem enxergá-lo. As duas situações são indistinguíveis de propósito,
/// para não revelar a existência do recurso (TEN-2).
/// </summary>
public sealed class ChannelsResourceNotFoundException : Exception
{
    public ChannelsResourceNotFoundException(string resource)
        : base($"{resource} não encontrada.")
    {
    }
}

/// <summary>
/// O identificador no provedor (conta ou número) já está cadastrado. A mensagem não diz de qual tenant: o identificador é único
/// na plataforma e quem o cadastra não pode descobrir, por tentativa, o que outro tenant usa.
/// </summary>
public sealed class ChannelIdentifierInUseException : Exception
{
    public ChannelIdentifierInUseException(string description)
        : base($"O identificador {description} já está em uso.")
    {
    }
}

/// <summary>Outra alteração do mesmo recurso aconteceu primeiro: releia e repita a operação.</summary>
public sealed class ChannelsConflictException : Exception
{
    public ChannelsConflictException()
        : base("O recurso foi alterado por outra operação. Releia e tente de novo.")
    {
    }
}

/// <summary>A chave de cifra das credenciais não está configurada: nenhuma credencial é gravada nem lida (falha fechada).</summary>
public sealed class ChannelSecretsNotConfiguredException : Exception
{
    public ChannelSecretsNotConfiguredException(string detail)
        : base($"O armazenamento de credenciais não está configurado: {detail}")
    {
    }
}

/// <summary>
/// Outro processo gravou o estado do despacho de uma mensagem de saída depois de este o ler: quem gravaria agora (um handler cujo lease venceu e continua rodando, ou o tratamento de morto de outro relay) NÃO sobrescreve
/// o que já está gravado. O estado final de quem gravou primeiro vale (nunca se troca <c>SendUnknown</c> por <c>Sent</c> ou por <c>Failed</c> por conta de uma resposta tardia), e quem perdeu relê na próxima entrega.
/// </summary>
public sealed class OutboundStateConflictException : Exception
{
    public OutboundStateConflictException()
        : base("O estado do despacho foi gravado por outro processo; esta gravação foi descartada.")
    {
    }
}
