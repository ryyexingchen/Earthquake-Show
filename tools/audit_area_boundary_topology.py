"""审计 JMA 地震区域 GeoJSON 的边界重复和相邻关系。"""

from __future__ import annotations

import argparse
import json
import math
import unittest
from collections import defaultdict
from pathlib import Path
from typing import Iterable


def normalize_point(point: object, precision: int) -> tuple[float, float] | None:
    if not isinstance(point, list) or len(point) < 2:
        return None
    try:
        longitude = round(float(point[0]), precision)
        latitude = round(float(point[1]), precision)
    except (TypeError, ValueError):
        return None
    if not math.isfinite(longitude) or not math.isfinite(latitude):
        return None
    return longitude, latitude


def iter_rings(geometry: dict[str, object]) -> Iterable[list[object]]:
    geometry_type = geometry.get("type")
    coordinates = geometry.get("coordinates")
    if not isinstance(coordinates, list):
        return
    if geometry_type == "Polygon":
        yield from coordinates
    elif geometry_type == "MultiPolygon":
        for polygon in coordinates:
            if isinstance(polygon, list):
                yield from polygon


def audit_geojson(path: Path, precision: int = 7) -> dict[str, int | float]:
    document = json.loads(path.read_text(encoding="utf-8"))
    features = document.get("features")
    if not isinstance(features, list):
        raise ValueError("GeoJSON 必须包含 features 数组")

    segment_areas: dict[tuple[tuple[float, float], tuple[float, float]], set[str]] = defaultdict(set)
    feature_count = 0
    ring_count = 0
    segment_count = 0
    invalid_ring_count = 0
    missing_code_count = 0

    for feature in features:
        if not isinstance(feature, dict):
            continue
        properties = feature.get("properties")
        geometry = feature.get("geometry")
        if not isinstance(properties, dict) or not isinstance(geometry, dict):
            continue
        feature_count += 1
        code = str(properties.get("areaCode") or properties.get("code") or "")
        if not code:
            missing_code_count += 1

        for ring in iter_rings(geometry):
            ring_count += 1
            if not isinstance(ring, list) or len(ring) < 2:
                invalid_ring_count += 1
                continue
            points = [normalize_point(point, precision) for point in ring]
            if any(point is None for point in points):
                invalid_ring_count += 1
                continue
            valid_points = [point for point in points if point is not None]
            for left, right in zip(valid_points, valid_points[1:]):
                if left == right:
                    continue
                key = tuple(sorted((left, right)))
                segment_areas[key].add(code)
                segment_count += 1

    shared = [areas for areas in segment_areas.values() if len(areas) > 1]
    return {
        "features": feature_count,
        "rings": ring_count,
        "segments": segment_count,
        "uniqueSegments": len(segment_areas),
        "duplicateOccurrences": segment_count - len(segment_areas),
        "sharedSegments": len(shared),
        "maxAdjacentAreas": max((len(areas) for areas in segment_areas.values()), default=0),
        "invalidRings": invalid_ring_count,
        "missingAreaCodes": missing_code_count,
        "precision": precision,
    }


class AuditSelfTests(unittest.TestCase):
    def test_audit_counts_reversed_shared_segment_once(self) -> None:
        import tempfile

        document = {
            "type": "FeatureCollection",
            "features": [
                {
                    "properties": {"areaCode": "A"},
                    "geometry": {
                        "type": "Polygon",
                        "coordinates": [[[0, 0], [1, 0], [1, 1], [0, 1], [0, 0]]],
                    },
                },
                {
                    "properties": {"areaCode": "B"},
                    "geometry": {
                        "type": "Polygon",
                        "coordinates": [[[1, 0], [2, 0], [2, 1], [1, 1], [1, 0]]],
                    },
                },
            ],
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "areas.geojson"
            path.write_text(json.dumps(document), encoding="utf-8")
            result = audit_geojson(path)

        self.assertEqual(2, result["features"])
        self.assertEqual(1, result["sharedSegments"])
        self.assertEqual(1, result["duplicateOccurrences"])

    def test_audit_reports_missing_code_and_invalid_ring(self) -> None:
        import tempfile

        document = {
            "type": "FeatureCollection",
            "features": [
                {
                    "properties": {},
                    "geometry": {"type": "Polygon", "coordinates": [[]]},
                }
            ],
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "areas.geojson"
            path.write_text(json.dumps(document), encoding="utf-8")
            result = audit_geojson(path)

        self.assertEqual(1, result["missingAreaCodes"])
        self.assertEqual(1, result["invalidRings"])


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="审计 JMA 地震区域 GeoJSON 的拓扑边界重复情况")
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--precision", type=int, default=7)
    parser.add_argument("--stdout", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=1).run(
            unittest.defaultTestLoader.loadTestsFromTestCase(AuditSelfTests)
        )
        return 0 if result.wasSuccessful() else 1
    if args.input is None:
        raise SystemExit("审计时必须提供 --input；或使用 --self-test")
    if args.precision < 0 or args.precision > 12:
        raise SystemExit("--precision 必须在 0 到 12 之间")

    report = audit_geojson(args.input, args.precision)
    serialized = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(serialized + "\n", encoding="utf-8")
    if args.stdout or args.output is None:
        print(serialized)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
