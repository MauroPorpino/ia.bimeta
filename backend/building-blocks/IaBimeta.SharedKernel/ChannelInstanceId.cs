using System.Text.Json;
using System.Text.Json.Serialization;

namespace IaBimeta.SharedKernel;

/// <summary>
/// Identificador de uma instância de canal: um número de WhatsApp específico de um tenant (o <c>Channels</c> é o dono do
/// cadastro, com WABA ID, Phone Number ID e credenciais). Os demais módulos só o <b>referenciam</b> como identificador
/// opaco, sem chave estrangeira entre esquemas e sem depender do <c>Channels</c>. Vive aqui porque vários módulos o usam
/// e a matriz de dependências não deixa o Conversations referenciar o Channels (DEP-4).
/// <para>
/// O identificador sozinho nunca estabelece o tenant: o tenant de toda operação vem do <see cref="RequestContext"/>
/// confiável, criado pelo host depois de o <c>Channels</c> validar a autenticidade do webhook e resolver o canal.
/// </para>
/// </summary>
[JsonConverter(typeof(ChannelInstanceIdJsonConverter))]
public readonly record struct ChannelInstanceId
{
    /// <exception cref="ArgumentException">O valor é vazio.</exception>
    public ChannelInstanceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("ChannelInstanceId não pode ser vazio.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    /// <summary>Verdadeiro para o valor padrão (<c>default</c>), que não identifica canal algum e nunca é aceito por uma operação.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    public static ChannelInstanceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>Lê e escreve o identificador como texto (<c>"0192…"</c>). Valor vazio ou mal formado recusa a leitura (HTTP 400).</summary>
public sealed class ChannelInstanceIdJsonConverter : JsonConverter<ChannelInstanceId>
{
    public override ChannelInstanceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !Guid.TryParse(reader.GetString(), out var valor) || valor == Guid.Empty)
        {
            throw new JsonException("O identificador da instância de canal deve ser um GUID não vazio.");
        }

        return new ChannelInstanceId(valor);
    }

    public override void Write(Utf8JsonWriter writer, ChannelInstanceId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
