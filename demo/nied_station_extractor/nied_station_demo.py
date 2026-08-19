"""从 NIED 强震监视器实时图像中提取逐站震度相当值。"""

from __future__ import annotations

import argparse
import colorsys
from datetime import datetime, timedelta, timezone
from io import BytesIO
import json
from pathlib import Path
import sys
import tempfile
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from PIL import Image


# 日本全年使用固定的 UTC+9，不需要额外安装 tzdata。
JST = timezone(timedelta(hours=9), name="JST")
POINTS_URL = (
    "https://raw.githubusercontent.com/ingen084/"
    "kyoshin-monitor-observation-points/master/intensity-points.json"
)
IMAGE_URL_TEMPLATE = (
    "http://www.kmoni.bosai.go.jp/data/map_img/RealTimeImg/"
    "jma_s/{date}/{timestamp}.jma_s.gif"
)
CACHE_FILE = (
    Path(tempfile.gettempdir())
    / "earthquake-show-nied-demo"
    / "intensity-points.json"
)
EXPECTED_IMAGE_SIZE = (352, 400)
USER_AGENT = "Earthquake-Show research demo/0.1"


def request_bytes(url: str, timeout: float = 10) -> bytes:
    request = Request(
        url,
        headers={
            "User-Agent": USER_AGENT,
            "Accept": "application/json,image/gif,image/*;q=0.8,*/*;q=0.5",
            "Referer": "http://www.kmoni.bosai.go.jp/",
        },
    )
    with urlopen(request, timeout=timeout) as response:
        return response.read()


def load_points() -> list[dict[str, Any]]:
    if not CACHE_FILE.exists():
        try:
            data = request_bytes(POINTS_URL)
        except (HTTPError, URLError, TimeoutError) as exc:
            raise RuntimeError(f"下载观测点表失败：{exc}") from exc
        CACHE_FILE.parent.mkdir(parents=True, exist_ok=True)
        CACHE_FILE.write_bytes(data)

    try:
        points = json.loads(CACHE_FILE.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"读取观测点缓存失败：{CACHE_FILE}：{exc}") from exc

    if not isinstance(points, list):
        raise RuntimeError("观测点表格式不正确：顶层应为数组")
    return points


def image_url(timestamp: datetime) -> str:
    return IMAGE_URL_TEMPLATE.format(
        date=timestamp.strftime("%Y%m%d"),
        timestamp=timestamp.strftime("%Y%m%d%H%M%S"),
    )


def download_realtime_image(
    requested_time: datetime, fallback_seconds: int = 10
) -> tuple[bytes, datetime, str]:
    for seconds_ago in range(fallback_seconds + 1):
        candidate = requested_time - timedelta(seconds=seconds_ago)
        url = image_url(candidate)
        try:
            return request_bytes(url), candidate, url
        except HTTPError as exc:
            if exc.code == 404:
                continue
            raise RuntimeError(f"下载实时震度图失败：HTTP {exc.code}：{url}") from exc
        except (URLError, TimeoutError) as exc:
            raise RuntimeError(f"下载实时震度图失败：{exc}：{url}") from exc

    raise RuntimeError(
        f"未找到 {requested_time:%Y-%m-%d %H:%M:%S} JST "
        f"及之前 {fallback_seconds} 秒内的实时震度图"
    )


def rgb_to_scale(red: int, green: int, blue: int) -> float:
    hue, saturation, value = colorsys.rgb_to_hsv(
        red / 255, green / 255, blue / 255
    )

    if value <= 0.1 or saturation <= 0.75:
        scale = 0.0
    elif hue > 0.1476:
        scale = (
            (((((280.31 * hue - 916.05) * hue + 1142.6) * hue - 709.95)
               * hue + 234.65) * hue - 40.27) * hue
            + 3.2217
        )
    elif hue > 0.001:
        scale = (
            (((151.4 * hue - 49.32) * hue + 6.753) * hue - 2.481) * hue
            + 0.9033
        )
    else:
        scale = (-0.005171 * value - 0.3282) * value + 1.2236

    return max(0.0, scale)


def jma_class(intensity: float) -> str:
    thresholds = (
        (0.5, "0"),
        (1.5, "1"),
        (2.5, "2"),
        (3.5, "3"),
        (4.5, "4"),
        (5.0, "5弱"),
        (5.5, "5强"),
        (6.0, "6弱"),
        (6.5, "6强"),
    )
    for upper_bound, label in thresholds:
        if intensity < upper_bound:
            return label
    return "7"


def point_matches_region(point: dict[str, Any], region_filter: str | None) -> bool:
    if not region_filter:
        return True
    needle = region_filter.casefold()
    fields = (point.get("region", ""), point.get("sub_region", ""))
    return any(needle in str(field).casefold() for field in fields)


def extract_stations(
    image: Image.Image,
    points: list[dict[str, Any]],
    region_filter: str | None,
) -> tuple[list[dict[str, Any]], int, int]:
    rgba = image.convert("RGBA")
    width, height = rgba.size
    stations: list[dict[str, Any]] = []
    eligible_count = 0
    missing_count = 0

    for point in points:
        image_point = point.get("point")
        if point.get("is_suspended") or not image_point:
            continue
        if not point_matches_region(point, region_filter):
            continue

        eligible_count += 1
        center = image_point["center_point"]
        offset = image_point["offset"]
        x = int(center["x"]) + int(offset["x"])
        y = int(center["y"]) + int(offset["y"])
        if not (0 <= x < width and 0 <= y < height):
            missing_count += 1
            continue

        red, green, blue, alpha = rgba.getpixel((x, y))
        if alpha != 255:
            missing_count += 1
            continue

        scale = rgb_to_scale(red, green, blue)
        intensity = scale * 10 - 3
        location = point.get("location") or {}
        stations.append(
            {
                "code": point.get("code"),
                "name": point.get("name"),
                "network": point.get("type"),
                "region": point.get("region"),
                "sub_region": point.get("sub_region"),
                "latitude": location.get("latitude"),
                "longitude": location.get("longitude"),
                "image_point": {"x": x, "y": y},
                "rgb": [red, green, blue],
                "intensity_equivalent": round(intensity, 3),
                "jma_class_approx": jma_class(intensity),
            }
        )

    stations.sort(key=lambda station: station["intensity_equivalent"], reverse=True)
    return stations, eligible_count, missing_count


def parse_time(value: str) -> datetime:
    try:
        parsed = datetime.strptime(value, "%Y%m%d%H%M%S")
    except ValueError as exc:
        raise argparse.ArgumentTypeError(
            "时间格式应为 YYYYMMDDHHMMSS，例如 20260819123000"
        ) from exc
    return parsed.replace(tzinfo=JST)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="从 NIED 强震监视器 GIF 提取逐站实时震度相当值"
    )
    parser.add_argument(
        "--time",
        type=parse_time,
        help="目标时间（JST），格式 YYYYMMDDHHMMSS；默认取当前时间前 3 秒",
    )
    parser.add_argument("--limit", type=int, default=20, help="输出前 N 个站点，默认 20")
    parser.add_argument(
        "--region", help="按 region 或 sub_region 子串筛选，例如 北海道"
    )
    parser.add_argument("--json", action="store_true", help="以 JSON 输出")
    return parser


def print_table(result: dict[str, Any]) -> None:
    metadata = result["metadata"]
    print(f"图像时间（JST）：{metadata['image_time_jst']}")
    print(f"图像地址：{metadata['image_url']}")
    print(
        "站点："
        f"总计 {metadata['total_points']}，"
        f"参与提取 {metadata['eligible_points']}，"
        f"成功 {metadata['sampled_points']}，"
        f"缺测/越界 {metadata['missing_points']}"
    )
    print()
    print(f"{'序':>3}  {'代码':<8} {'站点':<12} {'地区':<18} {'RGB':<15} {'相当值':>7} {'近似等级':>8}")
    for index, station in enumerate(result["stations"], start=1):
        area = "/".join(
            filter(None, (station["region"], station["sub_region"]))
        )
        rgb = ",".join(str(channel) for channel in station["rgb"])
        print(
            f"{index:>3}  {station['code']:<8} "
            f"{station['name']:<12} {area:<18} {rgb:<15} "
            f"{station['intensity_equivalent']:>7.3f} "
            f"{station['jma_class_approx']:>8}"
        )


def run(args: argparse.Namespace) -> dict[str, Any]:
    if args.limit < 1:
        raise RuntimeError("--limit 必须大于 0")

    requested_time = args.time or (datetime.now(JST) - timedelta(seconds=3))
    image_data, actual_time, url = download_realtime_image(requested_time)
    with Image.open(BytesIO(image_data)) as source_image:
        image_format = source_image.format
        image_size = source_image.size
        if image_format != "GIF" or image_size != EXPECTED_IMAGE_SIZE:
            raise RuntimeError(
                f"实时图格式异常：得到 {image_format} {image_size}，"
                f"预期 GIF {EXPECTED_IMAGE_SIZE}"
            )
        points = load_points()
        stations, eligible_count, missing_count = extract_stations(
            source_image, points, args.region
        )

    return {
        "metadata": {
            "image_time_jst": actual_time.strftime("%Y-%m-%dT%H:%M:%S%z"),
            "image_url": url,
            "image_format": image_format,
            "image_size": list(image_size),
            "station_source": POINTS_URL,
            "total_points": len(points),
            "eligible_points": eligible_count,
            "sampled_points": len(stations),
            "missing_points": missing_count,
            "region_filter": args.region,
            "returned_points": min(args.limit, len(stations)),
            "value_note": "NIED 强震监视器震度相当值；非 JMA 正式计测震度",
        },
        "stations": stations[: args.limit],
    }


def main() -> int:
    args = build_parser().parse_args()
    try:
        result = run(args)
    except (RuntimeError, OSError) as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 1

    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        print_table(result)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
