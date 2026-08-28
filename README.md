# Earthquake Show

用于查看日本地震情报的 Windows 原生桌面应用。应用整合气象厅 JMA XML 和 P2PQuake 数据源，在地图、事件列表和报文详情中展示地震信息。

当前版本：`0.66.63`。

## 运行环境

- 操作系统：Windows 10/11，x64。
- 从源码运行或开发：.NET 8 SDK。
- 运行框架依赖版发布程序：.NET 8 Desktop Runtime。
- 自包含发布程序：不需要另外安装 .NET Runtime。
- SQLite 由应用依赖提供，不需要单独安装数据库。
- 实时数据需要访问以下地址，不需要 API Key：
  - `https://www.jma.go.jp/`
  - `https://api.p2pquake.net/`
  - `wss://api.p2pquake.net/`

正式运行不依赖 Node.js、npm、Rust、浏览器、WebView 或 Python。

## 安装与启动

当前仓库没有 MSI 或其他安装器。可以选择从源码运行，或生成 Windows 发布目录。

### 从源码运行

1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。
2. 在仓库根目录执行：

```powershell
dotnet restore EarthquakeShow.sln
dotnet run --project src\EarthquakeShow.App --configuration Release
```

### 发布为 Windows 程序

框架依赖版（目标计算机需要 .NET 8 Desktop Runtime）：

```powershell
dotnet publish src\EarthquakeShow.App\EarthquakeShow.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained false `
  --output publish
```

发布完成后运行 `publish\EarthquakeShow.App.exe`。如需不安装 Runtime 的版本，可以使用自包含发布：

```powershell
dotnet publish src\EarthquakeShow.App\EarthquakeShow.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output publish
```

发布项目已经配置为复制地图、站点目录、固定报文和 `Assets` 下的应用资源。

## 使用说明

- 左侧事件列表按地震发生时间排序，选择事件后查看摘要、观测点、时间线和原始数据。
- 时间线可以按来源切换 JMA XML 或 P2PQuake 报文。
- 地图支持鼠标拖动、滚轮缩放、自动定位和选中都道府县、区域、市町村或观测点。
- 地图会根据全局 `ZoomLevel` 自动选择概览、中精度或高精度资源；高精度资源按当前视野后台加载。
- 收到新报文后，页面会自动选中发布时间最晚的最新情报。
- 没有网络时仍可查看本地缓存；恢复网络后可以继续接收实时更新。

## 数据与配置

应用首次启动会创建本地 SQLite 缓存，并优先从缓存恢复事件。默认路径为：

```text
%LOCALAPPDATA%\EarthquakeShow\earthquake-cache.db
```

连接策略和界面设置保存在：

```text
%LOCALAPPDATA%\EarthquakeShow\settings.json
```

网络请求失败、限流或单条报文解析失败不会清空已有缓存。地图轮廓和测试用固定报文随程序发布，不依赖在线地图瓦片服务。

## 常见问题

### 无法获取最新情报

确认 Windows 防火墙或代理允许访问 JMA、P2PQuake 的 HTTPS 和 WebSocket 地址。应用启动后仍会显示缓存内容；可以稍后使用刷新操作重试。

### 发布后地图或固定数据缺失

请从完整的 `dotnet publish` 输出目录运行，不要只复制可执行文件。地图和报文位于发布目录的 `Assets` 子目录。

### 如何清除缓存

关闭应用后备份并删除 `%LOCALAPPDATA%\EarthquakeShow\earthquake-cache.db`，再次启动会重新建立缓存。设置文件不会因此自动删除。

## 开发与测试

完整构建：

```powershell
dotnet build EarthquakeShow.sln --configuration Release
```

运行全部单元测试：

```powershell
dotnet test EarthquakeShow.sln --configuration Release
```

固定数据校验：

```powershell
python -X utf8 tools\validate_test_data.py
```

地图资源审计（开发期）：

```powershell
python -X utf8 tools\audit_jma_resources.py `
  --stations-json tmp\jma-stations.json `
  --fixed-csv tests\TestData\JmaStations.csv `
  --gis-zip resources\map\20240520_AreaForecastLocalE_GIS.zip `
  --strict
```

真实 P2PQuake 网络探针只用于开发期诊断，不属于默认测试套件：

```powershell
dotnet run --project tools\P2pQuakeNetworkProbe --configuration Release -- `
  --duration-minutes 10
```

## 项目文档

- [需求文档](docs/日本地震信息桌面应用需求文档.md)
- [UI 设计文档](docs/日本地震信息桌面应用UI设计文档.md)
- [UI 实现推进步骤](docs/UI实现推进步骤.md)
- [工程实现文档](docs/工程实现文档.md)
- [数据契约与格式](docs/数据契约与格式.md)
- [版本记录](docs/版本记录.md)
