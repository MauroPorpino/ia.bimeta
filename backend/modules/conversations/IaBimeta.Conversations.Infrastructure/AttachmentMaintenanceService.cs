using System.Security.Cryptography;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>O uso de armazenamento de uma empresa (só números: nunca nome de arquivo nem conteúdo).</summary>
/// <param name="QuotaBytes">A cota configurada (0 = sem limite).</param>
public sealed record AttachmentTenantUsage(Guid TenantId, long Files, long Bytes, long Inbound, long Outbound, long NotScanned, long Unavailable, long Rejected, long QuotaBytes);

/// <summary>
/// O resultado da verificação de consistência entre as linhas de arquivo do banco e os objetos do armazenamento (ADR 0032). Só identificadores: <c>Missing</c> (linha disponível sem objeto), <c>Unreadable</c> (o objeto
/// existe mas não decifra: chave ausente ou errada, ou adulterado), <c>Mismatch</c> (decifra, mas o tamanho ou o resumo SHA-256 não é o da linha), <c>Orphans</c> (objeto sem linha), <c>Legacy</c> (objeto em texto claro com a
/// cifra ligada). Cada lista traz no máximo <see cref="SampleSize"/> identificadores; as contagens são exatas.
/// </summary>
public sealed class AttachmentConsistencyReport
{
    public const int SampleSize = 50;

    public int Checked { get; set; }

    public int Ok { get; set; }

    public List<string> Missing { get; } = [];

    public List<string> Unreadable { get; } = [];

    public List<string> Mismatch { get; } = [];

    public List<string> Orphans { get; } = [];

    public List<string> Legacy { get; } = [];

    public int MissingCount { get; set; }

    public int UnreadableCount { get; set; }

    public int MismatchCount { get; set; }

    public int OrphanCount { get; set; }

    public int LegacyCount { get; set; }

    /// <summary>Com <c>repair</c>: quantas linhas sem objeto passaram a <c>Unavailable</c> (<c>lost</c>) e quantos órfãos antigos foram apagados.</summary>
    public int RepairedMissing { get; set; }

    public int RemovedOrphans { get; set; }

    /// <summary>Há algo que exige atenção (qualquer divergência que não seja só um objeto legado em texto claro).</summary>
    public bool HasProblems => MissingCount + UnreadableCount + MismatchCount + OrphanCount > 0;

    internal void Add(List<string> lista, string id)
    {
        if (lista.Count < SampleSize)
        {
            lista.Add(id);
        }
    }
}

public sealed record AttachmentRescanReport(int Candidates, int MarkedClean, int Infected, int Unavailable, int Unreadable);

public sealed record AttachmentEncryptReport(int Plaintext, int Converted, int AlreadyEncrypted, int Unreadable);

/// <summary>
/// A manutenção do armazenamento de arquivos, para o operador (comando <c>attachments</c>, ADR 0032): o uso por empresa, a verificação banco × armazenamento (com reparo opcional), a varredura posterior de arquivos
/// guardados antes de o verificador existir e a conversão dos objetos antigos para a cifra em repouso. Tudo é idempotente, atravessa os tenants só pelo identificador (nunca mistura conteúdo de empresas: cada objeto
/// é lido SÓ sob o tenant da própria linha) e nunca imprime nome de arquivo, legenda nem conteúdo.
/// </summary>
public sealed class AttachmentMaintenanceService
{
    private const int Batch = 100;

    private readonly IServiceScopeFactory _scopes;
    private readonly EncryptedAttachmentContentStore _content;
    private readonly AttachmentOptions _options;
    private readonly IAttachmentScanner _scanner;
    private readonly TimeProvider _clock;
    private readonly IRetentionAuditSink? _audit;

    public AttachmentMaintenanceService(
        IServiceScopeFactory scopes, EncryptedAttachmentContentStore content, AttachmentOptions options, IAttachmentScanner scanner, TimeProvider clock, IRetentionAuditSink? audit = null)
    {
        _scopes = scopes;
        _content = content;
        _options = options;
        _scanner = scanner;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>O uso por empresa: arquivos disponíveis, bytes, sentido, não verificados e os que ficaram indisponíveis ou recusados.</summary>
    public async Task<IReadOnlyList<AttachmentTenantUsage>> UsageAsync(CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var linhas = await db.Attachments.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(a => a.TenantId)
            .Select(g => new
            {
                Tenant = g.Key,
                Files = g.Count(a => a.Status == AttachmentStatus.Available),
                Bytes = g.Where(a => a.Status == AttachmentStatus.Available).Sum(a => a.SizeBytes ?? 0),
                Inbound = g.Count(a => a.Status == AttachmentStatus.Available && a.Direction == MessageDirection.Inbound),
                Outbound = g.Count(a => a.Status == AttachmentStatus.Available && a.Direction == MessageDirection.Outbound),
                NotScanned = g.Count(a => a.Status == AttachmentStatus.Available && a.ScanStatus == AttachmentScanStatus.NotScanned),
                Unavailable = g.Count(a => a.Status == AttachmentStatus.Unavailable),
                Rejected = g.Count(a => a.Status == AttachmentStatus.Rejected),
            })
            .ToListAsync(cancellationToken);
        return linhas.Select(l => new AttachmentTenantUsage(l.Tenant.Value, l.Files, l.Bytes, l.Inbound, l.Outbound, l.NotScanned, l.Unavailable, l.Rejected, _options.TenantQuotaBytes))
            .OrderBy(l => l.TenantId).ToList();
    }

    /// <summary>
    /// Confere cada linha disponível contra o objeto (existência, decifragem, tamanho e resumo) e lista os objetos sem linha. Com <paramref name="repair"/>: a linha sem objeto vira <c>Unavailable</c> com o motivo
    /// <c>lost</c> (para ninguém tentar abri-la) e o objeto órfão com mais de <paramref name="orphanMinAge"/> é apagado. O que não decifra ou não confere NUNCA é alterado (pode ser só a chave errada no ambiente).
    /// </summary>
    /// <param name="allowEmptyFolder">
    /// Só com <paramref name="repair"/>: permite marcar como perdidas TODAS as linhas conferidas mesmo quando a pasta não tem nenhum objeto para o alcance pedido. Sem isso o reparo recusa, porque esse quadro é quase sempre
    /// <c>RootPath</c> errado ou pasta não restaurada, e marcar tudo como perdido apagaria no banco a chave, o tamanho e o resumo que permitem reencontrar os arquivos.
    /// </param>
    public async Task<AttachmentConsistencyReport> CheckAsync(bool repair, TimeSpan orphanMinAge, Guid? tenantId = null, bool allowEmptyFolder = false, CancellationToken cancellationToken = default)
    {
        var relatorio = new AttachmentConsistencyReport();
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var conhecidas = new HashSet<(Guid Tenant, Guid Id)>();
        var perdidas = new List<(TenantId Tenant, Guid Id)>();
        var unica = tenantId is { } escolhida ? new TenantId(escolhida) : null;

        var deslocamento = 0;
        while (true)
        {
            // Ordem total pela chave primária (empresa e arquivo): a página seguinte nunca pula nem repete uma linha enquanto nada muda; o que mudar durante a verificação é visto na próxima rodada.
            var disponiveis = db.Attachments.IgnoreQueryFilters().AsNoTracking().Where(a => a.Status == AttachmentStatus.Available);
            var lote = await (unica is null ? disponiveis : disponiveis.Where(a => a.TenantId == unica))
                .OrderBy(a => a.TenantId).ThenBy(a => a.Id)
                .Skip(deslocamento).Take(Batch)
                .ToListAsync(cancellationToken);
            if (lote.Count == 0)
            {
                break;
            }

            deslocamento += lote.Count;

            foreach (var anexo in lote)
            {
                relatorio.Checked++;
                conhecidas.Add((anexo.TenantId.Value, anexo.Id));
                var id = $"{anexo.TenantId.Value:N}/{anexo.Id:N}";
                if (anexo.StorageKey is not { } chave)
                {
                    relatorio.MissingCount++;
                    relatorio.Add(relatorio.Missing, id);
                    perdidas.Add((anexo.TenantId, anexo.Id));
                    continue;
                }

                byte[]? bruto;
                try
                {
                    bruto = await _content.ReadRawAsync(anexo.TenantId, chave, cancellationToken);
                }
                catch (AttachmentContentUnreadableException)
                {
                    relatorio.UnreadableCount++;
                    relatorio.Add(relatorio.Unreadable, id);
                    continue;
                }

                if (bruto is null)
                {
                    relatorio.MissingCount++;
                    relatorio.Add(relatorio.Missing, id);
                    perdidas.Add((anexo.TenantId, anexo.Id));
                    continue;
                }

                byte[] conteudo;
                try
                {
                    await using var fluxo = await _content.OpenReadAsync(anexo.TenantId, chave, cancellationToken);
                    using var memoria = new MemoryStream();
                    await fluxo!.CopyToAsync(memoria, cancellationToken);
                    conteudo = memoria.ToArray();
                }
                catch (AttachmentContentUnreadableException)
                {
                    relatorio.UnreadableCount++;
                    relatorio.Add(relatorio.Unreadable, id);
                    continue;
                }

                if (conteudo.Length != anexo.SizeBytes || !string.Equals(Convert.ToHexString(SHA256.HashData(conteudo)), anexo.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    relatorio.MismatchCount++;
                    relatorio.Add(relatorio.Mismatch, id);
                    continue;
                }

                if (_options.Encryption.Enabled && !EncryptedAttachmentContentStore.HasEncryptedHeader(bruto))
                {
                    relatorio.LegacyCount++;
                    relatorio.Add(relatorio.Legacy, id);
                }

                relatorio.Ok++;
            }

        }

        // Os objetos sem linha disponível: o objeto de uma linha que NÃO está disponível (indisponível, recusada, pendente) também é órfão para fins de espaço, mas só conta aqui o objeto sem nenhuma linha.
        var corteDosOrfaos = _clock.GetUtcNow() - orphanMinAge;
        var aRemover = new List<StoredAttachmentObject>();
        var objetosNoAlcance = 0;
        await foreach (var objeto in _content.ListOlderThanAsync(DateTimeOffset.MaxValue, cancellationToken))
        {
            if (tenantId is { } soEsta && objeto.TenantId != soEsta)
            {
                continue;
            }

            objetosNoAlcance++;
            if (conhecidas.Contains((objeto.TenantId, objeto.AttachmentId)))
            {
                continue;
            }

            var tenant = new TenantId(objeto.TenantId);
            if (await db.Attachments.IgnoreQueryFilters().AsNoTracking().AnyAsync(a => a.TenantId == tenant && a.Id == objeto.AttachmentId, cancellationToken))
            {
                continue;
            }

            relatorio.OrphanCount++;
            relatorio.Add(relatorio.Orphans, $"{objeto.TenantId:N}/{objeto.AttachmentId:N}");
            if (repair && objeto.LastWriteUtc < corteDosOrfaos)
            {
                aRemover.Add(objeto);
            }
        }

        if (repair)
        {
            // A trava contra o erro mais comum: todas as linhas sem objeto e nenhum objeto na pasta para o alcance pedido é quase sempre RootPath errado (ou pasta ainda não restaurada), e marcar tudo como
            // perdido apagaria no banco o que permite reencontrar os arquivos. Nada é alterado; o operador corrige o caminho ou, se a perda é real, confirma com --allow-empty-folder.
            if (!allowEmptyFolder && perdidas.Count > 0 && perdidas.Count == relatorio.Checked && objetosNoAlcance == 0)
            {
                throw new InvalidOperationException(
                    $"Nenhuma das {relatorio.Checked} linha(s) conferida(s) tem objeto e a pasta de arquivos não tem nenhum objeto para o alcance pedido: quase certamente Conversations:Attachments:RootPath está errado "
                    + "(ou a pasta não foi restaurada). O reparo NÃO foi executado. Corrija o caminho; se os arquivos foram mesmo perdidos, repita com --allow-empty-folder.");
            }

            // A trilha de auditoria vem ANTES de qualquer remoção ou mudança (nada é alterado sem registro): se ela falhar, nada é feito.
            if (_audit is not null && (aRemover.Count > 0 || perdidas.Count > 0))
            {
                await _audit.RecordAsync(
                    [
                        .. aRemover.Select(o => new RetentionAuditItem(o.TenantId, AttachmentObjectsRetentionTask.OrphanAuditOperation, "attachment", o.AttachmentId.ToString())),
                        .. perdidas.Select(p => new RetentionAuditItem(p.Tenant.Value, "conversations.attachment.lost", "attachment", p.Id.ToString())),
                    ],
                    cancellationToken);
            }

            foreach (var objeto in aRemover)
            {
                await _content.DeleteAsync(new TenantId(objeto.TenantId), objeto.Key, cancellationToken);
                relatorio.RemovedOrphans++;
            }

            foreach (var (tenant, id) in perdidas)
            {
                var agora = _clock.GetUtcNow();
                var mudou = await db.Attachments.IgnoreQueryFilters()
                    .Where(a => a.TenantId == tenant && a.Id == id && a.Status == AttachmentStatus.Available)
                    .ExecuteUpdateAsync(
                        s => s
                            .SetProperty(a => a.Status, AttachmentStatus.Unavailable)
                            .SetProperty(a => a.FailureCode, MessageAttachment.LostFailureCode)
                            .SetProperty(a => a.MimeType, (string?)null)
                            .SetProperty(a => a.SizeBytes, (long?)null)
                            .SetProperty(a => a.Sha256, (string?)null)
                            .SetProperty(a => a.StorageKey, (string?)null)
                            .SetProperty(a => a.UpdatedAt, agora),
                        cancellationToken);
                relatorio.RepairedMissing += mudou;
            }
        }

        return relatorio;
    }

    /// <summary>
    /// Verifica os arquivos disponíveis que nunca passaram por um verificador (guardados antes de ele existir, ou enquanto estava fora do ar e a varredura não era exigida). Limpo vira "verificado"; infectado é apagado e a linha vira
    /// <c>Rejected</c> (<c>malware</c>); o verificador indisponível deixa como está. Exige um mecanismo configurado.
    /// </summary>
    public async Task<AttachmentRescanReport> RescanAsync(bool dryRun, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        if (_options.Scanner.Mode == AttachmentScannerMode.None)
        {
            throw new InvalidOperationException("Não há verificador configurado (Conversations:Attachments:Scanner:Mode): não há como verificar os arquivos.");
        }

        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        int candidatos = 0, limpos = 0, infectados = 0, indisponiveis = 0, ilegiveis = 0;
        var tentados = new HashSet<(Guid, Guid)>();
        var unica = tenantId is { } escolhida ? new TenantId(escolhida) : null;
        while (true)
        {
            var naoVerificados = db.Attachments.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.Status == AttachmentStatus.Available && a.ScanStatus == AttachmentScanStatus.NotScanned);
            var lote = await (unica is null ? naoVerificados : naoVerificados.Where(a => a.TenantId == unica))
                .OrderBy(a => a.CreatedAt).Take(Batch).ToListAsync(cancellationToken);
            var novos = lote.Where(a => tentados.Add((a.TenantId.Value, a.Id))).ToList();
            if (novos.Count == 0)
            {
                break;
            }

            foreach (var anexo in novos)
            {
                candidatos++;
                if (anexo.StorageKey is not { } chave || anexo.MimeType is not { } tipo)
                {
                    continue;
                }

                byte[] conteudo;
                try
                {
                    await using var fluxo = await _content.OpenReadAsync(anexo.TenantId, chave, cancellationToken);
                    if (fluxo is null)
                    {
                        ilegiveis++;
                        continue;
                    }

                    using var memoria = new MemoryStream();
                    await fluxo.CopyToAsync(memoria, cancellationToken);
                    conteudo = memoria.ToArray();
                }
                catch (AttachmentContentUnreadableException)
                {
                    ilegiveis++;
                    continue;
                }

                var veredito = await _scanner.ScanAsync(anexo.Kind, tipo, conteudo, cancellationToken);
                if (dryRun)
                {
                    switch (veredito)
                    {
                        case ScanVerdict.Clean:
                            limpos++;
                            break;
                        case ScanVerdict.Infected:
                            infectados++;
                            break;
                        default:
                            indisponiveis++;
                            break;
                    }

                    continue;
                }

                var agora = _clock.GetUtcNow();
                if (veredito == ScanVerdict.Clean)
                {
                    limpos += await db.Attachments.IgnoreQueryFilters()
                        .Where(a => a.TenantId == anexo.TenantId && a.Id == anexo.Id && a.Status == AttachmentStatus.Available && a.ScanStatus == AttachmentScanStatus.NotScanned)
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.ScanStatus, AttachmentScanStatus.Clean).SetProperty(a => a.ScannedAt, agora).SetProperty(a => a.UpdatedAt, agora), cancellationToken);
                }
                else if (veredito == ScanVerdict.Infected)
                {
                    // Registro ANTES de apagar: nada é apagado sem a trilha gravada.
                    if (_audit is not null)
                    {
                        await _audit.RecordAsync([new RetentionAuditItem(anexo.TenantId.Value, "conversations.attachment.malware-blocked", "attachment", anexo.Id.ToString())], cancellationToken);
                    }

                    await _content.DeleteAsync(anexo.TenantId, chave, cancellationToken);
                    infectados += await db.Attachments.IgnoreQueryFilters()
                        .Where(a => a.TenantId == anexo.TenantId && a.Id == anexo.Id && a.Status == AttachmentStatus.Available)
                        .ExecuteUpdateAsync(
                            s => s
                                .SetProperty(a => a.Status, AttachmentStatus.Rejected)
                                .SetProperty(a => a.FailureCode, "malware")
                                .SetProperty(a => a.MimeType, (string?)null)
                                .SetProperty(a => a.SizeBytes, (long?)null)
                                .SetProperty(a => a.Sha256, (string?)null)
                                .SetProperty(a => a.StorageKey, (string?)null)
                                .SetProperty(a => a.UpdatedAt, agora),
                            cancellationToken);
                }
                else
                {
                    indisponiveis++;
                }
            }

            // Em simulação nada muda no banco: o mesmo lote voltaria. O conjunto de já tentados evita repetir.
        }

        return new AttachmentRescanReport(candidatos, limpos, infectados, indisponiveis, ilegiveis);
    }

    /// <summary>Converte os objetos antigos em texto claro para o formato cifrado, no lugar (idempotente). Exige a cifra ligada. Em simulação, só conta.</summary>
    public async Task<AttachmentEncryptReport> EncryptExistingAsync(bool dryRun, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        if (!_options.Encryption.Enabled)
        {
            throw new InvalidOperationException("A cifra de arquivos não está ligada (Conversations:Attachments:Encryption:ActiveKeyId): não há como converter os objetos.");
        }

        int claros = 0, convertidos = 0, jaCifrados = 0, ilegiveis = 0;
        await foreach (var objeto in _content.ListOlderThanAsync(DateTimeOffset.MaxValue, cancellationToken))
        {
            if (tenantId is { } soEsta && objeto.TenantId != soEsta)
            {
                continue;
            }

            var tenant = new TenantId(objeto.TenantId);
            byte[]? bruto;
            try
            {
                bruto = await _content.ReadRawAsync(tenant, objeto.Key, cancellationToken);
            }
            catch (AttachmentContentUnreadableException)
            {
                ilegiveis++;
                continue;
            }

            if (bruto is null)
            {
                continue;
            }

            if (EncryptedAttachmentContentStore.HasEncryptedHeader(bruto))
            {
                jaCifrados++;
                continue;
            }

            claros++;
            if (!dryRun && await _content.EncryptInPlaceAsync(tenant, objeto.Key, cancellationToken))
            {
                convertidos++;
            }
        }

        return new AttachmentEncryptReport(claros, convertidos, jaCifrados, ilegiveis);
    }
}
