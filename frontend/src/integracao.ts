import { ApiError, descreverErro } from './api/client';
import type {
  CurrentUser,
  IntegrationAuthType,
  IntegrationCaller,
  IntegrationDefinitionStatus,
  IntegrationEffect,
  IntegrationExecutionStatus,
  IntegrationIdempotency,
  IntegrationParameter,
  IntegrationResponseField,
  IntegrationSpec,
} from './api/types';

/**
 * Quem vê a área "Integrações": administradores (do tenant ou de uma organização) e gerentes. O servidor é quem decide cada operação (403 sem a permissão, 404 fora do alcance): a tela só
 * evita mostrar uma área que não serve a quem só atende.
 */
export function podeVerIntegracoes(usuario: CurrentUser): boolean {
  return usuario.grants.some((g) => g.role === 'OrganizationAdmin' || g.role === 'Manager');
}

export const ROTULO_AUTENTICACAO: Record<IntegrationAuthType, string> = {
  None: 'Sem autenticação',
  ApiKey: 'Chave de API',
  Basic: 'Usuário e senha (Basic)',
  OAuth2ClientCredentials: 'OAuth 2.0 (client credentials)',
};

/** Os campos do segredo de cada tipo de autenticação (o mesmo que o servidor exige). */
export const CAMPOS_DO_SEGREDO: Record<IntegrationAuthType, Array<{ nome: string; rotulo: string }>> = {
  None: [],
  ApiKey: [{ nome: 'apiKey', rotulo: 'Chave de API' }],
  Basic: [
    { nome: 'username', rotulo: 'Usuário' },
    { nome: 'password', rotulo: 'Senha' },
  ],
  OAuth2ClientCredentials: [
    { nome: 'clientId', rotulo: 'Client ID' },
    { nome: 'clientSecret', rotulo: 'Client secret' },
  ],
};

export const ROTULO_EFEITO: Record<IntegrationEffect, string> = { Read: 'Leitura', Write: 'Escrita (altera dados)' };

export const ROTULO_ORIGEM: Record<IntegrationCaller, string> = {
  Operator: 'Pessoa (administração)',
  Agent: 'Agente de IA',
  Workflow: 'Fluxo / chatbot visual',
  EmployeeAssistant: 'Assistente interno',
  Chatbot: 'Chatbot determinístico',
};

export const ORIGENS: IntegrationCaller[] = ['Operator', 'Agent', 'Workflow', 'EmployeeAssistant', 'Chatbot'];

export const ROTULO_SITUACAO_DA_OPERACAO: Record<IntegrationDefinitionStatus, string> = { Draft: 'Rascunho', Published: 'Publicada', Retired: 'Aposentada' };

export const ROTULO_STATUS_DA_EXECUCAO: Record<IntegrationExecutionStatus, string> = {
  Succeeded: 'Sucesso',
  Failed: 'Falhou',
  Rejected: 'Recusada',
  AwaitingConfirmation: 'Aguardando confirmação',
  Unknown: 'Resultado ambíguo',
  InProgress: 'Em curso',
  Cancelled: 'Cancelada',
};

export const ROTULO_IDEMPOTENCIA: Record<IntegrationIdempotency, string> = {
  Unspecified: 'Não declarada (só leitura)',
  Proven: 'Comprovada (o sistema externo aceita chave de idempotência ou a operação é naturalmente idempotente)',
  Reconcilable: 'Reconciliável (uma consulta confirma o efeito; nunca reenviada)',
  None: 'Nenhuma (nunca reenviada automaticamente)',
};

/** Os códigos de erro curtos do motor, em português claro. O servidor nunca devolve o texto do sistema externo. */
const ROTULO_ERRO: Record<string, string> = {
  'validation-failed': 'Os parâmetros não respeitam o esquema da operação.',
  'not-found': 'Operação ou execução não encontrada.',
  disabled: 'A operação ou a conexão está desabilitada.',
  'caller-not-allowed': 'Esta origem não pode usar a operação.',
  'rate-limited': 'Limite de execuções atingido. Tente de novo em instantes.',
  'idempotency-required': 'Uma operação de escrita exige uma chave de idempotência válida.',
  'idempotency-conflict': 'A chave de idempotência já foi usada com outros parâmetros.',
  'outcome-unknown': 'Resultado ambíguo: confira no sistema de origem antes de repetir.',
  'confirmation-expired': 'A confirmação venceu. Peça a operação de novo.',
  'definition-changed': 'A operação mudou desde o pedido. Peça de novo.',
  'not-pending': 'A execução não está mais aguardando confirmação.',
  timeout: 'O sistema externo não respondeu a tempo.',
  unavailable: 'O sistema externo está indisponível.',
  'upstream-rejected': 'O sistema externo recusou a operação.',
  'upstream-auth': 'O sistema externo recusou as credenciais da conexão.',
  'redirect-blocked': 'O sistema externo respondeu com redirecionamento, que não é seguido.',
  'blocked-destination': 'O destino da conexão não é permitido.',
  'response-too-large': 'A resposta do sistema externo excede o limite.',
  'response-invalid': 'A resposta do sistema externo não tem o formato esperado.',
  'secret-unavailable': 'A credencial da conexão não está disponível.',
  'module-disabled': 'O motor de integração está desligado neste ambiente.',
  'connector-unavailable': 'Não há conector para esta conexão.',
  'write-not-testable': 'Operações de escrita não são executadas em teste.',
  'in-progress': 'Outra chamada com a mesma chave ainda está em curso.',
  cancelled: 'A execução foi cancelada.',
  'internal-error': 'Falha interna ao executar a operação.',
  'rate-limited-test': 'Muitos testes seguidos. Aguarde um minuto.',
  ok: 'Conectou.',
  'secrets-unavailable': 'A chave de cifra dos segredos não está configurada neste ambiente.',
};

export function rotuloDoCodigo(codigo: string | null | undefined): string {
  return codigo ? (ROTULO_ERRO[codigo] ?? `Erro (${codigo}).`) : '';
}

/** A mensagem de um erro da API do motor, em português: o código de conflito ou de indisponibilidade tem frase própria; os problemas de validação vêm em lista à parte (`ApiError.problemas`). */
export function mensagemDeErroDaIntegracao(erro: unknown): string {
  if (!(erro instanceof ApiError)) {
    return descreverErro(erro);
  }
  switch (erro.status) {
    case 400:
      return 'Os dados enviados não são válidos. Confira os campos abaixo.';
    case 403:
      return 'Você não tem permissão para esta operação de integração. Peça a um administrador da empresa.';
    case 404:
      return 'Este item não existe mais ou está fora do seu alcance.';
    case 409:
      if (erro.code === 'changed') {
        return 'Este item foi alterado por outra pessoa. Releia e tente de novo.';
      }
      if (erro.code === 'key-in-use') {
        return 'Já existe um item com esta chave (ou outra pessoa o criou agora).';
      }
      if (erro.code === 'limit-reached') {
        return 'O limite de itens desta empresa foi atingido.';
      }
      return descreverErro(erro);
    case 503:
      return erro.code === 'secrets-unavailable' ? ROTULO_ERRO['secrets-unavailable']! : descreverErro(erro);
    default:
      return descreverErro(erro);
  }
}

export function moldeVazio(efeito: IntegrationEffect): IntegrationSpec {
  return {
    method: efeito === 'Read' ? 'GET' : 'POST',
    pathTemplate: '/',
    parameters: [],
    response: [],
    idempotency: efeito === 'Read' ? 'Unspecified' : 'None',
    idempotencyHeader: null,
    responseRoot: null,
    maxItems: 20,
    maxAttempts: 1,
  };
}

export function parametroVazio(): IntegrationParameter {
  return { name: '', type: 'String', location: 'Query', required: true, wireName: null, maxLength: null, pattern: null, min: null, max: null, enumValues: null, default: null, description: null };
}

export function campoVazio(): IntegrationResponseField {
  return { name: '', pointer: '/', type: 'String', required: true, maxLength: null };
}

/** Um número digitado: vazio vira nulo; texto que não é número devolve `undefined` (a tela marca o campo). */
export function numeroOuNulo(texto: string): number | null | undefined {
  const limpo = texto.trim().replace(',', '.');
  if (limpo === '') {
    return null;
  }
  const n = Number(limpo);
  return Number.isFinite(n) ? n : undefined;
}

/** Valores separados por vírgula ou por linha, sem espaços nas pontas e sem vazios. */
export function listaDoTexto(texto: string): string[] {
  return texto
    .split(/[\n,]/)
    .map((v) => v.trim())
    .filter((v) => v !== '');
}

/** Pares "Nome: valor", um por linha (cabeçalhos públicos da conexão). Linha sem dois-pontos é ignorada pela leitura e apontada por `linhasInvalidas`. */
export function paresDoTexto(texto: string): Record<string, string> {
  const pares: Record<string, string> = {};
  for (const linha of texto.split(/\r?\n/)) {
    const i = linha.indexOf(':');
    if (i > 0) {
      pares[linha.slice(0, i).trim()] = linha.slice(i + 1).trim();
    }
  }
  return pares;
}

export function textoDosPares(pares: Record<string, string>): string {
  return Object.entries(pares)
    .map(([nome, valor]) => `${nome}: ${valor}`)
    .join('\n');
}

/** O JSON de exemplo digitado para um teste: precisa ser um OBJETO. Devolve o objeto ou a mensagem do problema. */
export function lerParametrosDeExemplo(texto: string): { valores: Record<string, unknown> } | { erro: string } {
  const limpo = texto.trim();
  if (limpo === '') {
    return { valores: {} };
  }
  try {
    const lido: unknown = JSON.parse(limpo);
    if (lido === null || typeof lido !== 'object' || Array.isArray(lido)) {
      return { erro: 'Informe um objeto JSON, por exemplo {"numero": 1001}.' };
    }
    return { valores: lido as Record<string, unknown> };
  } catch {
    return { erro: 'O texto não é um JSON válido.' };
  }
}

/** O endereço sem credenciais nem consulta, para listar na tela (o servidor já recusa os dois, mas a lista nunca deve ecoar um valor digitado de outra forma). */
export function enderecoParaExibir(url: string): string {
  try {
    const u = new URL(url);
    return `${u.protocol}//${u.host}${u.pathname === '/' ? '' : u.pathname}`;
  } catch {
    return url;
  }
}
