using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Core.Application;

/// <summary>
/// Lê e grava o documento do grafo (o mesmo JSON do editor). A leitura é estrita: campo desconhecido, documento grande ou profundo demais e enumeração inválida são recusados,
/// e o servidor nunca confia no que o navegador enviou (ADR 0037 §2).
/// </summary>
public static class FlowGraphSerializer
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            MaxDepth = 24,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return o;
    }

    /// <summary>Devolve o grafo ou o motivo curto da recusa (sem eco do conteúdo).</summary>
    public static bool TryParse(string json, FlowLimits limits, out FlowGraph? graph, out string? error)
    {
        graph = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "document-empty";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(json) > limits.MaxDocumentBytes)
        {
            error = "document-too-large";
            return false;
        }

        try
        {
            graph = JsonSerializer.Deserialize<FlowGraph>(json, Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            error = "document-invalid";
            return false;
        }

        if (graph is null || graph.Nodes is null || graph.Edges is null || graph.Start is null)
        {
            graph = null;
            error = "document-invalid";
            return false;
        }

        return true;
    }

    public static string Serialize(FlowGraph graph) => JsonSerializer.Serialize(graph, Options);

    /// <summary>
    /// O hash SHA-256 do significado do fluxo: ignora as posições do canvas e os rótulos (o editor pode mover nós sem "alterar" a versão) e serializa em ordem canônica
    /// (nós por id, conexões por origem e porta).
    /// </summary>
    public static string Hash(FlowGraph graph)
    {
        var canonical = graph with
        {
            Nodes = graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => n with { Position = null, Label = null }).ToList(),
            Edges = graph.Edges.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.Port, StringComparer.Ordinal).ToList(),
            Variables = (graph.Variables ?? []).OrderBy(v => v.Name, StringComparer.Ordinal).ToList(),
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(canonical)))).ToLowerInvariant();
    }
}
