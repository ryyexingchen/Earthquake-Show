# Earthquake-Show

一个用于展示日本地震信息的 Windows 桌面应用。

## 当前状态

- 正式技术路线：`C# + .NET 8 + WPF`
- 产品形态：Windows 原生桌面应用，不使用浏览器、WebView 或本地 Web 服务
- 当前版本：`0.6.0`
- 已实现：原生主窗口外壳、地震事件空状态、地图占位区、四个详情标签、JST 时钟和底部状态栏
- 固定数据：同一事件的官方 `VXSE51/52/53`、订正与缺字段夹具、75 个观测点坐标、7 个区域测试包络和震度定义
- 领域模型：地震事件、报文、震源、震度区域/市町村/观测点、来源引用与来源状态
- 事件归并：按事件合并报文、按来源消息去重、稳定排序，并生成支持订正和取消状态的事件摘要
- 数据边界：通过仓储接口读取、查询、订阅和刷新事件，开发阶段提供内存实现
- 页面状态：保存事件列表、当前事件、报文版本、查询、地图、来源、加载、离线和错误状态
- 下一步：完善主窗口三栏布局与动态状态绑定

`0.1.0` Tauri/Vue 技术原型已停止开发，并于 2026-08-19 按用户要求从工作区删除；历史决策保留在版本记录和工程实现文档中。

## 目标技术栈

- WPF：原生窗口、控件和布局。
- C#：界面、业务模型、数据源和后台任务统一使用一种语言。
- Mapsui/SkiaSharp：不依赖网页的地图与 GIS 图层渲染。
- Microsoft.Data.Sqlite：本地事件和报文缓存。
- xUnit：领域逻辑和数据适配器测试。

## 正式执行入口

```powershell
dotnet run --project src\EarthquakeShow.App --configuration Release
```

## 测试入口

```powershell
dotnet test EarthquakeShow.sln --configuration Release
```

首次开发或依赖变化后先执行 `dotnet restore`。完整开发校验还包括：

```powershell
dotnet build EarthquakeShow.sln --configuration Release
python -X utf8 tools\validate_test_data.py
```

当前应用界面仍只展示原生桌面外壳；页面状态已接入空内存仓储，但固定数据尚未解析或显示。地图、缓存、报文解析和网络数据源仍未实现。

## 项目文档

- [需求文档](./docs/日本地震信息桌面应用需求文档.md)
- [UI 设计文档](./docs/日本地震信息桌面应用UI设计文档.md)
- [UI 实现推进步骤](./docs/UI实现推进步骤.md)
- [版本记录](./docs/版本记录.md)
- [工程实现文档](./docs/工程实现文档.md)
- [数据契约与格式](./docs/数据契约与格式.md)
