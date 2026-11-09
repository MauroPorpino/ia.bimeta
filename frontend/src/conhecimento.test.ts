import { describe, expect, it } from 'vitest';
import { ApiError } from './api/client';
import { ADMINISTRADOR, USUARIO, entradaDoFaq } from './test/helpers';
import { aliasesDoTexto, compararComPublicada, dataHora, ehAdministradorDoTenant, mensagemDeErroDoConhecimento } from './conhecimento';

describe('ehAdministradorDoTenant', () => {
  it('só o papel OrganizationAdmin com escopo do tenant (organizationId nulo) vê a área', () => {
    expect(ehAdministradorDoTenant(ADMINISTRADOR)).toBe(true);
    expect(ehAdministradorDoTenant(USUARIO)).toBe(false); // sem concessões
    expect(ehAdministradorDoTenant({ ...USUARIO, grants: [{ organizationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', role: 'OrganizationAdmin' }] })).toBe(false);
    expect(ehAdministradorDoTenant({ ...USUARIO, grants: [{ organizationId: null, role: 'Manager' }] })).toBe(false);
    expect(ehAdministradorDoTenant({ ...USUARIO, grants: [{ organizationId: null, role: 'User' }] })).toBe(false);
  });

  it('uma concessão de tenant entre outras já basta', () => {
    expect(
      ehAdministradorDoTenant({
        ...USUARIO,
        grants: [
          { organizationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', role: 'Manager' },
          { organizationId: null, role: 'OrganizationAdmin' },
        ],
      }),
    ).toBe(true);
  });
});

describe('aliasesDoTexto', () => {
  it('um alias por linha, sem espaços nas pontas e sem linhas vazias', () => {
    expect(aliasesDoTexto('  primeiro  \r\n\r\nsegundo\n   \nterceiro ')).toEqual(['primeiro', 'segundo', 'terceiro']);
    expect(aliasesDoTexto('')).toEqual([]);
  });
});

describe('compararComPublicada', () => {
  const horario = entradaDoFaq({ question: 'Qual o horário?', answer: 'Das 7h às 17h.', aliases: ['horário'] });

  it('marca como nova a pergunta que não está na publicada, sem diferenciar caixa nem espaços', () => {
    const resultado = compararComPublicada(
      [entradaDoFaq({ question: '  QUAL O   HORÁRIO?  ', answer: 'Das 7h às 17h.', aliases: ['horário'] }), entradaDoFaq({ question: 'Onde fica?' })],
      [horario],
    );

    expect(resultado.map((r) => r.marca)).toEqual([null, 'nova']);
  });

  it('marca como alterada a entrada com a mesma pergunta e resposta, aliases ou resposta direta diferentes', () => {
    const marcas = compararComPublicada(
      [{ ...horario, answer: 'Das 8h às 16h.' }, { ...horario, aliases: null }, { ...horario, autoReply: true }, { ...horario }],
      [horario],
    ).map((r) => r.marca);

    expect(marcas).toEqual(['alterada', 'alterada', 'alterada', null]);
  });

  it('aliases nulos e lista vazia são a mesma coisa', () => {
    expect(compararComPublicada([{ ...horario, aliases: [] }], [{ ...horario, aliases: null }])[0]!.marca).toBeNull();
  });

  it('sem versão publicada nada é marcado', () => {
    expect(compararComPublicada([horario, entradaDoFaq()], null).map((r) => r.marca)).toEqual([null, null]);
  });
});

describe('dataHora', () => {
  it('formata em pt-BR e ignora o que não é data', () => {
    expect(dataHora('2026-11-09T14:35:00Z')).toMatch(/^\d{2}\/\d{2}\/\d{4}.*\d{2}:\d{2}/);
    expect(dataHora(null)).toBe('');
    expect(dataHora('não é data')).toBe('');
  });
});

describe('mensagemDeErroDoConhecimento', () => {
  it('403 e cada código de 409 têm mensagem clara', () => {
    expect(mensagemDeErroDoConhecimento(new ApiError(403, 'Acesso negado', 'x'))).toContain('Você não tem permissão para administrar o conhecimento');
    expect(mensagemDeErroDoConhecimento(new ApiError(409, 'Conflito', 'x', 'faq-conflict'))).toContain('ajuste a existente em vez de criar outra');
    expect(mensagemDeErroDoConhecimento(new ApiError(409, 'Conflito', 'x', 'gap-not-open'))).toContain('já foi decidida');
    expect(mensagemDeErroDoConhecimento(new ApiError(409, 'Conflito', 'x', 'gap-changed'))).toContain('alterada por outra pessoa');
    expect(mensagemDeErroDoConhecimento(new ApiError(409, 'Conflito', 'x', 'no-definition'))).toContain('ainda não tem uma definição do agente');
    expect(mensagemDeErroDoConhecimento(new ApiError(409, 'Conflito', 'x', 'definition-changed'))).toContain('rascunho foi alterado');
  });

  it('os demais erros seguem o texto padrão da API (título e detalhe)', () => {
    expect(mensagemDeErroDoConhecimento(new ApiError(400, 'Dados inválidos', 'A pergunta excede 300 caracteres.'))).toBe('Dados inválidos: A pergunta excede 300 caracteres.');
    expect(mensagemDeErroDoConhecimento(new ApiError(409, 'Conflito', 'Outro motivo.', 'outro'))).toBe('Conflito: Outro motivo.');
    expect(mensagemDeErroDoConhecimento(new ApiError(0, 'Sem conexão com a API', 'Tente de novo.'))).toBe('Sem conexão com a API: Tente de novo.');
    expect(mensagemDeErroDoConhecimento(new Error('falhou'))).toBe('falhou');
  });
});
