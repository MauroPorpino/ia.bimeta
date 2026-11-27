import { ApiError, descreverErro } from './api/client';
import type { CurrentUser, DiagnosticoDeIa, ModeloDeIa, ProvedorDeIa } from './api/types';

// Configurações → Inteligência artificial (Etapa 05). A interface só decide o que MOSTRAR: o servidor aplica cada permissão (403) e cada validação.

export type PermissaoDeIa = 'AiSettingsRead' | 'AiSettingsManage' | 'AiCredentialManage' | 'AiConnectivityTest' | 'AiGenerationTest';

/** Tem a permissão de IA no escopo do tenant, ou a permissão ampla de configurar o tenant (que já abrangia a IA). */
export function podeDeIa(me: CurrentUser, permissao: PermissaoDeIa): boolean {
  return (me.permissions ?? []).some((p) => p.organizationId === null && (p.permission === permissao || p.permission === 'TenantSettingsManage'));
}

export function podeVerIa(me: CurrentUser): boolean {
  return podeDeIa(me, 'AiSettingsRead');
}

/** A ordem e o nome com que os provedores aparecem (o catálogo da plataforma não traz nome comercial). */
export const ORDEM_DOS_PROVEDORES = ['openai', 'anthropic', 'ollama'];

export const ROTULO_DO_PROVEDOR: Record<string, string> = {
  openai: 'OpenAI',
  anthropic: 'Anthropic',
  ollama: 'Ollama (IA local)',
};

export function rotuloDoProvedor(id: string): string {
  return ROTULO_DO_PROVEDOR[id] ?? id;
}

export function provedoresOrdenados(provedores: ProvedorDeIa[]): ProvedorDeIa[] {
  const posicao = (id: string) => {
    const i = ORDEM_DOS_PROVEDORES.indexOf(id);
    return i < 0 ? ORDEM_DOS_PROVEDORES.length : i;
  };
  return [...provedores].sort((a, b) => posicao(a.providerId) - posicao(b.providerId) || a.providerId.localeCompare(b.providerId));
}

export const ROTULO_DA_CONECTIVIDADE: Record<string, string> = {
  Verified: 'Conectividade verificada',
  AuthenticationFailed: 'Falha de autenticação',
  Unreachable: 'Servidor inacessível',
  ModelUnavailable: 'Modelo indisponível',
  NotSupported: 'Verificação não suportada pelo provedor',
  Inconclusive: 'Resultado inconclusivo',
};

export const ROTULO_DA_GERACAO: Record<string, string> = {
  Succeeded: 'Geração testada com sucesso',
  Failed: 'Falha no teste de geração',
  NotExecuted: 'Teste não executado',
};

export const ROTULO_DO_CODIGO: Record<string, string> = {
  ok: 'ok',
  unauthorized: 'credencial recusada pelo provedor',
  forbidden: 'a chave não tem permissão para esta consulta',
  'model-not-found': 'modelo não encontrado no provedor',
  'model-not-installed': 'modelo não instalado no servidor',
  'not-conversational': 'o modelo não declara a capacidade de conversa',
  'capability-unknown': 'capacidade do modelo não confirmada',
  'model-retired': 'modelo aposentado pelo provedor',
  'model-deprecated': 'modelo em descontinuação',
  timeout: 'tempo esgotado',
  'network-error': 'falha de rede',
  'server-unavailable': 'servidor indisponível',
  'rate-limited': 'limite de taxa do provedor',
  'server-error': 'erro do servidor do provedor',
  billing: 'problema de cobrança na conta do provedor',
  'invalid-response': 'resposta inesperada',
  'adapter-error': 'falha interna do adaptador',
  'credential-invalid-format': 'a credencial não tem formato de chave',
  'tenant-suspended': 'empresa suspensa',
  'credential-missing': 'credencial não cadastrada',
  'limit-exceeded': 'limite de uso da empresa',
};

export function textoDoCodigo(codigo: string | null): string | null {
  return codigo === null ? null : (ROTULO_DO_CODIGO[codigo] ?? codigo);
}

export const AVISOS: Record<string, string> = {
  'no-default': 'A empresa ainda não escolheu um provedor e modelo padrão: a IA não responde aos clientes e a conversa vai para um atendente.',
  'default-unavailable': 'O provedor ou o modelo padrão não está mais disponível neste ambiente: a IA não responde e a conversa vai para um atendente. Escolha outro padrão.',
  'default-credential-missing': 'O provedor padrão está sem credencial: a IA não responde aos clientes e a conversa vai para um atendente até a credencial ser cadastrada ou outro padrão ser escolhido. Não há troca automática de provedor.',
  'ai-disabled': 'A IA desta empresa está desabilitada.',
  'ai-blocked': 'A IA desta empresa está bloqueada pela plataforma.',
  'connectivity-not-verified': 'A conectividade deste modelo ainda não foi verificada (ou o resultado está desatualizado).',
  'generation-not-tested': 'A geração com este modelo ainda não foi testada (ou o resultado está desatualizado). Salvar não faz chamada ao provedor.',
  'model-unavailable-per-last-check': 'A última verificação indica que o modelo não está disponível no servidor. A configuração foi salva, mas o atendimento só funciona depois que ele estiver instalado.',
};

export function textoDoAviso(codigo: string): string {
  return AVISOS[codigo] ?? codigo;
}

const MENSAGENS_DE_ERRO: Record<string, string> = {
  'credential-required': 'Cadastre a credencial deste provedor primeiro.',
  'credential-not-used': 'Este provedor não usa credencial.',
  'provider-not-implemented': 'Este provedor não está disponível neste ambiente.',
  'model-not-authorized': 'Este modelo não está autorizado para a sua empresa.',
  'model-not-conversational': 'O último diagnóstico indica que este modelo não serve à conversa.',
  'model-required': 'Escolha um modelo.',
  'confirmation-required': 'Confirme que entende que o teste pode gerar cobrança.',
  'tenant-suspended': 'A empresa está suspensa: o teste não pode ser executado.',
  'test-in-progress': 'Já existe um teste deste provedor em andamento. Aguarde a conclusão.',
  'rate-limited': 'Muitos testes em pouco tempo. Aguarde um pouco e tente de novo.',
  'secrets-unavailable': 'O armazenamento seguro de credenciais está indisponível neste ambiente. Fale com a BImeta.',
  'config-changed': 'A configuração foi alterada por outra pessoa. Os dados foram recarregados: confira e tente de novo.',
};

/** A mensagem para uma falha da API: nunca ecoa o que foi digitado (a chave nunca entra em mensagem). */
export function mensagemDeErroDeIa(falha: unknown): string {
  if (falha instanceof ApiError) {
    if (falha.status === 403) {
      return 'Você não tem permissão para esta operação de inteligência artificial.';
    }
    if (falha.status === 0 || falha.status >= 500) {
      return falha.code && MENSAGENS_DE_ERRO[falha.code] ? MENSAGENS_DE_ERRO[falha.code]! : 'Serviço indisponível no momento. Tente de novo em instantes.';
    }
    if (falha.code && MENSAGENS_DE_ERRO[falha.code]) {
      return MENSAGENS_DE_ERRO[falha.code]!;
    }
    return falha.status === 400 ? 'Dados inválidos. Confira o que foi informado.' : descreverErro(falha);
  }
  return descreverErro(falha);
}

export function ehFalhaDePermissao(falha: unknown): boolean {
  return falha instanceof ApiError && falha.status === 403;
}

/** O diagnóstico vale como confirmação? Só se existe, NÃO está desatualizado e é positivo. */
export function confirmado(diagnostico: DiagnosticoDeIa | null, estadoPositivo: string): boolean {
  return diagnostico !== null && !diagnostico.stale && diagnostico.state === estadoPositivo;
}

export function textoDaDesatualizacao(diagnostico: DiagnosticoDeIa): string | null {
  if (!diagnostico.stale) {
    return null;
  }
  return diagnostico.staleReason === 'credential-changed' ? 'desatualizado: a credencial mudou depois deste teste' : 'desatualizado: resultado antigo (mais de 24 horas)';
}

export function triEstado(valor: boolean | null): string {
  return valor === null ? 'não verificado' : valor ? 'sim' : 'não';
}

/** O modelo padrão atual do provedor, quando é o padrão. */
export function modeloPadrao(provedor: ProvedorDeIa): ModeloDeIa | undefined {
  return provedor.models.find((m) => m.isDefault);
}

/** Um provedor pode ser escolhido como padrão (elegível) quando está implementado, autorizado e, se exige credencial, tem credencial. Elegível NÃO é testado. */
export function elegivelComoPadrao(provedor: ProvedorDeIa): boolean {
  return provedor.implemented && provedor.authorized && (!provedor.credential.required || provedor.credential.present);
}

export function dataHora(iso: string | null): string {
  return iso === null ? '—' : new Date(iso).toLocaleString('pt-BR');
}
