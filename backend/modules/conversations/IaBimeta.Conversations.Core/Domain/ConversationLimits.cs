using System.Text.RegularExpressions;
using IaBimeta.Conversations.Contracts;

namespace IaBimeta.Conversations.Domain;

/// <summary>Limites e validações de formato do módulo. Valores fixos: não há configuração por tenant (CONVERSATION-LIFECYCLE.md §14).</summary>
public static class ConversationLimits
{
    public const int TextMaxLength = 4096;

    /// <summary>
    /// A referência do contato no provedor: 254 caracteres, o máximo de um endereço de e-mail (RFC 5321), para que o mesmo limite sirva ao WhatsApp, às demais
    /// redes de chat e ao e-mail (ADR 0012, seção A, item 6). Alargado de 128 (migração <c>ChatSessionAssociationAndLimits</c>); é o tamanho da coluna <c>external_ref</c>.
    /// </summary>
    public const int ContactRefMaxLength = 254;

    public const int DisplayNameMaxLength = 120;

    /// <summary>
    /// O identificador da mensagem no provedor (a chave de idempotência): 512 caracteres, para caber o <c>Message-ID</c> de e-mail e identificadores longos de
    /// outros provedores (ADR 0012, seção A, item 6). Alargado de 200; é o tamanho da coluna <c>provider_event_id</c>.
    /// </summary>
    public const int ProviderEventIdMaxLength = 512;

    public const int ReasonMaxLength = 100;

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public const int DefaultSnapshotMessages = 50;
    public const int MaxSnapshotMessages = 200;

    // Limites estruturais do bloco opaco de metadados do canal (ChannelData). São FIXOS: não dependem de configuração mutável nem de tenant, então uma
    // repetição de um pedido não quebra por mudança de limite (ADR 0014, seção A, passo 1). O contrato os documenta em `ChannelData`.
    public const int ChannelDataMinVersion = 1;
    public const int ChannelDataMaxEntries = 32;
    public const int ChannelDataKeyMaxLength = 64;
    public const int ChannelDataValueMaxLength = 1024;
    public const int ChannelDataMaxTotalLength = 8192;

    /// <summary>Motivos são códigos curtos (<c>manual</c>, <c>chatty-burst</c>), sem espaços: nada de texto livre que possa carregar segredo.</summary>
    private static readonly Regex ReasonPattern = new("^[a-z0-9][a-z0-9_.-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Chaves de <c>ChannelData</c>: o mesmo formato dos códigos curtos, mas ancorado em <c>\z</c>, para que uma quebra de linha no fim não passe.</summary>
    private static readonly Regex ChannelDataKeyPattern = new("^[a-z0-9][a-z0-9_.-]*\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// O identificador da mensagem no provedor, sem os espaços das pontas: de 1 a <see cref="ProviderEventIdMaxLength"/> caracteres. É a mesma regra da criação da mensagem,
    /// e a ingestão a aplica ANTES de qualquer consulta: um identificador sem limite não chega a ser usado como parâmetro de uma leitura.
    /// </summary>
    /// <exception cref="ConversationsValidationException">O identificador está vazio ou acima do limite.</exception>
    public static string ValidateProviderEventId(string? providerEventId)
    {
        var evento = (providerEventId ?? string.Empty).Trim();
        if (evento.Length == 0 || evento.Length > ProviderEventIdMaxLength)
        {
            throw new ConversationsValidationException($"O id do evento do provedor deve ter de 1 a {ProviderEventIdMaxLength} caracteres.");
        }

        return evento;
    }

    /// <exception cref="ConversationsValidationException">O motivo está fora do formato.</exception>
    public static string ValidateReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason) || reason.Length > ReasonMaxLength || !ReasonPattern.IsMatch(reason))
        {
            throw new ConversationsValidationException($"O motivo deve ter de 1 a {ReasonMaxLength} caracteres: letras minúsculas, números e '_.-'.");
        }

        return reason;
    }

    /// <summary>A origem de uma tradução é um código curto (<c>human</c>, <c>machine.provider-x</c>), no mesmo formato dos motivos: sem texto livre.</summary>
    /// <exception cref="ConversationsValidationException">A origem está fora do formato.</exception>
    public static string ValidateProvenance(string? provenance)
    {
        if (string.IsNullOrEmpty(provenance) || provenance.Length > ReasonMaxLength || !ReasonPattern.IsMatch(provenance))
        {
            throw new ConversationsValidationException($"A origem da tradução deve ter de 1 a {ReasonMaxLength} caracteres: letras minúsculas, números e '_.-'.");
        }

        return provenance;
    }

    /// <summary>
    /// Confere a FORMA do bloco de metadados do canal: versão de 1 em diante, no máximo <see cref="ChannelDataMaxEntries"/> entradas, chaves no formato dos códigos
    /// curtos, valores sem caractere de controle, e os limites de tamanho de chave, de valor e do total (chaves mais valores). Não interpreta nenhum valor.
    /// </summary>
    /// <exception cref="ConversationsValidationException">O bloco está fora dos limites ou do formato.</exception>
    public static void ValidateChannelData(ChannelData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Version < ChannelDataMinVersion)
        {
            throw new ConversationsValidationException($"A versão dos dados do canal deve ser {ChannelDataMinVersion} ou maior.");
        }

        if (data.Values is null)
        {
            throw new ConversationsValidationException("Os dados do canal precisam de um conjunto de valores (pode ser vazio).");
        }

        if (data.Values.Count > ChannelDataMaxEntries)
        {
            throw new ConversationsValidationException($"Os dados do canal aceitam no máximo {ChannelDataMaxEntries} entradas.");
        }

        var total = 0;
        foreach (var (chave, valor) in data.Values)
        {
            if (string.IsNullOrEmpty(chave) || chave.Length > ChannelDataKeyMaxLength || !ChannelDataKeyPattern.IsMatch(chave))
            {
                throw new ConversationsValidationException(
                    $"Cada chave dos dados do canal deve ter de 1 a {ChannelDataKeyMaxLength} caracteres: letras minúsculas, números e '_.-', começando por letra ou número.");
            }

            if (valor is null)
            {
                throw new ConversationsValidationException("Os valores dos dados do canal não podem ser nulos.");
            }

            if (valor.Length > ChannelDataValueMaxLength)
            {
                throw new ConversationsValidationException($"Cada valor dos dados do canal aceita no máximo {ChannelDataValueMaxLength} caracteres.");
            }

            if (valor.Any(char.IsControl))
            {
                throw new ConversationsValidationException("Os valores dos dados do canal não podem ter caractere de controle.");
            }

            // O bloco é GUARDADO como JSON (A3): um substituto UTF-16 sem par não é texto Unicode válido, e o banco o recusaria na gravação. Recusar aqui, antes de
            // qualquer acesso ao armazenamento, é o que mantém a falha limpa em vez de um erro do banco depois da trava da sessão.
            if (HasUnpairedSurrogate(valor))
            {
                throw new ConversationsValidationException("Os valores dos dados do canal precisam ser texto Unicode válido (sem substituto UTF-16 sem par).");
            }

            total += chave.Length + valor.Length;
            if (total > ChannelDataMaxTotalLength)
            {
                throw new ConversationsValidationException($"Os dados do canal aceitam no máximo {ChannelDataMaxTotalLength} caracteres somando chaves e valores.");
            }
        }
    }

    /// <summary>Há um substituto UTF-16 alto sem o baixo que o segue, ou um baixo sem o alto que o precede? Um par válido (por exemplo, um emoji) não conta.</summary>
    private static bool HasUnpairedSurrogate(string texto)
    {
        for (var i = 0; i < texto.Length; i++)
        {
            var c = texto[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= texto.Length || !char.IsLowSurrogate(texto[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }
}
