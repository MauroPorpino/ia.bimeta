using IaBimeta.Agents.Application;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.AiGateway.Application;
using IaBimeta.Channels.Application;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.ContactCenter.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Tenancy.Application;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Api.Development;

/// <summary>
/// SOMENTE DESENVOLVIMENTO. As rotas do simulador para o primeiro teste de IA: cadastrar a chave do provedor do tenant (cifrada, pelo serviço da plataforma) e olhar o que aconteceu (situação da IA,
/// ledger de consumo, turnos do agente e histórico da conversa). Mesmas regras do simulador: só com o contexto de sistema do cabeçalho <c>X-Dev-Tenant</c>, só em Development, e toda resposta leva
/// <c>X-Simulator: true</c>. NUNCA devolvem, registram nem ecoam a chave: a resposta do cadastro é a situação da IA (com a impressão digital, que não revela a chave). As leituras de ledger e de
/// turnos são o ferramental de verificação de quem desenvolve: não guardam nem mostram prompt, resposta do modelo nem credencial.
/// </summary>
internal static class SimulatorAiEndpoints
{
    private const int DefaultTake = 20;
    private const int MaxTake = 200;

    public static void MapAiRoutes(RouteGroupBuilder grupo)
    {
        // Cadastra (ou troca) a chave do provedor do tenant. O corpo é o único lugar por onde a chave entra; o serviço da plataforma a cifra (AES-256-GCM) e guarda só o texto cifrado e a impressão digital.
        grupo.MapPut("/ai/credential", async (SimulatedAiCredential corpo, DevelopmentTenancyOptions opcoes, AiTenantConfigurationService ia, CancellationToken ct) =>
        {
            var provedor = string.IsNullOrWhiteSpace(corpo.ProviderId) ? opcoes.Ai.ProviderId : corpo.ProviderId.Trim();
            await ia.SetCredentialAsync(provedor, (corpo.ApiKey ?? string.Empty).Trim(), ct);
            return Results.Ok(await ia.GetStatusAsync(ct));
        });

        // Revoga a chave (apaga o texto cifrado): o fim seguro de um teste.
        grupo.MapDelete("/ai/credential", async (string? providerId, DevelopmentTenancyOptions opcoes, AiTenantConfigurationService ia, CancellationToken ct) =>
        {
            var provedor = string.IsNullOrWhiteSpace(providerId) ? opcoes.Ai.ProviderId : providerId.Trim();
            return Results.Ok(new { revoked = await ia.RevokeCredentialAsync(provedor, ct) });
        });

        // A situação de tudo o que o primeiro teste precisa: IA do tenant, organização e fila para o simulador, agente publicado e modelos do catálogo.
        grupo.MapGet("/ai/status", async (
            DevelopmentTenancyOptions opcoes,
            AiTenantConfigurationService ia,
            AiModelCatalog catalogo,
            OrganizationService organizacoes,
            ServiceQueueService filas,
            AgentDefinitionService definicoes,
            CancellationToken ct) =>
        {
            var config = opcoes.Ai;
            var organizacao = (await organizacoes.ListAsync(ct)).FirstOrDefault(o => o.Name == config.OrganizationName);
            var fila = organizacao is null ? null : (await filas.ListAsync(ct)).FirstOrDefault(f => f.OrganizationId == organizacao.Id && f.Name == config.QueueName);
            var agente = await definicoes.GetPublishedAsync(ct);

            return Results.Ok(new
            {
                ai = await ia.GetStatusAsync(ct),
                organizationId = organizacao?.Id,
                queueId = fila?.Id,
                agent = agente is null ? null : new { publishedVersion = agente.Number, publishedAt = agente.PublishedAt, instructionsLength = agente.Content.Instructions.Length },
                catalog = catalogo.Models.Select(m => new
                {
                    m.ProviderId,
                    m.ModelId,
                    m.Profile,
                    m.Status,
                    m.Prices.Currency,
                    inputPerMillion = m.Prices.InputPerMillion,
                    outputPerMillion = m.Prices.OutputPerMillion,
                }),
            });
        });

        // O canal de demonstração do WhatsApp: o caminho do webhook (o identificador opaco), o número e as credenciais SINTÉTICAS de desenvolvimento, para o script que simula a Meta assinar e enviar o webhook.
        grupo.MapGet("/whatsapp/status", async (DevelopmentTenancyOptions opcoes, ChannelAccountService contas, ChannelWebhookAdminService webhooks, CancellationToken ct) =>
        {
            var conta = (await contas.ListAsync(ct)).FirstOrDefault(c => c.ProviderAccountId == opcoes.Ai.DemoWabaId);
            if (conta is null)
            {
                return Results.NotFound();
            }

            var webhook = await webhooks.EnsureAsync(conta.Id, ct);
            return Results.Ok(new
            {
                webhookPath = webhook.Path,
                wabaId = opcoes.Ai.DemoWabaId,
                phoneNumberId = opcoes.Ai.DemoPhoneNumberId,
                appSecret = DevelopmentAiSetup.DemoAppSecret,
                verifyToken = DevelopmentAiSetup.DemoVerifyToken,
                attendant = DevelopmentAiSetup.DemoAttendantEmail,
            });
        });

        // O ledger do tenant: uma linha por chamada ao modelo, com tokens, custo ESTIMADO e resultado. Sem prompt, sem resposta, sem credencial.
        grupo.MapGet("/ai/requests", async (int? take, AiGatewayDbContext banco, CancellationToken ct) =>
        {
            var linhas = await banco.Requests.AsNoTracking()
                .OrderByDescending(r => r.StartedAt)
                .Take(Math.Clamp(take ?? DefaultTake, 1, MaxTake))
                .ToListAsync(ct);

            var confirmadas = linhas.Where(r => r.State == AiRequestState.Confirmed).ToList();
            return Results.Ok(new
            {
                totals = new
                {
                    calls = linhas.Count,
                    confirmed = confirmadas.Count,
                    inputTokens = confirmadas.Sum(r => (long)r.InputTokens),
                    outputTokens = confirmadas.Sum(r => (long)r.OutputTokens),
                    estimatedCost = confirmadas.Sum(r => r.EstimatedCost),
                },
                items = linhas.Select(r => new
                {
                    r.AiRequestId,
                    turnId = r.CorrelationId,
                    r.ProviderId,
                    r.ModelId,
                    state = r.State,
                    outcome = r.Outcome,
                    certainty = r.UsageCertainty,
                    r.InputTokens,
                    r.CachedInputTokens,
                    r.OutputTokens,
                    r.ReasoningTokens,
                    r.ReservedTokens,
                    r.EstimatedCost,
                    currency = r.CostCurrency,
                    r.LatencyMs,
                    r.StopReason,
                    r.ErrorKind,
                    r.ErrorCode,
                    r.ProviderRequestId,
                    r.StartedAt,
                    r.CompletedAt,
                }),
            });
        });

        // Os turnos de IA do tenant, do mais recente ao mais antigo: o estado, as tentativas e como terminaram (código curto, nunca conteúdo).
        grupo.MapGet("/agents/turns", async (int? take, AgentsDbContext banco, CancellationToken ct) =>
        {
            var turnos = await banco.Turns.AsNoTracking()
                .OrderByDescending(t => t.CreatedAt)
                .Take(Math.Clamp(take ?? DefaultTake, 1, MaxTake))
                .ToListAsync(ct);

            // Um objeto com a lista (e não a lista solta): o PowerShell 5.1 entrega um array JSON de nível superior como um objeto só.
            return Results.Ok(new
            {
                items = turnos.Select(t => new
                {
                    t.Id,
                    t.SessionId,
                    state = t.State,
                    t.Attempts,
                    t.SupersedeCount,
                    t.HandoffReason,
                    t.NoticeDone,
                    t.EndReason,
                    t.CreatedAt,
                    t.EndedAt,
                }),
            });
        });

        // A sessão e o histórico dela (o que o cliente disse, o que a IA respondeu e se um humano assumiu): é onde se vê a resposta da IA, já que ainda não há envio real pelo canal.
        grupo.MapGet("/sessions/{id:guid}", async (Guid id, int? take, IConversationQueries consultas, CancellationToken ct) =>
        {
            var sessao = await consultas.GetSessionAsync(id, ct);
            if (sessao is null)
            {
                return Results.NotFound();
            }

            var historico = await consultas.GetHistoryAsync(new GetHistoryQuery(id, Math.Clamp(take ?? 50, 1, MaxTake), null, HistoryOrder.OldestFirst), ct);
            return Results.Ok(new
            {
                session = sessao,
                messages = historico?.Items.Select(m => new
                {
                    m.Id,
                    m.MessageSeq,
                    m.Direction,
                    m.Author,
                    m.Kind,
                    m.Text,
                    m.ReplyKind,
                    m.DeliveryState,
                    m.CoveredThroughSeq,
                    m.OccurredAt,
                }),
            });
        });
    }
}

/// <summary>O corpo do cadastro da chave. O texto da chave só existe aqui e dentro do serviço que o cifra: nenhum <c>ToString</c> o mostra.</summary>
internal sealed record SimulatedAiCredential(string? ProviderId, string? ApiKey)
{
    /// <summary>Não imprime a chave (um registro de log ou uma mensagem de exceção que mostrasse o objeto vazaria o segredo).</summary>
    public override string ToString() => $"SimulatedAiCredential {{ ProviderId = {ProviderId}, ApiKey = *** }}";
}
