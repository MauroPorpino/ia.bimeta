using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// O armazenamento privado dos arquivos de mídia no sistema de arquivos (ADR 0028), sem banco: a chave é gerada, o tenant é conferido em toda leitura, uma chave adulterada nunca sai da pasta raiz, a gravação é
/// atômica e idempotente por arquivo e o conteúdo só sai pela leitura (não há URL).
/// </summary>
public sealed class ArmazenamentoDeAnexosTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "iabimeta-armazenamento-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemAttachmentContentStore _loja;
    private readonly TenantId _a = new(Guid.NewGuid());
    private readonly TenantId _b = new(Guid.NewGuid());

    public ArmazenamentoDeAnexosTests()
    {
        _loja = new FileSystemAttachmentContentStore(new AttachmentOptions { RootPath = _raiz });
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz))
        {
            Directory.Delete(_raiz, recursive: true);
        }
    }

    private static async Task<byte[]> LerAsync(Stream? fluxo)
    {
        Assert.NotNull(fluxo);
        await using (fluxo)
        {
            using var memoria = new MemoryStream();
            await fluxo.CopyToAsync(memoria);
            return memoria.ToArray();
        }
    }

    [Fact]
    public async Task Guarda_com_chave_opaca_por_tenant_e_arquivo_e_devolve_os_mesmos_bytes()
    {
        var arquivo = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var chave = await _loja.WriteAsync(_a, arquivo, bytes, default);

        Assert.Equal($"{_a.Value:N}/{arquivo:N}.bin", chave);
        Assert.Equal(bytes, await LerAsync(await _loja.OpenReadAsync(_a, chave, default)));
        Assert.True(File.Exists(Path.Combine(_raiz, _a.Value.ToString("N"), arquivo.ToString("N") + ".bin")));
    }

    [Fact]
    public async Task Outro_tenant_nunca_le_o_arquivo_mesmo_com_a_chave_certa_na_mao()
    {
        var chave = await _loja.WriteAsync(_a, Guid.NewGuid(), new byte[] { 9, 9, 9 }, default);

        Assert.Null(await _loja.OpenReadAsync(_b, chave, default));
        Assert.Null(await _loja.OpenReadAsync(_b, $"{_b.Value:N}/{chave.Split('/')[1]}", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../fora.bin")]
    [InlineData("..\\fora.bin")]
    [InlineData("../../Windows/win.ini")]
    [InlineData("0123456789abcdef0123456789abcdef/../../x.bin")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("0123456789abcdef0123456789abcdef/0123456789abcdef0123456789abcdef.bin.parcial")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF/0123456789ABCDEF0123456789ABCDEF.bin")]
    public async Task Chave_adulterada_ou_fora_da_forma_gerada_e_inexistente_e_nunca_sai_da_raiz(string chave)
    {
        // Um arquivo FORA da raiz que a chave tentaria alcançar.
        var fora = Path.Combine(Path.GetDirectoryName(_raiz)!, "fora.bin");
        await File.WriteAllBytesAsync(fora, [7, 7, 7]);
        try
        {
            Assert.Null(await _loja.OpenReadAsync(_a, chave, default));
        }
        finally
        {
            File.Delete(fora);
        }
    }

    [Fact]
    public async Task Repetir_a_gravacao_regrava_o_mesmo_objeto_e_nao_deixa_arquivo_temporario()
    {
        var arquivo = Guid.NewGuid();

        var primeira = await _loja.WriteAsync(_a, arquivo, new byte[] { 1 }, default);
        var segunda = await _loja.WriteAsync(_a, arquivo, new byte[] { 1 }, default);

        Assert.Equal(primeira, segunda);
        var pasta = Path.Combine(_raiz, _a.Value.ToString("N"));
        Assert.Single(Directory.EnumerateFiles(pasta));
        Assert.Empty(Directory.EnumerateFiles(pasta, "*.parcial"));
    }

    [Fact]
    public async Task Gravacoes_simultaneas_do_mesmo_arquivo_terminam_com_um_objeto_inteiro()
    {
        var arquivo = Guid.NewGuid();
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _loja.WriteAsync(_a, arquivo, bytes, default)));

        Assert.Equal(bytes, await LerAsync(await _loja.OpenReadAsync(_a, $"{_a.Value:N}/{arquivo:N}.bin", default)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_raiz, _a.Value.ToString("N")), "*.parcial"));
    }

    [Fact]
    public async Task Arquivo_inexistente_e_nulo_e_o_identificador_vazio_e_recusado()
    {
        Assert.Null(await _loja.OpenReadAsync(_a, $"{_a.Value:N}/{Guid.NewGuid():N}.bin", default));
        await Assert.ThrowsAsync<ArgumentException>(() => _loja.WriteAsync(_a, Guid.Empty, new byte[] { 1 }, default));
    }

    [Fact]
    public void A_pasta_padrao_fica_nos_dados_locais_do_aplicativo_e_nunca_numa_pasta_servida()
    {
        var padrao = new FileSystemAttachmentContentStore(new AttachmentOptions());

        Assert.Equal(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IaBimeta", "attachments")), padrao.Root);
        Assert.DoesNotContain("wwwroot", padrao.Root, StringComparison.OrdinalIgnoreCase);
    }
}
