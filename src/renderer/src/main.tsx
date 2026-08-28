import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { resolveRendererSurfaceElement } from './renderer-surface';
import './styles.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {resolveRendererSurfaceElement(window.location.search)}
  </StrictMode>
);
