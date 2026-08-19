# 固定测试数据

本目录保存地震情报解析、事件合并和地图图层测试所需的离线输入。运行测试时不得重新下载远端文件，避免上游内容变化导致结果不稳定。

## 数据内容

- `JmaXml/Official/`：2026-08-19 从 JMA 地震火山 Feed 获取的三份官方 XML 原文，组成同一 `EventID` 的 `VXSE51 → VXSE52 → VXSE53` 链。
- `JmaXml/Synthetic/vxse53-correction.xml`：基于官方 `VXSE53` 制作的订正夹具，只修改发布时间、`InfoType`、`Serial` 和震级。
- `JmaXml/Synthetic/vxse53-missing-fields.xml`：字段缺失和坐标无法补齐场景的最小合成夹具。
- `JmaStations.csv`：从 JMA 官方震度观测点坐标 JSON 中按名称筛选，并与官方 `VXSE53` 中的观测点编码关联，共 75 条。
- `Gis/jma-area-test-envelopes.geojson`：根据观测点坐标生成的 7 个测试包络，仅验证区域编码匹配和离线图层加载。
- `Definitions/intensity-scale.json`：震度标准化、排序和应用显示颜色定义。
- `manifest.json`：每份数据的来源、哈希和预期解析结果。

字段、空值、编码和文件结构的长期约定参见[数据契约与格式](../../docs/数据契约与格式.md)。

## 来源

- JMA 地震火山 Feed：<https://www.data.jma.go.jp/developer/xml/feed/eqvol.xml>
- JMA XML 数据说明：<https://www.data.jma.go.jp/developer/xml/feed/other.html>
- JMA 震度观测点页面：<https://www.data.jma.go.jp/eqev/data/intens-st/index.html>
- JMA 震度观测点坐标：<https://www.data.jma.go.jp/eqev/data/intens-st/stations.json>
- JMA GIS 数据：<https://www.data.jma.go.jp/developer/gis.html>

官方 XML 保持下载时的原文。合成文件不是 JMA 发布内容，不能作为真实事件展示或用于验证完整 JMAXML Schema 兼容性。

`jma-area-test-envelopes.geojson` 不是 JMA 官方边界。它只由官方观测点坐标的范围生成，不能用于正式地图、行政判断或区域归属判断。正式地图阶段仍需从 JMA GIS Shapefile 生成经过许可和精度检查的边界资源。

## 离线校验

在仓库根目录执行：

```powershell
python tools\validate_test_data.py
```

校验器检查文件哈希、XML 关键字段、事件链、订正状态、缺失坐标、观测点坐标、GeoJSON 结构和震度定义。校验过程只读取本目录，不访问网络。
