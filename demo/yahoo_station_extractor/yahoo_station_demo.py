"""从 Yahoo 转存的强震监视器 JSON 中提取逐站震度。"""

from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import gzip
import json
import sys
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


# 日本全年使用固定的 UTC+9，不需要额外安装 tzdata。
JST = timezone(timedelta(hours=9), name="JST")
SITE_LIST_URL = (
    "https://weather-kyoshin.east.edge.storage-yahoo.jp/SiteList/sitelist.json"
)
REALTIME_URL_TEMPLATE = (
    "https://weather-kyoshin.east.edge.storage-yahoo.jp/RealTimeData/"
    "{date}/{timestamp}.json"
)
USER_AGENT = "Earthquake-Show Yahoo research demo/0.1"


def request_json(url: str, timeout: float = 8) -> dict[str, Any]:
    request = Request(
        url,
        headers={
            "User-Agent": USER_AGENT,
            "Accept": "application/json",
            "Cache-Control": "no-cache",
        },
    )
    try:
        with urlopen(request, timeout=timeout) as response:
            body = response.read()
            if response.headers.get("Content-Encoding", "").lower() == "gzip" or body.startswith(b"\x1f\x8b"):
                body = gzip.decompress(body)
            payload = json.loads(body.decode("utf-8-sig"))
    except HTTPError:
        raise
    except (URLError, TimeoutError, json.JSONDecodeError, UnicodeDecodeError) as exc:
        raise RuntimeError(f"请求 JSON 失败：{exc}：{url}") from exc

    if not isinstance(payload, dict):
        raise RuntimeError(f"JSON 顶层不是对象：{url}")
    return payload


def load_site_list() -> dict[str, Any]:
    try:
        payload = request_json(SITE_LIST_URL + "?time=" + str(int(datetime.now().timestamp() * 1000)))
    except (HTTPError, RuntimeError) as exc:
        raise RuntimeError(f"下载 Yahoo 站点表失败：{exc}") from exc

    site_config_id = payload.get("siteConfigId")
    items = payload.get("items")
    if not isinstance(site_config_id, str) or not isinstance(items, list):
        raise RuntimeError("Yahoo 站点表格式不正确：缺少 siteConfigId 或 items")

    stations: list[dict[str, Any]] = []
    for index, item in enumerate(items):
        if not isinstance(item, list) or len(item) < 2:
            raise RuntimeError(f"Yahoo 站点表第 {index} 项不是 [纬度, 经度]")
        try:
            latitude = float(item[0])
            longitude = float(item[1])
        except (TypeError, ValueError) as exc:
            raise RuntimeError(f"Yahoo 站点表第 {index} 项坐标无效") from exc
        stations.append(
            {
                "station_index": index,
                "latitude": latitude,
                "longitude": longitude,
            }
        )

    return {"site_config_id": site_config_id, "stations": stations}


def realtime_url(timestamp: datetime) -> str:
    return REALTIME_URL_TEMPLATE.format(
        date=timestamp.strftime("%Y%m%d"),
        timestamp=timestamp.strftime("%Y%m%d%H%M%S"),
    )


def load_realtime_data(
    requested_time: datetime, fallback_seconds: int
) -> tuple[dict[str, Any], datetime, str]:
    for seconds_ago in range(fallback_seconds + 1):
        candidate = requested_time - timedelta(seconds=seconds_ago)
        url = realtime_url(candidate)
        try:
            return request_json(url), candidate, url
        except HTTPError as exc:
            if exc.code == 404:
                continue
            raise RuntimeError(f"下载 Yahoo 实时数据失败：HTTP {exc.code}：{url}") from exc
        except RuntimeError as exc:
            raise RuntimeError(f"下载 Yahoo 实时数据失败：{exc}") from exc

    raise RuntimeError(
        f"未找到 {requested_time:%Y-%m-%d %H:%M:%S} JST 及之前 "
        f"{fallback_seconds} 秒内的 Yahoo 实时数据"
    )


def decode_level(value: str) -> int | None:
    """将 Yahoo intensity 字符转换为 0-20 的强震监视器等级。"""
    if len(value) != 1:
        return None
    level = ord(value) - 100
    return level if 0 <= level <= 20 else None


def jma_class(level: int) -> str:
    if level <= 7:
        return "0"
    if level <= 9:
        return "1"
    if level <= 11:
        return "2"
    if level <= 13:
        return "3"
    if level <= 15:
        return "4"
    if level == 16:
        return "5弱"
    if level == 17:
        return "5强"
    if level == 18:
        return "6弱"
    if level == 19:
        return "6强"
    return "7"


def extract_stations(
    site_list: dict[str, Any], realtime_payload: dict[str, Any]
) -> tuple[list[dict[str, Any]], int]:
    realtime_data = realtime_payload.get("realTimeData")
    if not isinstance(realtime_data, dict):
        raise RuntimeError("Yahoo 实时 JSON 缺少 realTimeData")

    expected_config_id = site_list["site_config_id"]
    actual_config_id = realtime_data.get("siteConfigId")
    if actual_config_id != expected_config_id:
        raise RuntimeError(
            "站点表版本与实时数据不一致："
            f"site list={expected_config_id}, realtime={actual_config_id}"
        )

    intensity = realtime_data.get("intensity")
    stations = site_list["stations"]
    if not isinstance(intensity, str) or len(intensity) != len(stations):
        raise RuntimeError(
            "Yahoo intensity 长度与站点数不一致："
            f"intensity={len(intensity) if isinstance(intensity, str) else '未知'}, "
            f"stations={len(stations)}"
        )

    result: list[dict[str, Any]] = []
    missing_count = 0
    for station, raw_value in zip(stations, intensity):
        level = decode_level(raw_value)
        if level is None:
            missing_count += 1
            continue
        result.append(
            {
                **station,
                "raw_value": raw_value,
                "level": level,
                "jma_class_approx": jma_class(level),
            }
        )

    result.sort(key=lambda item: (-item["level"], item["station_index"]))
    return result, missing_count


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
        description="从 Yahoo 转存的强震监视器 JSON 提取逐站震度"
    )
    parser.add_argument(
        "--time",
        type=parse_time,
        help="目标时间（JST）；默认取当前时间前 1.2 秒",
    )
    parser.add_argument(
        "--delay-ms",
        type=int,
        default=1200,
        help="默认目标时间延迟毫秒数，建议 1000-3000，默认 1200",
    )
    parser.add_argument(
        "--fallback-seconds",
        type=int,
        default=8,
        help="遇到 404 时向前顺序尝试的秒数，默认 8",
    )
    parser.add_argument("--limit", type=int, default=20, help="输出前 N 个站点，默认 20")
    parser.add_argument("--json", action="store_true", help="以 JSON 输出")
    return parser


def print_table(result: dict[str, Any]) -> None:
    metadata = result["metadata"]
    print(f"数据时间（JST）：{metadata['data_time_jst']}")
    print(f"数据地址：{metadata['data_url']}")
    print(
        "站点："
        f"总计 {metadata['total_points']}，"
        f"成功 {metadata['sampled_points']}，"
        f"缺测/未知 {metadata['missing_points']}"
    )
    print()
    print(
        f"{'序号':>4}  {'站点索引':>8} {'纬度':>9} {'经度':>10} "
        f"{'原始字符':>8} {'等级':>5} {'近似震度':>8}"
    )
    for index, station in enumerate(result["stations"], start=1):
        print(
            f"{index:>4}  {station['station_index']:>8} "
            f"{station['latitude']:>9.4f} {station['longitude']:>10.4f} "
            f"{station['raw_value']:>8} {station['level']:>5} "
            f"{station['jma_class_approx']:>8}"
        )


def run(args: argparse.Namespace) -> dict[str, Any]:
    if args.limit < 1:
        raise RuntimeError("--limit 必须大于 0")
    if args.delay_ms < 0 or args.fallback_seconds < 0:
        raise RuntimeError("--delay-ms 和 --fallback-seconds 不能为负数")

    requested_time = args.time or (
        datetime.now(JST) - timedelta(milliseconds=args.delay_ms)
    )
    site_list = load_site_list()
    realtime_payload, actual_time, data_url = load_realtime_data(
        requested_time, args.fallback_seconds
    )
    stations, missing_count = extract_stations(site_list, realtime_payload)
    realtime_data = realtime_payload["realTimeData"]
    return {
        "metadata": {
            "data_time_jst": str(realtime_data.get("dataTime", actual_time.isoformat())),
            "data_url": data_url,
            "station_source": SITE_LIST_URL,
            "site_config_id": site_list["site_config_id"],
            "total_points": len(site_list["stations"]),
            "sampled_points": len(stations),
            "missing_points": missing_count,
            "requested_time_jst": requested_time.isoformat(),
            "returned_points": min(args.limit, len(stations)),
            "value_note": "Yahoo 转存的强震监视器离散等级；非 JMA 正式计测震度",
        },
        "stations": stations[: args.limit],
    }


def main() -> int:
    args = build_parser().parse_args()
    try:
        result = run(args)
    except (RuntimeError, OSError, HTTPError) as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 1

    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        print_table(result)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
