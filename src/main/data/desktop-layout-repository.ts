import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';

export interface DesktopIconLayout {
  appId: string;
  x: number;
  y: number;
}

/**
 * 工作台宿主只持有桌面图标布局，不再持有应用业务数据。
 *
 * 应用的诊断包、任务和规则数据由各自应用的 backend 管理；宿主数据库只保留
 * 桌面布局表，继续复用旧数据库文件以避免升级时丢失用户图标位置。
 */
export class DesktopLayoutRepository {
  private readonly database: DatabaseSync;

  public constructor(databasePath: string) {
    mkdirSync(dirname(databasePath), { recursive: true });
    this.database = new DatabaseSync(databasePath);
    this.database.exec('CREATE TABLE IF NOT EXISTS desktop_layout (app_id TEXT PRIMARY KEY, x INTEGER NOT NULL, y INTEGER NOT NULL);');
  }

  public close(): void {
    this.database.close();
  }

  public save(layout: DesktopIconLayout[]): void {
    this.database.exec('BEGIN;');
    try {
      this.database.exec('DELETE FROM desktop_layout;');
      const insert = this.database.prepare('INSERT INTO desktop_layout (app_id, x, y) VALUES (?, ?, ?)');
      for (const item of layout) insert.run(item.appId, item.x, item.y);
      this.database.exec('COMMIT;');
    } catch (error) {
      this.database.exec('ROLLBACK;');
      throw error;
    }
  }

  public list(): DesktopIconLayout[] {
    return this.database.prepare('SELECT app_id AS appId, x, y FROM desktop_layout ORDER BY app_id').all() as unknown as DesktopIconLayout[];
  }
}
