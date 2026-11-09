import { useEffect, useMemo, useState, type FormEvent } from 'react';
import type { ReplyTemplate, ReplyTemplateParameter, TemplateParameterError } from '../api/types';

/**
 * Responder por MODELO de mensagem aprovado pela Meta (ADR 0027): o atendente escolhe o modelo, preenche os campos que a
 * aprovação definiu e vê o texto que o cliente vai ler. Os mesmos limites que a Meta e o servidor aplicam são conferidos
 * aqui para o erro aparecer ao lado do campo, antes de enviar; o servidor repete a validação (e é quem decide).
 */

const MARCADOR = /\{\{\s*([A-Za-z0-9_]+)\s*\}\}/g;

/** Os problemas locais de um valor (os mesmos limites do servidor): vazio, grande demais, quebra de linha, tabulação ou mais de 3 espaços seguidos. */
export function validarParametro(parametro: ReplyTemplateParameter, valor: string): string | null {
  const aparado = valor.trim();
  if (aparado === '') {
    return `Preencha ${parametro.label}.`;
  }
  if (aparado.length > parametro.maxLength) {
    return `${parametro.label} aceita no máximo ${parametro.maxLength} caracteres.`;
  }
  if (/[\u0000-\u001f\u007f]/.test(aparado) || / {4,}/.test(aparado)) {
    return `${parametro.label} não pode ter quebra de linha, tabulação nem mais de 3 espaços seguidos.`;
  }
  return null;
}

/** O texto do modelo com os valores no lugar; o campo ainda vazio mostra o marcador pelo rótulo (ex.: «Variável 1»), para o atendente ver onde cada valor entra. */
export function montarPrevia(modelo: ReplyTemplate, valores: Record<string, string>): string {
  const substituir = (texto: string, componente: 'header' | 'body') =>
    texto.replace(MARCADOR, (marcador: string, bruto: string) => {
      const parametro = modelo.parameters.find((p) => p.key === `${componente}.${bruto}`);
      if (!parametro) {
        return marcador;
      }
      const valor = (valores[parametro.key] ?? '').trim();
      return valor === '' ? `«${parametro.label}»` : valor;
    });

  const partes: string[] = [];
  if (modelo.headerText) {
    partes.push(substituir(modelo.headerText, 'header'));
  }
  partes.push(substituir(modelo.bodyText, 'body'));
  if (modelo.footerText) {
    partes.push(modelo.footerText);
  }
  return partes.join('\n');
}

interface Props {
  modelos: ReplyTemplate[];
  /** O envio está em andamento: o formulário fica travado. */
  ocupado: boolean;
  /** Os erros que o SERVIDOR devolveu no último envio, por chave de parâmetro (`*` = do conjunto). */
  errosDoServidor: TemplateParameterError[];
  /** Devolve verdadeiro se o modelo foi enviado (o formulário é limpo). Os erros de validação chegam por `errosDoServidor`. */
  aoEnviar: (modelo: ReplyTemplate, parametros: Record<string, string>) => Promise<boolean>;
  /** O servidor mudou o que se sabe sobre os modelos (a lista foi relida): os erros antigos deixam de valer. */
  aoEditar?: () => void;
}

export function ModeloDeMensagem({ modelos, ocupado, errosDoServidor, aoEnviar, aoEditar }: Props) {
  const [modeloId, setModeloId] = useState('');
  const [valores, setValores] = useState<Record<string, string>>({});
  const [tentou, setTentou] = useState(false);

  const modelo = modelos.find((m) => m.id === modeloId) ?? null;

  // A lista foi relida e o modelo escolhido não existe mais (pausado, desativado): volta à escolha.
  useEffect(() => {
    if (modeloId !== '' && !modelos.some((m) => m.id === modeloId)) {
      setModeloId('');
      setValores({});
      setTentou(false);
    }
  }, [modelos, modeloId]);

  const errosLocais = useMemo(() => {
    const erros: Record<string, string> = {};
    for (const parametro of modelo?.parameters ?? []) {
      const erro = validarParametro(parametro, valores[parametro.key] ?? '');
      if (erro) {
        erros[parametro.key] = erro;
      }
    }
    return erros;
  }, [modelo, valores]);

  const errosDoServidorPorChave = useMemo(() => {
    const porChave: Record<string, string> = {};
    for (const erro of errosDoServidor) {
      porChave[erro.key] ??= erro.message;
    }
    return porChave;
  }, [errosDoServidor]);

  const temErroLocal = Object.keys(errosLocais).length > 0;

  function escolher(id: string) {
    setModeloId(id);
    setValores({});
    setTentou(false);
    aoEditar?.();
  }

  function mudar(chave: string, valor: string) {
    setValores((atual) => ({ ...atual, [chave]: valor }));
    aoEditar?.();
  }

  async function enviar(evento: FormEvent) {
    evento.preventDefault();
    if (!modelo || ocupado) {
      return;
    }
    setTentou(true);
    if (temErroLocal) {
      return;
    }
    const aparados: Record<string, string> = {};
    for (const parametro of modelo.parameters) {
      aparados[parametro.key] = (valores[parametro.key] ?? '').trim();
    }
    if (await aoEnviar(modelo, aparados)) {
      setModeloId('');
      setValores({});
      setTentou(false);
    }
  }

  return (
    <form className="modelo-mensagem" aria-label="Enviar modelo de mensagem" onSubmit={(e) => void enviar(e)}>
      <div className="campo">
        <label htmlFor="modelo-mensagem-escolha">Modelo de mensagem</label>
        <select id="modelo-mensagem-escolha" value={modeloId} disabled={ocupado} onChange={(e) => escolher(e.target.value)}>
          <option value="">Escolha um modelo aprovado...</option>
          {modelos.map((m) => (
            <option key={m.id} value={m.id}>
              {m.name} ({m.language})
            </option>
          ))}
        </select>
      </div>

      {modelo && (
        <>
          {modelo.parameters.length > 0 && (
            <fieldset className="modelo-campos" disabled={ocupado}>
              <legend>Preencha os campos do modelo</legend>
              {modelo.parameters.map((parametro) => {
                const idDoCampo = `modelo-param-${parametro.key.replace(/[^A-Za-z0-9_-]/g, '-')}`;
                const erro = (tentou || (valores[parametro.key] ?? '') !== '' ? errosLocais[parametro.key] : undefined) ?? errosDoServidorPorChave[parametro.key];
                return (
                  <div className="campo" key={parametro.key}>
                    <label htmlFor={idDoCampo}>{parametro.label}</label>
                    <input
                      id={idDoCampo}
                      type="text"
                      value={valores[parametro.key] ?? ''}
                      maxLength={parametro.maxLength}
                      placeholder={parametro.example ? `Ex.: ${parametro.example}` : undefined}
                      autoComplete="off"
                      aria-invalid={erro ? true : undefined}
                      aria-describedby={erro ? `${idDoCampo}-erro` : undefined}
                      onChange={(e) => mudar(parametro.key, e.target.value)}
                    />
                    {erro && (
                      <p className="erro-campo" id={`${idDoCampo}-erro`} role="alert">
                        {erro}
                      </p>
                    )}
                  </div>
                );
              })}
            </fieldset>
          )}

          <blockquote className="modelo-previa" aria-label="Prévia do que o cliente vai receber">
            {montarPrevia(modelo, valores)}
          </blockquote>
          {errosDoServidorPorChave['*'] && (
            <p className="erro-campo" role="alert">
              {errosDoServidorPorChave['*']}
            </p>
          )}
          <div className="acoes">
            <button type="submit" className="btn btn-primario" disabled={ocupado || (tentou && temErroLocal)}>
              {ocupado ? 'Enviando...' : 'Enviar modelo'}
            </button>
            <span className="discreto">Categoria {modelo.category === 'Utility' ? 'utilitária' : modelo.category === 'Marketing' ? 'de marketing' : modelo.category}.</span>
          </div>
        </>
      )}
    </form>
  );
}
