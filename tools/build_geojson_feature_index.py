"""为大 GeoJSON 建立按 Feature 偏移和包络查询的轻量索引。"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


def iter_features(data: bytes):
    marker = data.find(b'"features"')
    if marker < 0:
        raise ValueError("GeoJSON 缺少 features 字段")
    array_start = data.find(b"[", marker)
    if array_start < 0:
        raise ValueError("GeoJSON features 不是数组")

    position = array_start + 1
    while position < len(data):
        while position < len(data) and data[position] in b" \t\r\n,":
            position += 1
        if position >= len(data) or data[position] == ord("]"):
            return
        if data[position] != ord("{"):
            raise ValueError(f"Feature 起始位置无效: {position}")

        start = position
        depth = 0
        in_string = False
        escaped = False
        while position < len(data):
            byte = data[position]
            if in_string:
                if escaped:
                    escaped = False
                elif byte == ord("\\"):
                    escaped = True
                elif byte == ord('"'):
                    in_string = False
            elif byte == ord('"'):
                in_string = True
            elif byte == ord("{"):
                depth += 1
            elif byte == ord("}"):
                depth -= 1
                if depth == 0:
                    end = position + 1
                    yield start, end, json.loads(data[start:end])
                    position = end
                    break
            position += 1
        else:
            raise ValueError("Feature 对象未闭合")


def coordinates_bounds(value: Any, points: list[tuple[float, float]]) -> None:
    if (
        isinstance(value, list)
        and len(value) >= 2
        and isinstance(value[0], (int, float))
        and isinstance(value[1], (int, float))
    ):
        points.append((float(value[0]), float(value[1])))
        return
    if isinstance(value, list):
        for child in value:
            coordinates_bounds(child, points)


def build_index(path: Path) -> Path:
    data = path.read_bytes()
    features: list[dict[str, Any]] = []
    for start, end, feature in iter_features(data):
        geometry = feature.get("geometry") or {}
        points: list[tuple[float, float]] = []
        coordinates_bounds(geometry.get("coordinates"), points)
        if not points:
            continue
        longitudes = [point[0] for point in points]
        latitudes = [point[1] for point in points]
        features.append(
            {
                "offset": start,
                "length": end - start,
                "minLongitude": min(longitudes),
                "maxLongitude": max(longitudes),
                "minLatitude": min(latitudes),
                "maxLatitude": max(latitudes),
            }
        )

    index = {
        "version": 1,
        "sourceLength": len(data),
        "source": "JMA GIS",
        "officialBoundary": True,
        "features": features,
    }
    index_path = Path(str(path) + ".index.json")
    index_path.write_text(
        json.dumps(index, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    return index_path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("paths", nargs="+", type=Path)
    args = parser.parse_args()
    for path in args.paths:
        index_path = build_index(path)
        print(f"{path}: {index_path}")


if __name__ == "__main__":
    main()
