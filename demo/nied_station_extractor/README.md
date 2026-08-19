# NIED 逐站震度提取 Demo

这是一个本地研究用的最小 Demo：下载 NIED 强震监视器的地表实时震度 GIF，按观测点在图中的像素坐标取色，再反算每个站点的连续震度相当值。

## 原理

1. 按日本标准时间（JST）构造实时图地址：

   ```text
   http://www.kmoni.bosai.go.jp/data/map_img/RealTimeImg/jma_s/YYYYMMDD/YYYYMMDDHHmmss.jma_s.gif
   ```

2. 从 [kyoshin-monitor-observation-points](https://github.com/ingen084/kyoshin-monitor-observation-points) 取得站点表。每个站点在 GIF 上的实际像素坐标为：

   ```text
   x = center_point.x + offset.x
   y = center_point.y + offset.y
   ```

3. 读取该像素的 RGBA。透明像素视为缺测；其他颜色转换到 HSV 后，使用 [KyoshinMonitorLib](https://github.com/ingen084/KyoshinMonitorLib) 所采用的多项式反算标尺值，最后计算：

   ```text
   震度相当值 = scale * 10 - 3
   ```

默认请求当前 JST 时间前 3 秒的数据。若该秒尚未生成，程序会逐秒向前尝试，最多 10 秒；请求是串行的，不会并发扫接口。站点表首次下载后缓存在系统临时目录 `earthquake-show-nied-demo` 中。

## 运行

建议在虚拟环境中安装唯一依赖：

```powershell
cd demo\nied_station_extractor
python -m venv .venv
.\.venv\Scripts\python -m pip install -r requirements.txt
.\.venv\Scripts\python nied_station_demo.py
```

常用参数：

```powershell
# 输出震度相当值最高的 50 个站点
python nied_station_demo.py --limit 50

# 只看北海道（匹配 region 或 sub_region）
python nied_station_demo.py --region 北海道

# 查询指定 JST 时间并输出 JSON
python nied_station_demo.py --time 20260819123000 --limit 100 --json
```

JSON 中保留站点代码、名称、网络类型、地区、经纬度、图像坐标、RGB、连续震度相当值及近似 JMA 等级，可直接交给桌面应用的地图层使用。

## 数据性质与限制

- 这里提取的是强震监视器图像的像素反算值，不是 NIED 提供的逐站 JSON API。
- `intensity_equivalent` 是强震监视器的“震度相当值”，`jma_class_approx` 只是按阈值划分的近似标签；二者都不是 JMA 正式发布的计测震度或地震情报。
- GIF、URL、色标和站点坐标都可能调整。正式应用需要监测图像尺寸、调色板和站点表版本，不能假定此实现永久稳定。
- 图像时间可能延迟或缺帧。实时循环建议至多每秒请求一次，并保留顺序回退与缓存，避免并发请求。
- 使用前应阅读 [NIED 强震观测网公开数据说明](https://www.kyoshin.bosai.go.jp/ja/about_pubdata/)；本 Demo 只面向个人本地研究，不包含再分发设计。

算法的多项式最初可追溯到 [JQuake 作者的颜色反算说明](https://qiita.com/NoneType1/items/a4d2cf932e20b56ca444)。
