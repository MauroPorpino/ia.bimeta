using System.Text;
using System.Text.RegularExpressions;
using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.Channels.Domain;

namespace IaBimeta.Channels.Application;

/// <summary>
/// Valida os parâmetros que o atendente preencheu contra a estrutura aprovada do modelo e monta (1) a prévia do texto que o cliente verá, que vira o texto da mensagem no histórico, e (2) o bloco de dados do canal
/// que acompanha a mensagem até o Sender (<c>tpl.id</c>, <c>tpl.name</c>, <c>tpl.lang</c> e um <c>tpl.p.{chave}</c> por parâmetro). Toda a validação acontece ANTES de a mensagem ser registrada: o que a Meta recusaria
/// por parâmetro (faltando, a mais, vazio, quebra de linha, tamanho) o atendente vê no formulário, sem gastar uma tentativa de envio.
/// </summary>
public static partial class TemplateRenderer
{
    /// <summary>A versão do bloco de dados do canal que descreve um envio por modelo.</summary>
    public const int ChannelDataVersion = 1;

    public const string KeyTemplateId = "tpl.id";
    public const string KeyTemplateName = "tpl.name";
    public const string KeyTemplateLanguage = "tpl.lang";
    public const string KeyParameterPrefix = "tpl.p.";

    // Os mesmos tetos do Conversations (ConversationLimits): o bloco de canal não passa deles e a prévia é o texto da mensagem.
    private const int ChannelDataMaxTotalLength = 8192;
    private const int PreviewMaxLength = 4096;

    public static class ErrorCodes
    {
        /// <summary>Faltando ou vazio.</summary>
        public const string Required = "required";
        public const string Unknown = "unknown";
        public const string TooLong = "too-long";
        public const string InvalidCharacters = "invalid-characters";

        /// <summary>O texto final (ou o bloco de dados do canal) passa do que o atendimento guarda.</summary>
        public const string RenderedTooLong = "rendered-too-long";
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(" {4,}", RegexOptions.CultureInvariant)]
    private static partial Regex ManySpacesRegex();

    /// <summary>A chave do parâmetro no bloco de dados do canal.</summary>
    public static string ChannelDataKey(string parameterKey) => KeyParameterPrefix + parameterKey;

    /// <summary>Valida e monta a prévia e o bloco de canal. Quando há erro, devolve a lista completa deles (o formulário mostra todos de uma vez).</summary>
    public static TemplatePreparation Prepare(ChannelTemplate template, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(parameters);

        var erros = Validate(template, parameters);
        if (erros.Count > 0)
        {
            return new TemplatePreparation(0, null, null, null, erros);
        }

        var preview = Render(template, parameters);
        if (preview.Length > PreviewMaxLength)
        {
            return new TemplatePreparation(0, null, null, null, new[] { new TemplateParameterError("*", ErrorCodes.RenderedTooLong, "O texto final do modelo ficou longo demais.") });
        }

        var dados = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyTemplateId] = template.Id.ToString("D"),
            [KeyTemplateName] = template.Name,
            [KeyTemplateLanguage] = template.Language,
        };
        foreach (var p in template.Parameters)
        {
            dados[ChannelDataKey(p.Key)] = parameters[p.Key].Trim();
        }

        var total = dados.Sum(par => par.Key.Length + par.Value.Length);
        if (total > ChannelDataMaxTotalLength)
        {
            return new TemplatePreparation(0, null, null, null, new[] { new TemplateParameterError("*", ErrorCodes.RenderedTooLong, "Os valores preenchidos somam texto demais para o modelo.") });
        }

        return new TemplatePreparation(ChannelDataVersion, dados, preview, null, Array.Empty<TemplateParameterError>());
    }

    /// <summary>Confere os parâmetros recebidos contra a estrutura do modelo: nenhum faltando, nenhum a mais, nenhum vazio ou com caractere que a Meta recusa, nenhum além do tamanho.</summary>
    public static IReadOnlyList<TemplateParameterError> Validate(ChannelTemplate template, IReadOnlyDictionary<string, string> parameters)
    {
        var erros = new List<TemplateParameterError>();
        var conhecidos = template.Parameters.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var chave in parameters.Keys.Where(k => !conhecidos.Contains(k)).Order(StringComparer.Ordinal))
        {
            erros.Add(new TemplateParameterError(Printable(chave), ErrorCodes.Unknown, "Este parâmetro não existe no modelo."));
        }

        foreach (var p in template.Parameters)
        {
            if (!parameters.TryGetValue(p.Key, out var valor) || valor is null)
            {
                erros.Add(new TemplateParameterError(p.Key, ErrorCodes.Required, $"Preencha {p.Label}."));
                continue;
            }

            var limpo = valor.Trim();
            if (limpo.Length == 0)
            {
                erros.Add(new TemplateParameterError(p.Key, ErrorCodes.Required, $"{p.Label} não pode ficar vazio."));
            }
            else if (limpo.Length > p.MaxLength)
            {
                erros.Add(new TemplateParameterError(p.Key, ErrorCodes.TooLong, $"{p.Label} aceita no máximo {p.MaxLength} caracteres."));
            }
            else if (limpo.Any(char.IsControl) || ManySpacesRegex().IsMatch(limpo))
            {
                // A Meta recusa quebra de linha, tabulação e mais de 3 espaços seguidos no valor de um parâmetro de texto.
                erros.Add(new TemplateParameterError(p.Key, ErrorCodes.InvalidCharacters, $"{p.Label} não pode ter quebra de linha, tabulação nem mais de 3 espaços seguidos."));
            }
        }

        return erros;
    }

    /// <summary>O texto do modelo com os parâmetros no lugar: cabeçalho, corpo e rodapé, nesta ordem. Os botões não entram (o do link só complementa o endereço).</summary>
    public static string Render(ChannelTemplate template, IReadOnlyDictionary<string, string> parameters)
    {
        var texto = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(template.HeaderText))
        {
            texto.Append(Substitute(template.HeaderText, TemplateParameterSpec.Header, template, parameters)).Append('\n');
        }

        texto.Append(Substitute(template.BodyText, TemplateParameterSpec.Body, template, parameters));

        if (!string.IsNullOrWhiteSpace(template.FooterText))
        {
            texto.Append('\n').Append(template.FooterText);
        }

        return texto.ToString();
    }

    private static string Substitute(string texto, string componente, ChannelTemplate template, IReadOnlyDictionary<string, string> parameters) =>
        MarkerRegex().Replace(texto, marcador =>
        {
            var bruto = marcador.Groups[1].Value;
            var chave = $"{componente}.{bruto}";
            var spec = template.Parameters.FirstOrDefault(p => p.Component == componente && p.Key == chave);
            return spec is not null && parameters.TryGetValue(spec.Key, out var valor) ? valor.Trim() : marcador.Value;
        });

    /// <summary>A chave recebida de fora, curta e sem caractere de controle, para ecoar na mensagem de erro.</summary>
    private static string Printable(string chave) => new(chave.Where(c => !char.IsControl(c)).Take(64).ToArray());
}
