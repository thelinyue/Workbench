import { Copy, Minus, Square, X } from 'lucide-react';

export interface WindowControlsProps {
  title: string;
  variant: 'shell' | 'window';
  maximized?: boolean;
  onMinimize: () => void;
  onMaximize: () => void;
  onClose: () => void;
}

/**
 * 工作台桌面、虚拟窗口和原生应用窗口共用的系统控制按钮。
 *
 * 组件只呈现调用方提供的真实窗口状态；最大化与还原使用不同的可访问名称和图标，
 * 避免自绘标题栏在系统快捷键或双击触发状态变化后向辅助技术报告错误操作。
 */
export function WindowControls({ title, variant, maximized = false, onMinimize, onMaximize, onClose }: WindowControlsProps) {
  const buttonClassName = `window-control-button window-control-button-${variant}`;

  return <div className={`window-controls ${variant === 'shell' ? 'shell-window-controls' : ''}`} aria-label={`窗口控制：${title}`}>
    <button className={buttonClassName} type="button" aria-label={`最小化${title}`} onClick={onMinimize}><Minus size={14} strokeWidth={1.5} aria-hidden="true" /></button>
    <button className={buttonClassName} type="button" aria-label={`${maximized ? '还原' : '最大化'}${title}`} onClick={onMaximize}>{maximized ? <Copy size={14} strokeWidth={1.5} aria-hidden="true" /> : <Square size={14} strokeWidth={1.5} aria-hidden="true" />}</button>
    <button className={buttonClassName} type="button" aria-label={`关闭${title}`} onClick={onClose}><X size={14} strokeWidth={1.5} aria-hidden="true" /></button>
  </div>;
}
