using System.Security.Cryptography;
using System.Text;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Domain;

/// <summary>Por que a mensagem do cliente NÃO teve resposta direta do FAQ, entre os casos que valem uma revisão (ADR 0030).</summary>
public enum KnowledgeGapKind : short
{
    /// <summary>Nenhuma entrada do FAQ tem a pergunta ou um alias igual à mensagem: a oportunidade de uma entrada nova.</summary>
    NoMatch = 1,

    /// <summary>Duas ou mais entradas correspondem à mesma mensagem: o FAQ tem uma duplicata a resolver.</summary>
    Ambiguous = 2,

    /// <summary>Uma única entrada corresponde, mas sem <c>AutoReply</c>: o FAQ já sabe responder e a resposta direta está desligada nela.</summary>
    NotAutoReply = 3,
}

/// <summary>A situação de uma lacuna na revisão. Só o administrador a muda.</summary>
public enum KnowledgeGapStatus : short
{
    /// <summary>Esperando revisão.</summary>
    Open = 1,

    /// <summary>O administrador decidiu que não vale uma entrada de FAQ.</summary>
    Dismissed = 2,

    /// <summary>O administrador a transformou em entrada do RASCUNHO da definição do agente (nada foi publicado por isso).</summary>
    Promoted = 3,
}

/// <summary>
/// Uma lacuna do FAQ (ADR 0030): uma pergunta de cliente, AGRUPADA por um hash com segredo do tenant do texto normalizado, que o FAQ publicado não respondeu direto e que vale uma revisão. NÃO guarda o texto do
/// cliente: guarda o hash (para agrupar e contar sem copiar o texto), o tipo, as datas e a decisão do administrador; as referências à mensagem e à conversa ficam nas <see cref="KnowledgeGapOccurrence"/>, e o
/// texto é lido do Conversations na revisão, com a autorização de quem lê. A decisão (descartar ou enviar ao rascunho) é do hash: as próximas ocorrências da mesma pergunta só contam. Quem grava as
/// ocorrências é o registrador do Agents, por SQL atômico e idempotente por turno; esta entidade serve à revisão e ao modelo do EF. A chave é <c>(tenant_id, id)</c> (TEN-3).
/// </summary>
public sealed class KnowledgeGap : ITenantOwned
{
    private KnowledgeGap()
    {
        TenantId = null!;
        QuestionHash = Array.Empty<byte>();
    }

    public TenantId TenantId { get; private set; }

    public Guid Id { get; private set; }

    /// <summary>O HMAC-SHA256 do texto normalizado com a chave DERIVADA do tenant (32 bytes): único por tenant, e sem a chave não se volta ao texto nem se confirma um palpite.</summary>
    public byte[] QuestionHash { get; private set; }

    /// <summary>O tipo da ÚLTIMA ocorrência (o FAQ muda, e a mesma pergunta pode passar de sem correspondência para ambígua).</summary>
    public KnowledgeGapKind Kind { get; private set; }

    public KnowledgeGapStatus Status { get; private set; }

    public DateTimeOffset FirstSeenAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public Guid? DecidedBy { get; private set; }

    /// <summary>O número da versão do rascunho que recebeu a entrada, quando <see cref="Status"/> é <see cref="KnowledgeGapStatus.Promoted"/>.</summary>
    public int? PromotedVersion { get; private set; }

    /// <summary>A concorrência otimista da decisão: dois administradores decidindo a mesma lacuna não se sobrescrevem; o segundo recebe conflito.</summary>
    public int Revision { get; private set; }

    public static KnowledgeGap Open(TenantId tenant, byte[] questionHash, KnowledgeGapKind kind, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(questionHash);
        if (questionHash.Length != KnowledgeGapHasher.HashLength)
        {
            throw new ArgumentException("O hash da pergunta tem o tamanho errado.", nameof(questionHash));
        }

        return new KnowledgeGap
        {
            TenantId = tenant,
            Id = Guid.NewGuid(),
            QuestionHash = (byte[])questionHash.Clone(),
            Kind = kind,
            Status = KnowledgeGapStatus.Open,
            FirstSeenAt = now,
            LastSeenAt = now,
            Revision = 1,
        };
    }

    /// <summary>Descarta a lacuna. Repetir não muda nada. Uma lacuna já enviada ao rascunho não se descarta: a decisão foi outra.</summary>
    /// <exception cref="KnowledgeGapStateException">A lacuna já foi enviada ao rascunho.</exception>
    public void Dismiss(DateTimeOffset now, Guid? userId)
    {
        if (Status == KnowledgeGapStatus.Dismissed)
        {
            return;
        }

        if (Status != KnowledgeGapStatus.Open)
        {
            throw new KnowledgeGapStateException("A lacuna já foi enviada ao rascunho e não pode ser descartada.");
        }

        Status = KnowledgeGapStatus.Dismissed;
        DecidedAt = now;
        DecidedBy = userId;
        Revision++;
    }

    /// <summary>Marca a lacuna como enviada ao rascunho. Só uma lacuna aberta.</summary>
    /// <exception cref="KnowledgeGapStateException">A lacuna não está aberta.</exception>
    public void MarkPromoted(DateTimeOffset now, Guid? userId)
    {
        if (Status != KnowledgeGapStatus.Open)
        {
            throw new KnowledgeGapStateException("Só uma lacuna aberta pode ser enviada ao rascunho.");
        }

        Status = KnowledgeGapStatus.Promoted;
        DecidedAt = now;
        DecidedBy = userId;
        Revision++;
    }

    public void SetPromotedVersion(int version)
    {
        if (Status != KnowledgeGapStatus.Promoted || version < 1)
        {
            throw new KnowledgeGapStateException("A versão do rascunho só se registra numa lacuna enviada a ele.");
        }

        PromotedVersion = version;
        Revision++;
    }
}

/// <summary>
/// Uma ocorrência de uma lacuna: UMA mensagem de cliente (por turno de IA, o que a torna idempotente: o turno repetido não conta duas vezes) com as REFERÊNCIAS ao atendimento, à conversa e à mensagem. Nenhum texto.
/// As referências são identificadores opacos do Conversations (DAD-2: nenhuma chave estrangeira para outro esquema); a mensagem pode não existir mais quando alguém revisa, e a revisão trata isso.
/// A chave é <c>(tenant_id, turn_id)</c> (TEN-3).
/// </summary>
public sealed class KnowledgeGapOccurrence : ITenantOwned
{
    private KnowledgeGapOccurrence()
    {
        TenantId = null!;
    }

    public TenantId TenantId { get; private set; }

    public Guid TurnId { get; private set; }

    public Guid GapId { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid MessageId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static KnowledgeGapOccurrence Create(TenantId tenant, Guid turnId, Guid gapId, Guid sessionId, Guid conversationId, Guid messageId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return new KnowledgeGapOccurrence
        {
            TenantId = tenant,
            TurnId = turnId,
            GapId = gapId,
            SessionId = sessionId,
            ConversationId = conversationId,
            MessageId = messageId,
            OccurredAt = at,
        };
    }
}

/// <summary>A transição pedida não existe para a situação atual da lacuna.</summary>
public sealed class KnowledgeGapStateException : Exception
{
    public KnowledgeGapStateException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// O hash das perguntas com um segredo ESPECÍFICO de cada tenant (ADR 0030): <c>HMAC-SHA256(chave do tenant, texto normalizado)</c>, onde a chave do tenant é derivada da chave mestra por
/// <c>HMAC-SHA256(mestra, "ia.bimeta/knowledge-gap/v1|" + tenant)</c>. O mesmo texto dá hashes diferentes em tenants diferentes (nenhum cruzamento entre empresas, nem por comparação de hashes) e, sem a chave
/// mestra, não há como confirmar um palpite nem voltar ao texto. A chave mestra vem da configuração do ambiente, nunca de arquivo versionado. Trocar a chave separa os grupos de antes dos de depois
/// (a lacuna antiga continua revisável; as ocorrências novas abrem outra).
/// </summary>
public sealed class KnowledgeGapHasher
{
    public const int HashLength = 32;
    public const int MinimumKeyLength = 32;

    private const string Context = "ia.bimeta/knowledge-gap/v1|";

    private readonly byte[] _masterKey;

    public KnowledgeGapHasher(byte[] masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        if (masterKey.Length < MinimumKeyLength)
        {
            throw new ArgumentException($"A chave do hash das lacunas tem de ter pelo menos {MinimumKeyLength} bytes.", nameof(masterKey));
        }

        _masterKey = (byte[])masterKey.Clone();
    }

    /// <summary>Lê a chave em Base64; vazia ou ausente é "sem chave" (nulo): o registro de lacunas fica desligado, nunca com uma chave fraca.</summary>
    /// <exception cref="FormatException">O texto não é Base64 válido ou a chave é curta (o valor NUNCA vai na mensagem).</exception>
    public static KnowledgeGapHasher? FromBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            throw new FormatException("A chave do hash das lacunas não é Base64 válido.");
        }

        if (bytes.Length < MinimumKeyLength)
        {
            throw new FormatException($"A chave do hash das lacunas tem de ter pelo menos {MinimumKeyLength} bytes.");
        }

        return new KnowledgeGapHasher(bytes);
    }

    /// <summary>O hash do texto JÁ NORMALIZADO (<see cref="FaqMatcher.Normalize"/>) no tenant.</summary>
    public byte[] Hash(TenantId tenant, string normalizedText)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrEmpty(normalizedText);
        var chaveDoTenant = HMACSHA256.HashData(_masterKey, Encoding.UTF8.GetBytes(Context + tenant.Value.ToString("N")));
        return HMACSHA256.HashData(chaveDoTenant, Encoding.UTF8.GetBytes(normalizedText));
    }
}
