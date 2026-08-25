import { describe, expect, it } from 'vitest';
import { createAppWindow, minimizeWindow, moveWindow, resizeWindow, type AppWindow } from '../../src/renderer/window-manager';

const window: AppWindow = {
  id: 'analysis-center',
  title: '分析中心',
  x: 160,
  y: 90,
  width: 980,
  height: 650,
  zIndex: 2,
  minimized: false,
  maximized: false
};

describe('虚拟窗口管理器', () => {
  it('移动窗口时保留其他窗口状态', () => {
    expect(moveWindow([window], 'analysis-center', 300, 240)).toEqual([
      expect.objectContaining({ x: 300, y: 240, minimized: false })
    ]);
  });

  it('最小化窗口后再次操作会恢复窗口', () => {
    const minimized = minimizeWindow([window], 'analysis-center');
    expect(minimized[0].minimized).toBe(true);
    expect(minimizeWindow(minimized, 'analysis-center')[0].minimized).toBe(false);
  });

  it('重复打开同一应用时返回已有窗口而不是创建第二个窗口', () => {
    expect(createAppWindow([window], 'analysis-center', '分析中心')).toEqual([window]);
  });

  it('调整窗口大小时保留最小尺寸并不影响其他状态', () => {
    expect(resizeWindow([window], 'analysis-center', 240, 180)[0]).toEqual(
      expect.objectContaining({ width: 640, height: 420, x: 160, y: 90 })
    );
  });
});
