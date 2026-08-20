"""审计 JMA 观测点目录和 GIS 原始资源，不修改任何输入文件。"""

from __future__ import annotations

import argparse
import csv
import json
import math
import sys
import zipfile
from collections import Counter
from pathlib import Path
from typing import Any


def normalize_name(value: str) -> str:
    """去除 JMA 报文名称常见的末尾标记，用于覆盖率核对。"""
    return value.strip().rstrip("＊*").strip()


def load_station_rows(path: Path) -> list[dict[str, Any]]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, list):
        raise ValueError("站点 JSON 根节点必须是数组")
    rows: list[dict[str, Any]] = []
    for index, item in enumerate(value, start=1):
        if not isinstance(item, dict):
            raise ValueError(f"站点 JSON 第 {index} 项不是对象")
        for field in ("lat", "lon", "name", "pref"):
            if field not in item:
                raise ValueError(f"站点 JSON 第 {index} 项缺少 {field}")
        latitude = float(item["lat"])
        longitude = float(item["lon"])
        if not math.isfinite(latitude) or not -90 <= latitude <= 90:
            raise ValueError(f"站点 JSON 第 {index} 项纬度无效")
        if not math.isfinite(longitude) or not -180 <= longitude <= 180:
            raise ValueError(f"站点 JSON 第 {index} 项经度无效")
        rows.append(item)
    return rows


def load_fixed_rows(path: Path) -> list[dict[str, str]]:
    with path.open(encoding="utf-8-sig", newline="") as handle:
        rows = list(csv.DictReader(handle))
    required = {"station_code", "report_name", "latitude", "longitude"}
    if not rows or not required.issubset(rows[0]):
        raise ValueError("固定站点 CSV 缺少必要字段")
    return rows


def audit_archive(path: Path) -> str | None:
    if not path.exists():
        return "GIS 压缩包不存在"
    try:
        with zipfile.ZipFile(path) as archive:
            bad_member = archive.testzip()
            if bad_member is not None:
                return f"GIS 压缩包成员校验失败：{bad_member}"
            if not archive.namelist():
                return "GIS 压缩包为空"
    except (OSError, zipfile.BadZipFile) as error:
        return f"GIS 压缩包不可用：{error}"
    return None


def audit(args: argparse.Namespace) -> tuple[dict[str, Any], list[str]]:
    station_rows = load_station_rows(args.stations_json)
    fixed_rows = load_fixed_rows(args.fixed_csv)
    station_names = Counter(normalize_name(str(row["name"])) for row in station_rows)
    station_name_set = set(station_names)
    fixed_names = [normalize_name(row["report_name"]) for row in fixed_rows]
    matched = [name for name in fixed_names if name in station_name_set]
    duplicate_names = sorted(name for name, count in station_names.items() if count > 1)
    archive_error = audit_archive(args.gis_zip)

    report = {
        "stationCount": len(station_rows),
        "uniqueStationNames": len(station_names),
        "duplicateStationNames": duplicate_names,
        "prefectureCount": len({str(row["pref"]) for row in station_rows}),
        "fixedStationCount": len(fixed_rows),
        "fixedStationNameMatches": len(matched),
        "fixedStationNameCoverage": len(matched) / len(fixed_rows),
        "gisArchive": str(args.gis_zip),
        "gisArchiveError": archive_error,
    }
    errors: list[str] = []
    if len(matched) != len(fixed_rows):
        errors.append("固定报文存在无法按名称对应的观测点")
    if duplicate_names:
        errors.append("正式站点目录存在重复名称，不能直接使用名称作为主键")
    if archive_error is not None:
        errors.append(archive_error)
    return report, errors


def main() -> int:
    parser = argparse.ArgumentParser(description="审计 JMA 观测点和 GIS 原始资源")
    parser.add_argument("--stations-json", type=Path, required=True)
    parser.add_argument("--fixed-csv", type=Path, required=True)
    parser.add_argument("--gis-zip", type=Path, required=True)
    parser.add_argument("--strict", action="store_true", help="发现资源问题时返回失败")
    args = parser.parse_args()

    try:
        report, errors = audit(args)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"资源审计失败：{error}", file=sys.stderr)
        return 2

    print(json.dumps(report, ensure_ascii=False, indent=2))
    if errors:
        print("审计问题：", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1 if args.strict else 0
    print("资源审计通过。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
