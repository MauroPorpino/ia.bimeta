using System.Globalization;
using IaBimeta.Api.Operations;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Api.Attachments;

/// <summary>
/// O comando <c>attachments</c> do host (ADR 0032), para quem opera o armazenamento dos arquivos de mídia: <c>report</c> (uso por empresa, cota, verificador e cifra), <c>check [--repair --confirm]</c> (consistência entre o
/// banco e a pasta de arquivos), <c>rescan [--confirm]</c> (verifica arquivos guardados antes do verificador existir) e <c>encrypt-existing [--confirm]</c> (converte os objetos antigos para a cifra em repouso). Roda na máquina
/// de quem opera, com acesso ao banco e à pasta (a MESMA configuração do Api e do Worker), sem rota HTTP. O que altera algo só age com <c>--confirm</c>; sem ele é uma simulação. Só identificadores e números saem: nunca nome de
/// arquivo, legenda, conteúdo, caminho de objeto nem chave. Saída: 0 (tudo certo), 1 (há divergência ou algo que exige atenção), 2 (uso inválido ou configuração que impede a operação).
/// </summary>
internal static class AttachmentsCommand
{
    public const string Verb = "attachments";

    internal const string Usage = "Uso: attachments report | check [--repair --confirm [--allow-empty-folder]] [--orphan-days N] [--tenant ID] | rescan [--confirm] [--tenant ID] | encrypt-existing [--confirm] [--tenant ID]";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] is not ("report" or "check" or "rescan" or "encrypt-existing"))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        var confirmado = false;
        var reparar = false;
        var diasDeOrfao = 1;
        var aceitarPastaVazia = false;
        Guid? empresa = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--confirm":
                    confirmado = true;
                    break;
                case "--tenant" when args[0] != "report" && i + 1 < args.Length && Guid.TryParse(args[i + 1], out var identificador) && identificador != Guid.Empty:
                    empresa = identificador;
                    i++;
                    break;
                case "--repair" when args[0] == "check":
                    reparar = true;
                    break;
                case "--allow-empty-folder" when args[0] == "check":
                    aceitarPastaVazia = true;
                    break;
                case "--orphan-days" when args[0] == "check" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var dias) && dias is >= 0 and <= 3650:
                    diasDeOrfao = dias;
                    i++;
                    break;
                default:
                    await output.WriteLineAsync($"Argumento desconhecido ou fora de lugar: {args[i]}");
                    await output.WriteLineAsync(Usage);
                    return 2;
            }
        }

        if (reparar && !confirmado)
        {
            await output.WriteLineAsync("O reparo só roda com --confirm. Rode antes 'attachments check' (sem --repair) e confira o que seria alterado.");
            return 2;
        }

        if (aceitarPastaVazia && !reparar)
        {
            await output.WriteLineAsync("--allow-empty-folder só vale com --repair --confirm.");
            return 2;
        }

        // A mesma composição do Api, sem abrir porta nem iniciar serviço algum, com só avisos e erros no console.
        await using var app = ApiHost.Build(Array.Empty<string>(), builder => OperationsLogging.Quiet(builder.Logging));
        return await ExecuteAsync(app.Services, args[0], confirmado, reparar, TimeSpan.FromDays(diasDeOrfao), output, empresa, aceitarPastaVazia);
    }

    /// <param name="tenantId">Limita a verificação, a varredura posterior ou a conversão a UMA empresa (o alcance do reparo fica só nela). Sem ele, todas.</param>
    /// <param name="allowEmptyFolder">Só com o reparo: aceita marcar tudo como perdido mesmo com a pasta sem nenhum objeto (a trava contra <c>RootPath</c> errado).</param>
    internal static async Task<int> ExecuteAsync(
        IServiceProvider root, string verb, bool confirmed, bool repair, TimeSpan orphanMinAge, TextWriter output, Guid? tenantId = null, bool allowEmptyFolder = false, CancellationToken cancellationToken = default)
    {
        await using var escopo = root.CreateAsyncScope();
        var servico = escopo.ServiceProvider.GetRequiredService<AttachmentMaintenanceService>();
        var opcoes = root.GetRequiredService<AttachmentOptions>();
        try
        {
            return verb switch
            {
                "report" => await ReportAsync(root, servico, opcoes, output, cancellationToken),
                "check" => await CheckAsync(servico, repair, orphanMinAge, tenantId, allowEmptyFolder, output, cancellationToken),
                "rescan" => await RescanAsync(servico, confirmed, tenantId, output, cancellationToken),
                _ => await EncryptAsync(servico, confirmed, tenantId, output, cancellationToken),
            };
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync($"Operação não executada: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> ReportAsync(IServiceProvider root, AttachmentMaintenanceService servico, AttachmentOptions opcoes, TextWriter output, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("Armazenamento de arquivos (somente leitura).");
        await output.WriteLineAsync($"  Cota por empresa : {(opcoes.TenantQuotaBytes == 0 ? "sem limite" : Bytes(opcoes.TenantQuotaBytes))}");
        await output.WriteLineAsync($"  Cifra em repouso : {(opcoes.Encryption.Enabled ? $"LIGADA (chave ativa '{opcoes.Encryption.ActiveKeyId}', {opcoes.Encryption.Keys.Count} chave(s) conhecida(s))" : "DESLIGADA (arquivos novos em texto claro)")}{(opcoes.Encryption.Required ? "; exigida" : string.Empty)}");
        await output.WriteLineAsync($"  Verificador      : {Verificador(opcoes)}");
        var atencao = false;
        if (opcoes.Scanner.Mode != AttachmentScannerMode.None)
        {
            // Um arquivo benigno de poucos bytes: só prova que o verificador responde (a amostra de teste padrão nunca vai ao verificador: a detecção embutida a pega antes).
            var responde = await root.GetRequiredService<IAttachmentScanner>().ScanAsync(MessageKind.Document, "text/plain", "verificacao de rotina"u8.ToArray(), cancellationToken);
            await output.WriteLineAsync($"  O verificador responde agora: {(responde == ScanVerdict.Clean ? "sim" : "NÃO")}");
            atencao |= responde != ScanVerdict.Clean;
        }

        await output.WriteLineAsync();
        var uso = await servico.UsageAsync(cancellationToken);
        if (uso.Count == 0)
        {
            await output.WriteLineAsync("Nenhum arquivo guardado ainda.");
            return atencao ? 1 : 0;
        }

        var slugs = new Dictionary<Guid, string>();
        await output.WriteLineAsync($"  {"Empresa",-24} {"Arquivos",9} {"Espaço",12} {"% cota",7} {"Receb.",7} {"Env.",6} {"Não verif.",11} {"Indisp.",8} {"Recus.",7}");
        foreach (var linha in uso)
        {
            var slug = await SlugAsync(root, slugs, linha.TenantId, cancellationToken);
            var porcentagem = linha.QuotaBytes == 0 ? "-" : $"{(100.0 * linha.Bytes / linha.QuotaBytes).ToString("0.0", CultureInfo.InvariantCulture)}%";
            await output.WriteLineAsync($"  {slug,-24} {linha.Files,9} {Bytes(linha.Bytes),12} {porcentagem,7} {linha.Inbound,7} {linha.Outbound,6} {linha.NotScanned,11} {linha.Unavailable,8} {linha.Rejected,7}");
            atencao |= linha.QuotaBytes > 0 && linha.Bytes >= linha.QuotaBytes;
        }

        return atencao ? 1 : 0;
    }

    private static async Task<int> CheckAsync(AttachmentMaintenanceService servico, bool repair, TimeSpan orphanMinAge, Guid? tenantId, bool allowEmptyFolder, TextWriter output, CancellationToken cancellationToken)
    {
        var relatorio = await servico.CheckAsync(repair, orphanMinAge, tenantId, allowEmptyFolder, cancellationToken);
        await output.WriteLineAsync(repair ? "Verificação de consistência COM REPARO." : "Verificação de consistência (somente leitura: nada foi alterado).");
        await output.WriteLineAsync($"  Linhas disponíveis conferidas : {relatorio.Checked}");
        await output.WriteLineAsync($"  Íntegras                      : {relatorio.Ok}");
        await Linha(output, "Sem objeto no armazenamento", relatorio.MissingCount, relatorio.Missing, repair ? $"{relatorio.RepairedMissing} marcada(s) como 'lost'" : "use --repair --confirm para marcá-las como indisponíveis");
        await Linha(output, "Objeto ilegível (chave ausente/errada ou adulterado)", relatorio.UnreadableCount, relatorio.Unreadable, "NÃO é alterado: confira a chave de cifra do ambiente");
        await Linha(output, "Conteúdo diferente do registrado (tamanho ou SHA-256)", relatorio.MismatchCount, relatorio.Mismatch, "NÃO é alterado: o arquivo não é entregue enquanto não conferir");
        await Linha(output, "Objetos sem linha (órfãos)", relatorio.OrphanCount, relatorio.Orphans, repair ? $"{relatorio.RemovedOrphans} apagado(s) (os mais antigos que o prazo mínimo)" : "o reparo apaga os mais antigos que --orphan-days (padrão 1)");
        await Linha(output, "Em texto claro com a cifra ligada", relatorio.LegacyCount, relatorio.Legacy, "rode 'attachments encrypt-existing --confirm'");
        return relatorio.HasProblems ? 1 : 0;
    }

    private static async Task Linha(TextWriter output, string titulo, int total, List<string> amostra, string acao)
    {
        await output.WriteLineAsync($"  {titulo,-58}: {total}{(total > 0 ? $"  ({acao})" : string.Empty)}");
        foreach (var id in amostra)
        {
            await output.WriteLineAsync($"      {id}");
        }

        if (total > amostra.Count)
        {
            await output.WriteLineAsync($"      ... e mais {total - amostra.Count}");
        }
    }

    private static async Task<int> RescanAsync(AttachmentMaintenanceService servico, bool confirmed, Guid? tenantId, TextWriter output, CancellationToken cancellationToken)
    {
        var relatorio = await servico.RescanAsync(dryRun: !confirmed, tenantId, cancellationToken);
        await output.WriteLineAsync(confirmed ? "Verificação dos arquivos não verificados executada." : "Simulação (nada foi alterado; use --confirm para aplicar).");
        await output.WriteLineAsync($"  Candidatos: {relatorio.Candidates}; limpos: {relatorio.MarkedClean}; infectados (bloqueados e apagados com --confirm): {relatorio.Infected}; verificador indisponível: {relatorio.Unavailable}; ilegíveis: {relatorio.Unreadable}");
        return relatorio.Infected + relatorio.Unavailable + relatorio.Unreadable > 0 ? 1 : 0;
    }

    private static async Task<int> EncryptAsync(AttachmentMaintenanceService servico, bool confirmed, Guid? tenantId, TextWriter output, CancellationToken cancellationToken)
    {
        var relatorio = await servico.EncryptExistingAsync(dryRun: !confirmed, tenantId, cancellationToken);
        await output.WriteLineAsync(confirmed ? "Conversão para a cifra em repouso executada." : "Simulação (nada foi alterado; use --confirm para aplicar).");
        await output.WriteLineAsync($"  Em texto claro: {relatorio.Plaintext}; convertidos: {relatorio.Converted}; já cifrados: {relatorio.AlreadyEncrypted}; ilegíveis: {relatorio.Unreadable}");
        return relatorio.Unreadable > 0 ? 1 : 0;
    }

    private static string Verificador(AttachmentOptions opcoes) => opcoes.Scanner.Mode switch
    {
        AttachmentScannerMode.ClamAv => $"ClamAV em {opcoes.Scanner.Host}:{opcoes.Scanner.Port}{(opcoes.Scanner.Required ? "; EXIGIDO (só arquivo verificado como limpo abre ou é enviado)" : "; não exigido")}",
        _ => "nenhum (só a amostra de teste EICAR é detectada); os arquivos ficam NÃO VERIFICADOS",
    };

    internal static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{(bytes / (double)(1L << 30)).ToString("0.00", CultureInfo.InvariantCulture)} GiB",
        >= 1L << 20 => $"{(bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture)} MiB",
        >= 1L << 10 => $"{(bytes / (double)(1L << 10)).ToString("0.0", CultureInfo.InvariantCulture)} KiB",
        _ => $"{bytes} B",
    };

    private static async Task<string> SlugAsync(IServiceProvider root, Dictionary<Guid, string> cache, Guid id, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(id, out var conhecido))
        {
            return conhecido;
        }

        await using var escopo = root.CreateAsyncScope();
        var tenant = await escopo.ServiceProvider.GetRequiredService<ITenantDirectory>().FindByIdAsync(new TenantId(id), cancellationToken);
        var slug = tenant?.Slug ?? id.ToString("N")[..8];
        cache[id] = slug;
        return slug;
    }
}
