import { ApiError, descreverErro } from './api/client';
import type { CurrentUser, FaqEntry, KnowledgeGapKind, KnowledgeGapStatus } from './api/types';

/** Os limites que o servidor aplica ao FAQ (AgentDefinitionContent): a tela os mostra como dica, o servidor é quem valida. */
export const LIMITE_PERGUNTA = 300;
export const LIMITE_RESPOSTA = 1500;
export const LIMITE_ALIASES = 10;
export const LIMITE_ENTRADAS_DO_FAQ = 50;

export const ROTULO_TIPO_DA_LACUNA: Record<KnowledgeGapKind, string> = {
  NoMatch: 'Sem correspondência no FAQ',
  Ambiguous: 'Pergunta ambígua (entradas duplicadas)',
  NotAutoReply: 'Entrada existente sem resposta direta',
};

export const ROTULO_SITUACAO_DA_LACUNA: Record<KnowledgeGapStatus, string> = {
  Open: 'Aberta',
  Dismissed: 'Descartada',
  Promoted: 'Enviada ao rascunho',
};

/**
 * Quem pode ver a área "Conhecimento": só quem tem a concessão de administrador no ESCOPO DO TENANT (`organizationId` nulo).
 * O administrador de uma única organização não configura o agente da empresa (o servidor responde 403 para ele).
 */
export function ehAdministradorDoTenant(usuario: CurrentUser): boolean {
  return usuario.grants.some((g) => g.role === 'OrganizationAdmin' && g.organizationId === null);
}

/** Os aliases digitados, um por linha: sem espaços nas pontas e sem linhas vazias. */
export function aliasesDoTexto(texto: string): string[] {
  return texto
    .split(/\r?\n/)
    .map((linha) => linha.trim())
    .filter((linha) => linha !== '');
}

/** A chave para comparar perguntas do FAQ entre versões: sem diferença de caixa nem de espaços. */
function chaveDaPergunta(pergunta: string): string {
  return pergunta.trim().replace(/\s+/g, ' ').toLocaleLowerCase('pt-BR');
}

export type MarcaDaEntrada = 'nova' | 'alterada' | null;

export interface EntradaComparada {
  entrada: FaqEntry;
  /** `nova`: a pergunta não está na versão publicada; `alterada`: está, mas a resposta, os aliases ou a resposta direta mudaram; nula: igual à publicada (ou sem publicada para comparar). */
  marca: MarcaDaEntrada;
}

function mesmosAliases(a: readonly string[] | null, b: readonly string[] | null): boolean {
  const x = a ?? [];
  const y = b ?? [];
  return x.length === y.length && x.every((alias, i) => alias === y[i]);
}

/**
 * O FAQ do rascunho com a marca de cada entrada em relação à versão publicada. Sem versão publicada, nada é marcado (todas seriam
 * "novas"): quem mostra a lista avisa isso uma vez só.
 */
export function compararComPublicada(rascunho: readonly FaqEntry[], publicada: readonly FaqEntry[] | null): EntradaComparada[] {
  if (publicada === null) {
    return rascunho.map((entrada) => ({ entrada, marca: null }));
  }
  const porPergunta = new Map(publicada.map((e) => [chaveDaPergunta(e.question), e]));
  return rascunho.map((entrada) => {
    const anterior = porPergunta.get(chaveDaPergunta(entrada.question));
    if (!anterior) {
      return { entrada, marca: 'nova' };
    }
    const igual = anterior.answer === entrada.answer && anterior.autoReply === entrada.autoReply && mesmosAliases(anterior.aliases, entrada.aliases);
    return { entrada, marca: igual ? null : 'alterada' };
  });
}

/** Data e hora locais para a tela ("09/11/2026 14:35"); data inválida vira vazio. */
export function dataHora(iso: string | null | undefined): string {
  if (!iso) {
    return '';
  }
  const data = new Date(iso);
  return Number.isNaN(data.getTime()) ? '' : data.toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' });
}

/**
 * O texto de um erro das rotas do conhecimento do agente: 403 e os códigos de 409 viram mensagens claras; os demais seguem o padrão
 * da API (título e detalhe do ProblemDetails, que no 400 explica o que está errado nos dados).
 */
export function mensagemDeErroDoConhecimento(erro: unknown): string {
  if (!(erro instanceof ApiError)) {
    return descreverErro(erro);
  }
  if (erro.status === 403) {
    return 'Você não tem permissão para administrar o conhecimento do agente desta empresa. Peça a um administrador da empresa.';
  }
  if (erro.status === 404) {
    return 'Esta lacuna não existe mais ou está fora do seu alcance.';
  }
  if (erro.status === 409) {
    switch (erro.code) {
      case 'faq-conflict':
        return 'O FAQ já tem uma entrada com essa pergunta ou alias: ajuste a existente em vez de criar outra.';
      case 'gap-not-open':
        return 'Esta lacuna já foi decidida (enviada ao rascunho ou descartada), talvez por outra pessoa. A situação foi atualizada.';
      case 'gap-changed':
        return 'A lacuna foi alterada por outra pessoa enquanto você a revisava. Confira a situação atual antes de decidir.';
      case 'no-definition':
        return 'A empresa ainda não tem uma definição do agente: ela precisa ser criada antes de enviar entradas ao FAQ.';
      case 'definition-changed':
        return 'O rascunho foi alterado por outra operação. Recarregue o rascunho e tente de novo.';
      default:
        break;
    }
  }
  return descreverErro(erro);
}
