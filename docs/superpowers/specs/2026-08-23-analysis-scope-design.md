# 分析中心分析范围设计

## 目标

分析中心始终使用唯一的日志分析插件，不向用户暴露插件或分析引擎选择。v2.0.0 当前日志分析能力定义为“综合分析”；后续日志分析插件升级后，可以在同一进程协议下增加“存储分析”。

## 冻结语义

- `Comprehensive`：当前日志分析插件已有的完整分析流程和报告，不改变现有规则与报告含义。
- `Storage`：日志分析插件未来新增的存储专项分析能力，不是新插件、独立导航或独立报告宿主。
- 两种分析均由唯一日志分析插件执行，并生成各自任务目录下的 `Report/index.html`。
- v2.0.0 首发插件只声明综合分析能力，界面不展示只有一个选项的分析范围选择器。
- 插件未来同时声明综合分析和存储分析能力后，分析中心显示两个分析范围，且每次默认选择综合分析，不记忆上一次存储分析选择。

## 能力声明

当前插件：

```json
{
  "capabilities": [
    "analysis.engine",
    "analysis.scope.comprehensive"
  ]
}
```

未来支持存储分析的插件：

```json
{
  "capabilities": [
    "analysis.engine",
    "analysis.scope.comprehensive",
    "analysis.scope.storage"
  ]
}
```

Host 不根据插件版本号推断能力。未声明 `analysis.scope.storage` 时，Host 必须在启动插件进程前拒绝存储分析请求。

## 界面规则

### 仅支持综合分析

- 快速分析区只显示文件选择、拖放和“开始分析”。
- 待分析与历史记录不显示重复的分析范围列。
- 不展示“存储分析即将支持”等不可用功能。

### 同时支持综合分析和存储分析

- 快速分析区显示“综合分析 / 存储分析”选择。
- 默认选择综合分析。
- 主按钮随选择显示“开始综合分析”或“开始存储分析”。
- 待分析和历史记录显示分析范围，并提供范围筛选。
- 旧的综合分析任务显示为“综合分析”。

## 协议与数据

`analysis-process-v1` 请求必须包含 `analysisScope`：

```json
{
  "protocolVersion": "analysis-process-v1",
  "requestId": "analysis-001",
  "analysisScope": "comprehensive",
  "sourcePath": "...",
  "extractPath": "...",
  "reportDirectory": ".../Report"
}
```

允许值固定为：

- `comprehensive`
- `storage`

数据库中的分析任务保存分析范围。即使首发版本只支持综合分析，也显式写入 `Comprehensive`，避免未来依赖插件版本或报告内容反推历史任务类型。

## 非目标

- 不提供插件选择器或默认分析插件设置。
- 不把存储分析实现为第二个分析插件。
- 不增加存储分析一级导航。
- 不为不同范围定义不同的 HTML 入口文件。
- 不在插件尚未支持时显示禁用的存储分析入口。
