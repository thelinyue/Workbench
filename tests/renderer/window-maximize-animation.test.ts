import { describe, expect, it } from 'vitest';
import { getVirtualWindowFlipValues, shouldSkipWindowMaximizeAnimation } from '../../src/renderer/src/window-maximize-animation';

describe('窗口最大化动效策略', () => {
  it('首次同步最大化状态时不播放动效', () => {
    expect(shouldSkipWindowMaximizeAnimation(undefined, false)).toBe(true);
  });

  it('用户要求减少动态效果时不播放动效', () => {
    expect(shouldSkipWindowMaximizeAnimation(false, true)).toBe(true);
  });

  it('后续最大化状态变化播放动效', () => {
    expect(shouldSkipWindowMaximizeAnimation(false, false)).toBe(false);
  });

  it('虚拟窗口最大化时使用切换前的边界计算 FLIP 几何差值', () => {
    expect(getVirtualWindowFlipValues(
      { left: 120, top: 80, width: 800, height: 560 },
      { left: 16, top: 10, width: 992, height: 654 },
      '8px',
      '0px'
    )).toEqual({
      from: { x: 104, y: 70, scaleX: 800 / 992, scaleY: 560 / 654, borderRadius: '8px', transformOrigin: 'top left' },
      to: { x: 0, y: 0, scaleX: 1, scaleY: 1, borderRadius: '0px' }
    });
  });
});
