namespace IaBimeta.Domain.Tests.Isolation;

public class ContextoDeAtendimentoTests
{
    // Teste de aceitação do CLAUDE.md: Mauro pergunta sobre o exame A no atendimento #1001,
    // Pedro sobre o exame B no #1002, e Mauro pergunta "preciso ficar em jejum para esse exame?".
    // A requisição de Mauro deve conter só o contexto de A e a resposta deve ir apenas ao #1001.
    [Fact(Skip = "Pendente: Conversation Context Manager (ARCHITECTURE.md, seção 11, etapa 3).")]
    public void Contexto_de_um_atendimento_nao_vaza_para_outro()
    {
    }
}
