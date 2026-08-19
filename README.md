# Earthquake-Show

一个用于展示日本地震信息的 Windows 桌面应用。

## 当前状态

- 正式技术路线：`C# + .NET 8 + WPF`
- 产品形态：Windows 原生桌面应用，不使用浏览器、WebView 或本地 Web 服务
- 当前版本：`0.3.0`
- 已实现：原生主窗口外壳、地震事件空状态、地图占位区、四个详情标签、JST 时钟和底部状态栏
- 固定数据：同一事件的官方 `VXSE51/52/53`、订正与缺字段夹具、75 个观测点坐标、7 个区域测试包络和震度定义
- 下一步：定义地震事件、报文、震源、区域和观测点领域模型

`0.1.0` Tauri/Vue 技术原型已停止开发，并于 2026-08-19 按用户要求从工作区删除；历史决策保留在版本记录和工程实现文档中。

## 目标技术栈

- WPF：原生窗口、控件和布局。
- C#：界面、业务模型、数据源和后台任务统一使用一种语言。
- Mapsui/SkiaSharp：不依赖网页的地图与 GIS 图层渲染。
- Microsoft.Data.Sqlite：本地事件和报文缓存。
- xUnit：领域逻辑和数据适配器测试。

## 开发运行

```powershell
dotnet restore
dotnet build EarthquakeShow.sln
dotnet run --project src\EarthquakeShow.App
python tools\validate_test_data.py
```

当前应用界面仍只展示原生桌面外壳，固定数据尚未接入 UI；地图、缓存、报文解析和网络数据源仍未实现。

## 项目文档

- [需求文档](./docs/日本地震信息桌面应用需求文档.md)
- [UI 设计文档](./docs/日本地震信息桌面应用UI设计文档.md)
- [UI 实现推进步骤](./docs/UI实现推进步骤.md)
- [版本记录](./docs/版本记录.md)
- [工程实现文档](./docs/工程实现文档.md)
