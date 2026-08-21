"""将 JMA 震度观测点原始 JSON 转为应用发布资源。"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Any


SCHEMA_VERSION = 1
SOURCE_URL = "https://www.data.jma.go.jp/eqev/data/intens-st/stations.json"


def convert(source: Path) -> dict[str, Any]:
    payload = json.loads(source.read_text(encoding="utf-8"))
    if not isinstance(payload, list):
        raise ValueError("观测点原始 JSON 根节点必须是数组。")

    stations: list[dict[str, Any]] = []
    for index, item in enumerate(payload, start=1):
        if not isinstance(item, dict):
            raise ValueError(f"第 {index} 条观测点不是对象。")

        name = str(item.get("name", "")).strip()
        if not name:
            raise ValueError(f"第 {index} 条观测点缺少名称。")
        latitude = float(item.get("lat"))
        longitude = float(item.get("lon"))
        if not math.isfinite(latitude) or not -90 <= latitude <= 90:
            raise ValueError(f"第 {index} 条观测点纬度无效。")
        if not math.isfinite(longitude) or not -180 <= longitude <= 180:
            raise ValueError(f"第 {index} 条观测点经度无效。")

        stations.append(
            {
                "stationCode": None,
                "name": name,
                "latitude": latitude,
                "longitude": longitude,
                "prefectureCode": str(item.get("pref", "")).strip(),
                "municipalityCode": None,
                "affiliation": str(item.get("affi", "")).strip(),
            }
        )

    normalized_names = [normalize_name(station["name"]) for station in stations]
    if len(set(normalized_names)) != len(normalized_names):
        raise ValueError("观测点目录存在规范化后的重复名称。")

    return {
        "schemaVersion": SCHEMA_VERSION,
        "datasetVersion": "jma-intensity-stations-2026-08-19",
        "retrievedDate": "2026-08-19",
        "sourceUrl": SOURCE_URL,
        "coordinateReferenceSystem": "WGS84 longitude/latitude",
        "stationCodeStatus": "source-does-not-provide-jmaxml-station-code",
        "stations": stations,
    }


def normalize_name(value: str) -> str:
    """去掉报文名称末尾标记和空白，仅用于唯一名称坐标补全。"""
    return value.strip().rstrip("＊*").strip()


def self_test() -> None:
    assert normalize_name(" 熊本市中央区大江＊ ") == "熊本市中央区大江"
    print("JMA 观测点转换器自测通过。")


def main() -> int:
    parser = argparse.ArgumentParser(description="生成 Earthquake Show JMA 观测点目录")
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        self_test()
        return 0
    if args.input is None or args.output is None:
        parser.error("转换时必须同时提供 --input 和 --output")

    result = convert(args.input)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(result, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    print(f"已生成 {len(result['stations'])} 个观测点：{args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
