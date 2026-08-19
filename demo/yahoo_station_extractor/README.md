# Yahoo 逐站震度提取 Demo

这是一个本地研究用的最小 Demo，使用 Yahoo 边缘存储转存的强震监视器 JSON，不需要下载或解析 GIF。

## 接口

站点表：

```text
https://weather-kyoshin.east.edge.storage-yahoo.jp/SiteList/sitelist.json
```

实时数据：

```text
https://weather-kyoshin.east.edge.storage-yahoo.jp/RealTimeData/YYYYMMDD/YYYYMMDDHHmmss.json
```

站点表的 `items` 是按顺序排列的 `[纬度, 经度]` 数组；实时数据中的 `realTimeData.intensity` 是同长度字符串，第 N 个字符对应站点表第 N 项。`siteConfigId` 用于确认两者属于同一版本的站点表。

字符解码遵循当前同类应用的映射：

```text
level = ord(character) - 100

d-k -> 0
l-m -> 1
n-o -> 2
p-q -> 3
r-s -> 4
t   -> 5弱
u   -> 5强
v   -> 6弱
w   -> 6强
x   -> 7
a-c -> 缺测
```

这里的 `level` 是强震监视器的离散等级，`jma_class_approx` 是按等级映射的近似震度分类，不是 JMA 正式发布的计测震度。

## 运行

此 Demo 只使用 Python 标准库，不需要安装依赖：

```powershell
cd demo\yahoo_station_extractor
python yahoo_station_demo.py --limit 50
```

常用参数：

```powershell
# 输出 JSON，供桌面应用读取
python yahoo_station_demo.py --limit 100 --json

# 默认目标时间为当前 JST 前 1.2 秒；可改为前 2 秒
python yahoo_station_demo.py --delay-ms 2000

# 指定 JST 时间，格式 YYYYMMDDHHMMSS
python yahoo_station_demo.py --time 20260819123000 --limit 20
```

默认遇到 404 时会逐秒向前尝试最多 8 秒，请求是串行的。实时循环不应每 500 ms 启动一个新的并发请求；建议每秒最多请求一次，并保留单请求在途限制。

## 数据限制

- Yahoo 站点表当前只提供坐标和顺序，不提供 NIED 站点代码、站点名或地区，因此输出使用 `station_index`。
- Yahoo 转存数据是第三方数据源，可能延迟、缺帧或变更站点版本。程序会校验 `siteConfigId` 和 `intensity` 长度。
- 该接口不是 NIED 官方逐站 JSON API，也不是 JMA 正式震度情报；仅用于本地研究和界面原型。
- 与 GIF 像素解析相比，这个接口无法直接得到连续震度相当值，但在无法稳定访问 NIED GIF 时更容易使用。
