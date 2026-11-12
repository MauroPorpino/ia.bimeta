using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Core.Application;

/// <summary>Moldes de texto <c>{{variável}}</c>: UMA passada, sem recursão. O valor inserido nunca é reinterpretado como molde (ADR 0037 §2).</summary>
public static partial class TemplateRenderer
{
    public const string VariablePattern = "[a-z][a-z0-9_.]{0,63}";

    [GeneratedRegex(@"\{\{\s*(?<n>[a-z][a-z0-9_.]{0,63})\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    public static string Render(string? template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return Placeholder().Replace(template, m => variables.TryGetValue(m.Groups["n"].Value, out var v) ? v : string.Empty);
    }

    public static IEnumerable<string> ReferencedVariables(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            yield break;
        }

        foreach (Match m in Placeholder().Matches(template))
        {
            yield return m.Groups["n"].Value;
        }
    }
}

/// <summary>Normalização e validação das respostas do cliente, independente de canal.</summary>
public static partial class AnswerParser
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex EmailPattern();

    /// <summary>Minúsculas, sem acentos e com espaços colapsados (comparação de rótulos e escolhas).</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastSpace = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace)
                {
                    sb.Append(' ');
                }

                lastSpace = true;
                continue;
            }

            lastSpace = false;
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>Devolve o valor normalizado a guardar, ou nulo se a entrada não responde à pergunta.</summary>
    public static string? Parse(NodeConfig cfg, InboundInput input, FlowLimits limits)
    {
        if (input.MediaKind is not null && cfg.AnswerType != AnswerType.Choice)
        {
            return null;
        }

        if (cfg.AnswerType == AnswerType.Choice)
        {
            return ParseChoice(cfg, input);
        }

        var text = input.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > limits.MaxVariableLength)
        {
            return null;
        }

        switch (cfg.AnswerType)
        {
            case AnswerType.Number:
                return decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null;
            case AnswerType.Email:
                return text.Length <= 254 && EmailPattern().IsMatch(text) ? text.ToLowerInvariant() : null;
            case AnswerType.Phone:
                {
                    var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
                    return digits.Length is >= 8 and <= 15 ? (text.StartsWith('+') ? "+" : string.Empty) + digits : null;
                }

            case AnswerType.Date:
                return DateTime.TryParseExact(text, ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null;
            case AnswerType.Pattern:
                try
                {
                    // NonBacktracking: tempo linear no tamanho da entrada, sem ReDoS (o padrão é do administrador, a entrada é do cliente).
                    return cfg.Pattern is not null && Regex.IsMatch(text, cfg.Pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)) ? text : null;
                }
                catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
                {
                    return null;
                }

            default:
                return text;
        }
    }

    private static string? ParseChoice(NodeConfig cfg, InboundInput input)
    {
        var choices = cfg.Choices ?? [];
        if (!string.IsNullOrEmpty(input.ChoiceId))
        {
            foreach (var c in choices)
            {
                if (string.Equals(c.Id, input.ChoiceId, StringComparison.Ordinal))
                {
                    return c.Id;
                }
            }

            return null;
        }

        var text = Normalize(input.Text);
        if (text.Length == 0)
        {
            return null;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 1 && index <= choices.Count)
        {
            return choices[index - 1].Id;
        }

        foreach (var c in choices)
        {
            if (Normalize(c.Label) == text || Normalize(c.Id) == text)
            {
                return c.Id;
            }
        }

        return null;
    }
}

/// <summary>Avalia as condições declarativas. Puro: o instante entra por parâmetro (ADR 0037 §3).</summary>
public static class ConditionEvaluator
{
    public static bool All(IReadOnlyList<FlowCondition> conditions, IReadOnlyDictionary<string, string> vars, DateTimeOffset now)
    {
        foreach (var c in conditions)
        {
            if (!One(c, vars, now))
            {
                return false;
            }
        }

        return true;
    }

    public static bool One(FlowCondition c, IReadOnlyDictionary<string, string> vars, DateTimeOffset now)
    {
        if (c.Op == ConditionOperator.WithinBusinessHours)
        {
            return c.Schedule is not null && WithinSchedule(c.Schedule, now);
        }

        vars.TryGetValue(c.Variable ?? string.Empty, out var value);
        var left = value ?? string.Empty;
        switch (c.Op)
        {
            case ConditionOperator.IsEmpty:
                return string.IsNullOrWhiteSpace(left);
            case ConditionOperator.IsNotEmpty:
                return !string.IsNullOrWhiteSpace(left);
            case ConditionOperator.Equals:
                return AnswerParser.Normalize(left) == AnswerParser.Normalize(c.Value);
            case ConditionOperator.NotEquals:
                return AnswerParser.Normalize(left) != AnswerParser.Normalize(c.Value);
            case ConditionOperator.Contains:
                return c.Value is { Length: > 0 } && AnswerParser.Normalize(left).Contains(AnswerParser.Normalize(c.Value), StringComparison.Ordinal);
            case ConditionOperator.In:
                {
                    var l = AnswerParser.Normalize(left);
                    return c.Values is not null && c.Values.Any(v => AnswerParser.Normalize(v) == l);
                }

            case ConditionOperator.GreaterThan:
            case ConditionOperator.LessThan:
                if (decimal.TryParse(left, NumberStyles.Number, CultureInfo.InvariantCulture, out var a) &&
                    decimal.TryParse(c.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var b))
                {
                    return c.Op == ConditionOperator.GreaterThan ? a > b : a < b;
                }

                return false;
            default:
                return false;
        }
    }

    public static bool TryParseTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out time);

    public static bool WithinSchedule(BusinessSchedule schedule, DateTimeOffset now)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var day = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;
        var time = local.TimeOfDay;
        foreach (var w in schedule.Windows)
        {
            if (w.Days.Contains(day) && TryParseTime(w.From, out var from) && TryParseTime(w.To, out var to) && time >= from && time < to)
            {
                return true;
            }
        }

        return false;
    }
}
