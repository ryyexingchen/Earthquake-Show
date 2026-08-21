"""将 JMA GIS Shapefile ZIP 转换为带元数据的 GeoJSON。"""

from __future__ import annotations

import argparse
import io
import json
import math
import struct
import unittest
import zipfile
from itertools import zip_longest
from pathlib import Path
from typing import BinaryIO, Iterator


JMA_GIS_URL = "https://www.data.jma.go.jp/developer/gis.html"


class GisConversionError(ValueError):
    """原始 GIS 结构无法转换时抛出。"""


def read_exact(stream: BinaryIO, size: int) -> bytes:
    data = stream.read(size)
    if len(data) != size:
        raise GisConversionError(f"GIS 文件提前结束，需要 {size} 字节，实际 {len(data)} 字节")
    return data


def read_dbf_fields(stream: BinaryIO) -> tuple[int, list[tuple[str, int]]]:
    header = read_exact(stream, 32)
    header_length = struct.unpack_from("<H", header, 8)[0]
    record_length = struct.unpack_from("<H", header, 10)[0]
    if header_length < 33 or record_length < 2:
        raise GisConversionError("DBF 头部长度或记录长度无效")

    descriptors = read_exact(stream, header_length - 32)
    fields: list[tuple[str, int]] = []
    for offset in range(0, len(descriptors) - 31, 32):
        if descriptors[offset] == 0x0D:
            break
        raw_name = descriptors[offset : offset + 11].split(b"\0", 1)[0]
        name = raw_name.decode("ascii", errors="replace").strip()
        if not name:
            raise GisConversionError("DBF 字段名为空")
        fields.append((name, int(descriptors[offset + 16])))
    if not fields:
        raise GisConversionError("DBF 没有字段")
    return record_length, fields


def read_dbf_records(entry: zipfile.ZipExtFile) -> Iterator[dict[str, str]]:
    record_length, fields = read_dbf_fields(entry)
    entry.seek(0)
    header_bytes = read_exact(entry, 32)
    record_count = struct.unpack_from("<I", header_bytes, 4)[0]
    header_length = struct.unpack_from("<H", header_bytes, 8)[0]
    entry.seek(header_length)
    for _ in range(record_count):
        record = read_exact(entry, record_length)
        if record[0] == 0x2A:
            continue
        offset = 1
        values: dict[str, str] = {}
        for name, width in fields:
            raw_value = record[offset : offset + width]
            values[name] = raw_value.decode("utf-8", errors="replace").replace("\0", "").strip()
            offset += width
        yield values


def iter_shape_records(entry: zipfile.ZipExtFile) -> Iterator[list[list[tuple[float, float]]]]:
    header = read_exact(entry, 100)
    if struct.unpack_from(">i", header, 0)[0] != 9994:
        raise GisConversionError("SHP 文件代码不是 9994")
    if struct.unpack_from("<i", header, 32)[0] != 5:
        raise GisConversionError("当前转换器只支持 Polygon SHP")

    while True:
        record_header = entry.read(8)
        if not record_header:
            break
        if len(record_header) != 8:
            raise GisConversionError("SHP 记录头部不完整")
        content_length = struct.unpack_from(">i", record_header, 4)[0] * 2
        content = io.BytesIO(read_exact(entry, content_length))
        shape_type = struct.unpack("<i", read_exact(content, 4))[0]
        if shape_type == 0:
            continue
        if shape_type != 5:
            raise GisConversionError(f"SHP 记录类型 {shape_type} 不是 Polygon")
        read_exact(content, 32)
        part_count, point_count = struct.unpack("<ii", read_exact(content, 8))
        if part_count <= 0 or point_count < 3:
            continue
        parts = list(struct.unpack(f"<{part_count}i", read_exact(content, part_count * 4)))
        points = [struct.unpack("<dd", read_exact(content, 16)) for _ in range(point_count)]
        rings: list[list[tuple[float, float]]] = []
        for index, start in enumerate(parts):
            end = parts[index + 1] if index + 1 < len(parts) else point_count
            ring = points[start:end]
            if len(ring) >= 3:
                rings.append(close_ring(ring))
        if rings:
            yield rings


def close_ring(ring: list[tuple[float, float]]) -> list[tuple[float, float]]:
    if ring[0] != ring[-1]:
        return [*ring, ring[0]]
    return ring


def ring_area(ring: list[tuple[float, float]]) -> float:
    return sum(
        (left[0] * right[1]) - (right[0] * left[1])
        for left, right in zip(ring, ring[1:])
    ) / 2


def simplify_open(points: list[tuple[float, float]], tolerance: float) -> list[tuple[float, float]]:
    if tolerance <= 0 or len(points) <= 2:
        return points
    tolerance_squared = tolerance * tolerance

    def distance_squared(point: tuple[float, float], start: tuple[float, float], end: tuple[float, float]) -> float:
        dx = end[0] - start[0]
        dy = end[1] - start[1]
        if dx == 0 and dy == 0:
            return (point[0] - start[0]) ** 2 + (point[1] - start[1]) ** 2
        projection = ((point[0] - start[0]) * dx + (point[1] - start[1]) * dy) / (dx * dx + dy * dy)
        projection = max(0.0, min(1.0, projection))
        nearest = (start[0] + projection * dx, start[1] + projection * dy)
        return (point[0] - nearest[0]) ** 2 + (point[1] - nearest[1]) ** 2

    farthest_index = 0
    farthest_distance = 0.0
    for index, point in enumerate(points[1:-1], start=1):
        distance = distance_squared(point, points[0], points[-1])
        if distance > farthest_distance:
            farthest_index = index
            farthest_distance = distance
    if farthest_distance <= tolerance_squared:
        return [points[0], points[-1]]
    left = simplify_open(points[: farthest_index + 1], tolerance)
    right = simplify_open(points[farthest_index:], tolerance)
    return [*left[:-1], *right]


def simplify_ring(ring: list[tuple[float, float]], tolerance: float) -> list[tuple[float, float]]:
    ring = close_ring(ring)
    open_ring = ring[:-1]
    if tolerance <= 0 or len(open_ring) <= 4:
        return ring
    farthest_index = max(
        range(1, len(open_ring)),
        key=lambda index: (open_ring[index][0] - open_ring[0][0]) ** 2
        + (open_ring[index][1] - open_ring[0][1]) ** 2,
    )
    first_path = [*open_ring[: farthest_index + 1], open_ring[0]]
    second_path = [open_ring[farthest_index], *open_ring[farthest_index + 1 :], open_ring[0]]
    first = simplify_open(first_path, tolerance)
    second = simplify_open(second_path, tolerance)
    simplified = [*first[:-1], *second[1:-1]]
    if len(simplified) < 3:
        return ring
    return close_ring(simplified)


def group_rings(rings: list[list[tuple[float, float]]]) -> list[list[list[tuple[float, float]]]]:
    polygons: list[list[list[tuple[float, float]]]] = []
    for ring in rings:
        # JMA Polygon 通常使用顺时针外环、逆时针内环；遇到异常方向时仍保留到最近外环。
        if not polygons or ring_area(ring) < 0:
            polygons.append([ring])
        else:
            polygons[-1].append(ring)
    return polygons


def property_mapping(layer: str, record: dict[str, str]) -> dict[str, str]:
    if layer == "earthquake-area":
        code = record.get("code", "")
        return {"areaCode": code, "name": record.get("name", code), "sourceLayer": "地震情報／細分区域"}
    if layer == "municipality":
        code = record.get("regioncode", "")
        return {
            "municipalityCode": code,
            "regionName": record.get("regionname", ""),
            "name": record.get("name", code),
            "nameKana": record.get("namekana", ""),
            "sourceLayer": "市町村等（地震津波関係）",
        }
    if layer == "prefecture":
        code = record.get("code", "")
        return {"prefectureCode": code, "name": record.get("name", code), "sourceLayer": "地震情報／都道府県等"}
    raise GisConversionError(f"不支持的转换层：{layer}")


def convert_archive(
    input_path: Path,
    output_path: Path,
    layer: str,
    source_version: str,
    acquired_at: str,
    tolerance: float,
) -> dict[str, int | str | float]:
    with zipfile.ZipFile(input_path) as archive:
        shp_names = [name for name in archive.namelist() if name.lower().endswith(".shp")]
        dbf_names = [name for name in archive.namelist() if name.lower().endswith(".dbf")]
        shx_names = [name for name in archive.namelist() if name.lower().endswith(".shx")]
        if len(shp_names) != 1 or len(shx_names) != 1 or len(dbf_names) != 1:
            raise GisConversionError("ZIP 必须包含且只能包含一个 .shp、.shx 和 .dbf")
        with archive.open(shp_names[0]) as shp_entry, archive.open(dbf_names[0]) as dbf_entry:
            shape_records = iter(iter_shape_records(shp_entry))
            dbf_records = iter(read_dbf_records(dbf_entry))
            output_path.parent.mkdir(parents=True, exist_ok=True)
            temporary_path = output_path.with_suffix(output_path.suffix + ".tmp")
            feature_count = 0
            record_count = 0
            with temporary_path.open("w", encoding="utf-8", newline="\n") as handle:
                handle.write('{"type":"FeatureCollection","metadata":')
                json.dump(
                    {
                        "source": "JMA GIS",
                        "sourceUrl": JMA_GIS_URL,
                        "sourceVersion": source_version,
                        "acquiredAt": acquired_at,
                        "coordinateSystem": "JGD2011 (原始 ZIP 无 .prj，发布前需确认 EPSG)",
                        "officialBoundary": True,
                        "sourceLayer": layer,
                        "simplificationToleranceDegrees": tolerance,
                        "sourceArchive": input_path.name,
                    },
                    handle,
                    ensure_ascii=False,
                    separators=(",", ":"),
                )
                handle.write(',"features":[')
                first_feature = True
                missing = object()
                for rings, record in zip_longest(shape_records, dbf_records, fillvalue=missing):
                    if rings is missing or record is missing:
                        raise GisConversionError("SHP 与 DBF 的有效记录数不一致")
                    record_count += 1
                    polygons = [
                        [
                            [[round(longitude, 7), round(latitude, 7)] for longitude, latitude in simplify_ring(ring, tolerance)]
                            for ring in polygon
                        ]
                        for polygon in group_rings(rings)
                    ]
                    geometry: dict[str, object]
                    if len(polygons) == 1:
                        geometry = {"type": "Polygon", "coordinates": polygons[0]}
                    else:
                        geometry = {"type": "MultiPolygon", "coordinates": polygons}
                    if not first_feature:
                        handle.write(",")
                    json.dump(
                        {"type": "Feature", "properties": property_mapping(layer, record), "geometry": geometry},
                        handle,
                        ensure_ascii=False,
                        separators=(",", ":"),
                    )
                    first_feature = False
                    feature_count += 1
                handle.write("]}")
            temporary_path.replace(output_path)
    if feature_count == 0 or feature_count != record_count:
        raise GisConversionError(f"SHP/DBF 记录数不一致或为空：SHP/DBF={feature_count}/{record_count}")
    return {
        "input": input_path.name,
        "output": str(output_path),
        "features": feature_count,
        "tolerance": tolerance,
    }


class ConverterSelfTests(unittest.TestCase):
    def test_simplify_and_group_rings(self) -> None:
        outer = [(0.0, 0.0), (0.0, 1.0), (1.0, 1.0), (1.0, 0.0), (0.0, 0.0)]
        hole = [(0.2, 0.2), (0.8, 0.2), (0.8, 0.8), (0.2, 0.8), (0.2, 0.2)]
        polygons = group_rings([outer, hole])
        self.assertEqual(1, len(polygons))
        self.assertEqual(2, len(polygons[0]))
        self.assertEqual(5, len(simplify_ring(outer, 0.01)))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="转换 JMA GIS Polygon Shapefile ZIP 为 GeoJSON")
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--layer", choices=("earthquake-area", "municipality", "prefecture"))
    parser.add_argument("--source-version", required=False, default="unknown")
    parser.add_argument("--acquired-at", required=False, default="unknown")
    parser.add_argument("--tolerance", type=float, default=0.0001)
    parser.add_argument("--self-test", action="store_true")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=1).run(
            unittest.defaultTestLoader.loadTestsFromTestCase(ConverterSelfTests)
        )
        return 0 if result.wasSuccessful() else 1
    if args.input is None or args.output is None or args.layer is None:
        raise SystemExit("转换时必须同时提供 --input、--output 和 --layer；或使用 --self-test")
    if args.tolerance < 0 or not math.isfinite(args.tolerance):
        raise SystemExit("--tolerance 必须是非负有限数")
    report = convert_archive(
        args.input,
        args.output,
        args.layer,
        args.source_version,
        args.acquired_at,
        args.tolerance,
    )
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
