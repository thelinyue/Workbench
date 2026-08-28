import { createElement, type ReactElement } from 'react';
import { App } from './App';
import { AppWindowHost } from './app-window-host';

/** 根据唯一受支持的查询值选择 renderer 根表面，其他查询一律回退到桌面壳。 */
export function resolveRendererSurfaceElement(search: string): ReactElement {
  const surface = new URLSearchParams(search).get('surface');
  return createElement(surface === 'app-window' ? AppWindowHost : App);
}
