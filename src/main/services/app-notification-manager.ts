import { z } from 'zod';
import type { AppHostEvent } from '../../shared/app-contract';

const activationPayloadSchema = z.record(z.unknown()).superRefine((value, context) => {
  let serialized: string;
  try { serialized = JSON.stringify(value); }
  catch {
    context.addIssue({ code: z.ZodIssueCode.custom, message: '通知激活参数必须可安全序列化' });
    return;
  }
  if (Buffer.byteLength(serialized, 'utf8') > 4 * 1024) {
    context.addIssue({ code: z.ZodIssueCode.custom, message: '通知激活参数不能超过 4 KB' });
  }
});

const notificationSchema = z.object({
  title: z.string().trim().min(1, '通知标题不能为空').max(120, '通知标题不能超过 120 个字符'),
  body: z.string().trim().min(1, '通知正文不能为空').max(500, '通知正文不能超过 500 个字符'),
  windowKey: z.string().regex(/^[A-Za-z0-9._-]+$/, '通知窗口标识格式无效').max(80).default('main'),
  activationPayload: activationPayloadSchema.optional()
}).strict();

export interface NativeAppNotification {
  show(): void;
  on(event: 'click', listener: () => void): this;
}

interface AppNotificationManagerOptions {
  isSupported(): boolean;
  createNotification(options: { title: string; body: string }): NativeAppNotification;
  activate(input: { appId: string; windowKey: string; event: AppHostEvent }): Promise<void>;
  logger?: Pick<Console, 'error'>;
}

/**
 * 将应用 backend 的单向请求转换为原生系统通知。
 *
 * 参数在主进程重新校验，点击事件只回到发起通知的应用与窗口；系统通知不可用、构造失败或
 * 激活失败都只记录中文错误，绝不能反向影响已经完成的分析任务。
 */
export class AppNotificationManager {
  public constructor(private readonly options: AppNotificationManagerOptions) {}

  public show(appId: string, input: unknown): void {
    const parsed = notificationSchema.safeParse(input);
    if (!parsed.success) {
      this.log(`应用 ${appId} 的通知参数无效，已跳过：${parsed.error.issues[0]?.message ?? '未知参数错误'}`);
      return;
    }
    if (!this.options.isSupported()) {
      this.log(`当前系统不支持应用通知，已跳过 ${appId} 的通知。`);
      return;
    }
    try {
      const value = parsed.data;
      const notification = this.options.createNotification({ title: value.title, body: value.body });
      notification.on('click', () => {
        const event: AppHostEvent = { appId, event: 'host.notification.activated', payload: value.activationPayload };
        void this.options.activate({ appId, windowKey: value.windowKey, event })
          .catch((error) => this.log(`无法打开 ${appId} 的通知目标：${errorMessage(error)}`));
      });
      notification.show();
    } catch (error) {
      this.log(`无法显示 ${appId} 的系统通知：${errorMessage(error)}`);
    }
  }

  private log(message: string): void { (this.options.logger ?? console).error(message); }
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
