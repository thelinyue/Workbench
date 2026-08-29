import { gsap } from 'gsap';
import { useLayoutEffect, useRef, type RefObject } from 'react';

type WindowTransitionKind = 'native' | 'virtual';
type WindowBounds = Pick<DOMRect, 'left' | 'top' | 'width' | 'height'>;

/** 首次同步系统状态或用户已要求减少动态效果时，窗口必须直接显示最终状态。 */
export function shouldSkipWindowMaximizeAnimation(previousMaximized: boolean | undefined, reducedMotion: boolean): boolean {
  return previousMaximized === undefined || reducedMotion;
}

/** 将最大化前后的真实边界转换为 GSAP 可直接使用的 FLIP 起止几何值。 */
export function getVirtualWindowFlipValues(
  previousBounds: WindowBounds,
  nextBounds: WindowBounds,
  previousRadius: string | undefined,
  nextRadius: string
) {
  return {
    from: {
      x: previousBounds.left - nextBounds.left,
      y: previousBounds.top - nextBounds.top,
      scaleX: previousBounds.width / Math.max(nextBounds.width, 1),
      scaleY: previousBounds.height / Math.max(nextBounds.height, 1),
      borderRadius: previousRadius,
      transformOrigin: 'top left'
    },
    to: { x: 0, y: 0, scaleX: 1, scaleY: 1, borderRadius: nextRadius }
  };
}

/**
 * 为不同窗口表面提供统一的最大化反馈。
 * 原生窗口由 Electron 负责真实尺寸调整，宿主只轻量反馈内容；虚拟窗口则使用前后边界做 FLIP 补间。
 */
export function useWindowMaximizeAnimation(
  targetRef: RefObject<HTMLElement | null>,
  maximized: boolean | undefined,
  kind: WindowTransitionKind,
  layoutVersion = ''
): void {
  const previousMaximizedRef = useRef<boolean | undefined>(undefined);
  const previousBoundsRef = useRef<DOMRect | undefined>(undefined);
  const previousRadiusRef = useRef<string | undefined>(undefined);
  const animationRef = useRef<gsap.core.Tween | null>(null);

  useLayoutEffect(() => {
    const target = targetRef.current;
    if (!target) return;
    const previousMaximized = previousMaximizedRef.current;
    const previousBounds = previousBoundsRef.current;
    const previousRadius = previousRadiusRef.current;
    const nextBounds = target.getBoundingClientRect();
    const nextRadius = getComputedStyle(target).borderTopLeftRadius;
    const reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;

    animationRef.current?.kill();
    previousMaximizedRef.current = maximized;
    previousBoundsRef.current = nextBounds;
    previousRadiusRef.current = nextRadius;

    if (previousMaximized === maximized || shouldSkipWindowMaximizeAnimation(previousMaximized, reducedMotion)) {
      gsap.set(target, { clearProps: 'transform,opacity,borderRadius,transformOrigin' });
      return;
    }

    const context = gsap.context(() => {
      if (kind === 'virtual' && previousBounds) {
        const flip = getVirtualWindowFlipValues(previousBounds, nextBounds, previousRadius, nextRadius);
        animationRef.current = gsap.fromTo(target, flip.from, {
          ...flip.to,
          duration: 0.24,
          ease: 'power2.out',
          clearProps: 'transform,borderRadius,transformOrigin'
        });
        return;
      }
      animationRef.current = gsap.fromTo(target, { opacity: 0.96, scale: 0.992 }, {
        opacity: 1,
        scale: 1,
        duration: 0.18,
        ease: 'power2.out',
        clearProps: 'transform,opacity'
      });
    }, target);

    return () => {
      animationRef.current?.kill();
      animationRef.current = null;
      context.revert();
    };
  }, [kind, layoutVersion, maximized, targetRef]);
}
