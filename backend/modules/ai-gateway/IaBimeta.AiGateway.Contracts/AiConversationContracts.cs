namespace IaBimeta.AiGateway.Contracts;

/// <summary>Quem escreveu a mensagem que o modelo recebe. A instrução de sistema não é uma mensagem: tem campo próprio no pedido.</summary>
public enum AiMessageRole
{
    User = 1,

    Assistant = 2,
}

/// <summary>Uma mensagem do contexto da chamada. Só texto (ADR 0017, seção C.2): nenhum conteúdo de mídia entra no contrato.</summary>
public sealed record AiMessage(AiMessageRole Role, string Text);

/// <summary>Como o modelo deve responder: texto livre ou um JSON que obedece a um esquema (a decisão estruturada do Agents).</summary>
public enum AiOutputMode
{
    Text = 1,

    /// <summary>A resposta é um objeto JSON que obedece ao <see cref="AiStructuredOutput"/> do pedido.</summary>
    StructuredJson = 2,
}

/// <summary>
/// O esquema da saída estruturada. O mínimo comum aos provedores (ADR 0017, seção C.5): objeto na raiz com <c>additionalProperties: false</c>, todos os
/// campos obrigatórios, sem <c>anyOf</c> na raiz e sem <c>minimum</c>, <c>maximum</c>, <c>minLength</c> nem <c>maxLength</c>. O AiGateway recusa o
/// pedido que fugir disso; o Agents continua validando a resposta contra a própria lista fechada de ações.
/// </summary>
/// <param name="Name">O nome do esquema (de 1 a 64 caracteres: letras, números, <c>_</c> e <c>-</c>).</param>
/// <param name="JsonSchema">O esquema, em JSON.</param>
public sealed record AiStructuredOutput(string Name, string JsonSchema);

/// <summary>
/// A credencial do tenant em memória, resolvida na hora da chamada e mantida pelo menor tempo possível. O valor NUNCA aparece em texto, em log nem em
/// mensagem de erro: <see cref="ToString"/> o oculta. Não é serializada nem persistida por este contrato.
/// </summary>
public sealed record AiCredential(string Secret)
{
    /// <summary>
    /// A ausência de credencial, para o provedor que declara <see cref="Ports.IAiProvider.RequiresCredential"/> falso (um servidor de IA local, Etapa 04). NÃO é uma chave fictícia: é vazia, e um adaptador
    /// que exige credencial nunca a recebe (o AiGateway falha fechado sem a credencial do tenant).
    /// </summary>
    public static AiCredential None { get; } = new(string.Empty);

    public override string ToString() => "AiCredential { Secret = [oculto] }";
}

/// <summary>O motivo normalizado de a geração ter parado.</summary>
public enum AiStopReason
{
    /// <summary>O modelo terminou a resposta.</summary>
    Completed = 1,

    /// <summary>O teto de tokens de saída cortou a resposta: o texto está incompleto (e uma saída estruturada, provavelmente inválida).</summary>
    Truncated = 2,

    /// <summary>O modelo recusou responder. O texto pode estar vazio, e os tokens consumidos foram cobrados.</summary>
    Refused = 3,
}

/// <summary>A resposta gerada.</summary>
/// <param name="Text">O texto (ou o JSON da saída estruturada). Vazio quando o modelo não produziu texto (por exemplo, numa recusa).</param>
public sealed record AiCompletion(string Text, AiStopReason StopReason);
