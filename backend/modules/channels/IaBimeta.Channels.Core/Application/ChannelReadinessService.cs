using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Providers;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>O resultado de uma verificação do teste de prontidão.</summary>
public enum ReadinessStatus
{
    Pass = 1,

    /// <summary>Atenção: não impede a conexão, mas merece decisão.</summary>
    Warn = 2,

    /// <summary>Impede a conexão: corrija antes de apontar o provedor para o webhook.</summary>
    Fail = 3,

    /// <summary>Não executada (depende de outra verificação que falhou, ou não foi pedida).</summary>
    Skipped = 4,
}

/// <summary>Uma verificação: um código estável (para scripts), o resultado e o que fazer. O detalhe NUNCA traz valor de credencial.</summary>
public sealed record ReadinessCheck(string Code, ReadinessStatus Status, string Detail);

/// <summary>
/// O relatório de prontidão de uma conta. <see cref="Ready"/> é verdadeiro quando nenhuma verificação executada falhou. <see cref="LiveProbe"/> diz se o provedor foi consultado (leitura, nenhuma mensagem enviada): sem
/// ela a prontidão é só LOCAL. <see cref="WebhookUrl"/> é o endereço completo a cadastrar no provedor, presente só quando o identificador do webhook existe e o endereço público está configurado (é segredo operacional: quem
/// recebe o relatório gere os canais da organização e não deve registrá-lo).
/// </summary>
public sealed record ChannelReadinessReport(Guid AccountId, bool Ready, bool LiveProbe, string? WebhookUrl, IReadOnlyList<ReadinessCheck> Checks);

/// <summary>
/// O TESTE DE PRONTIDÃO de uma conta, antes de apontar o provedor real para o webhook. Verifica a configuração SEM enviar mensagem alguma: a cifra das credenciais, o adaptador, as três credenciais (presentes, legíveis e
/// distintas), os números (ativos, com unidade e com a rota de entrada coerente), o identificador do webhook, o endereço público HTTPS e o estado de envio (não pausado). Com <c>live</c>, também LÊ o número no provedor
/// (uma consulta, só leitura) para provar que o token é aceito e que o número existe e é acessível por ele. Exige gerir canais da organização da conta (o relatório traz o endereço do webhook).
/// </summary>
public sealed class ChannelReadinessService
{
    private const string Resource = "channel-account";

    private readonly IChannelRepository _repository;
    private readonly IChannelWebhookStore _webhooks;
    private readonly IAccountSendStateStore _state;
    private readonly ChannelProviderAdapters _adapters;
    private readonly ISecretProtector _protector;
    private readonly ChannelSendingOptions _sending;
    private readonly ChannelWebhookOptions _webhookOptions;
    private readonly IRequestContextAccessor _context;
    private readonly IAuditWriter _audit;

    public ChannelReadinessService(
        IChannelRepository repository,
        IChannelWebhookStore webhooks,
        IAccountSendStateStore state,
        ChannelProviderAdapters adapters,
        ISecretProtector protector,
        ChannelSendingOptions sending,
        ChannelWebhookOptions webhookOptions,
        IRequestContextAccessor context,
        IAuditWriter audit)
    {
        _repository = repository;
        _webhooks = webhooks;
        _state = state;
        _adapters = adapters;
        _protector = protector;
        _sending = sending;
        _webhookOptions = webhookOptions;
        _context = context;
        _audit = audit;
    }

    /// <exception cref="ChannelsResourceNotFoundException">A conta não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir canais da organização.</exception>
    public async Task<ChannelReadinessReport> CheckAsync(Guid accountId, bool live, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var conta = await _repository.FindAccountAsync(accountId, cancellationToken);
        if (conta is null || !contexto.IsAllowed(Permission.ChannelRead, conta.OrganizationId))
        {
            throw new ChannelsResourceNotFoundException("Conta");
        }

        await AuditedAccess.RequireAsync(_audit, contexto, Permission.ChannelManage, conta.OrganizationId, "channels.account.readiness", Resource, conta.Id.ToString(), cancellationToken);

        var verificacoes = new List<ReadinessCheck>();
        var tenant = contexto.TenantId;

        // 1. A cifra das credenciais e o adaptador do provedor.
        var cifraOk = CheckSecretStore(tenant, verificacoes);
        var adaptador = _adapters.Find(conta.Provider);
        verificacoes.Add(adaptador is null
            ? new ReadinessCheck("provider.adapter", ReadinessStatus.Fail, "O adaptador do provedor não está registrado neste ambiente: o webhook responderia 503 e nenhuma mensagem sairia.")
            : new ReadinessCheck("provider.adapter", ReadinessStatus.Pass, "O adaptador do provedor está registrado."));

        // 2. As três credenciais: presentes, legíveis e distintas (um erro comum é colar o mesmo valor em dois campos).
        var valores = new Dictionary<ChannelSecretKind, string>();
        foreach (var tipo in new[] { ChannelSecretKind.AccessToken, ChannelSecretKind.AppSecret, ChannelSecretKind.WebhookVerifyToken })
        {
            verificacoes.Add(await CheckSecretAsync(tenant, conta.Id, tipo, cifraOk, valores, cancellationToken));
        }

        verificacoes.Add(CheckDistinct(valores));

        // 3. Os números: ativos, com unidade, e a rota de entrada coerente com o cadastro.
        var instancias = (await _repository.ListInstancesAsync(new[] { conta.OrganizationId }, cancellationToken)).Where(i => i.AccountId == conta.Id).OrderBy(i => i.PhoneNumberId, StringComparer.Ordinal).ToList();
        verificacoes.AddRange(await CheckInstancesAsync(conta, instancias, cancellationToken));

        // 4. O webhook: o identificador opaco e o endereço público (HTTPS).
        var webhookId = await _webhooks.FindAsync(conta.Id, cancellationToken);
        verificacoes.Add(webhookId is null
            ? new ReadinessCheck("webhook.id", ReadinessStatus.Fail, $"A conta ainda não tem o identificador do webhook: chame POST /channels/accounts/{conta.Id}/webhook.")
            : new ReadinessCheck("webhook.id", ReadinessStatus.Pass, "O identificador opaco do webhook existe."));

        string? url = null;
        if (_webhookOptions.IsPublicHttps(out var baseUrl))
        {
            verificacoes.Add(new ReadinessCheck("webhook.public-url", ReadinessStatus.Pass, $"O endereço público HTTPS está configurado ({baseUrl!.Host})."));
            if (webhookId is { } id)
            {
                url = baseUrl.ToString().TrimEnd('/') + ChannelWebhookAdminService.WhatsAppPathPrefix + id.ToString("D");
            }
        }
        else
        {
            verificacoes.Add(new ReadinessCheck(
                "webhook.public-url", ReadinessStatus.Fail,
                "O endereço público HTTPS do Api não está configurado (Channels:Webhook:PublicBaseUrl). A Meta só entrega webhook em HTTPS público com certificado válido (autoassinado não é aceito)."));
        }

        // 5. O estado de envio: pausa, espera do provedor e vazão.
        verificacoes.Add(await CheckSendingAsync(conta.Id, cancellationToken));

        // 6. A leitura no provedor (opcional): token aceito e número acessível. Só leitura, nenhuma mensagem.
        verificacoes.AddRange(await CheckProviderAsync(conta, instancias, live, cifraOk, cancellationToken));

        var pronta = verificacoes.All(v => v.Status != ReadinessStatus.Fail);
        await _audit.WriteAsync(new AuditEntry("channels.account.readiness", Resource, conta.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return new ChannelReadinessReport(conta.Id, pronta, live, url, verificacoes);
    }

    private bool CheckSecretStore(TenantId tenant, List<ReadinessCheck> verificacoes)
    {
        try
        {
            // Cifra e decifra um valor descartável, amarrado a uma referência que não existe: prova que a chave mestra ativa funciona, sem gravar nada.
            var amarra = new SecretBinding(tenant, Guid.NewGuid(), ChannelSecretKind.AccessToken);
            var cifrado = _protector.Protect("prontidao", amarra);
            if (_protector.Unprotect(cifrado, amarra) == "prontidao")
            {
                verificacoes.Add(new ReadinessCheck("secret-store", ReadinessStatus.Pass, "A chave mestra das credenciais está configurada e funciona."));
                return true;
            }
        }
        catch (Exception ex) when (ex is ChannelSecretsNotConfiguredException or System.Security.Cryptography.CryptographicException)
        {
        }

        verificacoes.Add(new ReadinessCheck("secret-store", ReadinessStatus.Fail, "A chave mestra das credenciais (Channels:Secrets) não está configurada ou não funciona: nenhuma credencial pode ser gravada nem lida."));
        return false;
    }

    private async Task<ReadinessCheck> CheckSecretAsync(
        TenantId tenant, Guid accountId, ChannelSecretKind tipo, bool cifraOk, Dictionary<ChannelSecretKind, string> valores, CancellationToken cancellationToken)
    {
        var codigo = $"credential.{Slug(tipo)}";
        var linha = await _repository.FindSecretAsync(accountId, tipo, cancellationToken);
        if (linha is null)
        {
            return new ReadinessCheck(codigo, ReadinessStatus.Fail, $"A credencial ({tipo}) não está cadastrada: PUT /channels/accounts/{accountId}/secrets/{tipo}.");
        }

        if (!cifraOk)
        {
            return new ReadinessCheck(codigo, ReadinessStatus.Skipped, "Não conferida: a chave mestra das credenciais não está funcionando.");
        }

        var valor = await ChannelSecretReader.ReadAsync(_repository, _protector, tenant, accountId, tipo, cancellationToken);
        if (valor is null)
        {
            return new ReadinessCheck(codigo, ReadinessStatus.Fail, $"A credencial ({tipo}) está cadastrada, mas não pôde ser lida (a chave mestra mudou ou o dado foi adulterado): cadastre-a de novo.");
        }

        if (valor.Any(c => c is < '!' or > '~'))
        {
            return new ReadinessCheck(codigo, ReadinessStatus.Fail, $"A credencial ({tipo}) tem espaço ou caractere fora do ASCII visível: cole só o valor, sem quebra de linha nem espaços.");
        }

        valores[tipo] = valor;
        return new ReadinessCheck(codigo, ReadinessStatus.Pass, $"A credencial ({tipo}) está cadastrada e legível (versão {linha.Version}).");
    }

    private static ReadinessCheck CheckDistinct(Dictionary<ChannelSecretKind, string> valores)
    {
        if (valores.Count < 2)
        {
            return new ReadinessCheck("credential.distinct", ReadinessStatus.Skipped, "Não conferida: faltam credenciais.");
        }

        var distintos = valores.Values.Distinct(StringComparer.Ordinal).Count() == valores.Count;
        return distintos
            ? new ReadinessCheck("credential.distinct", ReadinessStatus.Pass, "As credenciais são valores diferentes entre si.")
            : new ReadinessCheck("credential.distinct", ReadinessStatus.Fail, "Duas credenciais têm o mesmo valor: o token de acesso, o segredo do aplicativo e o token de verificação são três valores diferentes.");
    }

    private async Task<IEnumerable<ReadinessCheck>> CheckInstancesAsync(ChannelAccount conta, IReadOnlyList<ChannelInstance> instancias, CancellationToken cancellationToken)
    {
        var verificacoes = new List<ReadinessCheck>();
        if (instancias.Count == 0)
        {
            verificacoes.Add(new ReadinessCheck("instance.registered", ReadinessStatus.Fail, "A conta não tem nenhum número cadastrado (POST /channels/instances)."));
            return verificacoes;
        }

        if (!instancias.Any(i => i.Status == ChannelStatus.Active))
        {
            verificacoes.Add(new ReadinessCheck("instance.active", ReadinessStatus.Fail, "Nenhum número da conta está ativo: autorize ao menos uma unidade e ative o número."));
        }

        foreach (var instancia in instancias)
        {
            var rotulo = $"instance.{instancia.PhoneNumberId}";
            if (instancia.Status != ChannelStatus.Active)
            {
                verificacoes.Add(new ReadinessCheck(rotulo, ReadinessStatus.Warn, $"O número ({instancia.Name}) está {instancia.Status}: não recebe nem envia."));
                continue;
            }

            if (instancia.AuthorizedUnits.Count == 0)
            {
                verificacoes.Add(new ReadinessCheck(rotulo, ReadinessStatus.Fail, $"O número ({instancia.Name}) está ativo sem unidade autorizada."));
                continue;
            }

            // A rota de entrada precisa levar este número a ESTA instância e a ESTE tenant: é a associação segura entre o provedor e o tenant.
            var rota = await _repository.ResolveRouteAsync(conta.Provider, instancia.PhoneNumberId, cancellationToken);
            if (rota is null || rota.ChannelInstanceId != instancia.Id || rota.TenantId != instancia.TenantId)
            {
                verificacoes.Add(new ReadinessCheck(rotulo, ReadinessStatus.Fail, $"A rota de entrada do número ({instancia.Name}) não leva a esta instância: o webhook seria descartado."));
                continue;
            }

            var detalhe = instancia.DefaultBusinessUnitId is null
                ? $"O número ({instancia.Name}) está ativo, com {instancia.AuthorizedUnits.Count} unidade(s) autorizada(s) e a rota de entrada coerente. Sem unidade padrão: o roteamento depende das regras."
                : $"O número ({instancia.Name}) está ativo, com {instancia.AuthorizedUnits.Count} unidade(s) autorizada(s), unidade padrão e a rota de entrada coerente.";
            verificacoes.Add(new ReadinessCheck(rotulo, instancia.DefaultBusinessUnitId is null ? ReadinessStatus.Warn : ReadinessStatus.Pass, detalhe));
        }

        return verificacoes;
    }

    private async Task<ReadinessCheck> CheckSendingAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var estado = await _state.FindAsync(accountId, cancellationToken);
        var vazao = estado?.EffectiveThroughput(_sending.DefaultThroughputPerSecond) ?? _sending.DefaultThroughputPerSecond;
        if (estado is { IsPaused: true })
        {
            return new ReadinessCheck("sending.state", ReadinessStatus.Fail, $"O envio da conta está PAUSADO (motivo: {estado.PauseReason}). Corrija a causa e retome: POST /channels/accounts/{accountId}/sending/resume.");
        }

        if (estado?.CooldownUntil is { } fim)
        {
            return new ReadinessCheck("sending.state", ReadinessStatus.Warn, $"O provedor mandou a conta esperar (limite de taxa) até {fim:O}. Vazão: {vazao} mensagens por segundo.");
        }

        var origem = estado?.ThroughputPerSecond is null ? "padrão" : "da conta";
        return new ReadinessCheck("sending.state", ReadinessStatus.Pass, $"O envio não está pausado. Vazão: {vazao} mensagens por segundo ({origem}).");
    }

    private async Task<IEnumerable<ReadinessCheck>> CheckProviderAsync(
        ChannelAccount conta, IReadOnlyList<ChannelInstance> instancias, bool live, bool cifraOk, CancellationToken cancellationToken)
    {
        if (!live)
        {
            return new[]
            {
                new ReadinessCheck("provider.live", ReadinessStatus.Skipped, "Não executada. Peça a leitura no provedor (live=true) para provar que o token é aceito e que o número existe: é só leitura, nenhuma mensagem é enviada."),
            };
        }

        if (!cifraOk || _adapters.Find(conta.Provider) is null)
        {
            return new[] { new ReadinessCheck("provider.live", ReadinessStatus.Skipped, "Não executada: faltam a chave mestra das credenciais ou o adaptador do provedor.") };
        }

        var tenant = _context.GetRequired().TenantId;
        var prova = await AccountProbe.RunAsync(_repository, _protector, _adapters, tenant, conta, cancellationToken);
        if (prova.Instances.Count == 0)
        {
            return new[] { new ReadinessCheck("provider.live", ReadinessStatus.Fail, prova.Refusal ?? "Nada a conferir no provedor.") };
        }

        var verificacoes = new List<ReadinessCheck>();
        foreach (var (numero, nome, resultado) in prova.Instances)
        {
            var codigo = $"provider.live.{numero}";
            switch (resultado.Outcome)
            {
                case ProviderProbeOutcome.Ok:
                    var instancia = instancias.First(i => i.PhoneNumberId == numero);
                    var qualidade = resultado.QualityRating ?? "não informada";
                    var rotuloDoNumero = resultado.DisplayPhoneNumber ?? "número não informado";
                    var detalhe = $"O provedor aceitou o token e confirmou o número ({nome}): {rotuloDoNumero}, empresa \"{resultado.VerifiedName ?? "não informada"}\", qualidade {qualidade}.";
                    var divergente = DiffersFromRegistered(instancia.DisplayPhoneNumber, resultado.DisplayPhoneNumber);
                    var ruim = string.Equals(resultado.QualityRating, "RED", StringComparison.OrdinalIgnoreCase);
                    if (divergente)
                    {
                        detalhe += " ATENÇÃO: o número exibido pelo provedor difere do cadastrado.";
                    }

                    if (ruim)
                    {
                        detalhe += " ATENÇÃO: qualidade RED limita o envio no provedor.";
                    }

                    verificacoes.Add(new ReadinessCheck(codigo, divergente || ruim ? ReadinessStatus.Warn : ReadinessStatus.Pass, detalhe));
                    break;

                case ProviderProbeOutcome.CredentialRejected:
                    verificacoes.Add(new ReadinessCheck(codigo, ReadinessStatus.Fail, $"O provedor recusou a credencial para o número ({nome}) (código {resultado.ErrorCode ?? "desconhecido"}): token expirado, inválido, sem as permissões whatsapp_business_messaging e whatsapp_business_management, ou conta restrita."));
                    break;

                case ProviderProbeOutcome.NotFound:
                    verificacoes.Add(new ReadinessCheck(codigo, ReadinessStatus.Fail, $"O provedor não encontrou o número ({nome}) ou o token não tem acesso a ele (código {resultado.ErrorCode ?? "desconhecido"}): confira o Phone Number ID e se o usuário do sistema tem acesso à conta WhatsApp."));
                    break;

                default:
                    verificacoes.Add(new ReadinessCheck(codigo, ReadinessStatus.Fail, $"Não foi possível concluir a leitura no provedor para o número ({nome}) (código {resultado.ErrorCode ?? "desconhecido"}): nada foi provado, tente de novo."));
                    break;
            }
        }

        return verificacoes;
    }

    /// <summary>Compara só os dígitos: o provedor mostra "+55 11 99999-0000" e o cadastro pode ter outra formatação. Falta de um dos lados não é divergência.</summary>
    private static bool DiffersFromRegistered(string? registered, string? fromProvider)
    {
        var a = Digits(registered);
        var b = Digits(fromProvider);
        return a.Length > 0 && b.Length > 0 && !string.Equals(a, b, StringComparison.Ordinal);
    }

    private static string Digits(string? valor) => new((valor ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    private static string Slug(ChannelSecretKind tipo) => tipo switch
    {
        ChannelSecretKind.AccessToken => "access-token",
        ChannelSecretKind.AppSecret => "app-secret",
        ChannelSecretKind.WebhookVerifyToken => "webhook-verify-token",
        _ => "unknown",
    };
}
