using System.Text;
using System.Text.RegularExpressions;

namespace IaBimeta.IntegrationTests.Deploy;

/// <summary>
/// As invariantes de SEGURANÇA e de estrutura dos artefatos de implantação (ADR 0033: Docker Compose, Dockerfiles, Caddyfile, modelos de variáveis e scripts) conferidas por TEXTO, sem Docker: este computador não tem
/// contêineres, então estas verificações protegem o que um erro de edição quebraria em silêncio (uma porta publicada sem querer, um segredo no modelo, um script com acento ou com a frase na linha de comando). Não
/// substituem o teste de aceitação na VM (<c>deploy/scripts/aceitacao.sh</c>), que prova o comportamento de verdade.
/// </summary>
public sealed class ArtefatosDeImplantacaoTests
{
    private static readonly string Raiz = ProcurarRaiz();
    private static string Deploy => Path.Combine(Raiz, "deploy");

    private static string ProcurarRaiz()
    {
        var pasta = new DirectoryInfo(AppContext.BaseDirectory);
        while (pasta is not null && !File.Exists(Path.Combine(pasta.FullName, "CLAUDE.md")))
        {
            pasta = pasta.Parent;
        }

        return pasta?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }

    private static string Ler(string relativo) => File.ReadAllText(Path.Combine(Deploy, relativo.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Os blocos de serviço do Compose (a chave de dois espaços sob <c>services:</c> e as linhas seguintes até a próxima chave do mesmo nível).</summary>
    private static Dictionary<string, string> Servicos()
    {
        var linhas = Ler("docker-compose.yml").Split('\n');
        var servicos = new Dictionary<string, string>();
        var dentro = false;
        string? atual = null;
        var bloco = new StringBuilder();
        foreach (var linha in linhas.Select(l => l.TrimEnd('\r')))
        {
            if (linha == "services:")
            {
                dentro = true;
                continue;
            }

            if (!dentro)
            {
                continue;
            }

            if (linha.Length > 0 && !char.IsWhiteSpace(linha[0]) && !linha.StartsWith('#'))
            {
                break; // outra chave de topo (networks, volumes)
            }

            var chave = Regex.Match(linha, @"^  ([a-z][a-z0-9_-]*):\s*$");
            if (chave.Success)
            {
                if (atual is not null)
                {
                    servicos[atual] = bloco.ToString();
                }

                atual = chave.Groups[1].Value;
                bloco.Clear();
                continue;
            }

            bloco.AppendLine(linha);
        }

        if (atual is not null)
        {
            servicos[atual] = bloco.ToString();
        }

        return servicos;
    }

    private static IEnumerable<string> ArquivosDeScript() =>
        Directory.EnumerateFiles(Path.Combine(Deploy, "scripts"), "*.sh").Concat(Directory.EnumerateFiles(Path.Combine(Deploy, "tests"), "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".sh", StringComparison.Ordinal) || Path.GetFileName(f) == "docker"));

    // O Compose

    [Fact]
    public void O_Compose_tem_os_cinco_servicos_e_so_o_proxy_publica_portas_80_e_443()
    {
        var servicos = Servicos();

        // Os cinco da pilha mais o servico de IA local, OPCIONAL (perfil ia-local, ADR 0034): sem o perfil ele nao sobe, e nunca publica porta.
        Assert.Equal(["api", "clamav", "db", "ollama", "web", "worker"], servicos.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("profiles: [\"ia-local\"]", servicos["ollama"], StringComparison.Ordinal);
        foreach (var (nome, bloco) in servicos.Where(s => s.Key != "web"))
        {
            Assert.DoesNotContain("ports:", bloco, StringComparison.Ordinal);
            Assert.True(!bloco.Contains("expose:", StringComparison.Ordinal) || nome == "api", $"{nome} não deve declarar portas");
        }

        var portas = Regex.Matches(servicos["web"], "\"(\\d+:\\d+)\"").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["443:443", "80:80"], portas);
    }

    [Fact]
    public void Todo_servico_reinicia_sozinho_e_roda_os_logs_com_rotacao_e_o_banco_e_o_api_tem_verificacao_de_saude()
    {
        var servicos = Servicos();

        foreach (var (nome, bloco) in servicos)
        {
            Assert.True(Regex.IsMatch(bloco, @"restart:\s*unless-stopped"), $"{nome} sem restart: unless-stopped");
            Assert.True(Regex.IsMatch(bloco, @"logging:\s*\*logging"), $"{nome} sem a rotação de logs");
        }

        Assert.Contains("healthcheck:", servicos["db"], StringComparison.Ordinal);
        Assert.Contains("pg_isready -h 127.0.0.1", servicos["db"], StringComparison.Ordinal); // por TCP: o servidor temporário da inicialização só escuta no socket
        Assert.Contains("healthcheck:", servicos["api"], StringComparison.Ordinal);
        Assert.Contains("nPING", servicos["clamav"], StringComparison.Ordinal);
        Assert.Contains("service_healthy", servicos["api"], StringComparison.Ordinal); // o Api espera o banco
        Assert.Contains("service_healthy", servicos["web"], StringComparison.Ordinal); // o proxy espera o Api
        Assert.DoesNotContain("service_healthy", servicos["clamav"], StringComparison.Ordinal);
    }

    [Fact]
    public void Os_segredos_da_aplicacao_so_chegam_ao_Api_e_ao_Worker_e_a_producao_exige_verificacao_e_cifra()
    {
        var servicos = Servicos();
        var texto = Ler("docker-compose.yml");

        foreach (var nome in new[] { "api", "worker" })
        {
            Assert.Contains("env_file: [app.env]", servicos[nome], StringComparison.Ordinal);
            Assert.Contains("<<: *app-environment", servicos[nome], StringComparison.Ordinal);
        }

        foreach (var nome in new[] { "db", "clamav", "web" })
        {
            Assert.DoesNotContain("app.env", servicos[nome], StringComparison.Ordinal);
            Assert.DoesNotContain("backup.env", servicos[nome], StringComparison.Ordinal);
        }

        Assert.DoesNotContain("backup.env", texto.Replace("backup.env, que SO os scripts do host leem", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        var anexo = Regex.Match(texto, @"x-app-environment: &app-environment\n(?<corpo>(?:  .*\n)+)").Groups["corpo"].Value;
        Assert.Contains("ASPNETCORE_ENVIRONMENT: Production", anexo, StringComparison.Ordinal);
        Assert.Contains("Conversations__Attachments__Scanner__Required: \"true\"", anexo, StringComparison.Ordinal);
        Assert.Contains("Conversations__Attachments__Encryption__Required: \"true\"", anexo, StringComparison.Ordinal);
        Assert.Contains("Conversations__Attachments__Scanner__Mode: ClamAv", anexo, StringComparison.Ordinal);
        Assert.DoesNotContain("Tenancy__Development", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("PublicListenerPort", texto, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rede_do_proxy_e_a_interna_o_banco_e_o_verificador_nao_ficam_na_rede_do_proxy_e_o_Api_confia_so_na_sub_rede_dele()
    {
        var servicos = Servicos();

        Assert.Contains("networks: [back]", servicos["db"], StringComparison.Ordinal);
        Assert.Contains("networks: [back]", servicos["clamav"], StringComparison.Ordinal);
        Assert.Contains("networks: [back]", servicos["worker"], StringComparison.Ordinal);
        Assert.Contains("networks: [edge, back]", servicos["api"], StringComparison.Ordinal);
        Assert.Contains("edge:", servicos["web"], StringComparison.Ordinal);
        Assert.DoesNotContain("back", servicos["web"].Replace("backup", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("ForwardedHeaders__Enabled: \"true\"", servicos["api"], StringComparison.Ordinal);
        Assert.Contains("ForwardedHeaders__KnownNetworks__0: ${EDGE_SUBNET:-172.29.89.0/24}", servicos["api"], StringComparison.Ordinal);
        Assert.DoesNotContain("ForwardedHeaders", servicos["worker"], StringComparison.Ordinal);
    }

    [Fact]
    public void Os_dados_ficam_em_volumes_nomeados_o_banco_o_verificador_os_anexos_e_o_estado_do_certificado()
    {
        var texto = Ler("docker-compose.yml");
        var volumes = texto[texto.IndexOf("\nvolumes:", StringComparison.Ordinal)..];

        foreach (var volume in new[] { "pgdata", "anexos", "clamdb", "caddy_data", "caddy_config" })
        {
            Assert.Contains($"  {volume}: {{}}", volumes, StringComparison.Ordinal);
        }

        Assert.Contains("pgdata:/var/lib/postgresql/data", texto, StringComparison.Ordinal);
        Assert.Contains("clamdb:/var/lib/clamav", texto, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(texto, @"anexos:/var/lib/iabimeta/anexos").Count); // Api e Worker, a MESMA pasta
        Assert.Contains("caddy_data:/data", texto, StringComparison.Ordinal);
    }

    // Os modelos de variáveis: nenhum segredo

    [Theory]
    [InlineData(".env.example")]
    [InlineData("app.env.example")]
    [InlineData("backup.env.example")]
    public void Os_modelos_de_variaveis_nao_trazem_nenhum_segredo_nem_valor_real(string arquivo)
    {
        var segredo = new Regex(@"PASSWORD|Keys__|HashKey|PASSPHRASE|WEBHOOK_URL|ACME_EMAIL|^DOMAIN$", RegexOptions.CultureInvariant);

        foreach (var linha in Ler(arquivo).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var nome = linha[..linha.IndexOf('=', StringComparison.Ordinal)];
            var valor = linha[(linha.IndexOf('=', StringComparison.Ordinal) + 1)..];
            if (segredo.IsMatch(nome))
            {
                Assert.True(valor.Length == 0, $"{arquivo}: {nome} deve ficar VAZIO no modelo");
            }

            Assert.DoesNotMatch(@"^[A-Za-z0-9+/]{43}=$", valor); // nada com cara de chave Base64 de 32 bytes
        }
    }

    [Fact]
    public void O_gitignore_protege_os_arquivos_de_segredos_reais_e_nao_os_modelos()
    {
        var gitignore = File.ReadAllText(Path.Combine(Raiz, ".gitignore"));

        Assert.Matches(@"(?m)^\.env\s*$", gitignore);
        Assert.Contains("deploy/app.env", gitignore, StringComparison.Ordinal);
        Assert.Contains("deploy/backup.env", gitignore, StringComparison.Ordinal);
        var regras = gitignore.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));
        Assert.DoesNotContain(regras, r => r.Contains("example", StringComparison.OrdinalIgnoreCase)); // os modelos *.example SÃO versionados
    }

    // Os Dockerfiles e o proxy

    [Fact]
    public void As_imagens_do_Api_e_do_Worker_rodam_sem_root_com_ponto_de_entrada_dos_comandos_de_operacao_e_sem_segredo()
    {
        var dockerfile = Ler("Dockerfile");

        Assert.Contains("USER ${APP_UID:-1654}", dockerfile, StringComparison.Ordinal); // nunca root
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"IaBimeta.Api.dll\"]", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"IaBimeta.Worker.dll\"]", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ARG DOTNET_TAG=10.0-noble", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?im)^\s*(ENV|ARG)\s+\S*(PASSWORD|KEY|SECRET|TOKEN)", dockerfile);
        Assert.DoesNotContain("COPY .env", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("app.env", dockerfile, StringComparison.Ordinal);
        Assert.Contains("deploy", Ler("Dockerfile.dockerignore"), StringComparison.Ordinal); // o contexto do .NET não leva os segredos
        Assert.Contains("**/bin", Ler("Dockerfile.dockerignore"), StringComparison.Ordinal);
    }

    [Fact]
    public void O_proxy_encaminha_o_webhook_inteiro_a_API_sem_o_prefixo_so_a_API_e_serve_a_interface_sem_admin_nem_certificado_inseguro()
    {
        var caddy = Ler("web/Caddyfile");

        Assert.Contains("{$DOMAIN} {", caddy, StringComparison.Ordinal);
        Assert.Contains("handle /webhooks/whatsapp/* {", caddy, StringComparison.Ordinal);
        Assert.Contains("handle /api/* {", caddy, StringComparison.Ordinal);
        Assert.Contains("uri strip_prefix /api", caddy, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(caddy, @"reverse_proxy api:8080").Count);
        Assert.Contains("admin off", caddy, StringComparison.Ordinal);
        Assert.Contains("try_files {path} /index.html", caddy, StringComparison.Ordinal);
        Assert.DoesNotContain("tls internal", caddy, StringComparison.Ordinal);
        Assert.DoesNotContain("insecure", caddy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trusted_proxies", caddy, StringComparison.Ordinal); // nada na frente do proxy: os X-Forwarded-* do cliente são IGNORADOS
        Assert.DoesNotContain("reverse_proxy db", caddy, StringComparison.Ordinal);
        Assert.DoesNotContain("clamav", caddy, StringComparison.Ordinal);
        Assert.Contains("max_size", caddy, StringComparison.Ordinal);
    }

    /// <summary>
    /// E1-R: o registro de acesso do Caddy NÃO pode conter cabeçalho de requisição algum (o Caddy oculta Authorization e Cookie sozinho, mas não X-IaB-Signature, X-IaB-Nonce, X-IaB-Key-Id nem um cabeçalho inventado). Aqui só o TEXTO da
    /// configuração é conferido; que o Caddy implantado de fato se comporta assim é provado por <c>aceitacao.sh</c> na VM (requisição com valores-canário e busca nos logs).
    /// </summary>
    [Fact]
    public void O_registro_de_acesso_do_proxy_apaga_todos_os_cabecalhos_da_requisicao_e_a_aceitacao_prova_com_um_canario()
    {
        var caddy = Ler("web/Caddyfile");
        var log = Regex.Match(caddy, @"\tlog \{(?<corpo>.*?)\n\t\}", RegexOptions.Singleline);

        Assert.True(log.Success, "O Caddyfile precisa de um bloco log.");
        Assert.Contains("format filter {", log.Groups["corpo"].Value, StringComparison.Ordinal);
        Assert.Contains("wrap json", log.Groups["corpo"].Value, StringComparison.Ordinal);
        Assert.Matches(@"request>headers\s+delete", log.Groups["corpo"].Value);
        Assert.DoesNotContain("format json", log.Groups["corpo"].Value, StringComparison.Ordinal); // o formato sem filtro registraria os cabeçalhos
        Assert.DoesNotContain("log_credentials", caddy, StringComparison.Ordinal);

        var aceitacao = Ler("scripts/aceitacao.sh");
        Assert.Contains("X-IaB-Signature", aceitacao, StringComparison.Ordinal);
        Assert.Contains("canario", aceitacao, StringComparison.Ordinal);
    }

    [Fact]
    public void O_papel_do_banco_da_aplicacao_nao_e_superusuario_e_a_senha_entra_como_literal_citado()
    {
        var init = Ler("postgres/init/01-app-role.sh");

        Assert.Contains("NOSUPERUSER NOCREATEDB NOCREATEROLE", init, StringComparison.Ordinal);
        Assert.Contains("PASSWORD :'senha'", init, StringComparison.Ordinal);
        Assert.Contains("OWNER iabimeta", init, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPERUSER'", init.Replace("NOSUPERUSER", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // Os scripts

    [Fact]
    public void Os_scripts_sao_ASCII_com_fim_de_linha_LF_e_modo_estrito()
    {
        var arquivos = ArquivosDeScript().Append(Path.Combine(Deploy, "postgres", "init", "01-app-role.sh")).ToList();
        Assert.True(arquivos.Count >= 14, $"esperava os scripts de operação, achou {arquivos.Count}");

        foreach (var arquivo in arquivos)
        {
            var bytes = File.ReadAllBytes(arquivo);
            var nome = Path.GetRelativePath(Deploy, arquivo);
            Assert.True(bytes.All(b => b < 0x80), $"{nome}: só ASCII");
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.True(bytes.Length > 0 && bytes[^1] == (byte)'\n', $"{nome}: termina com nova linha");
            var texto = Encoding.ASCII.GetString(bytes);
            Assert.StartsWith("#!", texto, StringComparison.Ordinal);

            // O modo estrito vale para todo script de operação (a biblioteca é só "sourced" e os auxiliares do teste têm a regra deles).
            if (!nome.StartsWith("tests", StringComparison.Ordinal) && Path.GetFileName(arquivo) != "lib.sh")
            {
                Assert.Matches(@"(?m)^set -(euo|uo|eu) pipefail$|^set -eu$|^set -e$", texto);
            }
        }
    }

    [Fact]
    public void Nenhum_script_passa_segredo_pela_linha_de_comando_nem_executa_texto_de_arquivo_de_configuracao()
    {
        foreach (var arquivo in ArquivosDeScript())
        {
            var nome = Path.GetRelativePath(Deploy, arquivo);
            var texto = File.ReadAllText(arquivo);
            var codigo = string.Join('\n', texto.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

            Assert.DoesNotMatch(@"--passphrase(?!-fd|-file)\b", codigo); // a frase só por descritor de arquivo
            Assert.DoesNotMatch(@"(?m)^\s*eval\b", codigo);
            Assert.DoesNotMatch(@"(?m)^\s*(source|\.)\s+[""']?\$?\{?[A-Za-z_]*(ENV|env)\b", codigo); // nunca "source" de .env: os valores são lidos, não executados
            Assert.True(!codigo.Contains("docker inspect") || !codigo.Contains("Config.Env"), $"{nome}: não leia as variáveis do contêiner");
        }
    }

    [Fact]
    public void Os_modelos_do_systemd_apontam_para_a_pasta_do_deploy_por_marcador_e_o_agendador_os_substitui()
    {
        foreach (var unidade in new[] { "iabimeta-backup.service", "iabimeta-monitor.service" })
        {
            Assert.Contains("@DEPLOY_DIR@/scripts/", Ler($"systemd/{unidade}"), StringComparison.Ordinal);
        }

        Assert.Contains("OnCalendar=", Ler("systemd/iabimeta-backup.timer"), StringComparison.Ordinal);
        Assert.Contains("Persistent=true", Ler("systemd/iabimeta-backup.timer"), StringComparison.Ordinal);
        Assert.Contains("OnUnitActiveSec=5min", Ler("systemd/iabimeta-monitor.timer"), StringComparison.Ordinal);
        Assert.Contains("@DEPLOY_DIR@", Ler("scripts/agendar.sh"), StringComparison.Ordinal);
    }
}
