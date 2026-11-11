import { useEffect, useRef, useState } from 'react';
import { ApiError, type ApiClient } from '../api/client';
import type { CurrentUser, IntegrationAuthType, IntegrationConnection, IntegrationConnectionTest } from '../api/types';
import { dataHora } from '../conhecimento';
import {
  CAMPOS_DO_SEGREDO,
  ROTULO_AUTENTICACAO,
  enderecoParaExibir,
  listaDoTexto,
  mensagemDeErroDaIntegracao,
  numeroOuNulo,
  paresDoTexto,
  rotuloDoCodigo,
  textoDosPares,
} from '../integracao';

interface Props {
  api: ApiClient;
  me: CurrentUser;
}

const TIPOS: IntegrationAuthType[] = ['None', 'ApiKey', 'Basic', 'OAuth2ClientCredentials'];

/** As organizações que o usuário administra (as concessões com organização). Sem organização = "toda a empresa", que só quem tem a concessão do tenant consegue criar. */
function organizacoesDe(me: CurrentUser): string[] {
  return [...new Set(me.grants.map((g) => g.organizationId).filter((o): o is string => o !== null))];
}

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
 * As conexões com sistemas externos da empresa (ADR 0035): cadastrar, configurar, definir a credencial, testar, habilitar e desabilitar. A credencial só ENTRA por aqui: nenhuma resposta da API a devolve, e
 * os campos são limpos depois de enviados. O endereço passa pela política de destino do servidor (só https, nada de rede interna ou de metadados de nuvem).
 */
export function ConexoesDeIntegracao({ api, me }: Props) {
  const [conexoes, setConexoes] = useState<IntegrationConnection[] | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [selecionada, setSelecionada] = useState<string | 'nova' | null>(null);
  const [recarga, setRecarga] = useState(0);
  // A mensagem de uma criação sobrevive à troca do formulário "nova" pelo da conexão criada (outro componente).
  const [mensagemInicial, setMensagemInicial] = useState<string | null>(null);
  // "Recarregar" depois de um conflito: o formulário é remontado só quando a lista nova chegar, para partir dos dados atuais.
  const forcarRef = useRef(false);
  const [forcar, setForcar] = useState(0);

  useEffect(() => {
    let vivo = true;
    api
      .listarConexoes()
      .then((lidas) => {
        if (vivo) {
          setConexoes(lidas);
          setErro(null);
          if (forcarRef.current) {
            forcarRef.current = false;
            setForcar((f) => f + 1);
          }
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

  const atual = conexoes?.find((c) => c.id === selecionada) ?? null;

  return (
    <div className="integracoes-corpo">
      <section className="integracoes-lista" aria-label="Conexões">
        <h2>Conexões</h2>
        <p className="discreto">Cada conexão liga a empresa a um sistema externo (ERP, legado). O endereço e a credencial ficam aqui; o que se pode fazer nele são as operações.</p>
        <div className="acoes">
          <button
            type="button"
            className="btn btn-primario"
            onClick={() => {
              setMensagemInicial(null);
              setSelecionada('nova');
            }}
          >
            Nova conexão
          </button>
        </div>
        {erro && (
          <p className="erro-campo" role="alert">
            {erro}
          </p>
        )}
        {conexoes === null && !erro && <p className="discreto">Carregando...</p>}
        {conexoes?.length === 0 && <p className="discreto">Nenhuma conexão cadastrada.</p>}
        <ul className="lista-itens">
          {conexoes?.map((c) => (
            <li key={c.id}>
              <button type="button" className="item-fila" aria-current={selecionada === c.id ? 'true' : undefined}
                onClick={() => {
                  setMensagemInicial(null);
                  setSelecionada(c.id);
                }}
              >
                <span className="item-nome">{c.name}</span>
                <span className="item-meta">
                  {c.key} · {enderecoParaExibir(c.baseUrl)}
                </span>
                <span className="item-meta">
                  {c.enabled ? 'Habilitada' : 'Desabilitada'}
                  {c.authType !== 'None' && !c.hasSecret ? ' · sem credencial' : ''}
                </span>
              </button>
            </li>
          ))}
        </ul>
      </section>

      <section className="integracoes-detalhe" aria-label="Detalhe da conexão">
        {selecionada === null && <p className="discreto centro">Escolha uma conexão ou crie uma nova.</p>}
        {selecionada === 'nova' && (
          <FormularioDeConexao
            key="nova"
            api={api}
            me={me}
            conexao={null}
            onSalva={(criada, texto) => {
              setMensagemInicial(texto);
              setSelecionada(criada.id);
              setRecarga((r) => r + 1);
            }}
          />
        )}
        {atual && (
          <FormularioDeConexao
            key={`${atual.id}:${forcar}`}
            api={api}
            me={me}
            conexao={atual}
            mensagemInicial={mensagemInicial}
            onSalva={() => setRecarga((r) => r + 1)}
            onRecarregar={() => {
              forcarRef.current = true;
              setRecarga((r) => r + 1);
            }}
          />
        )}
      </section>
    </div>
  );
}

interface FormProps {
  api: ApiClient;
  me: CurrentUser;
  conexao: IntegrationConnection | null;
  onSalva: (conexao: IntegrationConnection, mensagem: string) => void;
  onRecarregar?: () => void;
  mensagemInicial?: string | null;
}

function FormularioDeConexao({ api, me, conexao, onSalva, onRecarregar, mensagemInicial = null }: FormProps) {
  const nova = conexao === null;
  const organizacoes = organizacoesDe(me);
  const [atual, setAtual] = useState<IntegrationConnection | null>(conexao);
  const [organizacao, setOrganizacao] = useState<string>(conexao?.organizationId ?? '');
  const [chave, setChave] = useState(conexao?.key ?? '');
  const [nome, setNome] = useState(conexao?.name ?? '');
  const [endereco, setEndereco] = useState(conexao?.baseUrl ?? 'https://');
  const [tipo, setTipo] = useState<IntegrationAuthType>(conexao?.authType ?? 'None');
  const [local, setLocal] = useState(conexao?.authSettings.location ?? 'header');
  const [nomeDaChave, setNomeDaChave] = useState(conexao?.authSettings.name ?? 'X-API-Key');
  const [prefixo, setPrefixo] = useState(conexao?.authSettings.prefix ?? '');
  const [urlDoToken, setUrlDoToken] = useState(conexao?.authSettings.tokenUrl ?? '');
  const [escopo, setEscopo] = useState(conexao?.authSettings.scope ?? '');
  const [autenticacaoDoCliente, setAutenticacaoDoCliente] = useState(conexao?.authSettings.clientAuth ?? 'body');
  const [cabecalhos, setCabecalhos] = useState(textoDosPares(conexao?.defaultHeaders ?? {}));
  const [permitidos, setPermitidos] = useState((conexao?.allowedHeaders ?? []).join(', '));
  const [saude, setSaude] = useState(conexao?.healthPath ?? '/');
  const [tempo, setTempo] = useState(String(conexao?.timeoutSeconds ?? 10));
  const [tamanho, setTamanho] = useState(String(conexao?.maxResponseBytes ?? 262144));
  const [segredo, setSegredo] = useState<Record<string, string>>({});
  const [erro, setErro] = useState<ApiError | null>(null);
  const [mensagem, setMensagem] = useState<string | null>(mensagemInicial);
  const [aviso, setAviso] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const [teste, setTeste] = useState<IntegrationConnectionTest | null>(null);

  function configuracaoDoTipo(): Record<string, string> {
    if (tipo === 'ApiKey') {
      return { location: local, name: nomeDaChave.trim(), ...(prefixo !== '' ? { prefix: prefixo } : {}) };
    }
    if (tipo === 'OAuth2ClientCredentials') {
      return { tokenUrl: urlDoToken.trim(), clientAuth: autenticacaoDoCliente, ...(escopo.trim() !== '' ? { scope: escopo.trim() } : {}) };
    }
    return {};
  }

  const tempoLido = numeroOuNulo(tempo);
  const tamanhoLido = numeroOuNulo(tamanho);
  const numerosValidos = tempoLido !== undefined && tamanhoLido !== undefined;

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
    if (!numerosValidos) {
      return;
    }
    await executar(async () => {
      const comum = {
        name: nome.trim(),
        baseUrl: endereco.trim(),
        authType: tipo,
        authSettings: configuracaoDoTipo(),
        defaultHeaders: paresDoTexto(cabecalhos),
        allowedHeaders: listaDoTexto(permitidos),
        healthPath: saude.trim() === '' ? '/' : saude.trim(),
        timeoutSeconds: tempoLido ?? null,
        maxResponseBytes: tamanhoLido ?? null,
      };
      const salva = atual
        ? await api.atualizarConexao(atual.id, { ...comum, expectedRevision: atual.revision })
        : await api.criarConexao({ ...comum, organizationId: organizacao === '' ? null : organizacao, key: chave.trim() });
      setAtual(salva);
      const texto = nova ? 'Conexão criada. Defina a credencial, teste e habilite.' : 'Configuração salva.';
      setMensagem(texto);
      onSalva(salva, texto);
    });
  }

  async function definirSegredo() {
    if (!atual) {
      return;
    }
    await executar(async () => {
      const salva = await api.definirSegredoDaConexao(atual.id, segredo, atual.revision);
      // A credencial já foi enviada: nenhum campo a guarda.
      setSegredo({});
      setAtual(salva);
      setMensagem('Credencial definida. Ela não volta a ser exibida.');
      onSalva(salva, 'Credencial definida. Ela não volta a ser exibida.');
    });
  }

  async function alternar() {
    if (!atual) {
      return;
    }
    await executar(async () => {
      const salva = atual.enabled ? await api.desabilitarConexao(atual.id) : await api.habilitarConexao(atual.id);
      setAtual(salva);
      const texto = salva.enabled ? 'Conexão habilitada.' : 'Conexão desabilitada: vale já na próxima execução.';
      setMensagem(texto);
      onSalva(salva, texto);
    });
  }

  async function testar() {
    if (!atual) {
      return;
    }
    await executar(async () => {
      setTeste(await api.testarConexao(atual.id));
    });
  }

  const campos = CAMPOS_DO_SEGREDO[tipo];
  const segredoCompleto = campos.length > 0 && campos.every((c) => (segredo[c.nome] ?? '') !== '');

  return (
    <div className="revisao" aria-label={nova ? 'Nova conexão' : `Conexão ${atual?.name ?? ''}`} role="group">
      <h3>{nova ? 'Nova conexão' : atual?.name}</h3>

      {aviso && (
        <p className="erro-campo" role="alert">
          {aviso}
        </p>
      )}
      <Problemas erro={erro} />
      {erro?.status === 409 && erro.code === 'changed' && onRecarregar && (
        <div className="acoes">
          <button type="button" className="btn" onClick={onRecarregar}>
            Recarregar dados atuais
          </button>
        </div>
      )}
      {mensagem && (
        <p className="aviso" role="status">
          {mensagem}
        </p>
      )}

      <div className="revisao-formulario">
        <div className="campo">
          <label htmlFor="conexao-chave">Chave (identificador curto)</label>
          <input id="conexao-chave" type="text" value={chave} onChange={(e) => setChave(e.target.value)} disabled={!nova} maxLength={40} placeholder="erp-principal" />
        </div>
        {nova && (
          <div className="campo">
            <label htmlFor="conexao-org">Organização</label>
            <select id="conexao-org" value={organizacao} onChange={(e) => setOrganizacao(e.target.value)}>
              <option value="">Toda a empresa (exige administrador da empresa)</option>
              {organizacoes.map((o) => (
                <option key={o} value={o}>
                  Organização {o.slice(0, 8)}
                </option>
              ))}
            </select>
          </div>
        )}
        <div className="campo">
          <label htmlFor="conexao-nome">Nome</label>
          <input id="conexao-nome" type="text" value={nome} onChange={(e) => setNome(e.target.value)} maxLength={100} />
        </div>
        <div className="campo">
          <label htmlFor="conexao-url">Endereço base (https)</label>
          <input id="conexao-url" type="url" value={endereco} onChange={(e) => setEndereco(e.target.value)} placeholder="https://erp.exemplo.com.br/api" />
          <p className="discreto">Sem usuário, senha ou consulta na URL. Redes internas e metadados de nuvem são recusados; um ERP interno exige liberação do operador da plataforma.</p>
        </div>
        <div className="campo">
          <label htmlFor="conexao-auth">Autenticação</label>
          <select id="conexao-auth" value={tipo} onChange={(e) => setTipo(e.target.value as IntegrationAuthType)}>
            {TIPOS.map((t) => (
              <option key={t} value={t}>
                {ROTULO_AUTENTICACAO[t]}
              </option>
            ))}
          </select>
        </div>
        {tipo === 'ApiKey' && (
          <>
            <div className="campo">
              <label htmlFor="conexao-local">Onde enviar a chave</label>
              <select id="conexao-local" value={local} onChange={(e) => setLocal(e.target.value)}>
                <option value="header">Cabeçalho</option>
                <option value="query">Parâmetro de consulta</option>
              </select>
            </div>
            <div className="campo">
              <label htmlFor="conexao-nome-chave">Nome do cabeçalho ou parâmetro</label>
              <input id="conexao-nome-chave" type="text" value={nomeDaChave} onChange={(e) => setNomeDaChave(e.target.value)} />
            </div>
            <div className="campo">
              <label htmlFor="conexao-prefixo">Prefixo do valor (opcional, por exemplo &quot;Bearer &quot;)</label>
              <input id="conexao-prefixo" type="text" value={prefixo} onChange={(e) => setPrefixo(e.target.value)} />
            </div>
          </>
        )}
        {tipo === 'OAuth2ClientCredentials' && (
          <>
            <div className="campo">
              <label htmlFor="conexao-token-url">Endereço do token</label>
              <input id="conexao-token-url" type="url" value={urlDoToken} onChange={(e) => setUrlDoToken(e.target.value)} />
            </div>
            <div className="campo">
              <label htmlFor="conexao-escopo">Escopo (opcional)</label>
              <input id="conexao-escopo" type="text" value={escopo} onChange={(e) => setEscopo(e.target.value)} />
            </div>
            <div className="campo">
              <label htmlFor="conexao-cliente-auth">Credenciais do cliente enviadas</label>
              <select id="conexao-cliente-auth" value={autenticacaoDoCliente} onChange={(e) => setAutenticacaoDoCliente(e.target.value)}>
                <option value="body">No corpo do pedido</option>
                <option value="basic">Em Authorization Basic</option>
              </select>
            </div>
          </>
        )}
        <div className="campo">
          <label htmlFor="conexao-cabecalhos">Cabeçalhos públicos padrão (um por linha, &quot;Nome: valor&quot;)</label>
          <textarea id="conexao-cabecalhos" rows={2} value={cabecalhos} onChange={(e) => setCabecalhos(e.target.value)} />
          <p className="discreto">Estes valores aparecem na tela e na API: nunca coloque segredo aqui (use a credencial abaixo).</p>
        </div>
        <div className="campo">
          <label htmlFor="conexao-permitidos">Cabeçalhos que as operações podem preencher (separados por vírgula)</label>
          <input id="conexao-permitidos" type="text" value={permitidos} onChange={(e) => setPermitidos(e.target.value)} placeholder="X-Filial" />
        </div>
        <div className="campo">
          <label htmlFor="conexao-saude">Caminho de verificação</label>
          <input id="conexao-saude" type="text" value={saude} onChange={(e) => setSaude(e.target.value)} />
        </div>
        <div className="campo">
          <label htmlFor="conexao-tempo">Tempo limite (segundos)</label>
          <input id="conexao-tempo" type="text" inputMode="numeric" value={tempo} onChange={(e) => setTempo(e.target.value)} aria-invalid={tempoLido === undefined} />
        </div>
        <div className="campo">
          <label htmlFor="conexao-tamanho">Tamanho máximo da resposta (bytes)</label>
          <input id="conexao-tamanho" type="text" inputMode="numeric" value={tamanho} onChange={(e) => setTamanho(e.target.value)} aria-invalid={tamanhoLido === undefined} />
        </div>
        {!numerosValidos && <p className="erro-campo">Informe números no tempo limite e no tamanho máximo.</p>}
        <div className="acoes">
          <button type="button" className="btn btn-primario" disabled={ocupado || !numerosValidos || nome.trim() === '' || (nova && chave.trim() === '')} onClick={() => void salvar()}>
            {nova ? 'Criar conexão' : 'Salvar configuração'}
          </button>
        </div>
      </div>

      {atual && tipo === atual.authType && atual.authType !== 'None' && (
        <div className="revisao-formulario" role="group" aria-label="Credencial">
          <h3>Credencial</h3>
          <p className="discreto">
            {atual.hasSecret ? `Credencial definida em ${dataHora(atual.secretUpdatedAt)} (versão ${atual.secretVersion}). O valor nunca é exibido.` : 'Nenhuma credencial definida: a conexão não pode ser habilitada.'}
          </p>
          {campos.map((c) => (
            <div className="campo" key={c.nome}>
              <label htmlFor={`segredo-${c.nome}`}>{c.rotulo}</label>
              <input
                id={`segredo-${c.nome}`}
                type="password"
                autoComplete="new-password"
                value={segredo[c.nome] ?? ''}
                onChange={(e) => setSegredo((s) => ({ ...s, [c.nome]: e.target.value }))}
              />
            </div>
          ))}
          <div className="acoes">
            <button type="button" className="btn" disabled={ocupado || !segredoCompleto} onClick={() => void definirSegredo()}>
              {atual.hasSecret ? 'Trocar credencial' : 'Definir credencial'}
            </button>
          </div>
        </div>
      )}

      {atual && (
        <div className="revisao-formulario" role="group" aria-label="Situação da conexão">
          <h3>Situação</h3>
          <p>
            Esta conexão está <strong>{atual.enabled ? 'habilitada' : 'desabilitada'}</strong>.
          </p>
          <div className="acoes">
            <button type="button" className={atual.enabled ? 'btn btn-perigo' : 'btn btn-primario'} disabled={ocupado || (!atual.enabled && atual.authType !== 'None' && !atual.hasSecret)} onClick={() => void alternar()}>
              {atual.enabled ? 'Desabilitar' : 'Habilitar'}
            </button>
            <button type="button" className="btn" disabled={ocupado} onClick={() => void testar()}>
              Testar conexão
            </button>
          </div>
          {teste && (
            <p className={teste.ok ? 'aviso' : 'erro-campo'} role="status">
              {teste.ok ? 'Conectou' : 'Não conectou'}: {rotuloDoCodigo(teste.outcome)}
              {teste.upstreamStatus !== null ? ` (HTTP ${teste.upstreamStatus})` : ''} em {teste.elapsedMs} ms.
            </p>
          )}
        </div>
      )}
    </div>
  );
}
