"""将 JMA 海啸预报区 PolyLine Shapefile 转换为运行时 GeoJSON。"""

from __future__ import annotations

import argparse
import io
import json
import struct
import zipfile
from itertools import zip_longest
from pathlib import Path
from typing import BinaryIO, Iterator

from convert_jma_gis import GisConversionError, read_dbf_fields, read_exact, simplify_open


JMA_GIS_URL = "https://www.data.jma.go.jp/developer/gis.html"


def read_dbf_records_utf8(entry: zipfile.ZipExtFile) -> Iterator[dict[str, str]]:
    record_length, fields = read_dbf_fields(entry)
    entry.seek(0)
    header = read_exact(entry, 32)
    record_count = struct.unpack_from("<I", header, 4)[0]
    header_length = struct.unpack_from("<H", header, 8)[0]
    entry.seek(header_length)
    for _ in range(record_count):
        record = read_exact(entry, record_length)
        if record[0] == 0x2A:
            continue
        offset = 1
        values: dict[str, str] = {}
        for name, width in fields:
            values[name] = (
                record[offset : offset + width]
                .decode("utf-8", errors="replace")
                .replace("\0", "")
                .strip()
            )
            offset += width
        yield values


def iter_polyline_records(entry: zipfile.ZipExtFile) -> Iterator[list[list[tuple[float, float]]]]:
    header = read_exact(entry, 100)
    if struct.unpack_from(">i", header, 0)[0] != 9994:
        raise GisConversionError("SHP 文件代码不是 9994")
    if struct.unpack_from("<i", header, 32)[0] != 3:
        raise GisConversionError("海啸资源必须是 PolyLine SHP")

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
        if shape_type != 3:
            raise GisConversionError(f"SHP 记录类型 {shape_type} 不是 PolyLine")
        read_exact(content, 32)
        part_count, point_count = struct.unpack("<ii", read_exact(content, 8))
        if part_count <= 0 or point_count < 2:
            continue
        parts = list(struct.unpack(f"<{part_count}i", read_exact(content, part_count * 4)))
        points = [struct.unpack("<dd", read_exact(content, 16)) for _ in range(point_count)]
        lines: list[list[tuple[float, float]]] = []
        for index, start in enumerate(parts):
            end = parts[index + 1] if index + 1 < len(parts) else point_count
            line = points[start:end]
            if len(line) >= 2:
                lines.append(line)
        if lines:
            yield lines


def convert_archive(
    input_path: Path,
    output_path: Path,
    source_version: str,
    acquired_at: str,
    tolerance: float,
) -> dict[str, int | str | float]:
    with zipfile.ZipFile(input_path) as archive:
        shp_names = [name for name in archive.namelist() if name.lower().endswith(".shp")]
        dbf_names = [name for name in archive.namelist() if name.lower().endswith(".dbf")]
        if len(shp_names) != 1 or len(dbf_names) != 1:
            raise GisConversionError("ZIP 必须包含且只能包含一个 .shp 和 .dbf")

        output_path.parent.mkdir(parents=True, exist_ok=True)
        temporary_path = output_path.with_suffix(output_path.suffix + ".tmp")
        feature_count = 0
        point_count = 0
        with archive.open(shp_names[0]) as shp_entry, archive.open(dbf_names[0]) as dbf_entry:
            shape_records = iter(iter_polyline_records(shp_entry))
            dbf_records = iter(read_dbf_records_utf8(dbf_entry))
            grouped: dict[str, tuple[str, list[list[list[tuple[float, float]]]]]] = {}
            missing = object()
            for lines, record in zip_longest(shape_records, dbf_records, fillvalue=missing):
                if lines is missing or record is missing:
                    raise GisConversionError("SHP 与 DBF 的有效记录数不一致")
                code = record.get("code", "")
                name = record.get("name", code)
                if code not in grouped:
                    grouped[code] = (name, [])
                grouped[code][1].extend(lines)

            with temporary_path.open("w", encoding="utf-8", newline="\n") as handle:
                handle.write('{"type":"FeatureCollection","metadata":')
                json.dump(
                    {
                        "source": "JMA GIS",
                        "sourceUrl": JMA_GIS_URL,
                        "sourceVersion": source_version,
                        "acquiredAt": acquired_at,
                        "coordinateSystem": "JGD2011 (原始 ZIP 无 .prj，按经纬度使用)",
                        "officialBoundary": True,
                        "sourceLayer": "津波予報区",
                        "geometryType": "PolyLine",
                        "simplificationToleranceDegrees": tolerance,
                        "sourceArchive": input_path.name,
                    },
                    handle,
                    ensure_ascii=False,
                    separators=(",", ":"),
                )
                handle.write(',"features":[')
                first_feature = True
                for code, (name, lines) in grouped.items():
                    simplified_lines = [
                        simplify_open(line, tolerance)
                        for line in lines
                        if len(line) >= 2
                    ]
                    simplified_lines = [line for line in simplified_lines if len(line) >= 2]
                    if not simplified_lines:
                        continue
                    if not first_feature:
                        handle.write(",")
                    json.dump(
                        {
                            "type": "Feature",
                            "properties": {
                                "forecastAreaCode": code,
                                "name": name,
                                "officialBoundary": True,
                            },
                            "geometry": {
                                "type": "MultiLineString",
                                "coordinates": [
                                    [
                                        [round(longitude, 7), round(latitude, 7)]
                                        for longitude, latitude in line
                                    ]
                                    for line in simplified_lines
                                ],
                            },
                        },
                        handle,
                        ensure_ascii=False,
                        separators=(",", ":"),
                    )
                    first_feature = False
                    feature_count += 1
                    point_count += sum(len(line) for line in simplified_lines)
                handle.write("]}")
        temporary_path.replace(output_path)
    return {
        "featureCount": feature_count,
        "pointCount": point_count,
        "sourceVersion": source_version,
        "tolerance": tolerance,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="转换 JMA 海啸预报区 PolyLine Shapefile ZIP")
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-version", required=True)
    parser.add_argument("--acquired-at", required=True)
    parser.add_argument("--tolerance", type=float, default=0.002)
    args = parser.parse_args()
    result = convert_archive(
        args.input,
        args.output,
        args.source_version,
        args.acquired_at,
        args.tolerance,
    )
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
