using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.ContactCenter.Presentation;

public static class ContactCenterEndpoints
{
    /// <summary>
    /// Filas de atendimento, atendentes e as conversas em atendimento. Todas as rotas exigem o contexto confiável que o host cria
    /// a partir do token de sessão (sem ele, 401). Nenhuma rota aceita tenant nem o usuário que age em corpo, query string ou
    /// rota: o tenant e o usuário são os do contexto. Um recurso fora do alcance do usuário responde 404, e enxergar sem poder
    /// operar responde 403 (e fica na auditoria). A operação que o Conversations ainda não confirmou responde 202: repetir o
    /// pedido com o mesmo <c>requestId</c> a conclui.
    /// </summary>
    public static IEndpointRouteBuilder MapContactCenterEndpoints(this IEndpointRouteBuilder app)
    {
        var filas = app.MapGroup("/contact-center/queues").AddEndpointFilter(TratarErrosDoContactCenter);

        filas.MapPost("/", async (CreateServiceQueueRequest pedido, ServiceQueueService servico, CancellationToken ct) =>
        {
            var fila = await servico.CreateAsync(pedido.OrganizationId, pedido.BusinessUnitId, pedido.DepartmentId, pedido.Name, ct);
            return Results.Created($"/contact-center/queues/{fila.Id}", fila);
        });

        filas.MapGet("/", async (ServiceQueueService servico, CancellationToken ct) => Results.Ok(await servico.ListAsync(ct)));

        filas.MapGet("/{id:guid}", async (Guid id, ServiceQueueService servico, CancellationToken ct) =>
        {
            var fila = await servico.GetAsync(id, ct);
            return fila is null ? Results.NotFound() : Results.Ok(fila);
        });

        filas.MapPost("/{id:guid}/pause", async (Guid id, ServiceQueueService servico, CancellationToken ct) =>
            Results.Ok(await servico.PauseAsync(id, ct)));

        filas.MapPost("/{id:guid}/resume", async (Guid id, ServiceQueueService servico, CancellationToken ct) =>
            Results.Ok(await servico.ResumeAsync(id, ct)));

        filas.MapGet("/{id:guid}/members", async (Guid id, ServiceQueueService servico, CancellationToken ct) =>
        {
            var membros = await servico.ListMembersAsync(id, ct);
            return membros is null ? Results.NotFound() : Results.Ok(membros);
        });

        // Idempotente: repetir o mesmo papel não muda nada, e outro papel troca o que já existe.
        filas.MapPut("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, SetQueueMemberRequest pedido, ServiceQueueService servico, CancellationToken ct) =>
            Results.Ok(await servico.AddMemberAsync(id, userId, pedido.Role, ct)));

        filas.MapDelete("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, ServiceQueueService servico, CancellationToken ct) =>
        {
            await servico.RemoveMemberAsync(id, userId, ct);
            return Results.NoContent();
        });

        // Política da fila: o que fazer fora do horário e sem atendente, a contingência, a prioridade padrão e o limite por atendente.
        filas.MapPut("/{id:guid}/policy", async (Guid id, QueuePolicyRequest pedido, QueuePolicyService servico, CancellationToken ct) =>
            Results.Ok(await servico.ConfigureAsync(id, pedido.FallbackQueueId, pedido.OutOfHours, pedido.NoAgent, pedido.DefaultPriority, pedido.MaxItemsPerAgent, ct)));

        // O horário de atendimento, com o fuso horário (IANA) explícito. Trocar é por inteiro; remover faz a fila atender sempre.
        filas.MapGet("/{id:guid}/schedule", async (Guid id, QueuePolicyService servico, CancellationToken ct) =>
        {
            var horario = await servico.GetScheduleAsync(id, ct);
            return horario is null ? Results.NotFound() : Results.Ok(horario);
        });

        filas.MapPut("/{id:guid}/schedule", async (Guid id, SetScheduleRequest pedido, QueuePolicyService servico, CancellationToken ct) =>
            Results.Ok(await servico.SetScheduleAsync(
                id,
                pedido.TimeZone,
                (pedido.Windows ?? Array.Empty<ScheduleWindowRequest>()).Select(w => new ScheduleWindowInput(w.DayOfWeek, w.Start, w.End)).ToList(),
                pedido.ClosedDates ?? Array.Empty<DateOnly>(),
                ct)));

        filas.MapDelete("/{id:guid}/schedule", async (Guid id, QueuePolicyService servico, CancellationToken ct) =>
            Results.Ok(await servico.ClearScheduleAsync(id, ct)));

        filas.MapGet("/{id:guid}/availability", async (Guid id, QueuePolicyService servico, CancellationToken ct) =>
        {
            var disponibilidade = await servico.GetAvailabilityAsync(id, ct);
            return disponibilidade is null ? Results.NotFound() : Results.Ok(disponibilidade);
        });

        // O atendente assume a próxima conversa da fila (a mais urgente e, na mesma prioridade, a mais antiga). 204 se não há nenhuma.
        filas.MapPost("/{id:guid}/claim-next", async (Guid id, ItemActionRequest pedido, QueueItemService servico, CancellationToken ct) =>
        {
            var resultado = await servico.ClaimNextAsync(id, pedido.RequestId, pedido.Reason, ct);
            return resultado is null ? Results.NoContent() : Resultado(resultado);
        });

        var regras = app.MapGroup("/contact-center/routing-rules").AddEndpointFilter(TratarErrosDoContactCenter);

        regras.MapPost("/", async (CreateRoutingRuleRequest pedido, RoutingRuleService servico, CancellationToken ct) =>
        {
            var regra = await servico.CreateAsync(
                pedido.OrganizationId, pedido.Name, pedido.ChannelInstanceId, pedido.BusinessUnitId, pedido.DepartmentId, pedido.QueueId, pedido.Priority, pedido.Order, ct);
            return Results.Created($"/contact-center/routing-rules/{regra.Id}", regra);
        });

        regras.MapGet("/", async (RoutingRuleService servico, CancellationToken ct) => Results.Ok(await servico.ListAsync(ct)));

        regras.MapGet("/{id:guid}", async (Guid id, RoutingRuleService servico, CancellationToken ct) =>
        {
            var regra = await servico.GetAsync(id, ct);
            return regra is null ? Results.NotFound() : Results.Ok(regra);
        });

        regras.MapPost("/{id:guid}/enable", async (Guid id, RoutingRuleService servico, CancellationToken ct) => Results.Ok(await servico.EnableAsync(id, ct)));

        regras.MapPost("/{id:guid}/disable", async (Guid id, RoutingRuleService servico, CancellationToken ct) => Results.Ok(await servico.DisableAsync(id, ct)));

        regras.MapDelete("/{id:guid}", async (Guid id, RoutingRuleService servico, CancellationToken ct) =>
        {
            await servico.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        var central = app.MapGroup("/contact-center").AddEndpointFilter(TratarErrosDoContactCenter);

        // Simula o roteamento de uma conversa, sem mudar nada: os destinos que seriam tentados, na ordem.
        central.MapPost("/routing/preview", async (RoutingPreviewRequest pedido, RoutingRuleService servico, CancellationToken ct) =>
            Results.Ok(await servico.PreviewAsync(pedido.SessionId, ct)));

        // A presença do atendente: cada um informa a própria.
        central.MapGet("/me/presence", async (AgentPresenceService servico, CancellationToken ct) => Results.Ok(await servico.GetMineAsync(ct)));

        central.MapPut("/me/presence", async (SetPresenceRequest pedido, AgentPresenceService servico, CancellationToken ct) =>
            Results.Ok(await servico.SetMineAsync(pedido.Status, ct)));

        // As filas em que o usuário atende: a participação dele é o acesso, mesmo sem permissão de ler as filas da organização.
        central.MapGet("/me/queues", async (ServiceQueueService servico, CancellationToken ct) => Results.Ok(await servico.ListMineAsync(ct)));

        // O pedido de atendimento humano (handoff). Quem o faz pela IA é o Agents, com contexto de sistema; um usuário que gere a
        // conversa também pode. A fila vem do pedido e passa pela mesma validação de unidade e de autorização.
        central.MapPost("/handoffs", async (RequestHandoffRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.RequestHandoffAsync(pedido.SessionId, pedido.QueueId, pedido.RequestId, pedido.Reason ?? "handoff", ct)));

        // O histórico por cliente (ADR 0041, E2.3): só em sessão embutida com o cliente AUTORIZADO nela; 404 para tudo o mais (cliente não autorizado, sem sessão embutida, inexistente), sem oráculo. Paginado por chave estável
        // (cursor opaco, limite de 1 a 100), só com o que o usuário já enxerga; o conteúdo das mensagens segue pelas rotas de item.
        // LIMITAÇÃO DOCUMENTADA (ressalva da E2.3): o limite conta SESSÕES, e o que não vira item visível é omitido DEPOIS de paginar. Uma página pode trazer MENOS itens que o limite, ou ZERO, e mesmo assim trazer
        // nextCursor: quem consome segue o cursor até nextCursor = null para alcançar todos os itens (o cursor nunca trunca). Sessão que nunca entrou em fila não tem item e não aparece.
        central.MapGet("/customers/{customerRef}/history", async (string customerRef, int? limit, string? cursor, QueueItemService servico, CancellationToken ct) =>
        {
            var historico = await servico.GetCustomerHistoryAsync(customerRef, limit ?? 50, cursor, ct);
            return historico is null ? Results.NotFound() : Results.Ok(historico);
        });

        var itens = central.MapGroup("/items");

        itens.MapGet("/", async (Guid? queueId, string? status, bool? assignedToMe, int? take, QueueItemService servico, CancellationToken ct) =>
        {
            QueueItemStatus? situacao = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<QueueItemStatus>(status, ignoreCase: true, out var lida) || !Enum.IsDefined(lida))
                {
                    throw new ContactCenterValidationException("A situação do item é inválida.");
                }

                situacao = lida;
            }

            return Results.Ok(await servico.ListAsync(queueId, situacao, assignedToMe ?? false, take ?? 50, ct));
        });

        itens.MapGet("/{id:guid}", async (Guid id, QueueItemService servico, CancellationToken ct) =>
        {
            var item = await servico.GetAsync(id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        itens.MapGet("/{id:guid}/history", async (Guid id, QueueItemService servico, CancellationToken ct) =>
        {
            var historico = await servico.GetHistoryAsync(id, ct);
            return historico is null ? Results.NotFound() : Results.Ok(historico);
        });

        itens.MapGet("/{id:guid}/conversation", async (Guid id, QueueItemService servico, CancellationToken ct) =>
        {
            var sessao = await servico.GetConversationAsync(id, ct);
            return sessao is null ? Results.NotFound() : Results.Ok(sessao);
        });

        itens.MapGet("/{id:guid}/messages", async (Guid id, int? take, string? cursor, string? order, QueueItemService servico, CancellationToken ct) =>
        {
            var ordem = HistoryOrder.OldestFirst;
            if (!string.IsNullOrWhiteSpace(order) && (!Enum.TryParse(order, ignoreCase: true, out ordem) || !Enum.IsDefined(ordem)))
            {
                throw new ContactCenterValidationException("A ordem do histórico é inválida.");
            }

            var pagina = await servico.GetMessagesAsync(id, take ?? 50, cursor, ordem, ct);
            return pagina is null ? Results.NotFound() : Results.Ok(pagina);
        });

        // O arquivo de UMA mensagem de mídia (ADR 0028): só para quem enxerga a mensagem no histórico do item, lido do armazenamento privado (nenhuma URL pública ou permanente existe). O navegador recebe o
        // tipo VERIFICADO, sem adivinhação de tipo, sem execução (sandbox) e sem cache compartilhado; imagem e áudio podem ser exibidos, o resto só baixado.
        itens.MapGet("/{id:guid}/messages/{messageId:guid}/attachment", async (Guid id, Guid messageId, QueueItemService servico, HttpContext http, CancellationToken ct) =>
        {
            var anexo = await servico.OpenAttachmentAsync(id, messageId, ct);
            if (anexo is null)
            {
                return Results.NotFound();
            }

            var exibivel = anexo.MimeType.StartsWith("image/", StringComparison.Ordinal) || anexo.MimeType.StartsWith("audio/", StringComparison.Ordinal);
            var disposicao = new System.Net.Http.Headers.ContentDispositionHeaderValue(exibivel ? "inline" : "attachment") { FileNameStar = anexo.FileName };
            http.Response.Headers.ContentDisposition = disposicao.ToString();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            http.Response.Headers.CacheControl = "private, no-store";
            if (anexo.Attachment.SizeBytes is { } tamanho)
            {
                http.Response.ContentLength = tamanho;
            }

            http.Response.RegisterForDisposeAsync(anexo);
            return Results.Stream(anexo.Content, anexo.MimeType);
        });

        itens.MapPost("/{id:guid}/claim", async (Guid id, ItemActionRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.ClaimAsync(id, pedido.RequestId, pedido.Reason, ct)));

        itens.MapPost("/{id:guid}/assign", async (Guid id, AssignItemRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.AssignAsync(id, pedido.UserId, pedido.RequestId, pedido.Reason, ct)));

        itens.MapPost("/{id:guid}/release", async (Guid id, ItemActionRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.ReleaseAsync(id, pedido.RequestId, pedido.Reason, ct)));

        itens.MapPost("/{id:guid}/transfer", async (Guid id, TransferItemRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.TransferAsync(id, pedido.QueueId, pedido.UserId, pedido.RequestId, pedido.Reason, ct)));

        itens.MapPost("/{id:guid}/return-to-ai", async (Guid id, ItemActionRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.ReturnToAiAsync(id, pedido.RequestId, pedido.Reason, ct)));

        // A resposta do atendente ao cliente e o encerramento do atendimento (7ª e 9ª operações do gateway elevado). O desfecho do Conversations vira o status HTTP.
        itens.MapPost("/{id:guid}/reply", async (Guid id, ReplyToItemRequest pedido, QueueItemService servico, CancellationToken ct) =>
            ReplyResult(await servico.ReplyAsync(id, pedido.RequestId, pedido.Text, ct)));

        // Responder por modelo de mensagem aprovado (ADR 0027): o que vale fora da janela de 24 horas. As opções dizem se a janela está aberta e quais modelos o número pode enviar.
        itens.MapGet("/{id:guid}/reply-options", async (Guid id, QueueItemService servico, CancellationToken ct) =>
            Results.Ok(await servico.GetReplyOptionsAsync(id, ct)));

        itens.MapPost("/{id:guid}/reply-template", async (Guid id, ReplyWithTemplateRequest pedido, QueueItemService servico, CancellationToken ct) =>
            ReplyResult(await servico.ReplyWithTemplateAsync(id, pedido.RequestId, pedido.TemplateId, pedido.Parameters, ct)));

        // Responder com um ARQUIVO (ADR 0031): imagem, áudio ou documento, com legenda quando o tipo a aceita. Formulário multipart com o requestId, o arquivo e a legenda; o Conversations valida e guarda. O
        // mesmo caminho da resposta de texto: só o responsável, janela de 24 horas aberta, requestId como chave de efeito, e a mensagem vai pelo outbox e pelo Sender de sempre.
        itens.MapPost("/{id:guid}/reply-media", async (Guid id, HttpContext http, QueueItemService servico, CancellationToken ct) =>
        {
            if (!http.Request.HasFormContentType)
            {
                throw new ContactCenterValidationException("Envie o arquivo como formulário (multipart/form-data).");
            }

            // O pedido de envio é limitado ANTES de ler: nada maior que o teto é aceito nem escrito em disco.
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limite)
            {
                limite.MaxRequestBodySize = MaxReplyMediaRequestBytes + (1024 * 1024);
            }

            IFormCollection formulario;
            try
            {
                formulario = await http.Request.ReadFormAsync(ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or Microsoft.AspNetCore.Http.BadHttpRequestException)
            {
                return Results.Problem(title: "Arquivo grande demais", detail: "O arquivo passa do tamanho máximo aceito para envio.", statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            if (!Guid.TryParse(formulario["requestId"], out var requestId) || requestId == Guid.Empty)
            {
                throw new ContactCenterValidationException("O pedido de envio precisa de um identificador (requestId).");
            }

            if (formulario.Files.Count != 1 || formulario.Files[0] is not { Length: > 0 } arquivo)
            {
                throw new ContactCenterValidationException("Envie exatamente um arquivo, que não esteja vazio.");
            }

            if (arquivo.Length > MaxReplyMediaRequestBytes)
            {
                return Results.Problem(title: "Arquivo grande demais", detail: "O arquivo passa do tamanho máximo aceito para envio.", statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var conteudo = new byte[arquivo.Length];
            await using (var fluxo = arquivo.OpenReadStream())
            {
                await fluxo.ReadExactlyAsync(conteudo, ct);
            }

            var legenda = formulario["caption"].ToString();
            var resultado = await servico.ReplyWithMediaAsync(
                id, requestId, new OutboundAttachment(arquivo.FileName, arquivo.ContentType, conteudo, string.IsNullOrWhiteSpace(legenda) ? null : legenda), ct);
            return MediaReplyResult(resultado);
        });

        // O vínculo do cliente em foco com o contato do item (ADR 0041, E2.2): só em sessão embutida, com ConversationManage e o cliente autorizado pelo servidor do sistema externo. 404 para tudo o que não é permissão
        // (item fora do escopo, sessão que não é embutida, cliente não autorizado): a mesma resposta, sem oráculo. O vínculo apenas localiza o contato; nunca concede visibilidade.
        itens.MapPost("/{id:guid}/customer-link", async (Guid id, CustomerLinkRequest pedido, CustomerLinkService servico, CancellationToken ct) =>
        {
            var resultado = await servico.LinkAsync(id, pedido.CustomerRef, ct);
            return resultado is null ? Results.NotFound() : Results.Ok(new { shared = resultado.Shared });
        });

        // Desvincula (lógico) e é idempotente: 204 existindo ou não vínculo ativo, desde que o atendente possa agir sobre o cliente.
        itens.MapDelete("/{id:guid}/customer-link/{customerRef}", async (Guid id, string customerRef, CustomerLinkService servico, CancellationToken ct) =>
            await servico.UnlinkAsync(id, customerRef, ct) ? Results.NoContent() : Results.NotFound());

        // O consentimento do contato do item (ADR 0041, E2.4). Só ConsentManage (administrador e gestor da organização do item); atendente comum e TODA sessão embutida recebem 403 (a rota nem consta da lista embutida).
        // O telefone é o do contato do item. 404 para item fora do escopo; 409 consent-phone-unavailable para contato de referência opaca; 409 consent-superseded para registro defasado.
        itens.MapPost("/{id:guid}/consents", async (Guid id, RecordConsentRequest pedido, ItemConsentService servico, CancellationToken ct) =>
        {
            if (!TryParsePurpose(pedido.Purpose, out var finalidade))
            {
                throw new ContactCenterValidationException("invalid-purpose", "A finalidade deve ser Utility ou Marketing.");
            }

            if (!TryParseStatus(pedido.Status, out var situacao))
            {
                throw new ContactCenterValidationException("invalid-status", "A situação deve ser Granted ou Revoked.");
            }

            var resultado = await servico.RecordAsync(id, finalidade, situacao, pedido.Evidence, ParseCollectedAt(pedido.CollectedAt), ct);
            return resultado switch
            {
                null => Results.NotFound(),
                { Outcome: ConsentRecordOutcome.Recorded or ConsentRecordOutcome.Unchanged } r => Results.Ok(new { recorded = r.Outcome == ConsentRecordOutcome.Recorded, status = r.Status.ToString(), purpose = r.Purpose.ToString() }),
                { Outcome: ConsentRecordOutcome.Superseded } => ConsentProblem(StatusCodes.Status409Conflict, "Registro defasado", "Há uma revogação posterior à data da coleta informada.", "consent-superseded"),
                { Outcome: ConsentRecordOutcome.PhoneUnavailable } => ConsentProblem(StatusCodes.Status409Conflict, "Telefone indisponível", "O contato desta conversa não tem telefone para o consentimento.", "consent-phone-unavailable"),
                { Outcome: ConsentRecordOutcome.Invalid } r => ConsentProblem(StatusCodes.Status400BadRequest, "Dados inválidos", "Um campo do consentimento é inválido.", r.RejectionCode ?? "invalid-request"),
                _ => Results.NotFound(),
            };
        });

        itens.MapGet("/{id:guid}/consents", async (Guid id, int? limit, ItemConsentService servico, CancellationToken ct) =>
        {
            var visao = await servico.GetAsync(id, limit ?? ConsentLimits.DefaultHistory, ct);
            if (visao is null)
            {
                return Results.NotFound();
            }

            if (visao.Phone is null)
            {
                return ConsentProblem(StatusCodes.Status409Conflict, "Telefone indisponível", "O contato desta conversa não tem telefone para o consentimento.", "consent-phone-unavailable");
            }

            // O estado efetivo (a última linha por finalidade) traz a evidência completa; o histórico, só um resumo. A origem distingue o que a integração afirmou, o que o atendente registrou e o que o próprio titular disse.
            return Results.Ok(new
            {
                organizationId = visao.OrganizationId,
                phone = visao.Phone,
                effective = visao.Effective.Select(ConsentJson),
                history = visao.History.Select(ConsentJson),
            });
        });

        // As ocorrências de possível saída (a estrutura de revisão; o reconhecimento por palavra-chave é da E2.5) e a decisão da pessoa autorizada, uma única vez.
        itens.MapGet("/{id:guid}/optout-reviews", async (Guid id, ItemConsentService servico, CancellationToken ct) =>
        {
            var lista = await servico.ListOptOutReviewsAsync(id, ct);
            return lista is null
                ? Results.NotFound()
                : Results.Ok(lista.Select(o => new
                {
                    id = o.Id,
                    messageId = o.MessageId,
                    reason = o.Reason.ToString(),
                    keyword = o.Keyword,
                    openedAt = o.OpenedAt,
                    resolution = o.Resolution?.ToString(),
                    resolvedAt = o.ResolvedAt,
                    resolvedBy = o.ResolvedBy,
                }));
        });

        itens.MapPost("/{id:guid}/optout-reviews/{messageId:guid}", async (Guid id, Guid messageId, ResolveOptOutRequest pedido, ItemConsentService servico, CancellationToken ct) =>
        {
            OptOutResolution decisao;
            switch (pedido.Resolution?.Trim().ToLowerInvariant())
            {
                case "revoked":
                    decisao = OptOutResolution.Revoked;
                    break;
                case "kept":
                    decisao = OptOutResolution.Kept;
                    break;
                default:
                    throw new ContactCenterValidationException("invalid-resolution", "A decisão deve ser Revoked ou Kept.");
            }

            var resultado = await servico.ResolveOptOutReviewAsync(id, messageId, decisao, ct);
            return resultado switch
            {
                null => Results.NotFound(),
                { Outcome: OptOutResolveOutcome.Resolved or OptOutResolveOutcome.AlreadyResolved } r => Results.Ok(new { resolved = r.Outcome == OptOutResolveOutcome.Resolved, resolution = r.Resolution.ToString() }),
                { Outcome: OptOutResolveOutcome.Conflict } r => ConsentProblem(StatusCodes.Status409Conflict, "Ocorrência já resolvida", $"A ocorrência já foi resolvida como {r.Resolution}.", "optout-review-already-resolved"),
                { Outcome: OptOutResolveOutcome.PhoneUnavailable } => ConsentProblem(StatusCodes.Status409Conflict, "Telefone indisponível", "O contato desta conversa não tem telefone para o consentimento.", "consent-phone-unavailable"),
                _ => Results.NotFound(),
            };
        });

        itens.MapPost("/{id:guid}/close", async (Guid id, CloseItemRequest pedido, QueueItemService servico, CancellationToken ct) =>
        {
            var resultado = await servico.CloseAsync(id, pedido.RequestId, pedido.ReasonCode, ct);
            return resultado.Outcome switch
            {
                CloseSessionDelegatedOutcome.Applied or CloseSessionDelegatedOutcome.AlreadyApplied or CloseSessionDelegatedOutcome.AlreadyClosed => Results.Ok(resultado),
                CloseSessionDelegatedOutcome.SessionNotFound => Results.NotFound(),
                _ => Results.Problem(title: "O atendimento mudou", detail: $"O encerramento não foi aplicado ({resultado.Outcome}). Releia a conversa e tente de novo.", statusCode: StatusCodes.Status409Conflict),
            };
        });

        // Só quem supervisiona a fila muda a prioridade; fica no histórico.
        itens.MapPost("/{id:guid}/priority", async (Guid id, SetPriorityRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.SetPriorityAsync(id, pedido.Priority, pedido.RequestId, pedido.Reason, ct)));

        return app;
    }

    private static object ConsentJson(ConsentRecordView c) => new
    {
        id = c.Id,
        purpose = c.Purpose.ToString(),
        status = c.Status.ToString(),
        origin = c.Origin.ToString(),
        evidence = c.Evidence,
        collectedAt = c.CollectedAt,
        recordedAt = c.RecordedAt,
        actorKind = c.ActorKind.ToString(),
        actorId = c.ActorId,
        customerRef = c.CustomerRef,
    };

    private static IResult ConsentProblem(int status, string title, string detail, string code) =>
        Results.Problem(title: title, detail: detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static bool TryParsePurpose(string? valor, out ConsentPurpose finalidade)
    {
        finalidade = default;
        switch (valor?.Trim().ToLowerInvariant())
        {
            case "utility":
                finalidade = ConsentPurpose.Utility;
                return true;
            case "marketing":
                finalidade = ConsentPurpose.Marketing;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseStatus(string? valor, out ConsentStatus situacao)
    {
        situacao = default;
        switch (valor?.Trim().ToLowerInvariant())
        {
            case "granted":
                situacao = ConsentStatus.Granted;
                return true;
            case "revoked":
                situacao = ConsentStatus.Revoked;
                return true;
            default:
                return false;
        }
    }

    // Só ISO 8601 COM fuso (Z ou ±hh:mm): sem fuso a data seria ambígua. Qualquer outra coisa vira "ausente", e o Conversations a recusa com collected-at-invalid.
    private static DateTimeOffset? ParseCollectedAt(string? texto) =>
        texto is not null
        && System.Text.RegularExpressions.Regex.IsMatch(texto.Trim(), @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$")
        && DateTimeOffset.TryParse(texto.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var data)
            ? data
            : null;

    // O desfecho do Conversations vira o status HTTP (o mesmo para a resposta de texto e para a de modelo).
    private static IResult ReplyResult(RegisterHumanReplyResult resultado) => resultado.Outcome switch
    {
        RegisterHumanReplyOutcome.Applied or RegisterHumanReplyOutcome.AlreadyApplied => Results.Ok(resultado),
        RegisterHumanReplyOutcome.ContentRejected => Results.Problem(title: "Resposta recusada", detail: "O conteúdo da resposta não é aceito.", statusCode: StatusCodes.Status400BadRequest),
        RegisterHumanReplyOutcome.SessionNotFound => Results.NotFound(),
        _ => Results.Problem(title: "O atendimento mudou", detail: $"A resposta não foi enviada ({resultado.Outcome}). Releia a conversa e tente de novo.", statusCode: StatusCodes.Status409Conflict),
    };

    /// <summary>
    /// O teto do PEDIDO de envio de arquivo (o formulário inteiro): nada maior é lido. O limite de cada tipo (imagem, áudio, documento) é do Conversations (<c>Conversations:Attachments</c>, padrão 5, 16 e 20 MB) e a
    /// recusa por tamanho volta como <c>MediaRejected</c>/<c>too-large</c>. Subir o limite de documento acima deste teto exige subir este também.
    /// </summary>
    public const long MaxReplyMediaRequestBytes = 25L * 1024 * 1024;

    // A recusa do arquivo (o motivo é um código fixo do Conversations) vira 422 com o motivo e uma mensagem clara; as demais saídas são as da resposta de texto.
    private static IResult MediaReplyResult(RegisterHumanReplyResult resultado)
    {
        if (resultado.Outcome != RegisterHumanReplyOutcome.ContentRejected || resultado.RejectionCode is not { } motivo)
        {
            return ReplyResult(resultado);
        }

        var mensagem = motivo switch
        {
            "empty" => "O arquivo está vazio.",
            "too-large" => "O arquivo é maior que o limite para este tipo (no padrão: imagem 5 MB, áudio 16 MB e documento 20 MB).",
            "type-not-allowed" => "Este tipo de arquivo não pode ser enviado. Use imagem (JPEG ou PNG), áudio (AAC, AMR, MP3, M4A ou Ogg Opus) ou documento (PDF, TXT, Word, Excel ou PowerPoint).",
            "content-mismatch" => "O conteúdo do arquivo não corresponde ao tipo ou à extensão informados.",
            "audio-codec-unsupported" => "O áudio Ogg precisa usar a codificação Opus, em mono.",
            "caption-not-supported" => "Áudio não aceita legenda.",
            "caption-too-long" => "A legenda passa de 1024 caracteres.",
            "malware" => "O arquivo foi bloqueado pela verificação de segurança.",
            "quota-exceeded" => "O limite de armazenamento de arquivos da empresa foi atingido. Peça ao administrador para liberar espaço ou ampliar o limite.",
            "scanner-unavailable" => "A verificação de segurança dos arquivos está indisponível agora. Tente enviar de novo em instantes.",
            _ => "O arquivo não é aceito.",
        };

        // O verificador fora do ar não é culpa do arquivo: 503 (a interface repete com o MESMO pedido depois). As demais recusas são do arquivo: 422.
        return Results.Problem(
            title: motivo == "scanner-unavailable" ? "Verificação indisponível" : "Arquivo recusado",
            detail: mensagem,
            statusCode: motivo == "scanner-unavailable" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["code"] = "MediaRejected", ["reason"] = motivo });
    }

    // 202 enquanto o Conversations não confirmou: a intenção está registrada e repetir o pedido (mesmo requestId) a conclui.
    private static IResult Resultado(QueueItemOperationResult resultado) =>
        resultado.Outcome == OperationOutcome.Pending ? Results.Accepted(uri: null, value: resultado) : Results.Ok(resultado);

    private static async ValueTask<object?> TratarErrosDoContactCenter(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
    {
        try
        {
            return await proximo(contexto);
        }
        catch (Exception ex) when (ex is TenantContextRequiredException or AuthenticationRequiredException)
        {
            return Results.Problem(
                title: "Autenticação necessária",
                detail: "Esta operação exige um usuário autenticado.",
                statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (Exception ex) when (ex is AccessDeniedException or QueueAccessDeniedException)
        {
            return Results.Problem(title: "Acesso negado", detail: "Você não tem permissão para esta operação.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ContactCenterResourceNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ContactCenterValidationException ex)
        {
            return ex.Code is null
                ? Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest)
                : Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
        catch (QueueItemConflictException ex)
        {
            return Results.Problem(
                title: "Conflito",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Conflict.ToString() });
        }
        catch (ReplyTemplateInvalidException ex)
        {
            // Todos os problemas de uma vez, por chave de parâmetro: o formulário os mostra junto de cada campo. Só chave, código e mensagem fixa (nunca o valor digitado).
            return Results.Problem(
                title: "Parâmetros do modelo inválidos",
                detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "TemplateParametersInvalid",
                    ["errors"] = ex.Errors.Select(e => new { key = e.Key, code = e.Code, message = e.Message }).ToList(),
                });
        }
        catch (ContactCenterRuleException ex)
        {
            return Results.Problem(
                title: "Regra não atendida",
                detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Rule.ToString() });
        }
        catch (Exception ex) when (ex is QueueNameInUseException or ContactCenterConflictException or QueueItemStateException)
        {
            return Results.Problem(title: "Conflito", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
