import { useEffect, useState } from 'react';
import { descreverErroPlataforma, type PlataformaApi } from './api';
import type { Overview } from './types';
import { formatarData } from './util';

function Cartao({ titulo, valor, detalhe }: { titulo: string; valor: number; detalhe?: string }) {
  return (
    <div className="pl-cartao" role="group" aria-label={titulo}>
      <span className="pl-cartao-titulo">{titulo}</span>
      <strong className="pl-cartao-valor">{valor}</strong>
      {detalhe && <span className="discreto">{detalhe}</span>}
    </div>
  );
}

/** Visão geral operacional da plataforma: empresas e vagas. Sem indicadores financeiros nem custo de IA. */
export function VisaoGeral({ api }: { api: PlataformaApi }) {
  const [dados, setDados] = useState<Overview | null>(null);
  const [erro, setErro] = useState<string | null>(null);

  useEffect(() => {
    let ativo = true;
    api
      .visaoGeral()
      .then((d) => ativo && setDados(d))
      .catch((f) => ativo && setErro(descreverErroPlataforma(f)));
    return () => {
      ativo = false;
    };
  }, [api]);

  if (erro) {
    return (
      <p className="erro-campo" role="alert">
        {erro}
      </p>
    );
  }
  if (!dados) {
    return <p className="discreto">Carregando a visão geral...</p>;
  }

  return (
    <div className="pl-pagina">
      <h2>Visão geral</h2>
      <section aria-labelledby="pl-vg-empresas">
        <h3 id="pl-vg-empresas">Empresas</h3>
        <div className="pl-cartoes">
          <Cartao titulo="Total de empresas" valor={dados.totalCompanies} />
          <Cartao titulo="Ativas" valor={dados.activeCompanies} />
          <Cartao titulo="Suspensas" valor={dados.suspendedCompanies} />
          <Cartao titulo="Aguardando provisionamento" valor={dados.provisioningCompanies} />
        </div>
      </section>

      <section aria-labelledby="pl-vg-vagas">
        <h3 id="pl-vg-vagas">Vagas de usuários</h3>
        <p className="discreto">Somam apenas as empresas com limite definido ({dados.seats.companiesWithLimit}).</p>
        <div className="pl-cartoes">
          <Cartao titulo="Vagas contratadas" valor={dados.seats.contracted} />
          <Cartao titulo="Vagas utilizadas" valor={dados.seats.active} />
          <Cartao titulo="Vagas reservadas" valor={dados.seats.reserved} detalhe="Primeiros acessos pendentes" />
          <Cartao titulo="Vagas disponíveis" valor={dados.seats.available} />
        </div>
      </section>

      <section aria-labelledby="pl-vg-atencao">
        <h3 id="pl-vg-atencao">Atenção</h3>
        <div className="pl-cartoes">
          <Cartao titulo="Empresas com limite excedido" valor={dados.companiesOverLimit} detalhe="Ninguém é desativado; novos ingressos ficam bloqueados." />
          <Cartao titulo="Empresas com novos cadastros desabilitados" valor={dados.companiesRegistrationDisabled} />
        </div>
      </section>

      <section aria-labelledby="pl-vg-legado" className="pl-bloco-separado">
        <h3 id="pl-vg-legado">Empresas sem limite definido (anteriores ou exceção)</h3>
        <p className="discreto">Estas empresas ficam fora dos totais de vagas acima.</p>
        <div className="pl-cartoes">
          <Cartao titulo="Empresas sem limite" valor={dados.legacy.companies} />
          <Cartao titulo="Usuários ativos (sem limite)" valor={dados.legacy.activeUsers} />
        </div>
      </section>

      <p className="discreto">Atualizado em {formatarData(dados.generatedAt)}.</p>
    </div>
  );
}
