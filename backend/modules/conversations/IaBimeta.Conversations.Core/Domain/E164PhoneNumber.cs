namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Normalização de telefone para E.164 (ADR 0041, seção 4): <c>+</c> seguido de 8 a 15 dígitos, sem zero inicial no código do país. É a CHAVE do consentimento (o contato pode ter uma referência opaca, como um BSUID, que
/// não é telefone). Aceita os separadores comuns (espaço, hífen, ponto, parênteses) e o prefixo internacional <c>00</c>. A entrada SEM <c>+</c> nem <c>00</c> é lida como já internacional (é o formato do <c>wa_id</c> do
/// WhatsApp, por exemplo <c>5511999990000</c>): o sistema externo precisa enviar o número com o código do país; um número nacional sem código seria aceito como outro número, por isso o contrato do vínculo o exige.
/// Qualquer outro caractere (letras, símbolos, controle) recusa: nunca se adivinha um número.
/// </summary>
public static class E164PhoneNumber
{
    public const int MinDigits = 8;
    public const int MaxDigits = 15;

    /// <summary>O padrão SQL do formato normalizado (usado no CHECK das tabelas: o banco recusa um telefone fora do formato).</summary>
    public const string SqlPattern = "'^\\+[1-9][0-9]{7,14}$'";

    public static bool TryNormalize(string? input, out string e164)
    {
        e164 = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 40)
        {
            return false;
        }

        var texto = input.Trim();
        var inicio = 0;
        if (texto[0] == '+')
        {
            inicio = 1;
        }
        else if (texto.StartsWith("00", StringComparison.Ordinal))
        {
            inicio = 2;
        }

        Span<char> digitos = stackalloc char[MaxDigits];
        var quantidade = 0;
        for (var i = inicio; i < texto.Length; i++)
        {
            var c = texto[i];
            if (c is ' ' or '-' or '.' or '(' or ')')
            {
                continue;
            }

            // Só dígitos ASCII (char.IsDigit aceitaria dígitos de outros alfabetos).
            if (c is < '0' or > '9' || quantidade == MaxDigits)
            {
                return false;
            }

            digitos[quantidade++] = c;
        }

        if (quantidade < MinDigits || digitos[0] == '0')
        {
            return false;
        }

        e164 = "+" + new string(digitos[..quantidade]);
        return true;
    }

    /// <exception cref="ConversationsValidationException">O telefone não pode ser normalizado.</exception>
    public static string Normalize(string? input) =>
        TryNormalize(input, out var e164) ? e164 : throw new ConversationsValidationException("O telefone deve estar no formato internacional (E.164): + e de 8 a 15 dígitos.");

    /// <summary>
    /// A versão ESTRITA para o que o sistema externo envia (E2.2): exige o prefixo internacional explícito (<c>+</c> ou <c>00</c>). Um número só com dígitos pode ser nacional (sem o código do país) e seria lido como outro
    /// número válido; como o sistema não tem como distinguir, recusa em vez de adivinhar. O formato <c>wa_id</c> (só dígitos) continua aceito por <see cref="TryNormalize"/> para o que o provedor entrega.
    /// </summary>
    public static bool TryNormalizeInternational(string? input, out string e164)
    {
        e164 = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var texto = input.Trim();
        return (texto.StartsWith('+') || texto.StartsWith("00", StringComparison.Ordinal)) && TryNormalize(texto, out e164);
    }
}
