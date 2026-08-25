"""从 JMA GeoJSON 生成都道府县-区域-市町村关系目录。"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Iterable


def polygons(feature: dict) -> Iterable[list[list[list[float]]]]:
    geometry = feature.get("geometry") or {}
    kind = geometry.get("type")
    coordinates = geometry.get("coordinates") or []
    if kind == "Polygon":
        yield coordinates
    elif kind == "MultiPolygon":
        yield from coordinates


def representative_point(feature: dict) -> tuple[float, float] | None:
    candidates = [polygon[0] for polygon in polygons(feature) if polygon]
    if not candidates or not candidates[0]:
        return None
    ring = max(candidates, key=lambda item: abs(sum(
        first[0] * second[1] - second[0] * first[1]
        for first, second in zip(item, item[1:] + item[:1])
    )))
    area = 0.0
    x = 0.0
    y = 0.0
    for first, second in zip(ring, ring[1:] + ring[:1]):
        cross = first[0] * second[1] - second[0] * first[1]
        area += cross
        x += (first[0] + second[0]) * cross
        y += (first[1] + second[1]) * cross
    if abs(area) < 1e-12:
        return (ring[0][0], ring[0][1])
    return (x / (3 * area), y / (3 * area))


def contains(ring: list[list[float]], point: tuple[float, float]) -> bool:
    x, y = point
    inside = False
    for first, second in zip(ring, ring[1:] + ring[:1]):
        if (first[1] > y) != (second[1] > y):
            crossing = (second[0] - first[0]) * (y - first[1]) / (second[1] - first[1]) + first[0]
            if x < crossing:
                inside = not inside
    return inside


def feature_contains(feature: dict, point: tuple[float, float]) -> bool:
    for polygon in polygons(feature):
        if polygon and contains(polygon[0], point) and not any(
            contains(hole, point) for hole in polygon[1:]
        ):
            return True
    return False


def squared_distance(left: tuple[float, float], right: tuple[float, float]) -> float:
    return (left[0] - right[0]) ** 2 + (left[1] - right[1]) ** 2


def load_features(path: Path) -> list[dict]:
    return json.loads(path.read_text(encoding="utf-8"))["features"]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--prefectures", type=Path, required=True)
    parser.add_argument("--areas", type=Path, required=True)
    parser.add_argument("--municipalities", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    prefecture_features = load_features(args.prefectures)
    area_features = load_features(args.areas)
    municipality_features = load_features(args.municipalities)
    prefectures = [
        {
            "code": item["properties"]["prefectureCode"],
            "name": item["properties"]["name"],
            "_feature": item,
        }
        for item in prefecture_features
    ]
    prefectures.sort(key=lambda item: len(item["name"]), reverse=True)
    areas = []
    for feature in area_features:
        properties = feature["properties"]
        if not properties.get("areaCode"):
            continue
        name = properties["name"]
        prefecture = next(
            (item for item in prefectures if name.startswith(item["name"])),
            None,
        )
        if prefecture is None:
            point = representative_point(feature)
            prefecture = next(
                (item for item in prefectures if point is not None and feature_contains(item["_feature"], point)),
                None,
            )
        if prefecture is None and properties["areaCode"].startswith("1"):
            prefecture = next(item for item in prefectures if item["code"] == "01")
        if prefecture is None and properties["areaCode"] in {"354", "355", "356", "357", "358", "359"}:
            prefecture = next(item for item in prefectures if item["code"] == "13")
        prefecture = prefecture or {"code": "", "name": ""}
        areas.append(
            {
                "code": properties["areaCode"],
                "name": name,
                "prefectureCode": prefecture["code"],
                "prefectureName": prefecture["name"],
                "_feature": feature,
            }
        )

    municipalities = []
    unmatched = []
    for feature in municipality_features:
        properties = feature["properties"]
        region_name = properties.get("regionName", "")
        prefecture = next(
            (item for item in prefectures if region_name.startswith(item["name"])),
            None,
        )
        if prefecture is None:
            unmatched.append(properties.get("municipalityCode", ""))
            continue
        remainder = region_name[len(prefecture["name"]):]
        aliases = {properties.get("name", "")}
        aliases.add(remainder)
        if "のうち" in remainder:
            base, suffix = remainder.split("のうち", 1)
            aliases.add(base + suffix)
        point = representative_point(feature)
        parent = next(
            (area for area in areas if point is not None and feature_contains(area["_feature"], point)),
            None,
        )
        if parent is None:
            candidates = [area for area in areas if area["prefectureCode"] == prefecture["code"]]
            if point is not None and candidates:
                parent = min(
                    candidates,
                    key=lambda area: squared_distance(
                        point,
                        representative_point(area["_feature"]) or point,
                    ),
                )
            else:
                unmatched.append(properties.get("municipalityCode", ""))
                parent = {"code": "", "name": ""}
        municipalities.append(
            {
                "code": properties["municipalityCode"],
                "name": properties["name"],
                "prefectureCode": prefecture["code"],
                "prefectureName": prefecture["name"],
                "areaCode": parent["code"],
                "areaName": parent["name"],
                "aliases": sorted(alias for alias in aliases if alias),
            }
        )

    output_areas = [{key: value for key, value in area.items() if key != "_feature"} for area in areas]
    document = {
        "version": 1,
        "source": "JMA GIS 20241128/20240520",
        "diagnostics": {
            "prefectureCount": len(prefectures),
            "areaCount": len(output_areas),
            "municipalityCount": len(municipalities),
            "unmatchedMunicipalityCount": len(unmatched),
            "unmatchedMunicipalityCodes": unmatched,
        },
        "prefectures": sorted(
            [{key: value for key, value in item.items() if key != "_feature"} for item in prefectures],
            key=lambda item: item["code"],
        ),
        "areas": sorted(output_areas, key=lambda item: item["code"]),
        "municipalities": sorted(municipalities, key=lambda item: item["code"]),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(document["diagnostics"], ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
