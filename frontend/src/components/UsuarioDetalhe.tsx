import { useEffect, useRef, useState, type FormEvent } from 'react';
import type { ApiClient } from '../api/client';
import type { CurrentUser, IssuedToken, OrganizationSummary, PermissionCatalog, PermissionEffect, UserDetail } from '../api/types';
import {
  PERFIS,
  ROTULO_SITUACAO,
  mensagemDeErroDeUsuario,
  nomeDaOrganizacao,
  organizacaoParaAcao,
  perfilExigePermissaoDePerfil,
  pode,
  podeSobre,
  rotuloDoPerfil,
} from '../usuarios';
import { ConfirmarAcao } from './Dialogo';
import { PermissoesDoUsuario } from './PermissoesDoUsuario';
import { PermissoesDoPerfil } from './UsoDeUsuarios';

interface Props {
  api: ApiClient;
  me: CurrentUser;
  usuarioId: string;
  organizacoes: OrganizationSummary[];
  catalogo: PermissionCatalog | null;
  /** O usuário ou a vaga mudou: a lista e o painel de uso se atualizam. */
  onAlterado: () => void;
  /** Um token foi emitido: quem chama o mostra uma única vez (este componente não o guarda). */
  onToken: (titulo: string, paraQuem: string, emitido: IssuedToken) => void;
  onFechar: () => void;
}

type Confirmacao = { tipo: 'desativar' } | { tipo: 'cancelar-admissao' } | { tipo: 'retirar-vinculo'; organizationId: string };

/** O detalhe de um usuário: dados, situação e acesso, vínculos com perfil por organização e permissões (herdadas ou personalizadas). */
export function UsuarioDetalhe({ api, me, usuarioId, organizacoes, catalogo, onAlterado, onToken, onFechar }: Props) {
  const [usuario, setUsuario] = useState<UserDetail | null>(null);
  const [erroDeCarga, setErroDeCarga] = useState<string | null>(null);
  const [recarga, setRecarga] = useState(0);
  const [nome, setNome] = useState('');
  const [email, setEmail] = useState('');
  const [erro, setErro] = useState<string | null>(null);
  const [aviso, setAviso] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const [confirmacao, setConfirmacao] = useState<Confirmacao | null>(null);
  const [perfisEscolhidos, setPerfisEscolhidos] = useState<Record<string, string>>({});
  const [novaOrganizacao, setNovaOrganizacao] = useState('');
  const [novoPerfil, setNovoPerfil] = useState<string>('Attendant');
  const emAndamento = useRef(false);

  useEffect(() => {
    let vivo = true;
    api
      .obterUsuario(usuarioId)
      .then((lido) => {
        if (vivo) {
          setUsuario(lido);
          setNome(lido.displayName);
          setEmail(lido.email);
          setErroDeCarga(null);
        }
      })
      .catch((falha: unknown) => {
        if (vivo) {
          setErroDeCarga(mensagemDeErroDeUsuario(falha));
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, usuarioId, recarga]);

  async function executar(acao: () => Promise<void>, mensagem: string | null = null, alterouVaga = true) {
    if (emAndamento.current) {
      return;
    }
    emAndamento.current = true;
    setOcupado(true);
    setErro(null);
    setAviso(null);
    try {
      await acao();
      if (mensagem) {
        setAviso(mensagem);
      }
      setRecarga((r) => r + 1);
      if (alterouVaga) {
        onAlterado();
      }
    } catch (falha) {
      setErro(mensagemDeErroDeUsuario(falha));
    } finally {
      emAndamento.current = false;
      setOcupado(false);
    }
  }

  if (erroDeCarga) {
    return (
      <div className="usuario-detalhe">
        <p className="erro-campo" role="alert">
          {erroDeCarga}
        </p>
        <button type="button" className="btn" onClick={onFechar}>
          Fechar
        </button>
      </div>
    );
  }
  if (!usuario) {
    return <p className="discreto centro">Carregando o usuário...</p>;
  }

  const alvo = usuario;
  const podeGerir = podeSobre(me, 'UserManage', alvo);
  const podeStatus = podeSobre(me, 'UserStatusManage', alvo);
  const organizacaoDaAcao = organizacaoParaAcao(alvo, me, organizacoes);
  const gerenciaPerfil = (organizacao: string) => pode(me, 'UserManage', organizacao);
  const podeVerPermissoes = catalogo !== null && alvo.permissions.length > 0;
  const vinculadas = new Set(alvo.grants.map((g) => g.organizationId));
  const organizacoesLivres = organizacoes.filter((o) => !vinculadas.has(o.id) && gerenciaPerfil(o.id));
  const organizacaoEscolhida = novaOrganizacao !== '' ? novaOrganizacao : (organizacoesLivres[0]?.id ?? '');
  const quem = `${alvo.displayName} (${alvo.email})`;

  function salvarDados(evento: FormEvent) {
    evento.preventDefault();
    void executar(async () => {
      await api.editarUsuario(alvo.id, { email: email.trim(), displayName: nome.trim() });
    }, 'Dados salvos.', false);
  }

  async function emitir(titulo: string, obter: () => Promise<IssuedToken>) {
    await executar(async () => {
      const emitido = await obter();
      onToken(titulo, quem, emitido);
    });
  }

  function confirmar() {
    const atual = confirmacao;
    setConfirmacao(null);
    if (!atual) {
      return;
    }
    if (atual.tipo === 'desativar') {
      void executar(() => api.desativarUsuario(alvo.id), 'Usuário desativado.');
    } else if (atual.tipo === 'cancelar-admissao') {
      void executar(() => api.cancelarAdmissao(alvo.id), 'Admissão cancelada.');
    } else {
      void executar(() => api.retirarVinculoComOrganizacao(atual.organizationId, alvo.id), 'Vínculo retirado.');
    }
  }

  return (
    <div className="usuario-detalhe">
      <div className="usuario-detalhe-topo">
        <h3>{alvo.displayName}</h3>
        <span className="etiqueta">{ROTULO_SITUACAO[alvo.status]}</span>
        {alvo.customized && <span className="etiqueta">Personalizado</span>}
        <button type="button" className="btn btn-pequeno" onClick={onFechar}>
          Fechar detalhe
        </button>
      </div>

      {erro && (
        <p className="erro-campo" role="alert">
          {erro}
        </p>
      )}
      {aviso && (
        <p className="aviso" role="status">
          {aviso}
        </p>
      )}

      <section aria-label="Dados do usuário">
        <h4>Dados</h4>
        <form className="revisao-formulario" onSubmit={salvarDados}>
          <label>
            Nome completo
            <input value={nome} onChange={(e) => setNome(e.target.value)} required maxLength={200} disabled={!podeGerir} />
          </label>
          <label>
            E-mail
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required maxLength={320} disabled={!podeGerir} />
          </label>
          {podeGerir && (
            <div className="acoes">
              <button type="submit" className="btn btn-primario" disabled={ocupado || (nome.trim() === alvo.displayName && email.trim() === alvo.email)}>
                Salvar dados
              </button>
            </div>
          )}
        </form>
      </section>

      <section aria-label="Situação e acesso">
        <h4>Situação e acesso</h4>
        <div className="acoes">
          {alvo.status === 'Active' && podeStatus && (
            <button type="button" className="btn btn-perigo" disabled={ocupado} onClick={() => setConfirmacao({ tipo: 'desativar' })}>
              Desativar
            </button>
          )}
          {alvo.status === 'Inactive' && podeStatus && (
            <button type="button" className="btn" disabled={ocupado} onClick={() => void executar(() => api.reativarUsuario(alvo.id), 'Usuário reativado.')}>
              Reativar
            </button>
          )}
          {(alvo.status === 'Pending' || alvo.status === 'Expired') && podeGerir && (
            <>
              <button type="button" className="btn btn-primario" disabled={ocupado} onClick={() => void emitir('Token de primeiro acesso', () => api.emitirPrimeiroAcesso(alvo.id))}>
                {alvo.hasLiveFirstAccessToken ? 'Substituir token de primeiro acesso' : 'Emitir token de primeiro acesso'}
              </button>
              {alvo.hasLiveFirstAccessToken && (
                <button type="button" className="btn" disabled={ocupado} onClick={() => void executar(() => api.cancelarTokenDoUsuario(alvo.id), 'Token cancelado.', false)}>
                  Cancelar token
                </button>
              )}
              <button type="button" className="btn btn-perigo" disabled={ocupado} onClick={() => setConfirmacao({ tipo: 'cancelar-admissao' })}>
                Cancelar admissão
              </button>
            </>
          )}
          {alvo.status === 'Cancelled' && podeGerir && (
            <button type="button" className="btn btn-primario" disabled={ocupado} onClick={() => void emitirReadmissao()}>
              Readmitir
            </button>
          )}
          {alvo.status === 'Active' && podeGerir && organizacaoDaAcao !== null && (
            <>
              <button
                type="button"
                className="btn"
                disabled={ocupado}
                onClick={() => void emitir('Token de redefinição de acesso', () => api.redefinirAcessoDoUsuario(organizacaoDaAcao, alvo.id))}
              >
                Redefinir acesso
              </button>
              <button
                type="button"
                className="btn"
                disabled={ocupado}
                onClick={() =>
                  void executar(async () => {
                    const { revoked } = await api.revogarSessoesDoUsuario(organizacaoDaAcao, alvo.id);
                    setAviso(`${revoked} ${revoked === 1 ? 'sessão revogada' : 'sessões revogadas'}.`);
                  }, null, false)
                }
              >
                Revogar sessões
              </button>
            </>
          )}
        </div>
        {alvo.status === 'Pending' && <p className="discreto">Pendente: o usuário ainda não definiu a senha e a vaga está reservada.</p>}
        {alvo.status === 'Expired' && <p className="discreto">A admissão expirou sem o primeiro acesso: emita um novo token (exige vaga e cadastro habilitado) ou cancele a admissão.</p>}
      </section>

      <section aria-label="Vínculos e perfil">
        <h4>Vínculos e perfil</h4>
        {alvo.grants.length === 0 && <p className="discreto">Sem vínculo com organização.</p>}
        <ul className="lista-vinculos">
          {alvo.grants.map((g) => {
            const org = g.organizationId;
            const escolhido = org !== null ? (perfisEscolhidos[org] ?? g.role) : g.role;
            const nomeDoVinculo = nomeDaOrganizacao(org, organizacoes);
            return (
              <li key={org ?? 'tenant'}>
                <span className="permissao-nome">{nomeDoVinculo}</span>
                {org === null || !gerenciaPerfil(org) ? (
                  <span className="etiqueta">{rotuloDoPerfil(g.role)}</span>
                ) : (
                  <>
                    <label>
                      <span className="sr-only">Perfil em {nomeDoVinculo}</span>
                      <select value={escolhido} disabled={ocupado} aria-label={`Perfil em ${nomeDoVinculo}`} onChange={(e) => setPerfisEscolhidos((atual) => ({ ...atual, [org]: e.target.value }))}>
                        {PERFIS.map((p) => (
                          <option key={p} value={p} disabled={perfilExigePermissaoDePerfil(p) && p !== g.role && !pode(me, 'UserPermissionManage', org)}>
                            {rotuloDoPerfil(p)}
                          </option>
                        ))}
                      </select>
                    </label>
                    <button
                      type="button"
                      className="btn btn-pequeno"
                      disabled={ocupado || escolhido === g.role}
                      aria-label={`Alterar perfil em ${nomeDoVinculo}`}
                      onClick={() => void executar(() => api.definirPerfilNaOrganizacao(org, alvo.id, escolhido), 'Perfil alterado.')}
                    >
                      Alterar perfil
                    </button>
                    <button type="button" className="btn btn-pequeno btn-perigo" disabled={ocupado} aria-label={`Retirar vínculo com ${nomeDoVinculo}`} onClick={() => setConfirmacao({ tipo: 'retirar-vinculo', organizationId: org })}>
                      Retirar vínculo
                    </button>
                  </>
                )}
                {org === null && <span className="discreto">definido para a empresa inteira</span>}
              </li>
            );
          })}
        </ul>
        {organizacoesLivres.length > 0 && (
          <div className="vinculo-novo">
            <h4>Vincular a outra organização</h4>
            <div className="acoes filtros">
              <label>
                Organização
                <select value={organizacaoEscolhida} onChange={(e) => setNovaOrganizacao(e.target.value)}>
                  {organizacoesLivres.map((o) => (
                    <option key={o.id} value={o.id}>
                      {o.name}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                Perfil
                <select value={novoPerfil} onChange={(e) => setNovoPerfil(e.target.value)}>
                  {PERFIS.map((p) => (
                    <option key={p} value={p} disabled={perfilExigePermissaoDePerfil(p) && !pode(me, 'UserPermissionManage', organizacaoEscolhida)}>
                      {rotuloDoPerfil(p)}
                    </option>
                  ))}
                </select>
              </label>
              <button type="button" className="btn" disabled={ocupado || organizacaoEscolhida === ''} onClick={() => void executar(() => api.definirPerfilNaOrganizacao(organizacaoEscolhida, alvo.id, novoPerfil), 'Vínculo criado.')}>
                Vincular
              </button>
            </div>
            <PermissoesDoPerfil catalogo={catalogo} perfil={novoPerfil} />
          </div>
        )}
      </section>

      {podeVerPermissoes && catalogo && (
        <section aria-label="Permissões do usuário">
          <h4>Permissões</h4>
          <p className="discreto">Cada permissão vem do perfil do vínculo (herdada) ou foi personalizada para este usuário.</p>
          <PermissoesDoUsuario
            me={me}
            catalogo={catalogo}
            organizacoes={organizacoes}
            permissoes={alvo.permissions}
            ocupado={ocupado}
            onDefinir={(org: string | null, permissao: string, efeito: PermissionEffect) =>
              void executar(() => api.definirPermissaoDoUsuario(alvo.id, org, permissao, efeito), 'Permissão atualizada.', false)
            }
            onRemover={(org, permissao) => void executar(() => api.removerPersonalizacaoDaPermissao(alvo.id, permissao, org), 'Personalização removida.', false)}
            onRestaurar={(escopo) => void executar(() => api.restaurarPermissoesDoPerfil(alvo.id, escopo), 'Permissões restauradas ao padrão do perfil.')}
          />
        </section>
      )}

      {confirmacao?.tipo === 'desativar' && (
        <ConfirmarAcao
          titulo="Desativar usuário"
          texto={`Desativar ${alvo.displayName}? Isso preserva o histórico, libera a vaga e encerra as sessões do usuário.`}
          rotuloDeConfirmar="Desativar"
          perigo
          onConfirmar={confirmar}
          onCancelar={() => setConfirmacao(null)}
        />
      )}
      {confirmacao?.tipo === 'cancelar-admissao' && (
        <ConfirmarAcao
          titulo="Cancelar admissão"
          texto={`Cancelar a admissão de ${alvo.displayName}? O token de primeiro acesso deixa de valer e a vaga é liberada.`}
          rotuloDeConfirmar="Cancelar admissão"
          perigo
          onConfirmar={confirmar}
          onCancelar={() => setConfirmacao(null)}
        />
      )}
      {confirmacao?.tipo === 'retirar-vinculo' && (
        <ConfirmarAcao
          titulo="Retirar vínculo"
          texto={`Retirar o vínculo de ${alvo.displayName} com ${nomeDaOrganizacao(confirmacao.organizationId, organizacoes)}? O usuário perde o perfil e as permissões dessa organização.`}
          rotuloDeConfirmar="Retirar vínculo"
          perigo
          onConfirmar={confirmar}
          onCancelar={() => setConfirmacao(null)}
        />
      )}
    </div>
  );

  async function emitirReadmissao() {
    await executar(async () => {
      const resultado = await api.readmitirUsuario(alvo.id);
      onToken('Token de primeiro acesso (readmissão)', quem, { token: resultado.token, expiresAt: resultado.expiresAt });
    });
  }
}
