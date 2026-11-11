import { useEffect, useState } from 'react';
import { ApiError, type ApiClient } from '../api/client';
import type {
  IntegrationCaller,
  IntegrationConnection,
  IntegrationDefinition,
  IntegrationEffect,
  IntegrationFieldType,
  IntegrationIdempotency,
  IntegrationParameter,
  IntegrationParameterLocation,
  IntegrationParameterType,
  IntegrationResponseField,
  IntegrationResult,
  IntegrationSpec,
} from '../api/types';
import {
  ORIGENS,
  ROTULO_EFEITO,
  ROTULO_IDEMPOTENCIA,
  ROTULO_ORIGEM,
  ROTULO_SITUACAO_DA_OPERACAO,
  campoVazio,
  lerParametrosDeExemplo,
  listaDoTexto,
  mensagemDeErroDaIntegracao,
  moldeVazio,
  numeroOuNulo,
  parametroVazio,
  rotuloDoCodigo,
} from '../integracao';

interface Props {
  api: ApiClient;
}

const METODOS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'];
const TIPOS: IntegrationParameterType[] = ['String', 'Integer', 'Number', 'Boolean', 'Date', 'Enum'];
const LUGARES: Array<{ valor: IntegrationParameterLocation; rotulo: string }> = [
  { valor: 'Path', rotulo: 'Caminho' },
  { valor: 'Query', rotulo: 'Consulta' },
  { valor: 'Header', rotulo: 'Cabeçalho' },
  { valor: 'Body', rotulo: 'Corpo' },
];
const TIPOS_DE_CAMPO: IntegrationFieldType[] = ['String', 'Integer', 'Number', 'Boolean'];
const IDEMPOTENCIAS: IntegrationIdempotency[] = ['Unspecified', 'Proven', 'Reconcilable', 'None'];

function Problemas({ erro }: { erro: ApiError | null }) {
  if (!erro || erro.problemas.length === 0) {
    return null;
  }
  return (
    <ul className="problemas" aria-label="Problemas encontrados">
      {erro.problemas.map((p) => (
        <li key={p}>{p}</li>
      ))}
    </ul>
  );
}

/**
 * O catálogo de operações da empresa (ADR 0035): cada operação é um molde REST/JSON (método, caminho, parâmetros tipados, mapeamento da resposta, efeito, idempotência e confirmação), versionado: o
 * rascunho se edita, a publicada nunca muda (a mudança vira uma versão nova) e só a publicada executa. A operação nunca contém a credencial: ela só referencia uma conexão. Escrita nunca é executada em teste.
 */
export function OperacoesDeIntegracao({ api }: Props) {
  const [operacoes, setOperacoes] = useState<IntegrationDefinition[] | null>(null);
  const [conexoes, setConexoes] = useState<IntegrationConnection[]>([]);
  const [erro, setErro] = useState<string | null>(null);
  const [selecionada, setSelecionada] = useState<string | 'nova' | null>(null);
  const [recarga, setRecarga] = useState(0);
  // A mensagem do primeiro salvamento sobrevive à troca do formulário "nova" pelo da operação criada.
  const [mensagemInicial, setMensagemInicial] = useState<string | null>(null);

  useEffect(() => {
    let vivo = true;
    Promise.all([api.listarOperacoes(), api.listarConexoes()])
      .then(([ops, cons]) => {
        if (vivo) {
          setOperacoes(ops);
          setConexoes(cons);
          setErro(null);
        }
      })
      .catch((falha: unknown) => {
        if (vivo) {
          setErro(mensagemDeErroDaIntegracao(falha));
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, recarga]);

  const atual = operacoes?.find((o) => o.id === selecionada) ?? null;

  return (
    <div className="integracoes-corpo">
      <section className="integracoes-lista" aria-label="Operações">
        <h2>Operações</h2>
        <p className="discreto">O que a plataforma pode consultar ou fazer em cada sistema. Só operações publicadas executam, e quem as usa (pessoa, agente, fluxo) é escolhido aqui.</p>
        <div className="acoes">
          <button
            type="button"
            className="btn btn-primario"
            disabled={conexoes.length === 0}
            onClick={() => {
              setMensagemInicial(null);
              setSelecionada('nova');
            }}
          >
            Nova operação
          </button>
        </div>
        {conexoes.length === 0 && operacoes !== null && <p className="discreto">Cadastre uma conexão antes de criar operações.</p>}
        {erro && (
          <p className="erro-campo" role="alert">
            {erro}
          </p>
        )}
        {operacoes === null && !erro && <p className="discreto">Carregando...</p>}
        {operacoes?.length === 0 && <p className="discreto">Nenhuma operação cadastrada.</p>}
        <ul className="lista-itens">
          {operacoes?.map((o) => (
            <li key={o.id}>
              <button type="button" className="item-fila" aria-current={selecionada === o.id ? 'true' : undefined}
                onClick={() => {
                  setMensagemInicial(null);
                  setSelecionada(o.id);
                }}
              >
                <span className="item-nome">{o.name}</span>
                <span className="item-meta">
                  {o.key} · v{o.version} · {ROTULO_SITUACAO_DA_OPERACAO[o.status]}
                </span>
                <span className="item-meta">
                  {ROTULO_EFEITO[o.effect]}
                  {o.status === 'Published' && !o.enabled ? ' · desligada' : ''}
                </span>
              </button>
            </li>
          ))}
        </ul>
      </section>

      <section className="integracoes-detalhe" aria-label="Detalhe da operação">
        {selecionada === null && <p className="discreto centro">Escolha uma operação ou crie uma nova.</p>}
        {selecionada === 'nova' && (
          <EditorDeOperacao
            key="nova"
            api={api}
            conexoes={conexoes}
            operacao={null}
            onAlterada={(salva, texto) => {
              setMensagemInicial(texto);
              setSelecionada(salva.id);
              setRecarga((r) => r + 1);
            }}
          />
        )}
        {atual && (
          <EditorDeOperacao key={atual.id} api={api} conexoes={conexoes} operacao={atual} mensagemInicial={mensagemInicial} onAlterada={() => setRecarga((r) => r + 1)} />
        )}
      </section>
    </div>
  );
}

interface EditorProps {
  api: ApiClient;
  conexoes: IntegrationConnection[];
  operacao: IntegrationDefinition | null;
  onAlterada: (operacao: IntegrationDefinition, mensagem: string) => void;
  mensagemInicial?: string | null;
}

function EditorDeOperacao({ api, conexoes, operacao, onAlterada, mensagemInicial = null }: EditorProps) {
  const [atual, setAtual] = useState<IntegrationDefinition | null>(operacao);
  const nova = atual === null;
  const editavel = nova || atual.status === 'Draft';
  const [conexaoId, setConexaoId] = useState(operacao?.connectionId ?? conexoes[0]?.id ?? '');
  const [chave, setChave] = useState(operacao?.key ?? '');
  const [nome, setNome] = useState(operacao?.name ?? '');
  const [descricao, setDescricao] = useState(operacao?.description ?? '');
  const [efeito, setEfeito] = useState<IntegrationEffect>(operacao?.effect ?? 'Read');
  const [confirmar, setConfirmar] = useState(operacao?.requiresConfirmation ?? true);
  const [origens, setOrigens] = useState<IntegrationCaller[]>(operacao?.allowedCallers ?? ['Operator']);
  const [molde, setMolde] = useState<IntegrationSpec>(operacao?.spec ?? moldeVazio('Read'));
  const [exemplo, setExemplo] = useState('{}');
  const [resultado, setResultado] = useState<IntegrationResult | null>(null);
  const [validacao, setValidacao] = useState<string | null>(null);
  const [versoes, setVersoes] = useState<IntegrationDefinition[]>([]);
  const [erro, setErro] = useState<ApiError | null>(null);
  const [aviso, setAviso] = useState<string | null>(null);
  const [mensagem, setMensagem] = useState<string | null>(mensagemInicial);
  const [ocupado, setOcupado] = useState(false);

  useEffect(() => {
    if (!operacao) {
      return;
    }
    let vivo = true;
    api
      .versoesDaOperacao(operacao.key)
      .then((lidas) => {
        if (vivo) {
          setVersoes(lidas);
        }
      })
      .catch(() => undefined);
    return () => {
      vivo = false;
    };
  }, [api, operacao]);

  function atualizarMolde(mudanca: Partial<IntegrationSpec>) {
    setMolde((m) => ({ ...m, ...mudanca }));
  }

  function trocarEfeito(novo: IntegrationEffect) {
    setEfeito(novo);
    // Escrita pede a idempotência declarada; leitura não usa. O método acompanha a natureza da operação.
    setMolde((m) => ({
      ...m,
      method: novo === 'Read' ? 'GET' : m.method === 'GET' ? 'POST' : m.method,
      idempotency: novo === 'Read' ? 'Unspecified' : m.idempotency === 'Unspecified' ? 'None' : m.idempotency,
      idempotencyHeader: novo === 'Read' ? null : m.idempotencyHeader,
    }));
    setConfirmar(true);
  }

  function atualizarParametro(i: number, mudanca: Partial<IntegrationParameter>) {
    atualizarMolde({ parameters: molde.parameters.map((p, j) => (j === i ? { ...p, ...mudanca } : p)) });
  }

  function atualizarCampo(i: number, mudanca: Partial<IntegrationResponseField>) {
    atualizarMolde({ response: molde.response.map((c, j) => (j === i ? { ...c, ...mudanca } : c)) });
  }

  async function executar(acao: () => Promise<void>) {
    setOcupado(true);
    setErro(null);
    setAviso(null);
    setMensagem(null);
    try {
      await acao();
    } catch (falha) {
      if (falha instanceof ApiError) {
        setErro(falha);
      }
      setAviso(mensagemDeErroDaIntegracao(falha));
    } finally {
      setOcupado(false);
    }
  }

  async function salvar() {
    await executar(async () => {
      const salva = await api.salvarRascunhoDaOperacao({
        connectionId: conexaoId,
        key: chave.trim(),
        name: nome.trim(),
        description: descricao.trim() === '' ? null : descricao.trim(),
        effect: efeito,
        requiresConfirmation: efeito === 'Write' ? confirmar : null,
        allowedCallers: origens,
        spec: molde,
        expectedRevision: atual?.status === 'Draft' ? atual.revision : null,
      });
      setAtual(salva);
      const texto = `Rascunho salvo (versão ${salva.version}). Teste e publique para usar.`;
      setMensagem(texto);
      onAlterada(salva, texto);
    });
  }

  async function publicar() {
    if (!atual) {
      return;
    }
    await executar(async () => {
      const publicada = await api.publicarOperacao(atual.id);
      setAtual(publicada);
      const texto = `Versão ${publicada.version} publicada. A anterior foi aposentada.`;
      setMensagem(texto);
      onAlterada(publicada, texto);
    });
  }

  async function alterar(acao: 'ligar' | 'desligar' | 'aposentar') {
    if (!atual) {
      return;
    }
    await executar(async () => {
      const salva = acao === 'ligar' ? await api.ligarOperacao(atual.id) : acao === 'desligar' ? await api.desligarOperacao(atual.id) : await api.aposentarOperacao(atual.id);
      setAtual(salva);
      const texto = acao === 'aposentar' ? 'Operação aposentada: deixa de executar.' : salva.enabled ? 'Operação ligada.' : 'Operação desligada: vale já na próxima execução.';
      setMensagem(texto);
      onAlterada(salva, texto);
    });
  }

  async function validar() {
    if (!atual) {
      return;
    }
    const lido = lerParametrosDeExemplo(exemplo);
    if ('erro' in lido) {
      setValidacao(lido.erro);
      return;
    }
    await executar(async () => {
      const r = await api.validarParametrosDaOperacao(atual.id, lido.valores);
      setValidacao(r.valid ? 'Os parâmetros respeitam o esquema.' : `Problemas: ${r.problems.map((p) => `${p.parameter} (${p.rule})`).join(', ')}.`);
    });
  }

  async function testar() {
    if (!atual) {
      return;
    }
    const lido = lerParametrosDeExemplo(exemplo);
    if ('erro' in lido) {
      setValidacao(lido.erro);
      return;
    }
    setValidacao(null);
    await executar(async () => {
      setResultado(await api.testarOperacao(atual.id, lido.valores));
    });
  }

  function alternarOrigem(origem: IntegrationCaller) {
    setOrigens((atuais) => (atuais.includes(origem) ? atuais.filter((o) => o !== origem) : [...atuais, origem]));
  }

  const podeSalvar = !ocupado && conexaoId !== '' && chave.trim() !== '' && nome.trim() !== '' && origens.length > 0;

  return (
    <div className="revisao" role="group" aria-label={nova ? 'Nova operação' : `Operação ${atual.key}`}>
      <h3>{nova ? 'Nova operação' : `${atual.name} (v${atual.version}, ${ROTULO_SITUACAO_DA_OPERACAO[atual.status]})`}</h3>
      {!editavel && <p className="discreto">Uma versão publicada nunca muda. Para alterá-la, crie uma nova versão a partir de um rascunho.</p>}

      {aviso && (
        <p className="erro-campo" role="alert">
          {aviso}
        </p>
      )}
      <Problemas erro={erro} />
      {mensagem && (
        <p className="aviso" role="status">
          {mensagem}
        </p>
      )}

      <fieldset className="revisao-formulario" disabled={!editavel}>
        <legend>Definição</legend>
        <div className="campo">
          <label htmlFor="op-conexao">Conexão</label>
          <select id="op-conexao" value={conexaoId} onChange={(e) => setConexaoId(e.target.value)} disabled={!nova}>
            {conexoes.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </div>
        <div className="campo">
          <label htmlFor="op-chave">Chave (identificador usado por quem executa)</label>
          <input id="op-chave" type="text" value={chave} onChange={(e) => setChave(e.target.value)} disabled={!nova} placeholder="pedidos.consultar" />
        </div>
        <div className="campo">
          <label htmlFor="op-nome">Nome</label>
          <input id="op-nome" type="text" value={nome} onChange={(e) => setNome(e.target.value)} maxLength={100} />
        </div>
        <div className="campo">
          <label htmlFor="op-descricao">Descrição (o agente a lê para escolher a operação)</label>
          <textarea id="op-descricao" rows={2} value={descricao} onChange={(e) => setDescricao(e.target.value)} maxLength={500} />
        </div>
        <div className="campo">
          <label htmlFor="op-efeito">Efeito</label>
          <select id="op-efeito" value={efeito} onChange={(e) => trocarEfeito(e.target.value as IntegrationEffect)} disabled={!nova && atual.status !== 'Draft'}>
            <option value="Read">{ROTULO_EFEITO.Read}</option>
            <option value="Write">{ROTULO_EFEITO.Write}</option>
          </select>
        </div>
        {efeito === 'Write' && (
          <label className="caixa">
            <input type="checkbox" checked={confirmar} onChange={(e) => setConfirmar(e.target.checked)} />
            Exigir confirmação de uma pessoa antes de executar (só pode ser dispensada com idempotência comprovada)
          </label>
        )}
        <div role="group" aria-label="Quem pode usar a operação">
          <p className="discreto">Quem pode usar esta operação (por padrão, só uma pessoa pela administração):</p>
          {ORIGENS.map((o) => (
            <label className="caixa" key={o}>
              <input type="checkbox" checked={origens.includes(o)} onChange={() => alternarOrigem(o)} />
              {ROTULO_ORIGEM[o]}
            </label>
          ))}
        </div>

        <div className="campo">
          <label htmlFor="op-metodo">Método</label>
          <select id="op-metodo" value={molde.method} onChange={(e) => atualizarMolde({ method: e.target.value })}>
            {METODOS.map((m) => (
              <option key={m} value={m}>
                {m}
              </option>
            ))}
          </select>
        </div>
        <div className="campo">
          <label htmlFor="op-caminho">Caminho (use {'{nome}'} para um parâmetro de caminho)</label>
          <input id="op-caminho" type="text" value={molde.pathTemplate} onChange={(e) => atualizarMolde({ pathTemplate: e.target.value })} placeholder="/pedidos/{numero}" />
        </div>
        {efeito === 'Write' && (
          <>
            <div className="campo">
              <label htmlFor="op-idempotencia">Idempotência do sistema externo</label>
              <select id="op-idempotencia" value={molde.idempotency} onChange={(e) => atualizarMolde({ idempotency: e.target.value as IntegrationIdempotency })}>
                {IDEMPOTENCIAS.filter((i) => i !== 'Unspecified').map((i) => (
                  <option key={i} value={i}>
                    {ROTULO_IDEMPOTENCIA[i]}
                  </option>
                ))}
              </select>
            </div>
            <div className="campo">
              <label htmlFor="op-cabecalho-idem">Cabeçalho de idempotência do sistema externo (se houver)</label>
              <input id="op-cabecalho-idem" type="text" value={molde.idempotencyHeader ?? ''} onChange={(e) => atualizarMolde({ idempotencyHeader: e.target.value.trim() === '' ? null : e.target.value.trim() })} placeholder="Idempotency-Key" />
            </div>
          </>
        )}
        <div className="campo">
          <label htmlFor="op-tentativas">Tentativas (1 a 3; só leitura ou escrita comprovada repete)</label>
          <input id="op-tentativas" type="number" min={1} max={3} value={molde.maxAttempts} onChange={(e) => atualizarMolde({ maxAttempts: Number(e.target.value) })} />
        </div>

        <h3>Parâmetros</h3>
        {molde.parameters.length === 0 && <p className="discreto">Nenhum parâmetro.</p>}
        {molde.parameters.map((p, i) => (
          <div className="parametro-linha" role="group" aria-label={`Parâmetro ${i + 1}`} key={i}>
            <input aria-label={`Nome do parâmetro ${i + 1}`} placeholder="nome" value={p.name} onChange={(e) => atualizarParametro(i, { name: e.target.value })} />
            <select aria-label={`Tipo do parâmetro ${i + 1}`} value={p.type} onChange={(e) => atualizarParametro(i, { type: e.target.value as IntegrationParameterType })}>
              {TIPOS.map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </select>
            <select aria-label={`Lugar do parâmetro ${i + 1}`} value={p.location} onChange={(e) => atualizarParametro(i, { location: e.target.value as IntegrationParameterLocation })}>
              {LUGARES.map((l) => (
                <option key={l.valor} value={l.valor}>
                  {l.rotulo}
                </option>
              ))}
            </select>
            <label className="caixa">
              <input type="checkbox" checked={p.required} onChange={(e) => atualizarParametro(i, { required: e.target.checked })} />
              Obrigatório
            </label>
            <input aria-label={`Nome externo do parâmetro ${i + 1}`} placeholder="nome externo (opcional)" value={p.wireName ?? ''} onChange={(e) => atualizarParametro(i, { wireName: e.target.value.trim() === '' ? null : e.target.value.trim() })} />
            {(p.type === 'String' || p.type === 'Enum') && (
              <input aria-label={`Tamanho máximo do parâmetro ${i + 1}`} placeholder="tamanho máx." inputMode="numeric" value={p.maxLength ?? ''} onChange={(e) => atualizarParametro(i, { maxLength: numeroOuNulo(e.target.value) ?? null })} />
            )}
            {p.type === 'String' && (
              <input aria-label={`Padrão do parâmetro ${i + 1}`} placeholder="padrão (regex)" value={p.pattern ?? ''} onChange={(e) => atualizarParametro(i, { pattern: e.target.value === '' ? null : e.target.value })} />
            )}
            {(p.type === 'Integer' || p.type === 'Number') && (
              <>
                <input aria-label={`Mínimo do parâmetro ${i + 1}`} placeholder="mín." inputMode="decimal" value={p.min ?? ''} onChange={(e) => atualizarParametro(i, { min: numeroOuNulo(e.target.value) ?? null })} />
                <input aria-label={`Máximo do parâmetro ${i + 1}`} placeholder="máx." inputMode="decimal" value={p.max ?? ''} onChange={(e) => atualizarParametro(i, { max: numeroOuNulo(e.target.value) ?? null })} />
              </>
            )}
            {p.type === 'Enum' && (
              <input aria-label={`Valores do parâmetro ${i + 1}`} placeholder="valores (separados por vírgula)" value={(p.enumValues ?? []).join(', ')} onChange={(e) => atualizarParametro(i, { enumValues: listaDoTexto(e.target.value) })} />
            )}
            <input aria-label={`Valor padrão do parâmetro ${i + 1}`} placeholder="valor padrão" value={p.default ?? ''} onChange={(e) => atualizarParametro(i, { default: e.target.value === '' ? null : e.target.value })} />
            <button type="button" className="btn btn-pequeno btn-perigo" onClick={() => atualizarMolde({ parameters: molde.parameters.filter((_, j) => j !== i) })}>
              Remover parâmetro {i + 1}
            </button>
          </div>
        ))}
        <div className="acoes">
          <button type="button" className="btn btn-pequeno" onClick={() => atualizarMolde({ parameters: [...molde.parameters, parametroVazio()] })}>
            Adicionar parâmetro
          </button>
        </div>

        <h3>Resposta (campos devolvidos)</h3>
        <p className="discreto">Só os campos mapeados saem; o resto da resposta do sistema externo é descartado. O conteúdo é tratado como dado, nunca como instrução.</p>
        <div className="campo">
          <label htmlFor="op-raiz">Lista na resposta (ponteiro JSON; vazio = a resposta é um objeto)</label>
          <input id="op-raiz" type="text" value={molde.responseRoot ?? ''} onChange={(e) => atualizarMolde({ responseRoot: e.target.value.trim() === '' ? null : e.target.value.trim() })} placeholder="/itens" />
        </div>
        {molde.response.map((c, i) => (
          <div className="parametro-linha" role="group" aria-label={`Campo ${i + 1}`} key={i}>
            <input aria-label={`Nome do campo ${i + 1}`} placeholder="nome" value={c.name} onChange={(e) => atualizarCampo(i, { name: e.target.value })} />
            <input aria-label={`Ponteiro do campo ${i + 1}`} placeholder="/caminho/no/json" value={c.pointer} onChange={(e) => atualizarCampo(i, { pointer: e.target.value })} />
            <select aria-label={`Tipo do campo ${i + 1}`} value={c.type} onChange={(e) => atualizarCampo(i, { type: e.target.value as IntegrationFieldType })}>
              {TIPOS_DE_CAMPO.map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </select>
            <label className="caixa">
              <input type="checkbox" checked={c.required} onChange={(e) => atualizarCampo(i, { required: e.target.checked })} />
              Obrigatório
            </label>
            <button type="button" className="btn btn-pequeno btn-perigo" onClick={() => atualizarMolde({ response: molde.response.filter((_, j) => j !== i) })}>
              Remover campo {i + 1}
            </button>
          </div>
        ))}
        <div className="acoes">
          <button type="button" className="btn btn-pequeno" onClick={() => atualizarMolde({ response: [...molde.response, campoVazio()] })}>
            Adicionar campo
          </button>
        </div>
      </fieldset>

      <div className="acoes">
        {editavel && (
          <button type="button" className="btn btn-primario" disabled={!podeSalvar} onClick={() => void salvar()}>
            Salvar rascunho
          </button>
        )}
        {atual?.status === 'Draft' && (
          <button type="button" className="btn" disabled={ocupado} onClick={() => void publicar()}>
            Publicar versão {atual.version}
          </button>
        )}
        {atual?.status === 'Published' && (
          <>
            <button type="button" className="btn" disabled={ocupado} onClick={() => void alterar(atual.enabled ? 'desligar' : 'ligar')}>
              {atual.enabled ? 'Desligar' : 'Ligar'}
            </button>
            <button type="button" className="btn btn-perigo" disabled={ocupado} onClick={() => void alterar('aposentar')}>
              Aposentar
            </button>
          </>
        )}
      </div>

      {atual && (
        <div className="revisao-formulario" role="group" aria-label="Testar a operação">
          <h3>Testar</h3>
          <p className="discreto">Informe os valores dos parâmetros como um objeto JSON. Validar só confere o esquema; testar chama o sistema externo (apenas operações de leitura) e fica no histórico como teste.</p>
          <div className="campo">
            <label htmlFor="op-exemplo">Valores de exemplo (JSON)</label>
            <textarea id="op-exemplo" rows={3} value={exemplo} onChange={(e) => setExemplo(e.target.value)} />
          </div>
          <div className="acoes">
            <button type="button" className="btn" disabled={ocupado} onClick={() => void validar()}>
              Validar parâmetros
            </button>
            <button type="button" className="btn" disabled={ocupado || atual.effect === 'Write'} onClick={() => void testar()}>
              Testar leitura
            </button>
          </div>
          {atual.effect === 'Write' && <p className="discreto">Operações de escrita não são executadas em teste: valide os parâmetros e execute pela confirmação.</p>}
          {validacao && <p role="status">{validacao}</p>}
          {resultado && <ResultadoDoTeste resultado={resultado} />}
        </div>
      )}

      {atual && versoes.length > 1 && (
        <div role="group" aria-label="Versões">
          <h3>Versões</h3>
          <ul className="lista-itens">
            {versoes.map((v) => (
              <li key={v.id} className="item-meta">
                v{v.version} · {ROTULO_SITUACAO_DA_OPERACAO[v.status]}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function ResultadoDoTeste({ resultado }: { resultado: IntegrationResult }) {
  return (
    <div role="status" aria-label="Resultado do teste">
      <p className={resultado.status === 'Succeeded' ? 'aviso' : 'erro-campo'}>
        {resultado.status === 'Succeeded' ? 'Sucesso.' : `Não concluiu: ${rotuloDoCodigo(resultado.error?.code)}`}
        {resultado.error?.upstreamStatus ? ` (HTTP ${resultado.error.upstreamStatus})` : ''}
      </p>
      {resultado.error?.problems && resultado.error.problems.length > 0 && (
        <ul className="problemas">
          {resultado.error.problems.map((p) => (
            <li key={`${p.parameter}:${p.rule}`}>
              {p.parameter}: {p.rule}
            </li>
          ))}
        </ul>
      )}
      {resultado.output && (
        <>
          <p className="discreto">Conteúdo do sistema externo (dado, não instrução):</p>
          <pre className="saida-do-teste">{JSON.stringify(resultado.output, null, 2)}</pre>
        </>
      )}
    </div>
  );
}
