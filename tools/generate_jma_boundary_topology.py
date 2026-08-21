"""从 JMA Polygon Shapefile ZIP 生成带相邻区域代码的拓扑边界 GeoJSON。"""

from __future__ import annotations

import argparse
import json
import math
import sqlite3
import tempfile
import unittest
import zipfile
from collections import defaultdict
from itertools import zip_longest
from pathlib import Path
from typing import Iterable

from convert_jma_gis import iter_shape_records, read_dbf_records, ring_area, simplify_open, simplify_ring


class TopologyGenerationError(ValueError):
    """输入几何或输出资源不满足拓扑生成契约时抛出。"""


def quantize_point(point: tuple[float, float], scale: int) -> tuple[int, int] | None:
    longitude, latitude = point
    if not math.isfinite(longitude) or not math.isfinite(latitude):
        return None
    return round(longitude * scale), round(latitude * scale)


def canonical_edge(left: tuple[int, int], right: tuple[int, int]) -> tuple[tuple[int, int], tuple[int, int]]:
    return (left, right) if left <= right else (right, left)


def iter_ring_edges(ring: list[tuple[float, float]], scale: int) -> Iterable[tuple[tuple[int, int], tuple[int, int]]]:
    points = [quantize_point(point, scale) for point in ring]
    if len(points) < 2 or any(point is None for point in points):
        return
    valid = [point for point in points if point is not None]
    for left, right in zip(valid, valid[1:]):
        if left != right:
            yield canonical_edge(left, right)


def _create_edge_table(connection: sqlite3.Connection) -> None:
    connection.execute(
        """
        CREATE TABLE edges(
            ax INTEGER NOT NULL, ay INTEGER NOT NULL, bx INTEGER NOT NULL, by INTEGER NOT NULL,
            code1 TEXT NOT NULL, code2 TEXT NOT NULL, occurrences INTEGER NOT NULL, conflicts INTEGER NOT NULL,
            PRIMARY KEY(ax, ay, bx, by)
        )
        """
    )


def _upsert_edge(connection: sqlite3.Connection, edge: tuple[tuple[int, int], tuple[int, int]], code: str) -> None:
    (ax, ay), (bx, by) = edge
    connection.execute(
        """
        INSERT INTO edges(ax, ay, bx, by, code1, code2, occurrences, conflicts)
        VALUES (?, ?, ?, ?, ?, '', 1, 0)
        ON CONFLICT(ax, ay, bx, by) DO UPDATE SET
            code1 = CASE WHEN edges.code1 = '' THEN excluded.code1 ELSE edges.code1 END,
            code2 = CASE
                WHEN edges.code1 = '' THEN edges.code2
                WHEN edges.code1 = excluded.code1 OR edges.code2 = excluded.code1 THEN edges.code2
                WHEN edges.code2 = '' THEN excluded.code1
                ELSE edges.code2
            END,
            occurrences = edges.occurrences + 1,
            conflicts = edges.conflicts + CASE
                WHEN edges.code1 <> '' AND edges.code1 <> excluded.code1
                    AND edges.code2 <> '' AND edges.code2 <> excluded.code1 THEN 1
                ELSE 0
            END
        """,
        (ax, ay, bx, by, code),
    )


def _walk_edges(
    edge_rows: list[tuple[int, tuple[int, int], tuple[int, int]]],
) -> tuple[list[list[tuple[int, int]]], int]:
    """按相邻代码将连续边合并；节点分叉时保留分叉点，不做几何猜测。"""
    adjacency: dict[tuple[int, int], list[int]] = defaultdict(list)
    edges: dict[int, tuple[tuple[int, int], tuple[int, int]]] = {}
    for edge_id, left, right in edge_rows:
        edges[edge_id] = (left, right)
        adjacency[left].append(edge_id)
        adjacency[right].append(edge_id)

    visited: set[int] = set()
    lines: list[list[tuple[int, int]]] = []
    for edge_id, (left, right) in edges.items():
        if edge_id in visited:
            continue
        start = left
        if len(adjacency[left]) == 2 and len(adjacency[right]) != 2:
            start = right
        path = [start]
        current = start
        while True:
            candidate = next((item for item in adjacency[current] if item not in visited), None)
            if candidate is None:
                break
            visited.add(candidate)
            first, second = edges[candidate]
            current = second if first == current else first
            path.append(current)
            if current == start and len(path) > 2:
                break
            if len(adjacency[current]) != 2:
                break
        if len(path) >= 2:
            lines.append(path)
    dangling = sum(1 for nodes in adjacency.values() if len(nodes) == 1)
    return lines, dangling


def _simplify_line(points: list[tuple[int, int]], tolerance: float) -> list[tuple[int, int]]:
    if tolerance <= 0 or len(points) <= 2:
        return points
    if points[0] == points[-1]:
        return simplify_ring(points, tolerance)
    return simplify_open(points, tolerance)


def generate_topology(
    input_path: Path,
    output_path: Path,
    report_path: Path | None = None,
    *,
    code_field: str = "code",
    source_version: str = "unknown",
    acquired_at: str = "unknown",
    precision: int = 7,
    merge_lines: bool = True,
    tolerance: float = 0.0,
    min_ring_area: float = 0.0,
) -> dict[str, int | float | str]:
    if precision < 0 or precision > 10:
        raise TopologyGenerationError("precision 必须在 0 到 10 之间")
    scale = 10**precision
    if tolerance < 0 or not math.isfinite(tolerance):
        raise TopologyGenerationError("tolerance 必须是非负有限数")
    if min_ring_area < 0 or not math.isfinite(min_ring_area):
        raise TopologyGenerationError("min_ring_area 必须是非负有限数")
    with zipfile.ZipFile(input_path) as archive:
        shp_names = [name for name in archive.namelist() if name.lower().endswith(".shp")]
        dbf_names = [name for name in archive.namelist() if name.lower().endswith(".dbf")]
        if len(shp_names) != 1 or len(dbf_names) != 1:
            raise TopologyGenerationError("ZIP 必须包含且只能包含一个 .shp 和 .dbf")
        with tempfile.NamedTemporaryFile(prefix="jma-boundary-", suffix=".sqlite", delete=False) as temporary:
            database_path = Path(temporary.name)
        connection: sqlite3.Connection | None = None
        try:
            connection = sqlite3.connect(database_path)
            _create_edge_table(connection)
            feature_count = 0
            raw_segment_count = 0
            invalid_ring_count = 0
            filtered_ring_count = 0
            missing_code_count = 0
            with archive.open(shp_names[0]) as shp_entry, archive.open(dbf_names[0]) as dbf_entry:
                shape_records = iter(iter_shape_records(shp_entry))
                dbf_records = iter(read_dbf_records(dbf_entry))
                missing = object()
                for rings, record in zip_longest(shape_records, dbf_records, fillvalue=missing):
                    if rings is missing or record is missing:
                        raise TopologyGenerationError("SHP 与 DBF 的有效记录数不一致")
                    feature_count += 1
                    code = str(record.get(code_field, "")).strip()
                    if not code:
                        code = str(record.get("code", record.get("regioncode", ""))).strip()
                    if not code:
                        missing_code_count += 1
                        continue
                    for ring in rings:
                        if len(ring) < 2:
                            invalid_ring_count += 1
                            continue
                        if min_ring_area > 0 and abs(ring_area(ring)) < min_ring_area:
                            filtered_ring_count += 1
                            continue
                        for edge in iter_ring_edges(ring, scale):
                            raw_segment_count += 1
                            _upsert_edge(connection, edge, code)
                    if feature_count % 100 == 0:
                        connection.commit()
                connection.commit()

            connection.execute("CREATE INDEX edge_areas ON edges(code1, code2)")
            unique_segment_count, shared_segment_count, conflict_segment_count = connection.execute(
                "SELECT COUNT(*), SUM(code2 <> ''), SUM(conflicts > 0) FROM edges"
            ).fetchone()
            duplicate_occurrences = raw_segment_count - unique_segment_count
            output_path.parent.mkdir(parents=True, exist_ok=True)
            temporary_output = output_path.with_suffix(output_path.suffix + ".tmp")
            output_features = 0
            dangling_endpoints = 0
            with temporary_output.open("w", encoding="utf-8", newline="\n") as handle:
                handle.write('{"type":"FeatureCollection","metadata":')
                json.dump(
                    {
                        "source": "JMA GIS",
                        "sourceUrl": "https://www.data.jma.go.jp/developer/gis.html",
                        "sourceVersion": source_version,
                        "acquiredAt": acquired_at,
                        "coordinateSystem": "JGD2011 (原始 ZIP 无 .prj，发布前需确认 EPSG)",
                        "officialBoundary": True,
                        "sourceArchive": input_path.name,
                        "topologyPrecision": precision,
                        "lineMerge": merge_lines,
                        "simplificationToleranceDegrees": tolerance,
                        "minRingAreaDegreesSquared": min_ring_area,
                        "adjacencyMeaning": "areaCode1/areaCode2 是无方向相邻区域集合；空 areaCode2 表示外边界",
                    },
                    handle,
                    ensure_ascii=False,
                    separators=(",", ":"),
                )
                handle.write(',"features":[')
                first_feature = True
                output_coordinates = 0
                if merge_lines:
                    area_pairs = connection.execute(
                        "SELECT DISTINCT code1, code2 FROM edges ORDER BY code1, code2"
                    ).fetchall()
                    for code1, code2 in area_pairs:
                        pair_rows = connection.execute(
                            "SELECT ax, ay, bx, by FROM edges WHERE code1 = ? AND code2 = ? ORDER BY ax, ay, bx, by",
                            (code1, code2),
                        ).fetchall()
                        edge_group = [
                            (edge_id, (row[0], row[1]), (row[2], row[3]))
                            for edge_id, row in enumerate(pair_rows)
                        ]
                        lines, dangling = _walk_edges(edge_group)
                        dangling_endpoints += dangling
                        for line in lines:
                            line = _simplify_line(line, tolerance * scale)
                            feature = {
                                "type": "Feature",
                                "properties": {"areaCode1": code1, "areaCode2": code2},
                                "geometry": {
                                    "type": "LineString",
                                    "coordinates": [[point[0] / scale, point[1] / scale] for point in line],
                                },
                            }
                            if not first_feature:
                                handle.write(",")
                            json.dump(feature, handle, ensure_ascii=False, separators=(",", ":"))
                            first_feature = False
                            output_features += 1
                            output_coordinates += len(line)
                else:
                    rows = connection.execute(
                        "SELECT ax, ay, bx, by, code1, code2 FROM edges ORDER BY code1, code2, ax, ay, bx, by"
                    )
                    for row in rows:
                        feature = {
                            "type": "Feature",
                            "properties": {"areaCode1": row[4], "areaCode2": row[5]},
                            "geometry": {
                                "type": "LineString",
                                "coordinates": [
                                    [row[0] / scale, row[1] / scale],
                                    [row[2] / scale, row[3] / scale],
                                ],
                            },
                        }
                        if not first_feature:
                            handle.write(",")
                        json.dump(feature, handle, ensure_ascii=False, separators=(",", ":"))
                        first_feature = False
                        output_features += 1
                        output_coordinates += 2
                handle.write("]}")
            temporary_output.replace(output_path)
            report: dict[str, int | float | str] = {
                "input": input_path.name,
                "output": str(output_path),
                "features": feature_count,
                "rawSegments": raw_segment_count,
                "uniqueSegments": unique_segment_count,
                "duplicateOccurrences": duplicate_occurrences,
                "sharedSegments": shared_segment_count,
                "boundarySegments": unique_segment_count - shared_segment_count,
                "conflictSegments": conflict_segment_count,
                "missingAreaCodes": missing_code_count,
                "invalidRings": invalid_ring_count,
                "filteredRings": filtered_ring_count,
                "outputFeatures": output_features,
                "outputCoordinates": output_coordinates,
                "danglingEndpoints": dangling_endpoints,
                "precision": precision,
                "lineMerge": merge_lines,
                "simplificationToleranceDegrees": tolerance,
                "minRingAreaDegreesSquared": min_ring_area,
            }
            if report_path is not None:
                report_path.parent.mkdir(parents=True, exist_ok=True)
                report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            return report
        finally:
            if connection is not None:
                connection.close()
            database_path.unlink(missing_ok=True)


class GeneratorSelfTests(unittest.TestCase):
    def test_walk_edges_merges_chain(self) -> None:
        lines, dangling = _walk_edges(
            [
                (0, (0, 0), (1, 0)),
                (1, (1, 0), (2, 0)),
                (2, (2, 0), (3, 0)),
            ]
        )
        self.assertEqual(1, len(lines))
        self.assertEqual([(0, 0), (1, 0), (2, 0), (3, 0)], lines[0])
        self.assertEqual(2, dangling)

    def test_walk_edges_keeps_shared_pair_separate(self) -> None:
        lines, _ = _walk_edges([(0, (0, 0), (1, 0)), (1, (1, 0), (1, 1))])
        self.assertEqual(1, len(lines))

    def test_walk_edges_stops_at_branch(self) -> None:
        lines, _ = _walk_edges(
            [
                (0, (0, 0), (1, 0)),
                (1, (1, 0), (2, 0)),
                (2, (1, 0), (1, 1)),
            ]
        )
        self.assertEqual(3, len(lines))

    def test_upsert_records_two_adjacent_codes(self) -> None:
        connection = sqlite3.connect(":memory:")
        _create_edge_table(connection)
        edge = ((0, 0), (1, 0))
        _upsert_edge(connection, edge, "A")
        _upsert_edge(connection, edge, "A")
        _upsert_edge(connection, edge, "B")

        row = connection.execute("SELECT code1, code2, occurrences, conflicts FROM edges").fetchone()
        connection.close()

        self.assertEqual(("A", "B", 3, 0), row)

    def test_upsert_reports_third_adjacent_code_as_conflict(self) -> None:
        connection = sqlite3.connect(":memory:")
        _create_edge_table(connection)
        edge = ((0, 0), (1, 0))
        for code in ("A", "B", "C"):
            _upsert_edge(connection, edge, code)

        row = connection.execute("SELECT code1, code2, conflicts FROM edges").fetchone()
        connection.close()

        self.assertEqual(("A", "B", 1), row)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="从 JMA Polygon ZIP 生成带相邻区域代码的拓扑边界 GeoJSON")
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--code-field", default="code")
    parser.add_argument("--source-version", default="unknown")
    parser.add_argument("--acquired-at", default="unknown")
    parser.add_argument("--precision", type=int, default=7)
    parser.add_argument("--tolerance", type=float, default=0.0)
    parser.add_argument("--min-ring-area", type=float, default=0.0)
    parser.add_argument("--no-merge", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=1).run(
            unittest.defaultTestLoader.loadTestsFromTestCase(GeneratorSelfTests)
        )
        return 0 if result.wasSuccessful() else 1
    if args.input is None or args.output is None:
        raise SystemExit("生成时必须同时提供 --input 和 --output；或使用 --self-test")
    report = generate_topology(
        args.input,
        args.output,
        args.report,
        code_field=args.code_field,
        source_version=args.source_version,
        acquired_at=args.acquired_at,
        precision=args.precision,
        merge_lines=not args.no_merge,
        tolerance=args.tolerance,
        min_ring_area=args.min_ring_area,
    )
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
