"""审计拓扑边界候选资源中的开放链端点和疑似非端点交点。"""

from __future__ import annotations

import argparse
import json
import math
import unittest
from collections import defaultdict
from pathlib import Path
from typing import Iterable


Point = tuple[int, int]
Segment = tuple[Point, Point]


def _normalize_point(value: object, precision: int) -> Point | None:
    if not isinstance(value, list) or len(value) < 2:
        return None
    try:
        longitude = float(value[0])
        latitude = float(value[1])
    except (TypeError, ValueError):
        return None
    if not math.isfinite(longitude) or not math.isfinite(latitude):
        return None
    scale = 10**precision
    return round(longitude * scale), round(latitude * scale)


def _canonical_segment(left: Point, right: Point) -> Segment:
    return (left, right) if left <= right else (right, left)


def _iter_lines(geometry: object) -> Iterable[list[object]]:
    if not isinstance(geometry, dict):
        return
    geometry_type = geometry.get("type")
    coordinates = geometry.get("coordinates")
    if geometry_type == "LineString" and isinstance(coordinates, list):
        yield coordinates
    elif geometry_type == "MultiLineString" and isinstance(coordinates, list):
        for line in coordinates:
            if isinstance(line, list):
                yield line


def _distance_squared(point: Point, left: Point, right: Point) -> tuple[float, float]:
    px, py = point
    ax, ay = left
    bx, by = right
    dx = bx - ax
    dy = by - ay
    length_squared = dx * dx + dy * dy
    if length_squared == 0:
        return float((px - ax) ** 2 + (py - ay) ** 2), 0.0
    projection = ((px - ax) * dx + (py - ay) * dy) / length_squared
    projection = max(0.0, min(1.0, projection))
    nearest_x = ax + projection * dx
    nearest_y = ay + projection * dy
    return float((px - nearest_x) ** 2 + (py - nearest_y) ** 2), projection


def _cell(point: Point, size: int) -> tuple[int, int]:
    return point[0] // size, point[1] // size


def _neighbor_cells(cell: tuple[int, int]) -> Iterable[tuple[int, int]]:
    x, y = cell
    for offset_x in (-1, 0, 1):
        for offset_y in (-1, 0, 1):
            yield x + offset_x, y + offset_y


def audit_open_endpoints(
    input_path: Path,
    *,
    precision: int = 7,
    near_tolerance: float = 0.00001,
    example_limit: int = 20,
) -> dict[str, object]:
    if precision < 0 or precision > 10:
        raise ValueError("precision 必须在 0 到 10 之间")
    if near_tolerance < 0 or not math.isfinite(near_tolerance):
        raise ValueError("near_tolerance 必须是非负有限数")
    if example_limit < 0:
        raise ValueError("example_limit 必须是非负整数")
    scale = 10**precision
    tolerance_units = max(1, round(near_tolerance * scale))
    document = json.loads(input_path.read_text(encoding="utf-8"))
    features = document.get("features")
    if not isinstance(features, list):
        raise ValueError("GeoJSON 必须包含 features 数组")

    pair_segments: dict[tuple[str, str], set[Segment]] = defaultdict(set)
    invalid_geometry_count = 0
    feature_count = 0
    for feature in features:
        if not isinstance(feature, dict):
            invalid_geometry_count += 1
            continue
        properties = feature.get("properties")
        if not isinstance(properties, dict):
            invalid_geometry_count += 1
            continue
        code1 = str(properties.get("areaCode1") or "")
        code2 = str(properties.get("areaCode2") or "")
        geometry = feature.get("geometry")
        lines = list(_iter_lines(geometry))
        if not lines:
            invalid_geometry_count += 1
            continue
        feature_count += 1
        for line in lines:
            points = [_normalize_point(point, precision) for point in line]
            if len(points) < 2 or any(point is None for point in points):
                invalid_geometry_count += 1
                continue
            valid_points = [point for point in points if point is not None]
            for left, right in zip(valid_points, valid_points[1:]):
                if left != right:
                    pair_segments[(code1, code2)].add(_canonical_segment(left, right))

    pair_dangling: dict[tuple[str, str], list[Point]] = {}
    pair_segment_count: dict[tuple[str, str], int] = {}
    pair_node_degrees: dict[tuple[str, str], dict[Point, int]] = {}
    for pair, segments in pair_segments.items():
        degrees: dict[Point, int] = defaultdict(int)
        for left, right in segments:
            degrees[left] += 1
            degrees[right] += 1
        pair_node_degrees[pair] = degrees
        pair_dangling[pair] = [point for point, degree in degrees.items() if degree == 1]
        pair_segment_count[pair] = len(segments)

    endpoint_pairs_by_point: dict[Point, set[tuple[str, str]]] = defaultdict(set)
    for pair, endpoints in pair_dangling.items():
        for endpoint in endpoints:
            endpoint_pairs_by_point[endpoint].add(pair)
    cross_pair_junctions = sum(1 for pairs in endpoint_pairs_by_point.values() if len(pairs) >= 2)
    cross_pair_endpoint_occurrences = sum(
        len(pairs) for pairs in endpoint_pairs_by_point.values() if len(pairs) >= 2
    )
    isolated_dangling_endpoints = sum(
        1 for pairs in endpoint_pairs_by_point.values() if len(pairs) == 1
    )
    junction_pair_count_distribution: dict[str, int] = defaultdict(int)
    for pairs in endpoint_pairs_by_point.values():
        if len(pairs) >= 2:
            junction_pair_count_distribution[str(len(pairs))] += 1

    pair_summaries = []
    dangling_examples: list[dict[str, object]] = []
    for pair in sorted(pair_segments):
        degrees = pair_node_degrees[pair]
        degree_counts: dict[str, int] = defaultdict(int)
        for degree in degrees.values():
            degree_counts[str(degree)] += 1
        pair_summaries.append(
            {
                "areaCode1": pair[0],
                "areaCode2": pair[1],
                "segments": pair_segment_count[pair],
                "nodes": len(degrees),
                "degree1": degree_counts.get("1", 0),
                "degree2": degree_counts.get("2", 0),
                "degreeOther": sum(count for degree, count in degree_counts.items() if degree not in {"1", "2"}),
                "maxDegree": max(degrees.values(), default=0),
            }
        )
        if len(dangling_examples) < example_limit:
            for endpoint in sorted(pair_dangling[pair]):
                if len(dangling_examples) >= example_limit:
                    break
                dangling_examples.append(
                    {
                        "areaCode1": pair[0],
                        "areaCode2": pair[1],
                        "point": [endpoint[0] / scale, endpoint[1] / scale],
                    }
                )

    near_endpoint_count = 0
    near_interior_count = 0
    examples: list[dict[str, object]] = []
    seen_endpoint_pairs: set[tuple[Point, Point, tuple[str, str]]] = set()
    index_cell_size = max(tolerance_units, 1_000_000)
    global_segment_cells: dict[tuple[int, int], list[tuple[tuple[str, str], Segment]]] = defaultdict(list)
    for pair, segments in pair_segments.items():
        for left, right in segments:
            min_x, max_x = sorted((left[0], right[0]))
            min_y, max_y = sorted((left[1], right[1]))
            first_cell = _cell((min_x, min_y), index_cell_size)
            last_cell = _cell((max_x, max_y), index_cell_size)
            for cell_x in range(first_cell[0], last_cell[0] + 1):
                for cell_y in range(first_cell[1], last_cell[1] + 1):
                    global_segment_cells[(cell_x, cell_y)].append((pair, (left, right)))
    for pair, endpoints in pair_dangling.items():
        endpoint_cells: dict[tuple[int, int], list[Point]] = defaultdict(list)
        for endpoint in endpoints:
            endpoint_cells[_cell(endpoint, index_cell_size)].append(endpoint)
        for endpoint in endpoints:
            nearest_distance: float | None = None
            for cell in _neighbor_cells(_cell(endpoint, index_cell_size)):
                for other in endpoint_cells.get(cell, []):
                    if other == endpoint:
                        continue
                    pair_key = (endpoint, other) if endpoint < other else (other, endpoint)
                    pair_key_with_area = (pair_key[0], pair_key[1], pair)
                    if pair_key_with_area in seen_endpoint_pairs:
                        continue
                    seen_endpoint_pairs.add(pair_key_with_area)
                    distance = math.dist(endpoint, other)
                    if distance <= tolerance_units:
                        near_endpoint_count += 1
                        nearest_distance = distance if nearest_distance is None else min(nearest_distance, distance)
            interior_distance: float | None = None
            interior_pair: tuple[str, str] | None = None
            for cell in _neighbor_cells(_cell(endpoint, index_cell_size)):
                for segment_pair, (left, right) in global_segment_cells.get(cell, []):
                    if endpoint == left or endpoint == right:
                        continue
                    distance_squared, projection = _distance_squared(endpoint, left, right)
                    if 0 < projection < 1 and distance_squared <= tolerance_units * tolerance_units:
                        near_interior_count += 1
                        distance = math.sqrt(distance_squared)
                        interior_distance = distance if interior_distance is None else min(interior_distance, distance)
                        if interior_pair is None:
                            interior_pair = segment_pair
            if (nearest_distance is not None or interior_distance is not None) and len(examples) < example_limit:
                examples.append(
                    {
                        "areaCode1": pair[0],
                        "areaCode2": pair[1],
                        "point": [endpoint[0] / scale, endpoint[1] / scale],
                        "nearestEndpointDistanceUnits": nearest_distance,
                        "nearestInteriorDistanceUnits": interior_distance,
                        "nearestInteriorAreaCode1": interior_pair[0] if interior_pair else None,
                        "nearestInteriorAreaCode2": interior_pair[1] if interior_pair else None,
                    }
                )

    dangling_endpoints = sum(len(points) for points in pair_dangling.values())
    return {
        "input": str(input_path),
        "features": feature_count,
        "segments": sum(pair_segment_count.values()),
        "areaPairs": len(pair_segments),
        "danglingEndpoints": dangling_endpoints,
        "crossPairJunctions": cross_pair_junctions,
        "crossPairEndpointOccurrences": cross_pair_endpoint_occurrences,
        "isolatedDanglingEndpoints": isolated_dangling_endpoints,
        "junctionPairCountDistribution": dict(sorted(junction_pair_count_distribution.items())),
        "nearEndpointPairs": near_endpoint_count,
        "nearInteriorSegments": near_interior_count,
        "invalidGeometries": invalid_geometry_count,
        "precision": precision,
        "nearToleranceDegrees": near_tolerance,
        "danglingExamples": dangling_examples,
        "examples": examples,
        "pairSummaries": pair_summaries,
    }


class OpenEndpointSelfTests(unittest.TestCase):
    def _write_document(self, directory: Path, features: list[dict[str, object]]) -> Path:
        path = directory / "boundaries.geojson"
        path.write_text(json.dumps({"type": "FeatureCollection", "features": features}), encoding="utf-8")
        return path

    def test_reports_open_endpoints_without_near_match(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as directory:
            path = self._write_document(
                Path(directory),
                [
                    {
                        "properties": {"areaCode1": "A", "areaCode2": ""},
                        "geometry": {"type": "LineString", "coordinates": [[0, 0], [1, 0]]},
                    }
                ],
            )
            result = audit_open_endpoints(path)
        self.assertEqual(2, result["danglingEndpoints"])
        self.assertEqual(0, result["nearEndpointPairs"])
        self.assertEqual(0, result["nearInteriorSegments"])

    def test_reports_endpoint_near_segment_interior(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as directory:
            path = self._write_document(
                Path(directory),
                [
                    {
                        "properties": {"areaCode1": "A", "areaCode2": "B"},
                        "geometry": {"type": "LineString", "coordinates": [[0, 0], [2, 0]]},
                    },
                    {
                        "properties": {"areaCode1": "A", "areaCode2": "B"},
                        "geometry": {"type": "LineString", "coordinates": [[1, 0.000001], [1, 1]]},
                    },
                ],
            )
            result = audit_open_endpoints(path, near_tolerance=0.00001)
        self.assertEqual(4, result["danglingEndpoints"])
        self.assertGreaterEqual(result["nearInteriorSegments"], 1)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="审计边界候选资源的开放链端点和疑似非端点交点")
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--precision", type=int, default=7)
    parser.add_argument("--near-tolerance", type=float, default=0.00001)
    parser.add_argument("--example-limit", type=int, default=20)
    parser.add_argument("--stdout", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=1).run(
            unittest.defaultTestLoader.loadTestsFromTestCase(OpenEndpointSelfTests)
        )
        return 0 if result.wasSuccessful() else 1
    if args.input is None:
        raise SystemExit("审计时必须提供 --input；或使用 --self-test")
    report = audit_open_endpoints(
        args.input,
        precision=args.precision,
        near_tolerance=args.near_tolerance,
        example_limit=args.example_limit,
    )
    serialized = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(serialized + "\n", encoding="utf-8")
    if args.stdout or args.output is None:
        print(serialized)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
