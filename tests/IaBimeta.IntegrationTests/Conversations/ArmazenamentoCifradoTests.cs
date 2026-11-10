using System.Security.Cryptography;
using System.Text;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// A cifra em repouso dos arquivos (ADR 0032), sem banco: AES-256-GCM com chave por empresa; o texto claro nunca vai ao disco; tenant, arquivo e chave são dado autenticado; chave ausente, errada, objeto
/// adulterado, truncado, trocado de lugar ou de outra empresa NUNCA devolvem conteúdo; objeto antigo em texto claro segue legível e é convertido no lugar; a configuração inválida falha sem repetir a chave.
/// </summary>
public sealed class ArmazenamentoCifradoTests : IDisposable
{
    private static readonly string ChaveA = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string ChaveB = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "iabimeta-cifra-" + Guid.NewGuid().ToString("N"));
    private readonly TenantId _a = new(Guid.NewGuid());
    private readonly TenantId _b = new(Guid.NewGuid());
    private static readonly byte[] Conteudo = Encoding.UTF8.GetBytes("Resultado do exame de Maria: hemoglobina 13,2 g/dL. Documento confidencial do laboratório.");

    public void Dispose()
    {
        if (Directory.Exists(_raiz))
        {
            Directory.Delete(_raiz, recursive: true);
        }
    }

    private FileSystemAttachmentContentStore Disco() => new(new AttachmentOptions { RootPath = _raiz });

    private static AttachmentEncryptionSettings Cifra(string ativa = "k1", params (string Id, string Chave)[] chaves) => new()
    {
        ActiveKeyId = ativa,
        Keys = chaves.Length == 0 ? new Dictionary<string, string> { ["k1"] = ChaveA } : chaves.ToDictionary(c => c.Id, c => c.Chave),
    };

    private EncryptedAttachmentContentStore Loja(AttachmentEncryptionSettings? configuracao = null) => new(Disco(), configuracao ?? Cifra());

    private string Caminho(TenantId tenant, Guid arquivo) => Path.Combine(_raiz, tenant.Value.ToString("N"), arquivo.ToString("N") + ".bin");

    private static async Task<byte[]?> LerAsync(IAttachmentContentStore loja, TenantId tenant, string chave)
    {
        await using var fluxo = await loja.OpenReadAsync(tenant, chave, default);
        if (fluxo is null)
        {
            return null;
        }

        using var memoria = new MemoryStream();
        await fluxo.CopyToAsync(memoria);
        return memoria.ToArray();
    }

    [Fact]
    public async Task O_conteudo_ida_e_volta_e_o_disco_nunca_tem_o_texto_claro_nem_o_nome_do_cliente()
    {
        var arquivo = Guid.NewGuid();
        var loja = Loja();

        var chave = await loja.WriteAsync(_a, arquivo, Conteudo, default);

        Assert.Equal(Conteudo, await LerAsync(loja, _a, chave));
        var noDisco = await File.ReadAllBytesAsync(Caminho(_a, arquivo));
        Assert.True(EncryptedAttachmentContentStore.HasEncryptedHeader(noDisco));
        Assert.Equal(-1, noDisco.AsSpan().IndexOf("hemoglobina"u8));
        Assert.Equal(-1, noDisco.AsSpan().IndexOf("Maria"u8));
        Assert.True(noDisco.Length > Conteudo.Length); // cabeçalho, nonce e tag
    }

    [Fact]
    public async Task Gravar_duas_vezes_o_mesmo_arquivo_gera_objetos_diferentes_pelo_nonce_e_os_dois_abrem()
    {
        var arquivo = Guid.NewGuid();
        var loja = Loja();

        var chave = await loja.WriteAsync(_a, arquivo, Conteudo, default);
        var primeiro = await File.ReadAllBytesAsync(Caminho(_a, arquivo));
        await loja.WriteAsync(_a, arquivo, Conteudo, default);
        var segundo = await File.ReadAllBytesAsync(Caminho(_a, arquivo));

        Assert.NotEqual(primeiro, segundo);
        Assert.Equal(Conteudo, await LerAsync(loja, _a, chave));
    }

    [Fact]
    public async Task O_objeto_de_uma_empresa_nao_abre_sob_outra_nem_trocado_para_outro_arquivo()
    {
        var arquivo = Guid.NewGuid();
        var outroArquivo = Guid.NewGuid();
        var loja = Loja();
        var chave = await loja.WriteAsync(_a, arquivo, Conteudo, default);
        await loja.WriteAsync(_a, outroArquivo, new byte[] { 1, 2, 3 }, default);

        // Copiar o objeto da empresa A para o caminho da empresa B (mesmo nome): a chave derivada e o dado autenticado são da empresa A.
        Directory.CreateDirectory(Path.GetDirectoryName(Caminho(_b, arquivo))!);
        File.Copy(Caminho(_a, arquivo), Caminho(_b, arquivo));
        await Assert.ThrowsAsync<AttachmentContentUnreadableException>(() => LerAsync(loja, _b, $"{_b.Value:N}/{arquivo:N}.bin"));

        // Copiar o objeto do arquivo X por cima do arquivo Y da mesma empresa: o identificador do arquivo também é autenticado.
        File.Copy(Caminho(_a, arquivo), Caminho(_a, outroArquivo), overwrite: true);
        await Assert.ThrowsAsync<AttachmentContentUnreadableException>(() => LerAsync(loja, _a, $"{_a.Value:N}/{outroArquivo:N}.bin"));
        Assert.Equal(Conteudo, await LerAsync(loja, _a, chave)); // o original continua abrindo

        // E a chave de armazenamento de outra empresa nunca chega ao objeto.
        Assert.Null(await LerAsync(loja, _b, chave));
    }

    [Theory]
    [InlineData("byte-do-texto-cifrado")]
    [InlineData("byte-da-tag")]
    [InlineData("byte-do-nonce")]
    [InlineData("truncado")]
    [InlineData("so-o-cabecalho")]
    public async Task Objeto_adulterado_ou_truncado_nunca_devolve_conteudo(string dano)
    {
        var arquivo = Guid.NewGuid();
        var loja = Loja();
        var chave = await loja.WriteAsync(_a, arquivo, Conteudo, default);
        var bytes = await File.ReadAllBytesAsync(Caminho(_a, arquivo));
        var cabecalho = 4 + 2 + "k1".Length;
        var danificado = dano switch
        {
            "byte-do-texto-cifrado" => Alterar(bytes, bytes.Length - 3),
            "byte-da-tag" => Alterar(bytes, cabecalho + 12 + 2),
            "byte-do-nonce" => Alterar(bytes, cabecalho + 1),
            "truncado" => bytes[..^5],
            _ => bytes[..(cabecalho + 3)],
        };
        await File.WriteAllBytesAsync(Caminho(_a, arquivo), danificado);

        await Assert.ThrowsAsync<AttachmentContentUnreadableException>(() => LerAsync(loja, _a, chave));
    }

    private static byte[] Alterar(byte[] bytes, int posicao)
    {
        var copia = (byte[])bytes.Clone();
        copia[posicao] ^= 0x01;
        return copia;
    }

    [Fact]
    public async Task Chave_errada_ou_ausente_nunca_devolve_conteudo_e_a_mensagem_nao_repete_a_chave()
    {
        var arquivo = Guid.NewGuid();
        var chave = await Loja().WriteAsync(_a, arquivo, Conteudo, default);

        // A MESMA identificação de chave com outro material: a autenticação falha.
        var errada = Loja(Cifra("k1", ("k1", ChaveB)));
        var erro = await Assert.ThrowsAsync<AttachmentContentUnreadableException>(() => LerAsync(errada, _a, chave));
        Assert.DoesNotContain(ChaveA, erro.Message);
        Assert.DoesNotContain(ChaveB, erro.Message);

        // A identificação da chave que cifrou não está configurada (a chave foi retirada do ambiente).
        var ausente = Loja(Cifra("k2", ("k2", ChaveB)));
        await Assert.ThrowsAsync<AttachmentContentUnreadableException>(() => LerAsync(ausente, _a, chave));

        // Sem nenhuma chave e com a cifra desligada: um objeto cifrado nunca é entregue como se fosse o arquivo.
        var semChave = new EncryptedAttachmentContentStore(Disco(), new AttachmentEncryptionSettings());
        await Assert.ThrowsAsync<AttachmentContentUnreadableException>(() => LerAsync(semChave, _a, chave));
    }

    [Fact]
    public async Task A_rotacao_simples_chave_nova_ativa_e_antigas_mantidas_para_decifrar()
    {
        var antigo = Guid.NewGuid();
        var novo = Guid.NewGuid();
        var chaveAntiga = await Loja(Cifra("k1", ("k1", ChaveA))).WriteAsync(_a, antigo, Conteudo, default);

        var rotacionada = Loja(Cifra("k2", ("k1", ChaveA), ("k2", ChaveB)));
        var chaveNova = await rotacionada.WriteAsync(_a, novo, Conteudo, default);

        Assert.Equal(Conteudo, await LerAsync(rotacionada, _a, chaveAntiga)); // cifrado com k1, ainda abre
        Assert.Equal(Conteudo, await LerAsync(rotacionada, _a, chaveNova)); // cifrado com k2
        var soComK1 = Loja(Cifra("k1", ("k1", ChaveA)));
        await Assert.ThrowsAsync<AttachmentContentUnreadableException>(() => LerAsync(soComK1, _a, chaveNova)); // k2 retirada: não abre
    }

    [Fact]
    public async Task Objeto_antigo_em_texto_claro_segue_legivel_e_e_convertido_no_lugar_uma_unica_vez()
    {
        var arquivo = Guid.NewGuid();
        var chave = await Disco().WriteAsync(_a, arquivo, Conteudo, default); // gravado antes da cifra existir
        var loja = Loja();

        Assert.Equal(Conteudo, await LerAsync(loja, _a, chave));
        Assert.True(await loja.EncryptInPlaceAsync(_a, chave, default));
        Assert.True(EncryptedAttachmentContentStore.HasEncryptedHeader(await File.ReadAllBytesAsync(Caminho(_a, arquivo))));
        Assert.Equal(Conteudo, await LerAsync(loja, _a, chave));

        Assert.False(await loja.EncryptInPlaceAsync(_a, chave, default)); // idempotente
        Assert.False(await loja.EncryptInPlaceAsync(_a, $"{_a.Value:N}/{Guid.NewGuid():N}.bin", default)); // inexistente
    }

    [Fact]
    public async Task Com_a_cifra_desligada_grava_em_texto_claro_e_converter_exige_a_cifra_ligada()
    {
        var arquivo = Guid.NewGuid();
        var semCifra = new EncryptedAttachmentContentStore(Disco(), new AttachmentEncryptionSettings());

        var chave = await semCifra.WriteAsync(_a, arquivo, Conteudo, default);

        Assert.Equal(Conteudo, await File.ReadAllBytesAsync(Caminho(_a, arquivo)));
        Assert.Equal(Conteudo, await LerAsync(semCifra, _a, chave));
        await Assert.ThrowsAsync<InvalidOperationException>(() => semCifra.EncryptInPlaceAsync(_a, chave, default));
    }

    [Fact]
    public async Task Apagar_e_listar_passam_direto_e_so_do_tenant_certo()
    {
        var arquivo = Guid.NewGuid();
        var loja = Loja();
        var chave = await loja.WriteAsync(_a, arquivo, Conteudo, default);

        await loja.DeleteAsync(_b, chave, default); // a chave de outra empresa nunca apaga
        Assert.True(File.Exists(Caminho(_a, arquivo)));

        var listados = new List<StoredAttachmentObject>();
        await foreach (var o in loja.ListOlderThanAsync(DateTimeOffset.MaxValue, default))
        {
            listados.Add(o);
        }

        Assert.Single(listados, o => o.TenantId == _a.Value && o.AttachmentId == arquivo);
        await loja.DeleteAsync(_a, chave, default);
        Assert.False(File.Exists(Caminho(_a, arquivo)));
    }

    [Fact]
    public async Task Objeto_que_o_sistema_operacional_recusa_ler_e_ilegivel_e_o_apagado_entre_a_conferencia_e_a_abertura_e_inexistente()
    {
        var arquivo = Guid.NewGuid();
        var disco = Disco();
        var chave = await disco.WriteAsync(_a, arquivo, Conteudo, default);

        // Outro processo (o antivírus do servidor, por exemplo) segura o arquivo: a leitura é recusada, e isso NÃO é um erro do servidor nem um arquivo inexistente.
        await using (var preso = new FileStream(Caminho(_a, arquivo), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var erro = await Assert.ThrowsAsync<AttachmentContentUnreadableException>(() => disco.OpenReadAsync(_a, chave, default));
            Assert.DoesNotContain(_raiz, erro.Message);
        }

        Assert.Equal(Conteudo, await LerAsync(disco, _a, chave)); // solto, o arquivo volta a abrir

        File.Delete(Caminho(_a, arquivo));
        Assert.Null(await disco.OpenReadAsync(_a, chave, default));
    }

    [Fact]
    public void A_configuracao_invalida_falha_sem_repetir_a_chave()
    {
        var curta = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var casos = new (AttachmentEncryptionSettings Configuracao, string Valor)[]
        {
            (new AttachmentEncryptionSettings { Required = true }, string.Empty),
            (new AttachmentEncryptionSettings { ActiveKeyId = "k1", Keys = { ["k1"] = curta } }, curta),
            (new AttachmentEncryptionSettings { ActiveKeyId = "k1", Keys = { ["k1"] = "isto-nao-e-base64!!" } }, "isto-nao-e-base64!!"),
            (new AttachmentEncryptionSettings { ActiveKeyId = "k9", Keys = { ["k1"] = ChaveA } }, ChaveA),
            (new AttachmentEncryptionSettings { ActiveKeyId = "k1", Keys = { ["k 1/../x"] = ChaveA } }, ChaveA),
        };

        foreach (var (configuracao, valor) in casos)
        {
            var erro = Assert.Throws<InvalidOperationException>(configuracao.Validate);
            if (valor.Length > 0)
            {
                Assert.DoesNotContain(valor, erro.Message);
            }
        }

        new AttachmentEncryptionSettings { ActiveKeyId = "k1", Keys = { ["k1"] = ChaveA }, Required = true }.Validate();
        new AttachmentEncryptionSettings().Validate(); // sem nada: cifra desligada, e é válido (desenvolvimento)
    }

    [Fact]
    public void O_AttachmentOptions_valida_cota_verificador_e_cifra_juntos()
    {
        Assert.Throws<InvalidOperationException>(() => new AttachmentOptions { TenantQuotaBytes = -1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AttachmentOptions { Scanner = { Required = true } }.Validate()); // exigir sem verificador
        Assert.Throws<InvalidOperationException>(() => new AttachmentOptions { Scanner = { Mode = AttachmentScannerMode.ClamAv, Port = 0 } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AttachmentOptions { Encryption = { Required = true } }.Validate());
        new AttachmentOptions { TenantQuotaBytes = 0, Scanner = { Mode = AttachmentScannerMode.ClamAv, Required = true }, Encryption = { ActiveKeyId = "k1", Keys = { ["k1"] = ChaveA }, Required = true } }.Validate();
    }
}
