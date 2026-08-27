# Earthquake-Show

一个用于展示日本地震信息的 Windows 桌面应用。

## 当前状态

- 正式技术路线：`C# + .NET 8 + WPF`
- 产品形态：Windows 原生桌面应用，不使用浏览器、WebView 或本地 Web 服务
- 当前版本：`0.66.26`
- 已实现：原生主窗口三栏布局、固定 JMAXML 解析与真实样本接入、4,368 条 JMA 震度观测点坐标目录及 XML/P2P 坐标补全、统一高饱和度震度配色与观测点对比边框、摘要/四层观测树/按来源切换的全宽换行报文时间线/原始数据详情、按来源统计的报文快照序号、JMA 离线真实地震区域地图、震度速報和无详细观测点震源情报的已知区域震度填色与黑白对比边界、已有详细观测点后的震源情报和震源・震度情报的震度色区域轮廓、有效市町村震度填色、震源/当前报文观测点图层、震源情报后的震度速報继承前序震源位置、按震度控制观测点绘制层级、区域和市町村地图定位、选中都道府县/区域/市町村时保留震度色并使用高对比双层描边、选中观测点时使用高对比双层圆环、选中对象自动定位和包络自适应缩放、重复点击观测树节点取消高亮且地图拖动/缩放保持高亮、空或未加载轮廓不会触发高亮绘制异常、观测树内容固定拉伸到详情栏、区域和市町村坐标可从概览几何回退解析、收到新增报文时按发布时间自动跳转到最新情报、以震源为中心的事件包络自适应跟随按钮、根据全局 ZoomLevel 选择概览/中精度/高精度的自动加载、震度速報无点位时按区域自动跟随、按当前最大震度连续显示的动态图例、虚拟化地震事件列表、搜索、五类筛选、默认按发生时间的三种排序、刷新、键盘选择和紧凑窗口详情抽屉
- 地图启动优化：默认加载约 3.20 MB 的区域概览层、约 4.58 MB 的市町村概览层和约 3.35 MB 的区域边界概览层；全局 `ZoomLevel=1.0` 表示全国概览自动适配，事件自动适配按全国比例换算为全局级别，手动每次调整一个级别对应约 `1.25x` 实际比例。缩放超过 `ZoomLevel > 2` 时后台按需切换 `0.003°` 中精度资源，超过 `ZoomLevel > 12` 时再按当前视野及 20% 缓冲范围切换区域/市町村高精度资源和区域轮廓，缩回时恢复对应层级；高精度视野越界时显式先显示中精度并让出 UI 调度周期，再异步加载新范围，最多保留当前和上一范围高精度几何，视口仍在在途缓冲范围内时复用加载任务并更新最新中心，旧请求不会覆盖新请求，回拖命中缓存时状态栏显示“高精度 · 缓存”。地图缩放范围统一限制为 `0.5–24` 级，拖动期间复用现有图形并仅更新位移，松手后再提交地理中心和重建图形；地图使用蓝色海洋和浅暖色陆地。区域面在正式边界存在时不再重复描边，支持鼠标左键平移和滚轮以指针位置为中心缩放，手动操作会暂停自动跟随；HTTP 实时源在线状态下每 5 秒并行检查，失败和限流仍按退避处理，P2P 流式来源收到报文即处理；观测点全局 ZoomLevel 达到 8 时显示放大圆圈和震度标签，低倍率恢复小圆点；真实 Release 启动性能基准仍待记录。
- 地图性能诊断：完整重绘使用后台调度，拖动期间只更新内容位移；高精度几何在拖动期间暂存，松开后按最新视口校验并复用或重新加载。`[MapDebug]` 日志输出到 Visual Studio“输出→调试”和启动应用的终端，重点字段为 `RenderComplete` 的 `elapsed/build/children` 以及 LOD 的 `LoadBegin/LoadApplied/LoadReadyDeferred`。
- 远地事件：JMA XML `遠地地震に関する情報` 和 P2PQuake `issue.type=Foreign` 进入统一事件列表；远地火山喷发按官方自由评论中的“噴火”识别，未知震级/深度保留为未知。相同远地类型、类别、发生时间和坐标的 P2P 多次更新会先归并，再与 XML 合并。地图暂不绘制远地震度图层，只显示可用震源并使用 `ZoomLevel=0.5`；拖动跨越日期变更线时规范化经度，且不加载仅覆盖日本的中/高精度资源。
- P2PQuake 海啸字段：按官方 JSON API v2 规范解析 `domesticTsunami` 和 `foreignTsunami`；国内 `None` 显示“津波の心配なし”，`Checking` 显示“津波 调查中”，`Warning` 按官方定义显示“津波预报”；海外状态仅作为带“海外：”前缀的补充，不把海外可能性误报为日本国内警报。历史 SQLite P2P 报文读取时会从原始 JSON 重新归一化海啸摘要。
- 固定数据：同一事件的官方 `VXSE51/52/53`、订正与缺字段夹具、令和八年熊本地震 7 份 JMA 官方详细 JSON、75 个带报文代码的固定观测点、4,368 个正式观测点坐标、7 个区域测试包络和震度定义；已有 SQLite 缓存启动时会幂等补入缺失的固定报文，缓存只读时仍保留已读取事件
- 领域模型：地震事件、报文、震源、震度区域/市町村/观测点、来源引用与来源状态
- 事件归并：按事件合并报文、按来源消息去重、稳定排序；P2PQuake 互补阶段先按发生时间、发布时间、震度和震源约束归并，再与 JMA XML 匹配，XML 始终是默认首选来源
- 数据边界：通过仓储接口读取、查询、订阅和刷新事件；Infrastructure 使用 SQLite 保存报文和来源状态，并接入 JMA XML 详情与 P2PQuake 补充源
- WebSocket 状态：P2PQuake 文本消息流已接入应用生命周期和 SQLite，支持单连接、传输层 keep-alive、主动连接轮换、断线重连、HTTP/WS 状态分离、状态栏显示重连次数、JST 下次重试时间、连接持续时间、最近错误详情、最近消息活性和连接异常统计；关闭窗口时会等待初始化、HTTP 刷新和 WebSocket 循环结束后再退出，设置页可调整 keep-alive 和连接轮换参数并即时重连
- 页面状态：保存事件列表、当前事件、报文版本、来源差异、可能关联来源、自动刷新状态、搜索筛选、排序、地图、来源、加载、离线和错误状态
- nTool 评估：事件 API 与现有来源重叠；逐站实时 JSON 仅保留为隔离研究对象，未进入正式数据链路
- 资源审计：已接入 4,368 条 JMA 站点坐标并核对 10 个正式 JMA GIS 压缩包；完整站点代码和市町村父级目录仍待补齐
- `0.29.4` 已完善报文时间线和详情摘要：缺失字段不再显示为未知变化，已收到的震源/规模/海啸信息在后续报文中持续显示，海啸状态使用分级颜色
- JMA 增量回补：短 Feed 用于实时刷新；短 Feed 覆盖不足时自动合并官方长期 `eqvol_l.xml`，并在来源状态显示 Feed 覆盖范围。2026-08-24 已从长期 Feed 向本机缓存补入 42 条 8 月 21–24 日 XML 报文
- 多来源刷新按来源独立计算 XML 增量起点；单个来源超时不会中断其他来源，空 Feed 会触发长期 Feed 回补，XML 报文存在时默认摘要和详情优先选择 XML
- JMAXML 海啸代码：保存 `ForecastComment/Code`，已将官方样例 `0215` 识别为“津波の心配なし”；`津波なし`、若干海面变动和解除文本按明确边界处理；未知代码保留原值并回退文本/调查中，避免把通用模板误判成警报。`0.44.0` 新增独立 `VTSE*` 海啸报文的结构化模型和基础 XML 解析器，数据源和海啸专页仍待真实报文校验
- 海啸数据源：`0.55.1` 修复海啸页地图在未选中报文时被详情容器一并隐藏的问题；同时记录官方海啸站点目录调查结论，当前仍不凭名称或地震站点目录猜测站点坐标
- 下一步：取得并接入带官方代码的海啸沿岸观测站坐标，再显示观测点位置，不复用地震站点坐标

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

当前应用启动时优先读取 `%LOCALAPPDATA%\EarthquakeShow\earthquake-cache.db`。首次运行或缓存为空时写入随程序复制的官方 `VXSE51/52/53` 和订正夹具，页面可用后请求 JMA XML Atom Feed 详情和 P2PQuake `https://api.p2pquake.net/v2/jma/quake` 补充数据。P2PQuake 作为非官方补充/降级源参与同一地震事件合并，并复用 JMA 观测点目录补齐可匹配坐标；网络、限流或解析失败不会清空缓存，页面会显示对应来源状态。当前地图轮廓来自随应用打包的 JMA 地震细分区域 GeoJSON，不依赖网络，不执行在线瓦片下载。

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
