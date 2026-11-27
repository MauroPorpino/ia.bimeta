import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { PlataformaApp } from './plataforma/PlataformaApp';
import './styles.css';

const raiz = document.getElementById('root');
if (!raiz) {
  throw new Error('Elemento #root não encontrado em index.html.');
}

// O painel da Plataforma BImeta é uma aplicação separada (sessão própria), servida em /plataforma.
const ehPlataforma = window.location.pathname === '/plataforma' || window.location.pathname.startsWith('/plataforma/');

createRoot(raiz).render(<StrictMode>{ehPlataforma ? <PlataformaApp /> : <App />}</StrictMode>);
