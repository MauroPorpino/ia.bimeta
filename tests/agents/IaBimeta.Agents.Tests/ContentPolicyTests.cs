using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// A política do conteúdo pendente (CONVERSATION-LIFECYCLE.md §10, MED-1 e MED-2): o modelo nunca vê conteúdo que não está pronto; dentro da espera adicional por tipo o turno espera; vencida (ou com a
/// falha definitiva), a política do tipo vale para o GRUPO inteiro: só áudio recebe a resposta informativa, e qualquer outro tipo vai ao atendente.
/// </summary>
public sealed class ContentPolicyTests
{
    private static readonly DateTimeOffset Agora = Snapshots.T0.AddMinutes(5);
    private static readonly AgentsOptions Opcoes = new();

    private static SnapshotMessage Sem(MessageKind tipo, ContentState estado, long seq = 2) => Snapshots.Customer(seq, null, tipo, estado);

    private static SnapshotMessage Texto(long seq = 1) => Snapshots.Customer(seq, "texto pronto");

    private static ContentVerdict Avaliar(IReadOnlyList<SnapshotMessage> grupo, DateTimeOffset? espera = null, AgentsOptions? opcoes = null, DateTimeOffset? agora = null) =>
        ContentPolicy.Evaluate(grupo, espera, agora ?? Agora, opcoes ?? Opcoes).Verdict;

    // Proceed

    [Fact]
    public void Todo_o_conteudo_pronto_segue_para_o_modelo()
    {
        Assert.Equal(ContentVerdict.Proceed, Avaliar(new[] { Texto(1), Texto(2) }));
    }

    [Fact]
    public void Grupo_vazio_segue_o_que_nao_ha_para_bloquear()
    {
        Assert.Equal(ContentVerdict.Proceed, Avaliar(Array.Empty<SnapshotMessage>()));
    }

    [Theory]
    [InlineData(MessageKind.Audio)]
    [InlineData(MessageKind.Image)]
    [InlineData(MessageKind.Document)]
    [InlineData(MessageKind.Video)]
    [InlineData(MessageKind.Other)]
    public void Midia_com_conteudo_pronto_segue_para_o_modelo(MessageKind tipo)
    {
        var pronta = Snapshots.Customer(1, "transcrição pronta", tipo, ContentState.Ready);

        Assert.Equal(ContentVerdict.Proceed, Avaliar(new[] { pronta }));
    }

    // Wait: dentro da espera adicional por tipo

    [Theory]
    [InlineData(MessageKind.Audio)]
    [InlineData(MessageKind.Image)]
    [InlineData(MessageKind.Document)]
    public void Na_primeira_avaliacao_midia_pendente_com_espera_adicional_espera(MessageKind tipo)
    {
        Assert.Equal(ContentVerdict.Wait, Avaliar(new[] { Sem(tipo, ContentState.Pending) }, espera: null));
    }

    [Theory]
    [InlineData(MessageKind.Audio, 9.999)]
    [InlineData(MessageKind.Image, 5)]
    [InlineData(MessageKind.Document, 0.1)]
    public void Antes_do_fim_da_espera_adicional_o_turno_continua_esperando(MessageKind tipo, double segundos)
    {
        var espera = Agora - TimeSpan.FromSeconds(segundos);

        Assert.Equal(ContentVerdict.Wait, Avaliar(new[] { Sem(tipo, ContentState.Pending) }, espera));
    }

    // Esgotada a espera: a política do tipo

    [Fact]
    public void Audio_pendente_com_a_espera_esgotada_recebe_a_resposta_informativa()
    {
        var espera = Agora - Opcoes.AudioExtraWait;

        Assert.Equal(ContentVerdict.InformativeReply, Avaliar(new[] { Sem(MessageKind.Audio, ContentState.Pending) }, espera));
    }

    [Theory]
    [InlineData(MessageKind.Image)]
    [InlineData(MessageKind.Document)]
    public void Imagem_e_documento_com_a_espera_esgotada_vao_ao_atendente(MessageKind tipo)
    {
        var espera = Agora - Opcoes.ImageExtraWait;

        Assert.Equal(ContentVerdict.Handoff, Avaliar(new[] { Sem(tipo, ContentState.Pending) }, espera));
    }

    [Theory]
    [InlineData(MessageKind.Video)]
    [InlineData(MessageKind.Other)]
    public void Video_e_outros_tipos_nao_esperam_e_vao_direto_ao_atendente_na_primeira_avaliacao(MessageKind tipo)
    {
        Assert.Equal(ContentVerdict.Handoff, Avaliar(new[] { Sem(tipo, ContentState.Pending) }, espera: null));
    }

    [Fact]
    public void A_espera_conta_do_inicio_registrado_no_turno_e_o_limite_exato_ja_esgota()
    {
        var umSegundoAntes = Agora - Opcoes.AudioExtraWait + TimeSpan.FromSeconds(1);
        var noLimite = Agora - Opcoes.AudioExtraWait;
        var grupo = new[] { Sem(MessageKind.Audio, ContentState.Pending) };

        Assert.Equal(ContentVerdict.Wait, Avaliar(grupo, umSegundoAntes));
        Assert.Equal(ContentVerdict.InformativeReply, Avaliar(grupo, noLimite));
    }

    [Fact]
    public void Na_primeira_avaliacao_a_espera_conta_do_agora_e_zero_ja_esgota_a_espera_zerada()
    {
        var semEspera = new AgentsOptions { AudioExtraWait = TimeSpan.Zero };

        var veredito = Avaliar(new[] { Sem(MessageKind.Audio, ContentState.Pending) }, espera: null, opcoes: semEspera);

        Assert.Equal(ContentVerdict.InformativeReply, veredito);
    }

    [Fact]
    public void A_espera_adicional_e_por_tipo_e_configuravel()
    {
        var imagemLonga = new AgentsOptions { ImageExtraWait = TimeSpan.FromSeconds(30) };
        var grupo = new[] { Sem(MessageKind.Image, ContentState.Pending) };

        Assert.Equal(ContentVerdict.Wait, Avaliar(grupo, Agora - TimeSpan.FromSeconds(20), imagemLonga));
        Assert.Equal(ContentVerdict.Handoff, Avaliar(grupo, Agora - TimeSpan.FromSeconds(30), imagemLonga));
    }

    // Failed esgota na hora

    [Fact]
    public void Audio_com_o_processamento_falho_recebe_a_resposta_informativa_sem_esperar()
    {
        Assert.Equal(ContentVerdict.InformativeReply, Avaliar(new[] { Sem(MessageKind.Audio, ContentState.Failed) }, espera: null));
    }

    [Theory]
    [InlineData(MessageKind.Image)]
    [InlineData(MessageKind.Document)]
    [InlineData(MessageKind.Video)]
    [InlineData(MessageKind.Other)]
    public void Outros_tipos_com_o_processamento_falho_vao_ao_atendente_sem_esperar(MessageKind tipo)
    {
        Assert.Equal(ContentVerdict.Handoff, Avaliar(new[] { Sem(tipo, ContentState.Failed) }, espera: null));
    }

    // Grupos mistos

    [Fact]
    public void MID_6_texto_pronto_com_audio_pendente_espera_e_depois_a_politica_vale_para_o_grupo_todo()
    {
        var grupo = new[] { Texto(1), Sem(MessageKind.Audio, ContentState.Pending, 2) };

        Assert.Equal(ContentVerdict.Wait, Avaliar(grupo, espera: null));
        Assert.Equal(ContentVerdict.InformativeReply, Avaliar(grupo, Agora - Opcoes.AudioExtraWait));
    }

    [Fact]
    public void Audio_pendente_e_imagem_falha_espera_o_audio_e_depois_o_grupo_vai_ao_atendente()
    {
        var grupo = new[] { Sem(MessageKind.Audio, ContentState.Pending, 1), Sem(MessageKind.Image, ContentState.Failed, 2) };

        Assert.Equal(ContentVerdict.Wait, Avaliar(grupo, espera: null));
        Assert.Equal(ContentVerdict.Handoff, Avaliar(grupo, Agora - Opcoes.AudioExtraWait));
    }

    [Fact]
    public void Audio_falho_com_outro_audio_ainda_pendente_espera_o_pendente()
    {
        var grupo = new[] { Sem(MessageKind.Audio, ContentState.Failed, 1), Sem(MessageKind.Audio, ContentState.Pending, 2) };

        Assert.Equal(ContentVerdict.Wait, Avaliar(grupo, espera: null));
        Assert.Equal(ContentVerdict.InformativeReply, Avaliar(grupo, Agora - Opcoes.AudioExtraWait));
    }

    [Fact]
    public void Dois_tipos_pendentes_so_esgotam_quando_o_de_espera_mais_longa_esgota()
    {
        var opcoes = new AgentsOptions { AudioExtraWait = TimeSpan.FromSeconds(5), ImageExtraWait = TimeSpan.FromSeconds(20) };
        var grupo = new[] { Sem(MessageKind.Audio, ContentState.Pending, 1), Sem(MessageKind.Image, ContentState.Pending, 2) };

        Assert.Equal(ContentVerdict.Wait, Avaliar(grupo, Agora - TimeSpan.FromSeconds(10), opcoes));
        Assert.Equal(ContentVerdict.Handoff, Avaliar(grupo, Agora - TimeSpan.FromSeconds(20), opcoes));
    }

    [Fact]
    public void O_texto_pronto_do_grupo_nunca_evita_a_politica_da_midia_indisponivel()
    {
        var grupo = new[] { Texto(1), Texto(2), Sem(MessageKind.Video, ContentState.Pending, 3) };

        Assert.Equal(ContentVerdict.Handoff, Avaliar(grupo, espera: null));
    }
}
