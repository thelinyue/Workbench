import { EventEmitter } from 'node:events';
import { describe, expect, it, vi } from 'vitest';
import { AppNotificationManager, type NativeAppNotification } from '../../src/main/services/app-notification-manager';

describe('应用 Windows 通知管理器', () => {
  it('校验通知内容并在点击时打开来源应用后投递固定激活事件', async () => {
    const native = new FakeNotification();
    const activate = vi.fn(async () => undefined);
    const manager = new AppNotificationManager({
      isSupported: () => true,
      createNotification: (options) => { native.options = options; return native; },
      activate
    });

    manager.show('analysis-center', {
      title: '分析完成',
      body: 'diagnostic.tgz：未发现明显异常',
      windowKey: 'main',
      activationPayload: { kind: 'result', packageId: 'package-1' }
    });
    native.emit('click');
    await vi.waitFor(() => expect(activate).toHaveBeenCalledOnce());

    expect(native.options).toEqual({ title: '分析完成', body: 'diagnostic.tgz：未发现明显异常' });
    expect(native.shown).toBe(true);
    expect(activate).toHaveBeenCalledWith({
      appId: 'analysis-center',
      windowKey: 'main',
      event: {
        appId: 'analysis-center',
        event: 'host.notification.activated',
        payload: { kind: 'result', packageId: 'package-1' }
      }
    });
  });

  it('通知不可用或参数非法时不影响后台任务并输出中文日志', () => {
    const errors: string[] = [];
    const unsupported = new AppNotificationManager({
      isSupported: () => false,
      createNotification: () => new FakeNotification(),
      activate: async () => undefined,
      logger: { error: (message) => errors.push(message) }
    });

    expect(() => unsupported.show('analysis-center', { title: '分析完成', body: '结果已生成' })).not.toThrow();
    expect(() => unsupported.show('analysis-center', { title: '', body: '结果已生成' })).not.toThrow();
    expect(errors).toEqual([
      '当前系统不支持应用通知，已跳过 analysis-center 的通知。',
      '应用 analysis-center 的通知参数无效，已跳过：通知标题不能为空'
    ]);
  });

  it('拒绝超大或不可序列化的通知激活参数', () => {
    const errors: string[] = [];
    const native = new FakeNotification();
    const manager = new AppNotificationManager({
      isSupported: () => true,
      createNotification: () => native,
      activate: async () => undefined,
      logger: { error: (message) => errors.push(message) }
    });
    const cyclic: Record<string, unknown> = {};
    cyclic.self = cyclic;

    manager.show('analysis-center', { title: '分析完成', body: '结果已生成', activationPayload: { value: 'x'.repeat(5_000) } });
    manager.show('analysis-center', { title: '分析完成', body: '结果已生成', activationPayload: cyclic });

    expect(native.shown).toBe(false);
    expect(errors).toEqual([
      '应用 analysis-center 的通知参数无效，已跳过：通知激活参数不能超过 4 KB',
      '应用 analysis-center 的通知参数无效，已跳过：通知激活参数必须可安全序列化'
    ]);
  });
});

class FakeNotification extends EventEmitter implements NativeAppNotification {
  public options?: { title: string; body: string };
  public shown = false;
  public show(): void { this.shown = true; }
}
