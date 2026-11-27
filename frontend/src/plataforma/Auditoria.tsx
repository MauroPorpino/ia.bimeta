import { useEffect, useState, type FormEvent } from 'react';
import { descreverErroPlataforma, type PlataformaApi } from './api';
import type { AuditFilters, AuditItem, Pagina } from './types';
import { POR_PAGINA, ROTULO_RESULTADO, fimDoDia, formatarData, inicioDoDia, rotuloDaOperacao } from './util';

const VAZIO: AuditFilters = { operation: '', resource: '', outcome: '', from: '', to: '' };

/** Auditoria administrativa da plataforma: filtros por operação, recurso, resultado e período, com paginação. */
export function Auditoria({ api }: { api: PlataformaApi }) {
  const [rascunho, setRascunho] = useState<AuditFilters>(VAZIO);
  const [filtros, setFiltros] = useState<AuditFilters>(VAZIO);
  const [pagina, setPagina] = useState(1);
  const [dados, setDados] = useState<Pagina<AuditItem> | null>(null);
  const [erro, setErro] = useState<string | null>(null);

  useEffect(() => {
    let ativo = true;
    setErro(null);
    api
      .auditoria({
        page: pagina,
        pageSize: POR_PAGINA,
        operation: filtros.operation,
        resource: filtros.resource,
        outcome: filtros.outcome,
        from: inicioDoDia(filtros.from),
        to: fimDoDia(filtros.to),
      })
      .then((d) => ativo && setDados(d))
      .catch((f) => ativo && setErro(descreverErroPlataforma(f)));
    return () => {
      ativo = false;
    };
  }, [api, filtros, pagina]);

  function aplicar(e: FormEvent) {
    e.preventDefault();
    setPagina(1);
    setFiltros({ ...rascunho, operation: rascunho.operation.trim(), resource: rascunho.resource.trim() });
  }

  const campo = (nome: keyof AuditFilters, valor: string) => setRascunho((r) => ({ ...r, [nome]: valor }));
  const totalDePaginas = dados ? Math.max(1, Math.ceil(dados.total / POR_PAGINA)) : 1;

  return (
    <div className="pl-pagina">
      <h2>Auditoria</h2>
      <form className="filtros pl-filtros" onSubmit={aplicar} role="search" aria-label="Filtrar a auditoria">
        <label>
          Operação contém
          <input value={rascunho.operation} onChange={(e) => campo('operation', e.target.value)} placeholder="ex.: suspend" />
        </label>
        <label>
          Recurso contém
          <input value={rascunho.resource} onChange={(e) => campo('resource', e.target.value)} placeholder="id da empresa ou do operador" />
        </label>
        <label>
          Resultado
          <select value={rascunho.outcome} onChange={(e) => campo('outcome', e.target.value)}>
            <option value="">Todos</option>
            <option value="Success">Sucesso</option>
            <option value="Denied">Negado</option>
            <option value="Failed">Falhou</option>
          </select>
        </label>
        <label>
          De
          <input type="date" value={rascunho.from} onChange={(e) => campo('from', e.target.value)} />
        </label>
        <label>
          Até
          <input type="date" value={rascunho.to} onChange={(e) => campo('to', e.target.value)} />
        </label>
        <button type="submit" className="btn">
          Filtrar
        </button>
      </form>

      {erro && (
        <p className="erro-campo" role="alert">
          {erro}
        </p>
      )}
      {!dados && !erro && <p className="discreto">Carregando a auditoria...</p>}
      {dados && dados.items.length === 0 && <p className="discreto">Nenhum registro encontrado.</p>}
      {dados && dados.items.length > 0 && (
        <div className="pl-tabela-rolagem">
          <table className="tabela-execucoes">
            <thead>
              <tr>
                <th>Quando</th>
                <th>Operação</th>
                <th>Recurso</th>
                <th>Resultado</th>
                <th>Ator</th>
              </tr>
            </thead>
            <tbody>
              {dados.items.map((a) => {
                const amigavel = rotuloDaOperacao(a.operation);
                return (
                  <tr key={a.id}>
                    <td>{formatarData(a.occurredAt)}</td>
                    <td>
                      {amigavel && <strong>{amigavel}</strong>}
                      {amigavel && <br />}
                      <code>{a.operation}</code>
                    </td>
                    <td>
                      {a.resourceType ?? '-'}
                      {a.resourceId && (
                        <>
                          <br />
                          <code>{a.resourceId}</code>
                        </>
                      )}
                    </td>
                    <td>
                      <span className={a.outcome === 'Success' ? 'etiqueta' : 'etiqueta etiqueta-aviso'}>{ROTULO_RESULTADO[a.outcome] ?? a.outcome}</span>
                    </td>
                    <td>
                      <code>{a.actorUserId ?? '-'}</code>
                      {a.actorOrigin && (
                        <>
                          <br />
                          <span className="discreto">{a.actorOrigin}</span>
                        </>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {dados && dados.total > 0 && (
        <nav className="acoes" aria-label="Paginação">
          <button type="button" className="btn btn-pequeno" disabled={pagina <= 1} onClick={() => setPagina((p) => p - 1)}>
            Anterior
          </button>
          <span className="discreto">
            Página {pagina} de {totalDePaginas} ({dados.total} registros)
          </span>
          <button type="button" className="btn btn-pequeno" disabled={pagina >= totalDePaginas} onClick={() => setPagina((p) => p + 1)}>
            Próxima
          </button>
        </nav>
      )}
    </div>
  );
}
