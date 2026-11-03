namespace IaBimeta.Conversations.Contracts;

/// <summary>
/// Bloco opaco e versionado que o adaptador de um canal entrega junto com a mensagem (ADR 0011, seções B e D; ADR 0012, seção B; ADR 0014, seção A):
/// metadados que o Conversations guarda sem interpretar, como o tipo, o tamanho e o identificador de uma mídia no provedor (DP-05: os anexos foram
/// adiados, e o atendente vê o registro da mídia, não o arquivo). Quem define as chaves e a versão é o adaptador do canal; o Conversations só impõe os
/// limites estruturais FIXOS (sem configuração por tenant): no máximo 32 entradas; chave de 1 a 64 caracteres, só minúsculas, números e <c>_.-</c>,
/// começando por letra ou número; valor de até 1024 caracteres, sem caractere de controle; no máximo 8192 caracteres somando chaves e valores.
/// Não carrega segredo, tenant nem conteúdo da conversa.
/// <para>
/// Enquanto não houver onde guardá-lo, o Conversations RECUSA todo <c>ChannelData</c> não nulo: os metadados nunca são descartados em silêncio. A
/// persistência chega na fatia A3 (migração 6, versão reduzida do ADR 0011), e a recusa deixa de existir com ela.
/// </para>
/// </summary>
/// <param name="Version">A versão do esquema de chaves declarada pelo adaptador (1 ou mais). Mudar o significado de uma chave exige uma versão nova.</param>
/// <param name="Values">As chaves e os valores, tal como o adaptador os declara.</param>
public sealed record ChannelData(int Version, IReadOnlyDictionary<string, string> Values);
