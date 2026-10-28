using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// Teste de propriedades por modelo (CONVERSATION-LIFECYCLE.md §16, "sequências aleatórias com semente fixa"): milhares de
/// operações aleatórias sobre a sessão, verificando as invariantes contra um modelo independente e simples. A semente é
/// fixa, então uma falha se reproduz. Os testes de PostgreSQL provam a serialização; aqui se prova a regra.
/// </summary>
public class SessionModelTests
{
    private sealed class Modelo
    {
        public Guid? Ativo;
        public long Epoca;
        public long Versao = 1;
        public long UltimaEntrada;
        public long Coberto;
        public bool IaNoComando = true;
        public bool Aberta = true;
        public HashSet<Guid> Respondidos = new();
    }

    private sealed record Concessao(Guid Turno, long Epoca, long Versao, long Cerca);

    [Theory]
    [InlineData(1)]
    [InlineData(2026)]
    [InlineData(27102026)]
    [InlineData(424242)]
    public void As_invariantes_valem_em_mil_intercalacoes_aleatorias(int semente)
    {
        var acaso = new Random(semente);
        var sessao = Fabrica.Sessao();
        var modelo = new Modelo();
        var concessoes = new List<Concessao>();
        long lease = 0;
        var aceitas = 0;

        for (var passo = 0; passo < 1_000; passo++)
        {
            switch (acaso.Next(8))
            {
                case 0:
                    if (modelo.Aberta)
                    {
                        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
                        modelo.UltimaEntrada++;
                    }

                    break;

                case 2:
                {
                    var turno = Guid.NewGuid();
                    lease += acaso.Next(0, 3);
                    var resultado = sessao.BeginTurn(turno, lease, out var grant);
                    var esperado = !modelo.Aberta ? BeginTurnOutcome.SessionClosed : !modelo.IaNoComando ? BeginTurnOutcome.NotAiResponder : BeginTurnOutcome.Granted;
                    Assert.Equal(esperado, resultado);
                    if (resultado == BeginTurnOutcome.Granted)
                    {
                        modelo.Ativo = turno;
                        modelo.Epoca++;
                        concessoes.Add(new Concessao(turno, grant!.Epoch, grant.StateVersion, grant.BasedOnInboundSeq));
                        Assert.Equal(modelo.Epoca, grant.Epoch);
                        Assert.Equal(modelo.UltimaEntrada, grant.BasedOnInboundSeq);
                    }

                    break;
                }

                case 1 or 3 or 4:
                {
                    if (concessoes.Count == 0)
                    {
                        break;
                    }

                    // Quase sempre a concessão mais recente (o caminho feliz); às vezes uma antiga (o "zumbi" também responde).
                    var c = acaso.Next(10) < 7 ? concessoes[^1] : concessoes[acaso.Next(concessoes.Count)];
                    var resultado = sessao.CheckReplyFences(c.Turno, c.Epoca, c.Versao, c.Cerca);

                    var deveAceitar = modelo.Ativo == c.Turno
                        && modelo.Epoca == c.Epoca
                        && modelo.Aberta
                        && modelo.IaNoComando
                        && modelo.Versao == c.Versao
                        && modelo.UltimaEntrada == c.Cerca;
                    Assert.Equal(deveAceitar, resultado is null);

                    if (resultado is null && !modelo.Respondidos.Contains(c.Turno))
                    {
                        sessao.AcceptAiReply(c.Cerca, Fabrica.Agora);
                        modelo.Coberto = Math.Max(modelo.Coberto, c.Cerca);
                        modelo.Ativo = null;
                        modelo.Respondidos.Add(c.Turno);
                        aceitas++;
                    }

                    break;
                }

                case 5:
                    if (concessoes.Count > 0)
                    {
                        var c = concessoes[acaso.Next(concessoes.Count)];
                        var terminou = sessao.EndTurn(c.Turno, c.Epoca);
                        Assert.Equal(modelo.Ativo == c.Turno && modelo.Epoca == c.Epoca, terminou);
                        if (terminou)
                        {
                            modelo.Ativo = null;
                        }
                    }

                    break;

                case 6:
                    sessao.CancelTurn();
                    modelo.Ativo = null;
                    modelo.Epoca++;
                    break;

                default:
                    if (modelo.Aberta && acaso.Next(25) == 0)
                    {
                        // Alterna entre a IA e um atendente com a versão certa, ou erra a versão de propósito.
                        var paraHumano = modelo.IaNoComando;
                        var versaoEsperada = acaso.Next(5) == 0 ? modelo.Versao + 1 : modelo.Versao;
                        var resultado = paraHumano
                            ? sessao.Transition(HandlingMode.HumanAssisted, Guid.NewGuid(), versaoEsperada)
                            : sessao.Transition(HandlingMode.AiAutonomous, null, versaoEsperada);
                        Assert.Equal(versaoEsperada == modelo.Versao ? TransitionOutcome.Applied : TransitionOutcome.StaleState, resultado);
                        if (resultado == TransitionOutcome.Applied)
                        {
                            modelo.Versao++;
                            modelo.IaNoComando = !modelo.IaNoComando;
                        }
                    }
                    else if (modelo.Aberta && acaso.Next(400) == 0)
                    {
                        sessao.Close(Fabrica.Agora);
                        modelo.Aberta = false;
                        modelo.Versao++;
                    }

                    break;
            }

            // Invariantes, em todo passo.
            Assert.True(sessao.AnsweredThroughSeq >= modelo.Coberto - 0 && sessao.AnsweredThroughSeq == modelo.Coberto, "AnsweredThroughSeq diverge do modelo");
            Assert.True(sessao.AnsweredThroughSeq <= sessao.LastInboundSeq, "I4: a cobertura passou da última entrada");
            Assert.Equal(modelo.UltimaEntrada, sessao.LastInboundSeq);
            Assert.Equal(modelo.Ativo, sessao.ActiveTurnId);
            Assert.Equal(modelo.Epoca, sessao.TurnEpoch);
            Assert.Equal(modelo.Versao, sessao.StateVersion);
        }

        Assert.True(aceitas > 0, "O cenário aleatório precisa aceitar respostas, ou não prova nada");
    }

    [Fact]
    public void A_cobertura_so_cresce_ao_longo_de_toda_a_sequencia_aleatoria()
    {
        var acaso = new Random(7);
        var sessao = Fabrica.Sessao();
        long anterior = 0;

        for (var passo = 0; passo < 2_000; passo++)
        {
            if (acaso.Next(3) == 0)
            {
                sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
            }
            else
            {
                var turno = Guid.NewGuid();
                if (sessao.BeginTurn(turno, passo, out var grant) == BeginTurnOutcome.Granted
                    && sessao.CheckReplyFences(turno, grant!.Epoch, grant.StateVersion, grant.BasedOnInboundSeq) is null)
                {
                    sessao.AcceptAiReply(grant.BasedOnInboundSeq, Fabrica.Agora);
                }
            }

            Assert.True(sessao.AnsweredThroughSeq >= anterior);
            anterior = sessao.AnsweredThroughSeq;
        }
    }
}
