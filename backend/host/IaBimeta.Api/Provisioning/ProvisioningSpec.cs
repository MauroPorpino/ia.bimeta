using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IaBimeta.Agents.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Api.Provisioning;

/// <summary>
/// A descrição de uma empresa (tenant) para o comando <c>provision</c>: quem é, quem a administra, a organização e a fila de atendimento humano, a IA (provedor, modelo, instruções e perguntas frequentes), a conta de
/// WhatsApp (simulada ou real) e os atendentes. É um arquivo JSON de quem opera o ambiente. <b>Nenhum segredo entra no arquivo</b>: senhas e chaves são lidas de variáveis de ambiente, cujos NOMES o arquivo informa
/// (<c>passwordEnv</c>, <c>apiKeyEnv</c>, <c>appSecretEnv</c>, <c>accessTokenEnv</c>, <c>verifyTokenEnv</c>).
/// </summary>
internal sealed class ProvisioningSpec
{
    public const int MaxAttendants = 50;

    private static readonly Regex EnvNamePattern = new("^[A-Za-z_][A-Za-z0-9_]{0,99}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public TenantSpec Tenant { get; set; } = new();

    public AdministratorSpec Administrator { get; set; } = new();

    public OrganizationSpec Organization { get; set; } = new();

    public QueueSpec Queue { get; set; } = new();

    public AiSpec Ai { get; set; } = new();

    public WhatsAppSpec? WhatsApp { get; set; }

    public List<AttendantSpec> Attendants { get; set; } = new();

    /// <summary>Lê e valida o arquivo. Um campo desconhecido é erro (um erro de digitação não pode virar configuração ignorada em silêncio).</summary>
    /// <exception cref="ProvisioningSpecException">O arquivo não existe, não é JSON válido ou tem campo inválido.</exception>
    public static ProvisioningSpec Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProvisioningSpecException(new[] { $"Não foi possível ler o arquivo '{path}': {ex.Message}" });
        }

        return Parse(json);
    }

    public static ProvisioningSpec Parse(string json)
    {
        ProvisioningSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<ProvisioningSpec>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ProvisioningSpecException(new[] { $"O arquivo não é um JSON válido da empresa: {ex.Message}" });
        }

        if (spec is null)
        {
            throw new ProvisioningSpecException(new[] { "O arquivo está vazio." });
        }

        var erros = spec.Validate();
        if (erros.Count > 0)
        {
            throw new ProvisioningSpecException(erros);
        }

        return spec;
    }

    /// <summary>As regras que não dependem do banco. O que depende dele (slug em uso, modelo fora do catálogo, senha fraca) quem recusa é o próprio módulo, com a mensagem dele.</summary>
    public List<string> Validate()
    {
        var erros = new List<string>();

        Exigir(erros, Tenant.Slug, "tenant.slug");
        Exigir(erros, Tenant.Name, "tenant.name");
        Exigir(erros, Administrator.Email, "administrator.email");
        Exigir(erros, Administrator.DisplayName, "administrator.displayName");
        Exigir(erros, Organization.Name, "organization.name");
        Exigir(erros, Queue.Name, "queue.name");
        Exigir(erros, Ai.ModelId, "ai.modelId");
        Exigir(erros, Ai.ProviderId, "ai.providerId");

        VerificarNomeDeVariavel(erros, Administrator.PasswordEnv, "administrator.passwordEnv");
        VerificarNomeDeVariavel(erros, Ai.ApiKeyEnv, "ai.apiKeyEnv");

        if (Ai.Faq.Count > 50)
        {
            erros.Add("ai.faq aceita no máximo 50 perguntas.");
        }

        for (var i = 0; i < Ai.Faq.Count; i++)
        {
            Exigir(erros, Ai.Faq[i].Question, $"ai.faq[{i}].question");
            Exigir(erros, Ai.Faq[i].Answer, $"ai.faq[{i}].answer");
            if ((Ai.Faq[i].Aliases?.Count ?? 0) > AgentDefinitionContent.AliasMaxCount)
            {
                erros.Add($"ai.faq[{i}].aliases aceita no máximo {AgentDefinitionContent.AliasMaxCount} aliases.");
            }

            var aliases = Ai.Faq[i].Aliases;
            for (var a = 0; aliases is not null && a < aliases.Count; a++)
            {
                Exigir(erros, aliases[a], $"ai.faq[{i}].aliases[{a}]");
            }
        }

        if (Ai.FaqLocale is not null && !Locale.TryParse(Ai.FaqLocale, out _))
        {
            erros.Add("ai.faqLocale deve ser um identificador BCP 47 bem formado, como pt-BR ou en-US.");
        }

        if (WhatsApp is { } wa)
        {
            if (wa.Mode == WhatsAppMode.Real)
            {
                Exigir(erros, wa.WabaId, "whatsApp.wabaId (obrigatório no modo real)");
                Exigir(erros, wa.PhoneNumberId, "whatsApp.phoneNumberId (obrigatório no modo real)");
                Exigir(erros, wa.AppSecretEnv, "whatsApp.appSecretEnv (obrigatório no modo real)");
                Exigir(erros, wa.AccessTokenEnv, "whatsApp.accessTokenEnv (obrigatório no modo real)");
            }

            VerificarNomeDeVariavel(erros, wa.AppSecretEnv, "whatsApp.appSecretEnv");
            VerificarNomeDeVariavel(erros, wa.AccessTokenEnv, "whatsApp.accessTokenEnv");
            VerificarNomeDeVariavel(erros, wa.VerifyTokenEnv, "whatsApp.verifyTokenEnv");

            if (wa.Templates.Count > IaBimeta.Channels.Application.ChannelTemplateService.MaxTemplatesPerRegistration)
            {
                erros.Add($"whatsApp.templates aceita no máximo {IaBimeta.Channels.Application.ChannelTemplateService.MaxTemplatesPerRegistration} modelos por execução.");
            }

            for (var i = 0; i < wa.Templates.Count; i++)
            {
                if (wa.Templates[i].ValueKind != JsonValueKind.Object)
                {
                    erros.Add($"whatsApp.templates[{i}] deve ser o modelo como a Meta o devolve (um objeto JSON).");
                }
            }
        }

        if (Attendants.Count > MaxAttendants)
        {
            erros.Add($"attendants aceita no máximo {MaxAttendants} atendentes por execução.");
        }

        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Administrator.Email ?? string.Empty };
        for (var i = 0; i < Attendants.Count; i++)
        {
            Exigir(erros, Attendants[i].Email, $"attendants[{i}].email");
            Exigir(erros, Attendants[i].DisplayName, $"attendants[{i}].displayName");
            VerificarNomeDeVariavel(erros, Attendants[i].PasswordEnv, $"attendants[{i}].passwordEnv");
            if (!string.IsNullOrWhiteSpace(Attendants[i].Email) && !emails.Add(Attendants[i].Email!.Trim()))
            {
                erros.Add($"attendants[{i}].email repete um e-mail já usado nesta empresa: {Attendants[i].Email}.");
            }
        }

        return erros;
    }

    private static void Exigir(List<string> erros, string? valor, string campo)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            erros.Add($"Falta {campo}.");
        }
    }

    private static void VerificarNomeDeVariavel(List<string> erros, string? nome, string campo)
    {
        if (!string.IsNullOrWhiteSpace(nome) && !EnvNamePattern.IsMatch(nome))
        {
            erros.Add($"{campo} deve ser o NOME de uma variável de ambiente (letras, números e sublinhado), não o valor do segredo.");
        }
    }
}

internal sealed class TenantSpec
{
    public string? Slug { get; set; }

    public string? Name { get; set; }

    /// <summary>Idioma padrão do tenant (BCP 47). Vazio usa o inicial da plataforma. Só vale na criação.</summary>
    public string? DefaultLocale { get; set; }
}

internal sealed class AdministratorSpec
{
    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>O NOME da variável de ambiente com a senha. Sem ele (ou com a variável vazia), a senha é gerada e mostrada uma única vez.</summary>
    public string? PasswordEnv { get; set; }
}

internal sealed class OrganizationSpec
{
    public string? Name { get; set; }

    /// <summary>A unidade (matriz) que o número de WhatsApp atende. Vazio usa "Matriz".</summary>
    public string? UnitName { get; set; }
}

internal sealed class QueueSpec
{
    public string? Name { get; set; } = "Atendimento humano";
}

internal sealed class AiSpec
{
    public string? ProviderId { get; set; } = "openai";

    /// <summary>O modelo que a conta do provedor tem e que consta em <c>AiGateway:Catalog:Models</c>.</summary>
    public string? ModelId { get; set; }

    /// <summary>As instruções do agente (comportamento, tom, o que pode e não pode responder). Vazio usa um texto neutro e seguro. Trocar o texto publica uma versão nova.</summary>
    public string? Instructions { get; set; }

    /// <summary>O aviso que o cliente recebe antes da transferência para uma pessoa. Vazio usa o aviso padrão.</summary>
    public string? HandoffNotice { get; set; }

    /// <summary>O aviso específico quando a consulta ao sistema da empresa falha (ADR 0036). Vazio = vale o aviso de transferência.</summary>
    public string? IntegrationUnavailableNotice { get; set; }

    /// <summary>O idioma em que o FAQ está escrito (BCP 47, como <c>pt-BR</c>); a resposta direta (<c>autoReply</c>) só vale numa conversa desse idioma. Vazio é <c>pt-BR</c> (ADR 0029).</summary>
    public string? FaqLocale { get; set; }

    public List<FaqSpec> Faq { get; set; } = new();

    /// <summary>O NOME da variável de ambiente com a chave do provedor (BYOK). Sem ela, a chave é cadastrada depois e a IA transfere tudo ao atendente enquanto isso.</summary>
    public string? ApiKeyEnv { get; set; }
}

internal sealed class FaqSpec
{
    public string? Question { get; set; }

    public string? Answer { get; set; }

    /// <summary>Outras formas, cadastradas de propósito, de fazer a MESMA pergunta (até 10). Servem só à resposta direta; não vão ao prompt.</summary>
    public List<string>? Aliases { get; set; }

    /// <summary>
    /// Liga a resposta direta (ADR 0029): a mensagem do cliente IGUAL à pergunta ou a um alias, depois de normalizada (caixa, acento, pontuação), é respondida com <c>answer</c> sem chamar a IA. Desligado por padrão;
    /// só ligue numa resposta que se sustenta sozinha (sem preço, agenda, cobertura nem referência ao que foi dito antes).
    /// </summary>
    public bool AutoReply { get; set; }
}

internal enum WhatsAppMode
{
    /// <summary>A conta de demonstração com credenciais SINTÉTICAS: funciona só com a "Meta" simulada local.</summary>
    Simulated,

    /// <summary>A conta REAL da Meta: as credenciais vêm de variáveis de ambiente e o número fica INATIVO até o teste de prontidão.</summary>
    Real,
}

internal sealed class WhatsAppSpec
{
    public WhatsAppMode Mode { get; set; } = WhatsAppMode.Simulated;

    public string? AccountName { get; set; }

    /// <summary>O WABA ID. No modo simulado, vazio gera <c>waba-sim-&lt;slug&gt;</c> (único na plataforma).</summary>
    public string? WabaId { get; set; }

    /// <summary>O Phone Number ID. No modo simulado, vazio gera um identificador estável a partir do slug.</summary>
    public string? PhoneNumberId { get; set; }

    public string? DisplayPhoneNumber { get; set; }

    public string? AppSecretEnv { get; set; }

    public string? AccessTokenEnv { get; set; }

    /// <summary>O token de verificação do webhook. Sem ele, no modo real, é gerado e mostrado uma única vez (para colar no painel da Meta).</summary>
    public string? VerifyTokenEnv { get; set; }

    /// <summary>
    /// Os modelos de mensagem (templates) JÁ aprovados na Meta que esta conta pode enviar (ADR 0027), cada um como a Graph API o devolve (<c>name</c>, <c>language</c>, <c>category</c>, <c>status</c>,
    /// <c>components</c>). O comando só os ASSOCIA à conta (não cria nem aprova nada na Meta); repetir o comando atualiza o que mudou. Um modelo que o MVP não sabe enviar (cabeçalho de mídia, copiar código,
    /// autenticação) é recusado com o motivo, sem impedir os demais.
    /// </summary>
    public List<JsonElement> Templates { get; set; } = new();
}

internal sealed class AttendantSpec
{
    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    public string? PasswordEnv { get; set; }
}

/// <summary>O arquivo da empresa tem erros (a lista diz quais).</summary>
internal sealed class ProvisioningSpecException : Exception
{
    public ProvisioningSpecException(IReadOnlyList<string> errors)
        : base(string.Join(" ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
