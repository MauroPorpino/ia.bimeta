import { useCallback, useEffect, useState } from 'react';
import type { ApiClient } from '../api/client';
import type { ConfiguracaoDeIa, CurrentUser, DiagnosticoDeIa, ModeloDeIa, ProvedorDeIa, TesteDeConectividade, TesteDeGeracao } from '../api/types';
import {
  ROTULO_DA_CONECTIVIDADE,
  ROTULO_DA_GERACAO,
  confirmado,
  dataHora,
  ehFalhaDePermissao,
  elegivelComoPadrao,
  mensagemDeErroDeIa,
  podeDeIa,
  provedoresOrdenados,
  rotuloDoProvedor,
  textoDaDesatualizacao,
  textoDoAviso,
  textoDoCodigo,
  triEstado,
} from '../ia';

interface Props {
  api: ApiClient;
  me: CurrentUser;
}

type Area = 'provedores' | 'padrao';

/**
 * Configurações → Inteligência artificial (Etapa 05). A interface mostra o que o servidor informa e NUNCA inventa estado positivo: "implementado" e "autorizado" não são "operacional"; um diagnóstico só confirma
 * se existe e não está desatualizado. A chave é digitada num campo de senha, enviada uma vez e apagada do estado: nunca é lida de volta, guardada no navegador nem exibida. O endereço dos servidores, os preços e
 * os limites comerciais são da plataforma e não aparecem aqui como campos.
 */
export function IaProvedores({ api, me }: Props) {
  const [dados, setDados] = useState<ConfiguracaoDeIa | null>(null);
  const [erro, setErro] = useState<{ texto: string; permissao: boolean } | null>(null);
  const [area, setArea] = useState<Area>('provedores');

  const carregar = useCallback(async () => {
    try {
      setDados(await api.obterConfiguracaoDeIa());
      setErro(null);
    } catch (falha) {
      setErro({ texto: mensagemDeErroDeIa(falha), permissao: ehFalhaDePermissao(falha) });
    }
  }, [api]);

  useEffect(() => {
    void carregar();
  }, [carregar]);

  return (
    <section className="ia" aria-label="Inteligência artificial">
      <h2>Inteligência artificial</h2>
      <p className="discreto">
        Configure os provedores de IA da empresa. Você usa a credencial do seu próprio contrato com cada provedor comercial; o Ollama é uma IA local oferecida pela infraestrutura, sem chave. Salvar uma
        configuração não chama o provedor: o que foi verificado ou testado aparece em cada modelo, com a data.
      </p>

      {erro && (
        <div role="alert" className="erro-campo">
          <p>{erro.permissao ? 'Permissão insuficiente: você não pode consultar a configuração de IA desta empresa.' : erro.texto}</p>
          {!erro.permissao && (
            <button type="button" className="btn" onClick={() => void carregar()}>
              Tentar de novo
            </button>
          )}
        </div>
      )}
      {!erro && dados === null && <p className="discreto">Carregando a configuração de IA...</p>}

      {dados && (
        <>
          <ul className="avisos-ia" aria-label="Avisos da configuração">
            {dados.warnings.map((aviso) => (
              <li key={aviso} role="status" className="aviso">
                {textoDoAviso(aviso)}
              </li>
            ))}
          </ul>

          <nav className="abas-ia" aria-label="Áreas da configuração de IA">
            <button type="button" className="area-aba" aria-current={area === 'provedores' ? 'page' : undefined} onClick={() => setArea('provedores')}>
              Provedores
            </button>
            <button type="button" className="area-aba" aria-current={area === 'padrao' ? 'page' : undefined} onClick={() => setArea('padrao')}>
              Modelo padrão
            </button>
          </nav>

          {area === 'provedores' && (
            <div className="ia-provedores">
              {provedoresOrdenados(dados.providers).map((p) => (
                <CartaoDoProvedor key={p.providerId} api={api} me={me} provedor={p} aoMudar={carregar} />
              ))}
            </div>
          )}
          {area === 'padrao' && <ModeloPadrao api={api} me={me} dados={dados} aoMudar={carregar} />}
        </>
      )}
    </section>
  );
}

function DiagnosticoLinha({ rotulo, diagnostico, rotulos, vazio }: { rotulo: string; diagnostico: DiagnosticoDeIa | null; rotulos: Record<string, string>; vazio: string }) {
  if (diagnostico === null) {
    return (
      <p>
        <strong>{rotulo}:</strong> {vazio}
      </p>
    );
  }
  const desatualizado = textoDaDesatualizacao(diagnostico);
  const codigo = diagnostico.state === 'Succeeded' || diagnostico.state === 'Verified' ? null : textoDoCodigo(diagnostico.code);
  return (
    <p>
      <strong>{rotulo}:</strong> {rotulos[diagnostico.state] ?? diagnostico.state}
      {codigo ? ` (${codigo})` : ''} em {dataHora(diagnostico.checkedAt)}
      {desatualizado && <em> — {desatualizado}</em>}
      {diagnostico.state === 'Succeeded' && diagnostico.inputTokens !== null && !desatualizado ? ` — ${diagnostico.inputTokens} tokens de entrada, ${diagnostico.outputTokens ?? 0} de saída` : ''}
    </p>
  );
}

function valorDoServidor(diagnostico: DiagnosticoDeIa | null, valor: boolean | null | undefined): string {
  if (diagnostico === null) {
    return 'não verificado';
  }
  const texto = triEstado(valor ?? null);
  return diagnostico.stale ? `${texto} (desatualizado)` : texto;
}

/** Os seis estados do Ollama, separados: nenhum é presumido a partir de outro. */
function EstadosDoOllama({ modelo }: { modelo: ModeloDeIa }) {
  const c = modelo.connectivity;
  const validado = confirmado(modelo.generation, 'Succeeded');
  return (
    <dl className="metricas" aria-label={`Estados do modelo ${modelo.modelId}`}>
      <dt>Modelo cadastrado no catálogo</dt>
      <dd>sim</dd>
      <dt>Modelo habilitado</dt>
      <dd>{modelo.isDefault ? 'sim (padrão da empresa)' : 'sim (autorizado pela plataforma)'}</dd>
      <dt>Servidor acessível</dt>
      <dd>{valorDoServidor(c, c?.reachable)}</dd>
      <dt>Modelo instalado</dt>
      <dd>{valorDoServidor(c, c?.modelAvailable)}</dd>
      <dt>Capacidade conversacional declarada</dt>
      <dd>{valorDoServidor(c, c?.conversationCapable)}</dd>
      <dt>Compatibilidade funcional validada</dt>
      <dd>{validado ? `sim, pelo teste de geração de ${dataHora(modelo.generation!.checkedAt)}` : 'não validada (execute o teste de geração)'}</dd>
    </dl>
  );
}

function CartaoDoProvedor({ api, me, provedor, aoMudar }: { api: ApiClient; me: CurrentUser; provedor: ProvedorDeIa; aoMudar: () => Promise<void> }) {
  const nome = rotuloDoProvedor(provedor.providerId);
  const [formulario, setFormulario] = useState<'nenhum' | 'credencial' | 'remocao' | 'geracao'>('nenhum');
  const [chave, setChave] = useState('');
  const [modeloDoTeste, setModeloDoTeste] = useState(provedor.models[0]?.modelId ?? '');
  const [confirmouCobranca, setConfirmouCobranca] = useState(false);
  const [trabalhando, setTrabalhando] = useState(false);
  const [mensagem, setMensagem] = useState<{ tipo: 'ok' | 'erro'; texto: string } | null>(null);
  const [conectividade, setConectividade] = useState<TesteDeConectividade | null>(null);
  const [geracao, setGeracao] = useState<TesteDeGeracao | null>(null);

  const exigeChave = provedor.credential.required;
  const podeCredencial = exigeChave && podeDeIa(me, 'AiCredentialManage');
  const podeConectividade = podeDeIa(me, 'AiConnectivityTest');
  const podeGeracao = podeDeIa(me, 'AiGenerationTest');
  const semCredencial = exigeChave && !provedor.credential.present;
  const comercial = provedor.supply === 'Byok';

  async function executar(acao: () => Promise<void>) {
    setTrabalhando(true);
    setMensagem(null);
    try {
      await acao();
    } catch (falha) {
      setMensagem({ tipo: 'erro', texto: mensagemDeErroDeIa(falha) });
    } finally {
      setTrabalhando(false);
    }
  }

  async function salvarChave() {
    const valor = chave;
    // A chave sai do estado ANTES do envio terminar: nenhum caminho (sucesso, falha ou fechamento do formulário) a deixa na memória da tela.
    setChave('');
    await executar(async () => {
      await api.salvarCredencialDeIa(provedor.providerId, valor);
      setFormulario('nenhum');
      setConectividade(null);
      setGeracao(null);
      setMensagem({ tipo: 'ok', texto: 'Credencial salva (cifrada). Ela não será exibida de novo. Os testes anteriores deste provedor foram invalidados.' });
      await aoMudar();
    });
  }

  async function removerChave() {
    await executar(async () => {
      const r = await api.removerCredencialDeIa(provedor.providerId);
      setFormulario('nenhum');
      setConectividade(null);
      setGeracao(null);
      setMensagem({ tipo: 'ok', texto: r.revoked ? 'Credencial removida.' : 'Não havia credencial cadastrada.' });
      await aoMudar();
    });
  }

  async function testarConexao() {
    await executar(async () => {
      const r = await api.testarConectividadeDeIa(provedor.providerId, modeloDoTeste || null);
      setConectividade(r);
      await aoMudar();
    });
  }

  async function testarGeracao() {
    await executar(async () => {
      const r = await api.testarGeracaoDeIa(provedor.providerId, modeloDoTeste, true);
      setGeracao(r);
      setFormulario('nenhum');
      setConfirmouCobranca(false);
      await aoMudar();
    });
  }

  const resultadoDaConexao = conectividade?.result ?? null;

  return (
    <article className="ia-cartao" aria-label={`Provedor ${nome}`}>
      <header>
        <h3>{nome}</h3>
        {provedor.isDefault && <span className="selo">Provedor padrão</span>}
        <span className="selo">{comercial ? 'Credencial própria da empresa' : 'Servidor da infraestrutura, sem chave'}</span>
      </header>

      <dl className="metricas">
        <dt>Provedor</dt>
        <dd>
          {provedor.implemented ? 'Implementado' : 'Indisponível neste ambiente (sem adaptador)'}; {provedor.authorized ? `autorizado (${provedor.models.length} modelo(s))` : 'sem modelo autorizado'}
        </dd>
        <dt>Credencial</dt>
        <dd>
          {!exigeChave
            ? 'Não se aplica: este provedor não usa chave.'
            : provedor.credential.present
              ? `Cadastrada (identificador ${provedor.credential.fingerprint ?? '—'}, atualizada em ${dataHora(provedor.credential.updatedAt)})`
              : 'Não cadastrada'}
        </dd>
        <dt>Operacional?</dt>
        <dd>{semCredencial ? 'Não: falta a credencial.' : 'Só os testes abaixo dizem se funciona: configurado não é o mesmo que testado.'}</dd>
      </dl>

      <h4>Modelos autorizados</h4>
      {provedor.models.length === 0 && <p className="discreto">Nenhum modelo autorizado pela plataforma para esta empresa.</p>}
      <ul className="ia-modelos">
        {provedor.models.map((m) => (
          <li key={m.modelId}>
            <p>
              <strong>{m.modelId}</strong>
              {m.isDefault ? ' — modelo padrão' : ''} · janela de contexto {m.contextWindowTokens.toLocaleString('pt-BR')} tokens · saída até {m.maxOutputTokens.toLocaleString('pt-BR')} tokens
            </p>
            {provedor.supply === 'Infrastructure' ? (
              <EstadosDoOllama modelo={m} />
            ) : (
              <>
                <DiagnosticoLinha rotulo="Conectividade" diagnostico={m.connectivity} rotulos={ROTULO_DA_CONECTIVIDADE} vazio="não verificada" />
                <DiagnosticoLinha rotulo="Geração" diagnostico={m.generation} rotulos={ROTULO_DA_GERACAO} vazio="não testada" />
              </>
            )}
          </li>
        ))}
      </ul>

      {mensagem && (
        <p role={mensagem.tipo === 'erro' ? 'alert' : 'status'} className={mensagem.tipo === 'erro' ? 'erro-campo' : 'aviso'}>
          {mensagem.texto}
        </p>
      )}

      {conectividade && (
        <div role="status" className="aviso">
          {!conectividade.supported && <p>Este provedor não oferece uma verificação sem geração de texto: nada foi verificado. Use o teste de geração se quiser confirmar o funcionamento.</p>}
          {conectividade.superseded && <p>A credencial foi alterada durante o teste: o resultado não foi guardado. Teste de novo.</p>}
          {resultadoDaConexao && conectividade.supported && !conectividade.superseded && (
            <p>
              {ROTULO_DA_CONECTIVIDADE[resultadoDaConexao.state] ?? resultadoDaConexao.state} ({conectividade.modelId}): acessível {triEstado(resultadoDaConexao.reachable)}, autenticação{' '}
              {triEstado(resultadoDaConexao.authenticated)}, modelo disponível {triEstado(resultadoDaConexao.modelAvailable)}, capacidade de conversa declarada{' '}
              {triEstado(resultadoDaConexao.conversationCapable)}.
              {resultadoDaConexao.state !== 'Verified' && textoDoCodigo(resultadoDaConexao.code) ? ` Motivo: ${textoDoCodigo(resultadoDaConexao.code)}.` : ''}
            </p>
          )}
        </div>
      )}
      {geracao && (
        <div role="status" className={geracao.succeeded ? 'aviso' : 'erro-campo'}>
          {geracao.superseded && <p>A credencial foi alterada durante o teste: o resultado não foi guardado como validado.</p>}
          <p>
            {ROTULO_DA_GERACAO[geracao.result.state] ?? geracao.result.state} ({geracao.modelId})
            {geracao.result.state !== 'Succeeded' && textoDoCodigo(geracao.result.code) ? `: ${textoDoCodigo(geracao.result.code)}` : ''}.
            {geracao.succeeded && geracao.result.replyPreview ? ` Resposta: "${geracao.result.replyPreview}".` : ''} O consumo, quando houve chamada, está registrado no relatório de uso.
          </p>
        </div>
      )}

      <div className="acoes">
        {podeCredencial && formulario === 'nenhum' && (
          <button type="button" className="btn" disabled={trabalhando} onClick={() => setFormulario('credencial')}>
            {provedor.credential.present ? 'Substituir credencial' : 'Cadastrar credencial'}
          </button>
        )}
        {podeCredencial && provedor.credential.present && formulario === 'nenhum' && (
          <button type="button" className="btn" disabled={trabalhando} onClick={() => setFormulario('remocao')}>
            Remover credencial
          </button>
        )}
        {podeConectividade && formulario === 'nenhum' && (
          <button type="button" className="btn" disabled={trabalhando || semCredencial || provedor.models.length === 0 || !provedor.connectivityCheckSupported} onClick={() => void testarConexao()}>
            {trabalhando ? 'Testando...' : 'Testar conexão'}
          </button>
        )}
        {podeGeracao && formulario === 'nenhum' && (
          <button type="button" className="btn" disabled={trabalhando || semCredencial || provedor.models.length === 0} onClick={() => setFormulario('geracao')}>
            Testar geração
          </button>
        )}
      </div>
      {podeConectividade && !provedor.connectivityCheckSupported && <p className="discreto">Este provedor não tem verificação de conexão sem geração de texto.</p>}

      {(podeConectividade || podeGeracao) && provedor.models.length > 1 && (
        <div className="campo">
          <label htmlFor={`modelo-teste-${provedor.providerId}`}>Modelo dos testes</label>
          <select id={`modelo-teste-${provedor.providerId}`} value={modeloDoTeste} onChange={(e) => setModeloDoTeste(e.target.value)}>
            {provedor.models.map((m) => (
              <option key={m.modelId} value={m.modelId}>
                {m.modelId}
              </option>
            ))}
          </select>
        </div>
      )}

      {formulario === 'credencial' && (
        <form
          className="revisao-formulario"
          aria-label={`Credencial ${nome}`}
          onSubmit={(e) => {
            e.preventDefault();
            void salvarChave();
          }}
        >
          <div className="campo">
            <label htmlFor={`chave-${provedor.providerId}`}>Chave de API da {nome}</label>
            <input id={`chave-${provedor.providerId}`} type="password" autoComplete="new-password" spellCheck={false} value={chave} onChange={(e) => setChave(e.target.value)} />
            <p className="discreto">A chave é enviada uma vez, guardada cifrada e nunca mais exibida. Para trocar, cadastre outra; para conferir, use os testes.</p>
          </div>
          <div className="acoes">
            <button type="submit" className="btn btn-primario" disabled={trabalhando || chave.trim() === ''}>
              {trabalhando ? 'Salvando...' : 'Salvar credencial'}
            </button>
            <button
              type="button"
              className="btn"
              disabled={trabalhando}
              onClick={() => {
                setChave('');
                setFormulario('nenhum');
              }}
            >
              Cancelar
            </button>
          </div>
        </form>
      )}

      {formulario === 'remocao' && (
        <div role="alertdialog" aria-label={`Remover credencial ${nome}`} className="revisao-formulario">
          <p>Remover a credencial da {nome}? O valor cifrado é apagado e não pode ser recuperado.</p>
          {provedor.isDefault && (
            <p className="erro-campo">
              Este é o provedor padrão da empresa. Sem a credencial a IA deixa de responder aos clientes e as conversas vão para um atendente, sem troca automática de provedor. A remoção por segurança nunca
              é bloqueada.
            </p>
          )}
          <div className="acoes">
            <button type="button" className="btn btn-primario" disabled={trabalhando} onClick={() => void removerChave()}>
              Confirmar remoção
            </button>
            <button type="button" className="btn" disabled={trabalhando} onClick={() => setFormulario('nenhum')}>
              Cancelar
            </button>
          </div>
        </div>
      )}

      {formulario === 'geracao' && (
        <div role="alertdialog" aria-label={`Testar geração ${nome}`} className="revisao-formulario">
          <p>
            O teste de geração envia uma mensagem sintética fixa do sistema (sem dados de clientes) ao modelo <strong>{modeloDoTeste}</strong> e registra o consumo real no relatório de uso. Não altera o
            padrão da empresa nem inicia atendimento.
          </p>
          <label className="caixa">
            <input type="checkbox" checked={confirmouCobranca} onChange={(e) => setConfirmouCobranca(e.target.checked)} />
            {comercial
              ? 'Entendo que provedores comerciais podem cobrar por este teste, na minha conta.'
              : 'Entendo que este teste usa o servidor de IA local e consome os recursos dele.'}
          </label>
          <div className="acoes">
            <button type="button" className="btn btn-primario" disabled={trabalhando || !confirmouCobranca} onClick={() => void testarGeracao()}>
              {trabalhando ? 'Testando...' : 'Executar teste de geração'}
            </button>
            <button
              type="button"
              className="btn"
              disabled={trabalhando}
              onClick={() => {
                setConfirmouCobranca(false);
                setFormulario('nenhum');
              }}
            >
              Cancelar
            </button>
          </div>
        </div>
      )}
    </article>
  );
}

function ModeloPadrao({ api, me, dados, aoMudar }: { api: ApiClient; me: CurrentUser; dados: ConfiguracaoDeIa; aoMudar: () => Promise<void> }) {
  const provedores = provedoresOrdenados(dados.providers).filter((p) => p.implemented && p.authorized);
  const [providerId, setProviderId] = useState(dados.defaultProviderId ?? provedores.find(elegivelComoPadrao)?.providerId ?? provedores[0]?.providerId ?? '');
  const provedor = provedores.find((p) => p.providerId === providerId);
  const [modelId, setModelId] = useState(dados.defaultProviderId === providerId ? (dados.defaultModelId ?? '') : (provedor?.models[0]?.modelId ?? ''));
  const [salvando, setSalvando] = useState(false);
  const [mensagem, setMensagem] = useState<{ tipo: 'ok' | 'erro'; texto: string; avisos?: string[] } | null>(null);
  const podeSalvar = podeDeIa(me, 'AiSettingsManage');
  const modelo = provedor?.models.find((m) => m.modelId === modelId);
  const elegivel = provedor !== undefined && elegivelComoPadrao(provedor);

  useEffect(() => {
    // Mudou o provedor: o modelo volta a ser um modelo DELE (nunca um identificador livre).
    if (provedor && !provedor.models.some((m) => m.modelId === modelId)) {
      setModelId(provedor.models[0]?.modelId ?? '');
    }
  }, [provedor, modelId]);

  async function salvar() {
    setSalvando(true);
    setMensagem(null);
    try {
      const r = await api.escolherPadraoDeIa(providerId, modelId);
      setMensagem({ tipo: 'ok', texto: 'Padrão salvo. Nenhuma chamada foi feita ao provedor.', avisos: r.warnings });
      await aoMudar();
    } catch (falha) {
      setMensagem({ tipo: 'erro', texto: mensagemDeErroDeIa(falha) });
      void aoMudar();
    } finally {
      setSalvando(false);
    }
  }

  return (
    <section className="revisao-formulario" aria-label="Modelo padrão">
      <h3>Modelo padrão</h3>
      <p className="discreto">
        {dados.configured
          ? `Padrão atual: ${rotuloDoProvedor(dados.defaultProviderId ?? '')} / ${dados.defaultModelId ?? ''}.`
          : 'A empresa ainda não tem um modelo padrão: a IA não responde e as conversas vão para um atendente.'}{' '}
        Só há um padrão por empresa. Se ele ficar indisponível, o atendimento não troca de provedor sozinho.
      </p>

      <div className="campo">
        <label htmlFor="provedor-padrao">Provedor</label>
        <select id="provedor-padrao" value={providerId} onChange={(e) => setProviderId(e.target.value)}>
          {provedores.map((p) => (
            <option key={p.providerId} value={p.providerId}>
              {rotuloDoProvedor(p.providerId)}
              {elegivelComoPadrao(p) ? '' : ' (sem credencial)'}
            </option>
          ))}
        </select>
      </div>
      <div className="campo">
        <label htmlFor="modelo-padrao">Modelo</label>
        <select id="modelo-padrao" value={modelId} onChange={(e) => setModelId(e.target.value)} disabled={!provedor}>
          {(provedor?.models ?? []).map((m) => (
            <option key={m.modelId} value={m.modelId}>
              {m.modelId}
            </option>
          ))}
        </select>
      </div>

      {modelo && (
        <dl className="metricas" aria-label="Características do modelo">
          <dt>Janela de contexto</dt>
          <dd>{modelo.contextWindowTokens.toLocaleString('pt-BR')} tokens</dd>
          <dt>Limite de saída</dt>
          <dd>{modelo.maxOutputTokens.toLocaleString('pt-BR')} tokens</dd>
          <dt>Perfil de geração</dt>
          <dd>{modelo.profile}</dd>
          <dt>Conectividade</dt>
          <dd>{modelo.connectivity ? `${ROTULO_DA_CONECTIVIDADE[modelo.connectivity.state] ?? modelo.connectivity.state} em ${dataHora(modelo.connectivity.checkedAt)}${modelo.connectivity.stale ? ' (desatualizado)' : ''}` : 'não verificada'}</dd>
          <dt>Geração</dt>
          <dd>{modelo.generation ? `${ROTULO_DA_GERACAO[modelo.generation.state] ?? modelo.generation.state} em ${dataHora(modelo.generation.checkedAt)}${modelo.generation.stale ? ' (desatualizado)' : ''}` : 'não testada'}</dd>
        </dl>
      )}
      {provedor && !elegivel && <p className="erro-campo">Cadastre a credencial deste provedor (aba Provedores) antes de escolhê-lo como padrão.</p>}

      {mensagem && (
        <div role={mensagem.tipo === 'erro' ? 'alert' : 'status'} className={mensagem.tipo === 'erro' ? 'erro-campo' : 'aviso'}>
          <p>{mensagem.texto}</p>
          {mensagem.avisos?.map((a) => (
            <p key={a} className="discreto">
              {textoDoAviso(a)}
            </p>
          ))}
        </div>
      )}

      {podeSalvar ? (
        <div className="acoes">
          <button type="button" className="btn btn-primario" disabled={salvando || !provedor || !elegivel || modelId === ''} onClick={() => void salvar()}>
            {salvando ? 'Salvando...' : 'Salvar padrão'}
          </button>
        </div>
      ) : (
        <p className="discreto">Você pode consultar o padrão, mas não alterá-lo (falta a permissão de gerir a configuração de IA).</p>
      )}
    </section>
  );
}
