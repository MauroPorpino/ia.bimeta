import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Dialogo } from '../components/Dialogo';
import { descreverErroPlataforma, type PlataformaApi } from './api';
import { SeloDaEmpresa } from './Empresas';
import { TokenUnico } from './TokenUnico';
import type { CompanyDetail, FirstAccess, PlatformMe } from './types';
import { ROTULO_SITUACAO_EMPRESA, formatarData, temPermissao } from './util';

interface Props {
  api: PlataformaApi;
  me: PlatformMe;
  slug: string;
  onVoltar: () => void;
}

type Dialogo2 = 'editar' | 'limite' | 'suspender' | 'reativar' | null;

const ROTULO_ADMIN: Record<string, string> = { Pending: 'Aguardando primeiro acesso', Expired: 'Primeiro acesso expirado', Active: 'Ativo' };

export function EmpresaDetalhe({ api, me, slug, onVoltar }: Props) {
  const [detalhe, setDetalhe] = useState<CompanyDetail | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [erroDeAcao, setErroDeAcao] = useState<string | null>(null);
  const [dialogo, setDialogo] = useState<Dialogo2>(null);
  const [ocupado, setOcupado] = useState(false);
  const [token, setToken] = useState<FirstAccess | null>(null);

  useEffect(() => {
    let ativo = true;
    setDetalhe(null);
    setErro(null);
    api
      .empresa(slug)
      .then((d) => ativo && setDetalhe(d))
      .catch((f) => ativo && setErro(descreverErroPlataforma(f)));
    return () => {
      ativo = false;
    };
  }, [api, slug]);

  async function executar(acao: () => Promise<CompanyDetail>): Promise<boolean> {
    setOcupado(true);
    setErroDeAcao(null);
    try {
      setDetalhe(await acao());
      return true;
    } catch (falha) {
      setErroDeAcao(descreverErroPlataforma(falha));
      return false;
    } finally {
      setOcupado(false);
    }
  }

  async function reemitir() {
    setOcupado(true);
    setErroDeAcao(null);
    try {
      const r = await api.reemitirPrimeiroAcesso(slug);
      setToken(r);
      setDetalhe(await api.empresa(slug));
    } catch (falha) {
      setErroDeAcao(descreverErroPlataforma(falha));
    } finally {
      setOcupado(false);
    }
  }

  const voltar = (
    <button type="button" className="btn-link" onClick={onVoltar}>
      Voltar para as empresas
    </button>
  );

  if (erro) {
    return (
      <div className="pl-pagina">
        {voltar}
        <p className="erro-campo" role="alert">
          {erro}
        </p>
      </div>
    );
  }
  if (!detalhe) {
    return (
      <div className="pl-pagina">
        {voltar}
        <p className="discreto">Carregando a empresa...</p>
      </div>
    );
  }

  const { company, seats, initialAdministrator: admin, statusHistory } = detalhe;
  const podeEditar = temPermissao(me, 'PlatformTenantUpdate');
  const podeLimite = temPermissao(me, 'PlatformTenantQuotaManage');
  const podeSuspender = temPermissao(me, 'PlatformTenantSuspend');
  const podeReemitir = temPermissao(me, 'PlatformFirstAdminManage');
  const adminPendente = admin !== null && (admin.status === 'Pending' || admin.status === 'Expired');

  return (
    <div className="pl-pagina">
      {voltar}
      <div className="pl-titulo-linha">
        <h2>
          {company.name} <SeloDaEmpresa status={company.status} />
        </h2>
        <span className="acoes">
          {podeSuspender && company.status === 'Active' && (
            <button type="button" className="btn btn-perigo" onClick={() => setDialogo('suspender')}>
              Suspender empresa
            </button>
          )}
          {podeSuspender && company.status === 'Suspended' && (
            <button type="button" className="btn btn-primario" onClick={() => setDialogo('reativar')}>
              Reativar empresa
            </button>
          )}
        </span>
      </div>

      {erroDeAcao && (
        <p className="erro-campo" role="alert">
          {erroDeAcao}
        </p>
      )}

      <section aria-labelledby="pl-emp-dados">
        <div className="pl-titulo-linha">
          <h3 id="pl-emp-dados">Dados cadastrais</h3>
          {podeEditar && (
            <button type="button" className="btn btn-pequeno" onClick={() => setDialogo('editar')}>
              Editar dados
            </button>
          )}
        </div>
        <dl className="pl-dl">
          <dt>Identificador técnico (permanente)</dt>
          <dd>{company.slug}</dd>
          <dt>Razão social</dt>
          <dd>{company.legalName || '-'}</dd>
          <dt>Documento</dt>
          <dd>{company.document || '-'}</dd>
          <dt>Contato</dt>
          <dd>
            {company.contactName || '-'} {company.contactEmail ? `(${company.contactEmail})` : ''}
          </dd>
          <dt>Situação operacional</dt>
          <dd>
            {ROTULO_SITUACAO_EMPRESA[company.status]}
            {company.suspendedAt ? ` desde ${formatarData(company.suspendedAt)}` : ''}
          </dd>
          <dt>Cadastrada em</dt>
          <dd>{formatarData(company.createdAt)}</dd>
        </dl>
      </section>

      <section aria-labelledby="pl-emp-vagas">
        <div className="pl-titulo-linha">
          <h3 id="pl-emp-vagas">Vagas de usuários</h3>
          {podeLimite && (
            <span className="acoes">
              <button type="button" className="btn btn-pequeno" onClick={() => setDialogo('limite')}>
                Alterar limite
              </button>
              <button type="button" className="btn btn-pequeno" disabled={ocupado} onClick={() => void executar(() => api.definirCadastro(slug, !seats.registrationEnabled))}>
                {seats.registrationEnabled ? 'Desabilitar novos cadastros' : 'Habilitar novos cadastros'}
              </button>
            </span>
          )}
        </div>
        {seats.excess > 0 && (
          <p className="aviso" role="alert">
            Excesso de utilização ({seats.excess}): ninguém é desativado; novos ingressos ficam bloqueados.
          </p>
        )}
        <dl className="pl-dl">
          <dt>Limite contratado</dt>
          <dd>{seats.limit ?? 'Sem limite definido'}</dd>
          <dt>Usuários ativos</dt>
          <dd>{seats.active}</dd>
          <dt>Reservas vigentes</dt>
          <dd>{seats.pendingReserved}</dd>
          <dt>Utilizadas</dt>
          <dd>{seats.used}</dd>
          <dt>Disponíveis</dt>
          <dd>{seats.available ?? '-'}</dd>
          <dt>Excesso</dt>
          <dd>{seats.excess}</dd>
          <dt>Novos cadastros</dt>
          <dd>{seats.registrationEnabled ? 'Habilitados' : 'Desabilitados'}</dd>
        </dl>
      </section>

      <section aria-labelledby="pl-emp-admin">
        <h3 id="pl-emp-admin">Administrador inicial</h3>
        {admin === null ? (
          <p className="discreto">Nenhum administrador inicial registrado.</p>
        ) : (
          <>
            <dl className="pl-dl">
              <dt>E-mail</dt>
              <dd>{admin.email}</dd>
              <dt>Nome</dt>
              <dd>{admin.displayName || '-'}</dd>
              <dt>Situação</dt>
              <dd>{ROTULO_ADMIN[admin.status] ?? admin.status}</dd>
              <dt>Token de primeiro acesso vigente</dt>
              <dd>{admin.hasLiveToken ? 'Sim' : 'Não'}</dd>
            </dl>
            {podeReemitir && adminPendente && (
              <button type="button" className="btn btn-pequeno" disabled={ocupado} onClick={() => void reemitir()}>
                Reemitir primeiro acesso
              </button>
            )}
          </>
        )}
      </section>

      <section aria-labelledby="pl-emp-hist">
        <h3 id="pl-emp-hist">Histórico de situação</h3>
        {statusHistory.length === 0 ? (
          <p className="discreto">Sem mudanças de situação registradas.</p>
        ) : (
          <ul className="pl-historico">
            {statusHistory.map((h, i) => (
              <li key={`${h.changedAt}-${i}`}>
                <strong>
                  {ROTULO_SITUACAO_EMPRESA[h.fromStatus as keyof typeof ROTULO_SITUACAO_EMPRESA] ?? h.fromStatus} para{' '}
                  {ROTULO_SITUACAO_EMPRESA[h.toStatus as keyof typeof ROTULO_SITUACAO_EMPRESA] ?? h.toStatus}
                </strong>{' '}
                em {formatarData(h.changedAt)}
                {h.reason ? ` - motivo: ${h.reason}` : ''}
              </li>
            ))}
          </ul>
        )}
      </section>

      {dialogo === 'editar' && (
        <EditarDados
          detalhe={detalhe}
          onCancelar={() => setDialogo(null)}
          onSalvar={async (pedido) => {
            const ok = await executar(() => api.atualizarEmpresa(slug, pedido));
            if (ok) {
              setDialogo(null);
            }
          }}
          ocupado={ocupado}
          erro={erroDeAcao}
        />
      )}
      {dialogo === 'limite' && (
        <AlterarLimite
          atual={seats.limit}
          ocupado={ocupado}
          erro={erroDeAcao}
          onCancelar={() => setDialogo(null)}
          onSalvar={async (n) => {
            const ok = await executar(() => api.definirLimite(slug, n));
            if (ok) {
              setDialogo(null);
            }
          }}
        />
      )}
      {(dialogo === 'suspender' || dialogo === 'reativar') && (
        <ConfirmarSituacao
          tipo={dialogo}
          slug={slug}
          ocupado={ocupado}
          erro={erroDeAcao}
          onCancelar={() => setDialogo(null)}
          onConfirmar={async (pedido) => {
            const ok = await executar(() => (dialogo === 'suspender' ? api.suspender(slug, pedido) : api.reativar(slug, pedido)));
            if (ok) {
              setDialogo(null);
            }
          }}
        />
      )}
      {token && (
        <TokenUnico
          titulo="Primeiro acesso reemitido"
          paraQuem={token.email}
          token={token.token}
          expiresAt={token.expiresAt}
          instrucao="O administrador inicial usa este token na tela de primeiro acesso para definir a própria senha."
          onFechar={() => setToken(null)}
        />
      )}
    </div>
  );
}

function EditarDados({
  detalhe,
  onCancelar,
  onSalvar,
  ocupado,
  erro,
}: {
  detalhe: CompanyDetail;
  onCancelar: () => void;
  onSalvar: (p: { name: string; legalName: string; document: string; contactName: string; contactEmail: string }) => Promise<void>;
  ocupado: boolean;
  erro: string | null;
}) {
  const c = detalhe.company;
  const [nome, setNome] = useState(c.name);
  const [razao, setRazao] = useState(c.legalName ?? '');
  const [documento, setDocumento] = useState(c.document ?? '');
  const [contato, setContato] = useState(c.contactName ?? '');
  const [email, setEmail] = useState(c.contactEmail ?? '');
  function enviar(e: FormEvent) {
    e.preventDefault();
    void onSalvar({ name: nome.trim(), legalName: razao.trim(), document: documento.trim(), contactName: contato.trim(), contactEmail: email.trim() });
  }
  return (
    <Dialogo titulo="Editar dados da empresa" onFechar={onCancelar}>
      <form className="revisao-formulario" onSubmit={enviar}>
        <p className="discreto">O identificador técnico ({c.slug}) não pode ser alterado.</p>
        <label>
          Nome da empresa
          <input value={nome} onChange={(e) => setNome(e.target.value)} required maxLength={200} />
        </label>
        <label>
          Razão social
          <input value={razao} onChange={(e) => setRazao(e.target.value)} maxLength={200} />
        </label>
        <label>
          Documento (CNPJ)
          <input value={documento} onChange={(e) => setDocumento(e.target.value)} maxLength={32} />
        </label>
        <label>
          Nome do contato
          <input value={contato} onChange={(e) => setContato(e.target.value)} maxLength={200} />
        </label>
        <label>
          E-mail do contato
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} maxLength={320} />
        </label>
        {erro && (
          <p className="erro-campo" role="alert">
            {erro}
          </p>
        )}
        <div className="acoes">
          <button type="submit" className="btn btn-primario" disabled={ocupado}>
            Salvar
          </button>
          <button type="button" className="btn" onClick={onCancelar}>
            Cancelar
          </button>
        </div>
      </form>
    </Dialogo>
  );
}

function AlterarLimite({ atual, ocupado, erro, onCancelar, onSalvar }: { atual: number | null; ocupado: boolean; erro: string | null; onCancelar: () => void; onSalvar: (n: number) => Promise<void> }) {
  const [valor, setValor] = useState(atual === null ? '' : String(atual));
  const [local, setLocal] = useState<string | null>(null);
  function enviar(e: FormEvent) {
    e.preventDefault();
    const n = Number(valor);
    if (!Number.isInteger(n) || n < 1) {
      setLocal('Informe um número inteiro, no mínimo 1.');
      return;
    }
    setLocal(null);
    void onSalvar(n);
  }
  return (
    <Dialogo titulo="Alterar limite de usuários" onFechar={onCancelar}>
      <form className="revisao-formulario" onSubmit={enviar}>
        <label>
          Limite de usuários contratados
          <input type="number" min={1} step={1} value={valor} onChange={(e) => setValor(e.target.value)} required />
        </label>
        <p className="discreto">Reduzir o limite abaixo da utilização não desativa ninguém: apenas bloqueia novos ingressos.</p>
        {(local ?? erro) && (
          <p className="erro-campo" role="alert">
            {local ?? erro}
          </p>
        )}
        <div className="acoes">
          <button type="submit" className="btn btn-primario" disabled={ocupado}>
            Salvar limite
          </button>
          <button type="button" className="btn" onClick={onCancelar}>
            Cancelar
          </button>
        </div>
      </form>
    </Dialogo>
  );
}

function ConfirmarSituacao({
  tipo,
  slug,
  ocupado,
  erro,
  onCancelar,
  onConfirmar,
}: {
  tipo: 'suspender' | 'reativar';
  slug: string;
  ocupado: boolean;
  erro: string | null;
  onCancelar: () => void;
  onConfirmar: (p: { reason: string; confirm: string }) => Promise<void>;
}) {
  const [motivo, setMotivo] = useState('');
  const [confirmacao, setConfirmacao] = useState('');
  const emAndamento = useRef(false);
  const suspender = tipo === 'suspender';
  const motivoOk = motivo.trim().length >= 5 && motivo.trim().length <= 500;
  const habilitado = motivoOk && confirmacao === slug && !ocupado;

  async function enviar(e: FormEvent) {
    e.preventDefault();
    if (!habilitado || emAndamento.current) {
      return;
    }
    emAndamento.current = true;
    try {
      await onConfirmar({ reason: motivo.trim(), confirm: confirmacao });
    } finally {
      emAndamento.current = false;
    }
  }

  return (
    <Dialogo titulo={suspender ? 'Suspender empresa' : 'Reativar empresa'} onFechar={onCancelar}>
      <form className="revisao-formulario" onSubmit={(e) => void enviar(e)}>
        {suspender ? (
          <p className="aviso" role="note">
            A suspensão bloqueia o login, derruba as sessões e bloqueia o Embedded, a IA, os fluxos e os envios. As mensagens recebidas ficam guardadas, mas SEM atendimento automático.
          </p>
        ) : (
          <p className="aviso" role="note">
            Reativar exige que os usuários entrem de novo (novas sessões) e NÃO reprocessa as mensagens antigas.
          </p>
        )}
        <label>
          Motivo (5 a 500 caracteres)
          <textarea value={motivo} onChange={(e) => setMotivo(e.target.value)} rows={3} maxLength={500} required />
        </label>
        <label>
          Digite o identificador da empresa ({slug}) para confirmar
          <input value={confirmacao} onChange={(e) => setConfirmacao(e.target.value)} autoComplete="off" autoCapitalize="none" spellCheck={false} required />
        </label>
        {erro && (
          <p className="erro-campo" role="alert">
            {erro}
          </p>
        )}
        <div className="acoes">
          <button type="submit" className={suspender ? 'btn btn-perigo' : 'btn btn-primario'} disabled={!habilitado}>
            {suspender ? 'Suspender empresa' : 'Reativar empresa'}
          </button>
          <button type="button" className="btn" onClick={onCancelar}>
            Cancelar
          </button>
        </div>
      </form>
    </Dialogo>
  );
}
