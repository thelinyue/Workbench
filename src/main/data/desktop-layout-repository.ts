import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';

export interface DesktopIconLayout {
  appId: string;
  x: number;
  y: number;
}

const VERTICAL_DEFAULT_LAYOUT_MIGRATION = 'vertical-default-layout-v1';

/**
 * 该仓储只持有桌面图标布局；原生窗口状态由 AppWindowStateRepository 独立管理，
 * 两者都不保存应用诊断包、任务或报告等业务数据。
 *
 * 应用的诊断包、任务和规则数据由各自应用的 backend 管理；宿主数据库只保留
 * 桌面布局表，继续复用旧数据库文件以避免升级时丢失用户图标位置。
 * 布局迁移记录与坐标放在同一事务中，保证首次升级重排后才标记迁移完成。
 */
export class DesktopLayoutRepository {
  private readonly database: DatabaseSync;

  public constructor(databasePath: string) {
    mkdirSync(dirname(databasePath), { recursive: true });
    this.database = new DatabaseSync(databasePath);
    this.database.exec(`
      CREATE TABLE IF NOT EXISTS desktop_layout (app_id TEXT PRIMARY KEY, x INTEGER NOT NULL, y INTEGER NOT NULL);
      CREATE TABLE IF NOT EXISTS desktop_layout_migration (migration_id TEXT PRIMARY KEY);
    `);
  }

  public close(): void {
    this.database.close();
  }

  public save(layout: DesktopIconLayout[]): void {
    this.database.exec('BEGIN;');
    try {
      this.replaceLayout(layout);
      this.database.exec('COMMIT;');
    } catch (error) {
      this.database.exec('ROLLBACK;');
      throw error;
    }
  }

  public list(): DesktopIconLayout[] {
    return this.database.prepare('SELECT app_id AS appId, x, y FROM desktop_layout ORDER BY app_id').all() as unknown as DesktopIconLayout[];
  }

  /**
   * 首次使用竖向默认布局的版本会覆盖旧坐标；完成后只返回用户已保存的位置。
   * 标记和坐标更新必须原子提交，避免应用在首次启动中断后错过重排机会。
   */
  public initializeDefaultLayout(defaultLayout: DesktopIconLayout[]): DesktopIconLayout[] {
    this.database.exec('BEGIN;');
    try {
      const migrated = this.database.prepare('SELECT 1 FROM desktop_layout_migration WHERE migration_id = ?').get(VERTICAL_DEFAULT_LAYOUT_MIGRATION);
      if (!migrated) {
        this.replaceLayout(defaultLayout);
        this.database.prepare('INSERT INTO desktop_layout_migration (migration_id) VALUES (?)').run(VERTICAL_DEFAULT_LAYOUT_MIGRATION);
      }
      const layout = this.list();
      this.database.exec('COMMIT;');
      return layout;
    } catch (error) {
      this.database.exec('ROLLBACK;');
      throw error;
    }
  }

  private replaceLayout(layout: DesktopIconLayout[]): void {
    this.database.exec('DELETE FROM desktop_layout;');
    const insert = this.database.prepare('INSERT INTO desktop_layout (app_id, x, y) VALUES (?, ?, ?)');
    for (const item of layout) insert.run(item.appId, item.x, item.y);
  }
}
