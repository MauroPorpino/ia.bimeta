namespace IaBimeta.Conversations.Domain;

/// <summary>O mecanismo de varredura de código malicioso (ADR 0032).</summary>
public enum AttachmentScannerMode
{
    /// <summary>Nenhum mecanismo externo. A detecção embutida da amostra de teste padrão (EICAR) continua valendo, mas o arquivo NÃO é considerado verificado.</summary>
    None = 0,

    /// <summary>O daemon do ClamAV (<c>clamd</c>) por TCP, pelo protocolo <c>INSTREAM</c>: o conteúdo vai por soquete, sem arquivo temporário. É o padrão do piloto (funciona em Linux e Windows).</summary>
    ClamAv = 1,
}

/// <summary>
/// A varredura de arquivos (<c>Conversations:Attachments:Scanner</c>, ADR 0032). Com <see cref="Required"/>, SÓ o arquivo que o verificador declarou limpo vira disponível, e só o limpo abre ou é enviado ao Sender:
/// verificador fora do ar nunca libera arquivo (o recebido fica pendente e é tentado de novo; o enviado é recusado). Sem <see cref="Required"/>, a indisponibilidade do verificador deixa o arquivo
/// passar como "não verificado" (o padrão do desenvolvimento).
/// </summary>
public sealed class AttachmentScannerSettings
{
    public AttachmentScannerMode Mode { get; set; } = AttachmentScannerMode.None;

    /// <summary>O endereço do <c>clamd</c>. Padrão: o laço local.</summary>
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 3310;

    /// <summary>O tempo máximo de uma varredura (conexão, envio e resposta). Esgotado, o resultado é "indisponível", nunca "limpo".</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Exige a verificação: sem verificador ou fora do ar, o arquivo não é liberado.</summary>
    public bool Required { get; set; }

    /// <exception cref="InvalidOperationException">A configuração é inválida ou contraditória.</exception>
    public void Validate()
    {
        if (Required && Mode == AttachmentScannerMode.None)
        {
            throw new InvalidOperationException("Conversations:Attachments:Scanner:Required exige um verificador (Mode = ClamAv): sem ele nenhum arquivo poderia ser liberado.");
        }

        if (Mode == AttachmentScannerMode.ClamAv && (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535 || Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromMinutes(10)))
        {
            throw new InvalidOperationException("Conversations:Attachments:Scanner é inválido: Host, Port (1 a 65535) e Timeout (1 s a 10 min) são obrigatórios com o ClamAV.");
        }
    }
}

/// <summary>
/// A cifra em repouso dos arquivos (<c>Conversations:Attachments:Encryption</c>, ADR 0032): AES-256-GCM, o mesmo mecanismo e as mesmas convenções de chave das credenciais do Channels (Base64 de 32 bytes, identificador
/// da chave ativa, chaves antigas mantidas para decifrar). As chaves vêm do ambiente, NUNCA do repositório nem do banco. Com <see cref="Required"/>, a partida falha sem chave ativa; sem ele, sem chave ativa os
/// arquivos novos ficam em texto claro (o padrão do desenvolvimento). Perder a chave é perder os arquivos: ela é guardada junto da frase de cifra do backup, fora da máquina.
/// </summary>
public sealed class AttachmentEncryptionSettings
{
    public const int KeySizeBytes = 32;

    /// <summary>O identificador da chave que cifra os arquivos novos. Sem ele, nada é cifrado.</summary>
    public string? ActiveKeyId { get; set; }

    /// <summary>As chaves conhecidas, por identificador, em Base64 (32 bytes cada). Decifrar aceita qualquer uma delas.</summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Exige a cifra: a partida falha sem chave ativa válida.</summary>
    public bool Required { get; set; }

    /// <summary>Se a cifra está ligada para os arquivos novos.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ActiveKeyId);

    /// <exception cref="InvalidOperationException">A configuração é inválida (a mensagem nunca repete o valor de uma chave).</exception>
    public void Validate()
    {
        if (Required && !Enabled)
        {
            throw new InvalidOperationException("Conversations:Attachments:Encryption:Required exige uma chave ativa (ActiveKeyId e a chave em Keys).");
        }

        foreach (var (id, base64) in Keys)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            {
                throw new InvalidOperationException("Conversations:Attachments:Encryption:Keys tem um identificador de chave inválido (letras, números, hífen e sublinhado; até 32).");
            }

            if (!TryReadKey(base64, out _))
            {
                throw new InvalidOperationException($"Conversations:Attachments:Encryption:Keys:{id} não é um Base64 de {KeySizeBytes} bytes.");
            }
        }

        if (Enabled && !Keys.ContainsKey(ActiveKeyId!))
        {
            throw new InvalidOperationException("Conversations:Attachments:Encryption:ActiveKeyId aponta para uma chave que não está em Keys.");
        }
    }

    /// <summary>Lê uma chave de 32 bytes em Base64; nunca lança.</summary>
    public static bool TryReadKey(string? base64, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        try
        {
            var lida = Convert.FromBase64String(base64);
            if (lida.Length != KeySizeBytes)
            {
                return false;
            }

            key = lida;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
