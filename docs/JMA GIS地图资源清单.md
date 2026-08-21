# JMA GIS 地图资源清单

## 1. 文档用途

本文档长期记录 JMA GIS 原始资源的文件版本、包内结构、用途、实现阶段和发布边界。地图实现、区域代码映射或资源更新前必须先核对本文档。

- 官方来源：<https://www.data.jma.go.jp/developer/gis.html>
- 本次审计日期：2026-08-20
- 当前暂存目录：`resources/map/`
- 当前文件数量：10 个 ZIP，共约 1.366 GiB
- 当前状态：已完成地震细分区域、市町村和都道府县三个 Polygon 图层的离线转换；`0.30.0` 接入地震细分区域概览层，`0.33.0` 接入市町村概览层，`0.33.3` 加载区域边界 LineString 资源，`0.33.4` 完成按报文震度分组，`0.33.5` 接入 WPF 分组绘制，`0.34.0` 完成区域面移除和市町村有效震度填色

## 2. 包内通用结构

10 个 ZIP 均包含一组 Shapefile：`.shp`、`.shx`、`.dbf`，没有 `.prj` 或 `.cpg`。文件名使用 CP932/Shift-JIS，DBF 属性值按 UTF-8 可正确读取。所有图层范围约为东经 `122.9337–153.9868`、北纬 `20.4227–45.5572`。

原始 ZIP 不能直接随正式应用发布：体积过大，且运行时不应解析和简化 Shapefile。正式流程应在开发期转换为带来源、版本、坐标系和简化参数的派生 GeoJSON 或后续确认的本地矢量格式。

JMA 页面说明地图制作使用了国土地理院数据。发布前仍需记录 JMA 使用条款、国土地理院署名要求和明确的坐标系元数据；不能仅凭经纬度范围推断后静默写入 EPSG。

## 3. 资源用途总表

| 原始 ZIP | 包内图层 | 几何/数量 | 主要用途 | 工程使用阶段 |
| --- | --- | ---: | --- | --- |
| `20190125_AreaForecast_GIS.zip` | 全国・地方予報区等 | Polygon / 67 | 全国和地方预报区层级，包含重叠的全国、地方等多级区域；不是日本基础轮廓 | 当前地震应用不接入；未来一般气象页面再评估 |
| `20190125_AreaForecastEEW_GIS.zip` | 緊急地震速報／地方予報区 | Polygon / 14 | EEW 地方预报区，适合大范围 EEW 告警着色和区域摘要 | EEW 页面 |
| `20190125_AreaForecastLocalEEW_GIS.zip` | 緊急地震速報／府県予報区 | Polygon / 56 | EEW 府县预报区，适合更细的 EEW 预计震度和告警范围 | EEW 页面 |
| `20190125_AreaForecastLocalM_1saibun_GIS.zip` | 一次細分区域等 | Polygon / 143 | 一般气象预报/警报的一次细分区域 | 当前地震、EEW、海啸功能不使用 |
| `20190125_AreaForecastLocalM_prefecture_GIS.zip` | 府県予報区等 | Polygon / 64 | 一般气象业务的府县预报区 | 当前地震、EEW、海啸功能不使用 |
| `20190125_AreaInformationPrefectureEarthquake_GIS.zip` | 地震情報／都道府県等 | Polygon / 47 | 地震信息都道府县聚合边界，可用于全国概览和都道府县级摘要 | 地震页面辅助层，非区域树主键 |
| `20230517_AreaForecastLocalM_matome_GIS.zip` | 市町村等をまとめた地域等 | Polygon / 384 | 一般气象业务中若干市町村的组合发布区 | 当前地震、EEW、海啸功能不使用 |
| `20240520_AreaForecastLocalE_GIS.zip` | 地震情報／細分区域 | Polygon / 194 | 地震信息细分区域，代码对应 JMAXML 地震区域代码 | `0.30.0` 地震区域着色和区域树第一层，最高优先级 |
| `20240520_AreaTsunami_GIS.zip` | 津波予報区 | PolyLine / 70 | 海啸预报区沿岸线，按预报区代码着色；不是可直接填充的面 | 海啸页面和地震详情海啸范围 |
| `20241128_AreaInformationCity_quake_GIS.zip` | 市町村等（地震津波関係） | Polygon / 1910 | 地震/海啸业务市町村边界，代码用于市町村震度映射 | `0.33.0` 市町村着色、三层树定位，最高优先级 |

## 4. 字段和样例

| 图层 | 关键字段 | 样例 |
| --- | --- | --- |
| 全国・地方予報区等 | `code`, `name` | `010000 全国`、`010100 北海道地方` |
| EEW 地方预报区 | `code`, `name`, `namekana` | `9910 北海道`、`9920 東北` |
| EEW 府县预报区 | `code`, `name`, `namekana` | `9011 北海道道央` |
| 一次细分区域/府县预报区 | `code`, `name` | `011000 宗谷地方` |
| 地震信息都道府县 | `code`, `name` | `01 北海道`、`02 青森県` |
| 市町村组合区域 | `code`, `name` | `011011 宗谷北部` |
| 地震信息细分区域 | `code`, `name`, `namekana` | `100 石狩地方北部` |
| 海啸预报区 | `code`, `name`, `namekana` | `120 オホーツク海沿岸` |
| 地震/海啸市町村 | `regioncode`, `regionname`, `name`, `namekana` | `0110100 札幌中央区` |

正式转换时必须保留代码字符串的前导零，禁止转换为整数。名称只用于显示和诊断，区域关联必须使用代码。

## 5. 按功能选择地图

### 5.1 当前地震情报页面

必须使用：

1. `20240520_AreaForecastLocalE_GIS.zip`：把 `IntensityArea.Code` 映射到地震信息细分区域，生成相邻区域边界震度；区域内部不再绘制最大震度面。
2. `20241128_AreaInformationCity_quake_GIS.zip`：把 `IntensityMunicipality.Code` 映射到市町村边界，支持有效震度填色、点选和三层观测树。

可选辅助：

- `20190125_AreaInformationPrefectureEarthquake_GIS.zip`：用于都道府县级概览、无细分区域数据时的降级摘要或代码覆盖诊断。它不能替代 194 个地震细分区域。

这些 GIS 包不包含观测点坐标。观测点层仍需要独立、带 JMAXML 观测点代码的正式站点目录。

### 5.2 EEW 页面

- `20190125_AreaForecastEEW_GIS.zip`：14 个地方预报区，用于宽范围告警。
- `20190125_AreaForecastLocalEEW_GIS.zip`：56 个府县预报区，用于细化预计震度和告警范围。

不能用普通气象的府县预报区替代 EEW 专用区；名称相近不表示代码或边界等价。

### 5.3 海啸页面

- `20240520_AreaTsunami_GIS.zip`：70 条海啸预报区沿岸线，按注意报、警报和大海啸警报等级改变线色和线宽。
- `20241128_AreaInformationCity_quake_GIS.zip`：需要市町村级详情或关联地震观测时作为辅助面图层。

海啸预报区是 `PolyLine`，不能按普通 Polygon 直接填充。正式渲染器必须支持沿岸线选中、命中测试和多段线。

### 5.4 当前不使用的普通气象图层

`AreaForecast_GIS`、`AreaForecastLocalM_prefecture`、`AreaForecastLocalM_1saibun` 和 `AreaForecastLocalM_matome` 属于一般气象预报/警报区域。当前产品范围是地震、EEW 和海啸，不应为了“补齐地图”把这些图层混入地震代码映射。

## 6. 接入顺序

1. 先转换和验证地震细分区域、市町村、都道府县三个 Polygon 图层。
2. 建立 `IntensityArea.Code` 和 `IntensityMunicipality.Code` 的精确覆盖率报告，保留未映射诊断。
3. 生成全国概览简化层和按需加载的市町村详情层；原始 ZIP 留在开发资源目录。
4. 完成地震页面真实地图和三层观测树后，再转换 EEW 两级图层。
5. 实现海啸页面时单独增加 PolyLine 资源和渲染测试。

## 7. 派生资源

`0.30.0` 已使用 `tools/convert_jma_gis.py` 生成以下高精度和概览资源：

| 派生文件 | 特征数 | 用途 | 当前状态 |
| --- | ---: | --- | --- |
| `src/EarthquakeShow.App/Assets/Data/Map/jma-earthquake-areas.geojson` | 194 | 地震信息细分区域高精度层 | 已生成，后续按需加载 |
| `src/EarthquakeShow.App/Assets/Data/Map/jma-earthquake-areas-overview.geojson` | 194 | 地震信息细分区域低内存概览层 | 当前运行时入口；0.015 度简化、0.0002 平方度碎片过滤 |
| `src/EarthquakeShow.App/Assets/Data/Map/jma-earthquake-municipalities.geojson` | 1910 | 地震/海啸市町村高精度层 | 已生成，后续按需加载 |
| `src/EarthquakeShow.App/Assets/Data/Map/jma-earthquake-municipalities-overview.geojson` | 1910 | 地震/海啸市町村低内存层 | `0.33.0` 当前运行时入口；0.015 度简化、0.0002 平方度碎片过滤 |
| `src/EarthquakeShow.App/Assets/Data/Map/jma-earthquake-area-boundaries-overview.geojson` | 1069 | 带相邻区域代码的区域边界 LineString 候选层 | `0.34.0` App 启动加载、建立索引、按报文震度分组并接入 WPF 绘制；0.015 度简化、0.0002 平方度环过滤 |
| `src/EarthquakeShow.App/Assets/Data/Map/jma-earthquake-prefectures.geojson` | 47 | 都道府县概览辅助层 | 已生成，等待完整几何解析 |

每个文件的 `metadata.simplificationToleranceDegrees` 记录几何简化容差，概览层另外记录 `minPolygonAreaDegreesSquared`。转换器保留 Polygon/MultiPolygon 的全部环；运行时默认加载概览层，精确层不在启动阶段解析。

## 8. 当前审计结论

- 10 个 ZIP 均可打开并读取 `.shp/.shx/.dbf` 结构。
- 三个当前关键包 `AreaForecastLocalE`、`AreaInformationCity_quake`、`AreaTsunami` 已通过现有严格资源审计工具。
- 五个派生文件已进入 `src/EarthquakeShow.App/Assets/Data/Map/`，项目的通配复制规则会将其带入构建输出；当前入口使用低内存概览层，区域边界资源已完成解析、震度分组和 WPF 绘制。
- 高精度层用于后续地图放大或详情页按需加载，不用于应用启动阶段的全国概览；市町村概览层随应用启动加载，当前事件只绘制代码匹配的市町村。

`0.33.1` 已使用 `tools/generate_jma_boundary_topology.py` 从未简化的 `20240520_AreaForecastLocalE_GIS.zip` 生成带无方向相邻代码 `areaCode1/areaCode2` 的 `LineString` 候选资源。工具通过 SQLite 临时索引处理约 1,032 万条原始线段，并支持连续边合并、线简化和微小环过滤；概览参数输出约 3.35 MB。当前报告仍有 1,128 个开放链端点，候选资源暂不进入 App；下一步先区分正常过滤/分叉与需要非端点交点拆分的异常。

`0.33.2` 已完成开放端点审计：1,128 个端点全部归属于 376 个三叉交汇节点，每个节点有 3 组相邻区域；没有孤立端点，也没有端点落在其他相邻关系线段内部。该结果支持继续使用当前候选资源进入 App 解析阶段，但不表示其他版本的 JMA ZIP 可以跳过审计。

`0.33.3` 已将候选资源正式复制为 `src/EarthquakeShow.App/Assets/Data/Map/jma-earthquake-area-boundaries-overview.geojson`，共 1,069 条边界 Feature，构建输出由 `Assets\Data\**\*` 规则自动复制。`0.33.4` 在 ViewModel 中按 `areaCode1/areaCode2` 双向索引和当前报文最大有效震度生成 `BoundaryLayers`；`0.33.5` 将每个震度组转换为一个冻结的 WPF `StreamGeometry`；`0.34.0` 移除区域最大震度面填充，仅保留市町村有效震度面和区域边界。
