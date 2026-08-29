"""从已转换的 JMA 海啸沿岸线 GeoJSON 生成运行时 LOD 资源。"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


def normalize_line(line: list[Any], stride: int) -> list[list[float]]:
    points: list[list[float]] = []
    for value in line:
        if not isinstance(value, list) or len(value) < 2:
            continue
        longitude, latitude = value[0], value[1]
        if not isinstance(longitude, (int, float)) or not isinstance(latitude, (int, float)):
            continue
        point = [round(float(longitude), 7), round(float(latitude), 7)]
        if not points or point != points[-1]:
            points.append(point)

    if len(points) < 2:
        return []

    sampled = points[::stride]
    if sampled[-1] != points[-1]:
        sampled.append(points[-1])
    return sampled


def generate(input_path: Path, output_path: Path, stride: int, detail_level: str) -> dict[str, int | str]:
    source = json.loads(input_path.read_text(encoding="utf-8"))
    features: list[dict[str, Any]] = []
    line_count = 0
    point_count = 0

    for feature in source.get("features", []):
        geometry = feature.get("geometry", {})
        geometry_type = geometry.get("type")
        coordinates = geometry.get("coordinates", [])
        lines = coordinates if geometry_type == "MultiLineString" else [coordinates]
        output_lines = [normalize_line(line, stride) for line in lines]
        output_lines = [line for line in output_lines if len(line) >= 2]
        if not output_lines:
            continue

        properties = dict(feature.get("properties", {}))
        features.append(
            {
                "type": "Feature",
                "properties": properties,
                "geometry": {"type": "MultiLineString", "coordinates": output_lines},
            }
        )
        line_count += len(output_lines)
        point_count += sum(len(line) for line in output_lines)

    metadata = dict(source.get("metadata", {}))
    metadata.update(
        {
            "derivedFrom": input_path.name,
            "detailLevel": detail_level,
            "pointStride": stride,
            "duplicatePointsRemoved": True,
        }
    )
    result = {"type": "FeatureCollection", "metadata": metadata, "features": features}
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(
        json.dumps(result, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
        newline="\n",
    )
    return {"featureCount": len(features), "lineCount": line_count, "pointCount": point_count, "detailLevel": detail_level}


def main() -> int:
    parser = argparse.ArgumentParser(description="生成 JMA 海啸沿岸线 LOD GeoJSON")
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--stride", type=int, choices=range(1, 1000), required=True)
    parser.add_argument("--detail-level", choices=("low", "medium", "overview"), required=True)
    args = parser.parse_args()
    print(json.dumps(generate(args.input, args.output, args.stride, args.detail_level), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
