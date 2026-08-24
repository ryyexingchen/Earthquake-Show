# Earthquake-Show

一个用于展示日本地震信息的 Windows 桌面应用。

## 当前状态

- 正式技术路线：`C# + .NET 8 + WPF`
- 产品形态：Windows 原生桌面应用，不使用浏览器、WebView 或本地 Web 服务
- 当前版本：`0.49.0`
- 已实现：原生主窗口三栏布局、固定 JMAXML 解析与真实样本接入、4,368 条 JMA 震度观测点坐标目录及 JMAXML 坐标补全、统一高饱和度震度配色与观测点对比边框、摘要/区域-市町村-观测点树/报文时间线/原始数据详情、报文快照切换、JMA 离线真实地震区域地图、按相邻区域震度绘制边界、有效市町村震度填色、震源/当前报文观测点图层、按震度控制观测点绘制层级、区域和市町村地图定位、震度速報无点位时按区域自动跟随、虚拟化地震事件列表、搜索、五类筛选、三种排序、刷新、键盘选择和紧凑窗口详情抽屉
- 地图启动优化：默认加载约 3.20 MB 的区域概览层、约 4.58 MB 的市町村概览层和约 3.35 MB 的区域边界概览层；缩放超过 `ZoomLevel > 2` 时后台按需切换约 1.03 MB 区域、2.77 MB 市町村和 0.86 MB 边界的 `0.003°` 中精度资源，缩回时恢复概览，失败时保留当前地图。高精度资源仍不在运行时解析；地图使用蓝色海洋和浅暖色陆地。区域面在正式边界存在时不再重复描边，避免简化几何错位；地图缩放上限为 12 倍，支持鼠标左键平移和滚轮以指针位置为中心缩放，手动操作会暂停自动跟随；真实 Release 启动性能基准仍待记录。
- 固定数据：同一事件的官方 `VXSE51/52/53`、订正与缺字段夹具、令和八年熊本地震 7 份 JMA 官方详细 JSON、75 个带报文代码的固定观测点、4,368 个正式观测点坐标、7 个区域测试包络和震度定义；已有 SQLite 缓存启动时会幂等补入缺失的固定报文，缓存只读时仍保留已读取事件
- 领域模型：地震事件、报文、震源、震度区域/市町村/观测点、来源引用与来源状态
- 事件归并：按事件合并报文、按来源消息去重、稳定排序，并生成支持订正和取消状态的事件摘要
- 数据边界：通过仓储接口读取、查询、订阅和刷新事件；Infrastructure 使用 SQLite 保存报文和来源状态，并接入 JMA JSON 摘要、JMA XML 详情与 P2PQuake 补充源
- WebSocket 状态：P2PQuake 文本消息流已接入应用生命周期和 SQLite，支持单连接、传输层 keep-alive、主动连接轮换、断线重连、HTTP/WS 状态分离、状态栏显示重连次数、JST 下次重试时间、连接持续时间、最近错误详情、最近消息活性和连接异常统计；关闭窗口时会等待初始化、HTTP 刷新和 WebSocket 循环结束后再退出，设置页可调整 keep-alive 和连接轮换参数并即时重连
- 页面状态：保存事件列表、当前事件、报文版本、来源差异、可能关联来源、自动刷新状态、搜索筛选、排序、地图、来源、加载、离线和错误状态
- nTool 评估：事件 API 与现有来源重叠；逐站实时 JSON 仅保留为隔离研究对象，未进入正式数据链路
- 资源审计：已接入 4,368 条 JMA 站点坐标并核对 10 个正式 JMA GIS 压缩包；完整站点代码和市町村父级目录仍待补齐
- `0.29.4` 已完善报文时间线和详情摘要：缺失字段不再显示为未知变化，已收到的震源/规模/海啸信息在后续报文中持续显示，海啸状态使用分级颜色
- JMA 增量回补：短 Feed 用于实时刷新；短 Feed 覆盖不足时自动合并官方长期 `eqvol_l.xml`，并在来源状态显示 Feed 覆盖范围。2026-08-24 已从长期 Feed 向本机缓存补入 42 条 8 月 21–24 日 XML 报文
- JMAXML 海啸代码：保存 `ForecastComment/Code`，已将官方样例 `0215` 识别为“津波の心配なし”；`津波なし`、若干海面变动和解除文本按明确边界处理；未知代码保留原值并回退文本/调查中，避免把通用模板误判成警报。`0.44.0` 新增独立 `VTSE*` 海啸报文的结构化模型和基础 XML 解析器，数据源和海啸专页仍待真实报文校验
- 海啸数据源：`0.49.0` 已新增只读 `TsunamiPageState/TsunamiPageViewModel`，从 `tsunami_reports` 排序、选择和刷新报文；海啸报文仍不进入地震事件列表
- 下一步：制作海啸报文查询页视图，先展示报文列表和当前报文摘要，不改变地震页面数据链路

`0.1.0` Tauri/Vue 技术原型已停止开发，并于 2026-08-19 按用户要求从工作区删除；历史决策保留在版本记录和工程实现文档中。

## 目标技术栈

- WPF：原生窗口、控件和布局。
- C#：界面、业务模型、数据源和后台任务统一使用一种语言。
- Mapsui/SkiaSharp：不依赖网页的地图与 GIS 图层渲染。
- Microsoft.Data.Sqlite：本地事件和报文缓存。
- xUnit：领域逻辑和数据适配器测试。

## 运行环境

- 操作系统：Windows 10/11 x64。
- 源码开发和 `dotnet run`：需要安装 .NET 8 SDK。
- 普通框架依赖版程序：需要安装 .NET 8 Desktop Runtime；后续发布为 `win-x64` 自包含版本后不需要单独安装 Runtime。
- SQLite：不需要单独安装。`Microsoft.Data.Sqlite` 和 SQLite 原生运行库由应用依赖提供。
- 实时刷新：需要能够访问 `https://www.jma.go.jp/`、`https://api.p2pquake.net/` 和 `wss://api.p2pquake.net/`；不需要 API Key。没有网络时仍可读取本地缓存。
- Node.js、npm、Rust、浏览器、WebView 和 Python 都不是正式运行依赖；Python 只用于开发期固定数据校验。

## 正式执行入口

```powershell
dotnet run --project src\EarthquakeShow.App --configuration Release
```

## 测试入口

```powershell
dotnet test EarthquakeShow.sln --configuration Release
```

## 地图资源审计入口

```powershell
python -X utf8 tools\audit_jma_resources.py --stations-json tmp\jma-stations.json --fixed-csv tests\TestData\JmaStations.csv --gis-zip resources\map\20240520_AreaForecastLocalE_GIS.zip --strict
```

## 真实网络验证入口

该工具不属于默认测试套件，只用于开发期验证 P2PQuake 握手、重连和主动轮换：

```powershell
dotnet restore tools\P2pQuakeNetworkProbe\P2pQuakeNetworkProbe.csproj
dotnet run --project tools\P2pQuakeNetworkProbe --configuration Release -- --duration-minutes 10
```

需要访问 `wss://api.p2pquake.net/v2/ws`；外部 `429`、无事件报文或上游格式变化会在输出中作为警告记录。

需要保存完整文本消息以分析上游协议时，追加 `--capture-messages PATH`；该选项只用于开发期取证，文件每行保存一条重组后的原始 JSON，不应作为正式运行日志长期保留：

```powershell
dotnet run --project tools\P2pQuakeNetworkProbe --configuration Release -- --duration-minutes 10 --capture-messages tmp\p2pquake-ws-messages.jsonl
```

首次开发或依赖变化后先执行 `dotnet restore`。完整开发校验还包括：

```powershell
dotnet build EarthquakeShow.sln --configuration Release
python -X utf8 tools\validate_test_data.py
```

当前应用启动时优先读取 `%LOCALAPPDATA%\EarthquakeShow\earthquake-cache.db`。首次运行或缓存为空时写入随程序复制的官方 `VXSE51/52/53` 和订正夹具，页面可用后依次请求 JMA `list.json` 摘要、JMA XML Atom Feed 详情和 P2PQuake `https://api.p2pquake.net/v2/jma/quake` 补充数据。P2PQuake 只作为非官方补充/降级源，使用独立 `p2pquake:{id}` 事件身份，不与 JMA 事件强行合并；网络、限流或解析失败不会清空缓存，页面会显示对应来源状态。当前地图轮廓来自随应用打包的 JMA 地震细分区域 GeoJSON，不依赖网络，不执行在线瓦片下载。

## 资源放置

- 原始音频素材：`resources\sounds\`。
- 普通图片、图标和背景：`resources\images\`。
- 地图、图层或渲染贴图：`resources\textures\`。
- 大型模型和外部数据文件：`resources\data\`。
- 原生 DLL：`resources\lib\`，必须先确认架构、来源和许可证。

根目录 `resources\` 已被 Git 忽略，也不会自动进入发布包。确认需要随程序发布后，再将资源复制到 `src\EarthquakeShow.App\Assets\Audio|Images|Textures|Data`；这些目录已经配置为自动复制到构建和发布输出。

## 项目文档

- [需求文档](./docs/日本地震信息桌面应用需求文档.md)
- [UI 设计文档](./docs/日本地震信息桌面应用UI设计文档.md)
- [UI 实现推进步骤](./docs/UI实现推进步骤.md)
- [版本记录](./docs/版本记录.md)
- [工程实现文档](./docs/工程实现文档.md)
- [数据契约与格式](./docs/数据契约与格式.md)
