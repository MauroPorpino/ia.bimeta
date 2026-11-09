using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;

namespace IaBimeta.Channels.Application;

/// <summary>A definição de um modelo, normalizada a partir do JSON da Meta.</summary>
public sealed record ParsedTemplate(
    string Name,
    string Language,
    TemplateCategory Category,
    TemplateStatus Status,
    string? MetaTemplateId,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<TemplateParameterSpec> Parameters);

/// <summary>
/// Lê a definição de um modelo NO FORMATO DA META (o objeto que a Graph API devolve em <c>GET /{waba}/message_templates</c> e que o Gerenciador de WhatsApp mostra): <c>name</c>, <c>language</c>, <c>status</c>,
/// <c>category</c>, <c>id</c>, <c>parameter_format</c> e <c>components</c> (<c>HEADER</c>, <c>BODY</c>, <c>FOOTER</c>, <c>BUTTONS</c>) com os marcadores <c>{{1}}</c> ou <c>{{nome}}</c>. Extrai a estrutura de
/// parâmetros e RECUSA, com o motivo, o que o envio de modelos do MVP não sabe montar (cabeçalho de mídia, botão de copiar código, fluxo, catálogo, autenticação): melhor recusar no cadastro do que falhar na Meta
/// com o atendente esperando. Nunca lança por JSON estranho: o motivo vai numa <see cref="ChannelsValidationException"/>.
/// </summary>
public static class TemplateDefinitionParser
{
    private static readonly Regex Placeholder = new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Até 40 caracteres: a chave do parâmetro entra no bloco de canal do Conversations ("tpl.p.body.<nome>"), cujas chaves têm no máximo 64.
    private static readonly Regex NamedKey = new("^[a-z][a-z0-9_]{0,39}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const int HeaderParameterMaxLength = 60;
    public const int BodyParameterMaxLength = 1024;
    public const int ButtonParameterMaxLength = 512;

    /// <exception cref="ChannelsValidationException">O JSON não é um modelo que o MVP saiba enviar.</exception>
    public static ParsedTemplate Parse(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object)
        {
            throw new ChannelsValidationException("A definição do modelo deve ser um objeto JSON.");
        }

        var nome = Text(definition, "name") ?? throw new ChannelsValidationException("O modelo não tem nome (name).");
        var idioma = Text(definition, "language") ?? throw new ChannelsValidationException($"O modelo '{Safe(nome)}' não tem idioma (language).");
        var categoria = ParseCategory(Text(definition, "category"), nome);
        var situacao = ParseStatus(Text(definition, "status"), nome);
        var metaId = Text(definition, "id");
        var nomeado = string.Equals(Text(definition, "parameter_format"), "NAMED", StringComparison.OrdinalIgnoreCase);

        if (!definition.TryGetProperty("components", out var componentes) || componentes.ValueKind != JsonValueKind.Array)
        {
            throw new ChannelsValidationException($"O modelo '{Safe(nome)}' não tem componentes (components).");
        }

        string? cabecalho = null;
        string? corpo = null;
        string? rodape = null;
        var parametros = new List<TemplateParameterSpec>();

        foreach (var componente in componentes.EnumerateArray())
        {
            if (componente.ValueKind != JsonValueKind.Object)
            {
                throw new ChannelsValidationException($"O modelo '{Safe(nome)}' tem um componente inválido.");
            }

            switch ((Text(componente, "type") ?? string.Empty).ToUpperInvariant())
            {
                case "HEADER":
                {
                    var formato = (Text(componente, "format") ?? "TEXT").ToUpperInvariant();
                    if (formato != "TEXT")
                    {
                        throw new ChannelsValidationException($"O modelo '{Safe(nome)}' tem cabeçalho de {formato}: só cabeçalho de texto é suportado por enquanto.");
                    }

                    cabecalho = Text(componente, "text");
                    if (cabecalho is not null)
                    {
                        var marcadores = Markers(cabecalho, nomeado, nome, "cabeçalho");
                        if (marcadores.Count > 1)
                        {
                            throw new ChannelsValidationException($"O cabeçalho do modelo '{Safe(nome)}' tem mais de uma variável (a Meta aceita uma).");
                        }

                        parametros.AddRange(marcadores.Select(m => Spec(TemplateParameterSpec.Header, m, componente, "header_text", "header_text_named_params", HeaderParameterMaxLength)));
                    }

                    break;
                }

                case "BODY":
                    corpo = Text(componente, "text") ?? throw new ChannelsValidationException($"O modelo '{Safe(nome)}' tem um corpo sem texto.");
                    parametros.AddRange(Markers(corpo, nomeado, nome, "corpo").Select(m => Spec(TemplateParameterSpec.Body, m, componente, "body_text", "body_text_named_params", BodyParameterMaxLength)));
                    break;

                case "FOOTER":
                    rodape = Text(componente, "text");
                    if (rodape is not null && Placeholder.IsMatch(rodape))
                    {
                        throw new ChannelsValidationException($"O rodapé do modelo '{Safe(nome)}' tem variável, o que a Meta não aceita.");
                    }

                    break;

                case "BUTTONS":
                    parametros.AddRange(ParseButtons(componente, nome));
                    break;

                default:
                    throw new ChannelsValidationException($"O modelo '{Safe(nome)}' tem um componente que não é suportado ({Safe(Text(componente, "type") ?? "sem tipo")}).");
            }
        }

        if (corpo is null)
        {
            throw new ChannelsValidationException($"O modelo '{Safe(nome)}' não tem corpo (BODY).");
        }

        // Os parâmetros do cabeçalho e do corpo e dos botões: a ordem de preenchimento do formulário.
        var ordem = new[] { TemplateParameterSpec.Header, TemplateParameterSpec.Body, TemplateParameterSpec.Button };
        var ordenados = parametros.OrderBy(p => Array.IndexOf(ordem, p.Component)).ThenBy(p => p.Index).ToList();

        try
        {
            // A validação final (nome, idioma, tamanhos) é a da entidade: o mesmo cadastro pelas duas portas.
            var modelo = ChannelTemplate.Create(
                new IaBimeta.SharedKernel.TenantId(Guid.NewGuid()), Guid.NewGuid(), nome, idioma, categoria, situacao, metaId, cabecalho, corpo, rodape, ordenados, DateTimeOffset.UnixEpoch);
            return new ParsedTemplate(modelo.Name, modelo.Language, modelo.Category, modelo.Status, modelo.MetaTemplateId, modelo.HeaderText, modelo.BodyText, modelo.FooterText, modelo.Parameters);
        }
        catch (ChannelsValidationException ex)
        {
            throw new ChannelsValidationException($"Modelo '{Safe(nome)}': {ex.Message}");
        }
    }

    private static IEnumerable<TemplateParameterSpec> ParseButtons(JsonElement componente, string nome)
    {
        if (!componente.TryGetProperty("buttons", out var botoes) || botoes.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var indice = 0;
        foreach (var botao in botoes.EnumerateArray())
        {
            var tipo = (Text(botao, "type") ?? string.Empty).ToUpperInvariant();
            switch (tipo)
            {
                case "QUICK_REPLY":
                case "PHONE_NUMBER":
                    break;

                case "URL":
                {
                    var url = Text(botao, "url") ?? string.Empty;
                    var marcadores = Placeholder.Matches(url);
                    if (marcadores.Count > 1 || (marcadores.Count == 1 && !url.EndsWith("{{1}}", StringComparison.Ordinal)))
                    {
                        throw new ChannelsValidationException($"O botão de link do modelo '{Safe(nome)}' deve ter, no máximo, uma variável {{{{1}}}} no fim do endereço.");
                    }

                    if (marcadores.Count == 1)
                    {
                        string? exemplo = null;
                        if (botao.TryGetProperty("example", out var exemplos) && exemplos.ValueKind == JsonValueKind.Array && exemplos.GetArrayLength() > 0 && exemplos[0].ValueKind == JsonValueKind.String)
                        {
                            // A Meta guarda o endereço COMPLETO de exemplo; o parâmetro é só o complemento depois do prefixo fixo do botão.
                            exemplo = exemplos[0].GetString();
                            var prefixo = url[..url.IndexOf("{{1}}", StringComparison.Ordinal)];
                            if (exemplo is not null && prefixo.Length > 0 && exemplo.StartsWith(prefixo, StringComparison.Ordinal))
                            {
                                exemplo = exemplo[prefixo.Length..];
                            }
                        }

                        yield return new TemplateParameterSpec($"button.{indice}", TemplateParameterSpec.Button, indice, null, exemplo, ButtonParameterMaxLength);
                    }

                    break;
                }

                default:
                    throw new ChannelsValidationException($"O modelo '{Safe(nome)}' tem um botão que não é suportado ({Safe(tipo.Length == 0 ? "sem tipo" : tipo)}): só resposta rápida, telefone e link são aceitos.");
            }

            indice++;
        }
    }

    /// <summary>Os marcadores do texto, na ordem de aparição e sem repetição. Posicionais: de 1 a N seguidos; nomeados: só nomes. Misturar é recusado.</summary>
    private static List<(string Raw, int Index, string? Name)> Markers(string texto, bool nomeado, string nome, string onde)
    {
        var vistos = new List<string>();
        foreach (Match m in Placeholder.Matches(texto))
        {
            if (!vistos.Contains(m.Groups[1].Value, StringComparer.Ordinal))
            {
                vistos.Add(m.Groups[1].Value);
            }
        }

        var resultado = new List<(string, int, string?)>();
        if (vistos.Count == 0)
        {
            return resultado;
        }

        var numericos = vistos.Where(v => v.All(char.IsAsciiDigit)).ToList();
        if (numericos.Count != 0 && numericos.Count != vistos.Count)
        {
            throw new ChannelsValidationException($"O {onde} do modelo '{Safe(nome)}' mistura variáveis numeradas e nomeadas.");
        }

        if (numericos.Count == vistos.Count && !nomeado)
        {
            var numeros = numericos.Select(int.Parse).OrderBy(n => n).ToList();
            if (!numeros.SequenceEqual(Enumerable.Range(1, numeros.Count)))
            {
                throw new ChannelsValidationException($"As variáveis do {onde} do modelo '{Safe(nome)}' devem ser numeradas de 1 em diante, sem pular.");
            }

            return numeros.Select(n => (n.ToString(System.Globalization.CultureInfo.InvariantCulture), n, (string?)null)).ToList();
        }

        var indice = 0;
        foreach (var v in vistos)
        {
            if (!NamedKey.IsMatch(v))
            {
                throw new ChannelsValidationException($"A variável '{Safe(v)}' do {onde} do modelo '{Safe(nome)}' deve ter só letras minúsculas, números e sublinhado, começando por letra.");
            }

            indice++;
            resultado.Add((v, indice, v));
        }

        return resultado;
    }

    private static TemplateParameterSpec Spec(string componente, (string Raw, int Index, string? Name) marcador, JsonElement definicao, string posicional, string nomeados, int tamanhoMaximo)
    {
        string? exemplo = null;
        if (definicao.TryGetProperty("example", out var exemplos) && exemplos.ValueKind == JsonValueKind.Object)
        {
            if (marcador.Name is null && exemplos.TryGetProperty(posicional, out var lista) && lista.ValueKind == JsonValueKind.Array && lista.GetArrayLength() > 0)
            {
                // O corpo traz uma lista de listas ([["a", "b"]]); o cabeçalho, uma lista simples (["a"]).
                var primeiro = lista[0];
                if (primeiro.ValueKind == JsonValueKind.Array && marcador.Index - 1 < primeiro.GetArrayLength() && primeiro[marcador.Index - 1].ValueKind == JsonValueKind.String)
                {
                    exemplo = primeiro[marcador.Index - 1].GetString();
                }
                else if (primeiro.ValueKind == JsonValueKind.String && marcador.Index == 1)
                {
                    exemplo = primeiro.GetString();
                }
            }
            else if (marcador.Name is not null && exemplos.TryGetProperty(nomeados, out var pares) && pares.ValueKind == JsonValueKind.Array)
            {
                foreach (var par in pares.EnumerateArray())
                {
                    if (string.Equals(Text(par, "param_name"), marcador.Name, StringComparison.Ordinal))
                    {
                        exemplo = Text(par, "example");
                    }
                }
            }
        }

        return new TemplateParameterSpec($"{componente}.{marcador.Raw}", componente, marcador.Index, marcador.Name, exemplo, tamanhoMaximo);
    }

    private static TemplateCategory ParseCategory(string? texto, string nome) => (texto ?? string.Empty).ToUpperInvariant() switch
    {
        "MARKETING" => TemplateCategory.Marketing,
        "UTILITY" => TemplateCategory.Utility,
        "AUTHENTICATION" => throw new ChannelsValidationException($"O modelo '{Safe(nome)}' é de autenticação: essa categoria ainda não é suportada (o botão de copiar código tem parâmetro próprio)."),
        _ => throw new ChannelsValidationException($"O modelo '{Safe(nome)}' tem categoria desconhecida ou ausente (category)."),
    };

    private static TemplateStatus ParseStatus(string? texto, string nome) => (texto ?? string.Empty).ToUpperInvariant() switch
    {
        "APPROVED" => TemplateStatus.Approved,
        "PENDING" or "IN_APPEAL" or "PENDING_DELETION" or "LIMIT_EXCEEDED" => TemplateStatus.Pending,
        "REJECTED" => TemplateStatus.Rejected,
        "PAUSED" or "FLAGGED" => TemplateStatus.Paused,
        "DISABLED" or "DELETED" => TemplateStatus.Disabled,
        _ => throw new ChannelsValidationException($"O modelo '{Safe(nome)}' tem situação desconhecida ou ausente (status)."),
    };

    private static string? Text(JsonElement objeto, string propriedade) =>
        objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(propriedade, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    /// <summary>O trecho vindo do JSON que entra numa mensagem de erro: curto e sem caractere de controle.</summary>
    private static string Safe(string? texto)
    {
        var limpo = new string((texto ?? string.Empty).Where(c => !char.IsControl(c)).Take(80).ToArray());
        return limpo;
    }
}
