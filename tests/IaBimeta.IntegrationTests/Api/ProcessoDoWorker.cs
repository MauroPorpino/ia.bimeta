using System.Collections.Concurrent;
using System.Diagnostics;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O Worker como PROCESSO do sistema operacional (<c>dotnet IaBimeta.Worker.dll</c>), com a configuração por variáveis de ambiente (o que a implantação faz). Serve a ensaios de queda: <see cref="Matar"/> é um kill da
/// árvore de processos, sem parada graciosa. O binário vem da saída de compilação do projeto do Worker (a mesma da solução).
/// </summary>
internal sealed class ProcessoDoWorker
{
    private readonly Process _processo;
    private readonly ConcurrentQueue<string> _saida = new();

    private ProcessoDoWorker(Process processo)
    {
        _processo = processo;
    }

    public bool Saiu => _processo.HasExited;

    /// <summary>As últimas linhas de saída do processo (para diagnóstico quando um teste falha).</summary>
    public string Saida => string.Join('\n', _saida.TakeLast(40));

    public static ProcessoDoWorker Iniciar(IReadOnlyDictionary<string, string?> configuracao)
    {
        var dll = LocalizarDll();
        var informacao = new ProcessStartInfo("dotnet", $"\"{dll}\" --environment=Development")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };

        // A configuração vai pelo ambiente do PROCESSO FILHO (a chave "A:B" vira "A__B"): nada do ambiente do desenvolvedor é herdado além do que o próprio dotnet precisa.
        foreach (var (chave, valor) in configuracao)
        {
            informacao.Environment[chave.Replace(":", "__")] = valor ?? string.Empty;
        }

        // Um user-secret de ConnectionStrings:<módulo> (o do desenvolvedor aponta o banco de DESENVOLVIMENTO) vale mais que ConnectionStrings:Default: o processo filho recebe a mesma conexão em todas as chaves.
        if (configuracao.TryGetValue("ConnectionStrings:Default", out var conexao) && conexao is not null)
        {
            foreach (var modulo in new[] { "Tenancy", "Audit", "Identity", "Conversations", "Channels", "ContactCenter", "AiGateway", "Agents" })
            {
                informacao.Environment[$"ConnectionStrings__{modulo}"] = conexao;
            }
        }

        var processo = new ProcessoDoWorker(Process.Start(informacao) ?? throw new InvalidOperationException("O Worker não iniciou."));
        processo._processo.OutputDataReceived += (_, e) => processo.Guardar(e.Data);
        processo._processo.ErrorDataReceived += (_, e) => processo.Guardar(e.Data);
        processo._processo.BeginOutputReadLine();
        processo._processo.BeginErrorReadLine();
        return processo;
    }

    /// <summary>O kill do sistema operacional na árvore inteira do processo: a queda sem aviso.</summary>
    public void Matar()
    {
        try
        {
            if (!_processo.HasExited)
            {
                _processo.Kill(entireProcessTree: true);
                _processo.WaitForExit(15_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Já saiu.
        }
    }

    private void Guardar(string? linha)
    {
        if (!string.IsNullOrEmpty(linha))
        {
            _saida.Enqueue(linha);
        }
    }

    private static string LocalizarDll()
    {
        // O teste roda em <repo>/tests/IaBimeta.IntegrationTests/bin/<Configuração>/net10.0/: o Worker compilado fica em <repo>/backend/host/IaBimeta.Worker/bin/<Configuração>/net10.0/.
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        var configuracao = diretorio.Parent?.Name ?? "Debug";
        var plataforma = diretorio.Name;
        while (diretorio is not null && !Directory.Exists(Path.Combine(diretorio.FullName, "backend", "host", "IaBimeta.Worker")))
        {
            diretorio = diretorio.Parent;
        }

        var dll = diretorio is null ? null : Path.Combine(diretorio.FullName, "backend", "host", "IaBimeta.Worker", "bin", configuracao, plataforma, "IaBimeta.Worker.dll");
        return dll is not null && File.Exists(dll) ? dll : throw new FileNotFoundException("O Worker compilado não foi encontrado (compile a solução antes).", dll);
    }
}
