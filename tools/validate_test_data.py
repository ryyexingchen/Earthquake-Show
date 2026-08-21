"""离线校验 Earthquake Show 固定测试数据。"""

from __future__ import annotations

import csv
import hashlib
import json
import math
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
DATA_ROOT = ROOT / "tests" / "TestData"
APP_STATION_CATALOG = (
    ROOT
    / "src"
    / "EarthquakeShow.App"
    / "Assets"
    / "Data"
    / "Stations"
    / "jma-intensity-stations.json"
)
EXPECTED_INTENSITY_CODES = (
    "unknown",
    "1",
    "2",
    "3",
    "4",
    "5-lower",
    "5-upper",
    "6-lower",
    "6-upper",
    "7",
)


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def descendants(element: ET.Element, name: str) -> list[ET.Element]:
    return [item for item in element.iter() if local_name(item.tag) == name]


def first(element: ET.Element, name: str) -> ET.Element | None:
    return next((item for item in element.iter() if local_name(item.tag) == name), None)


def text(element: ET.Element, name: str) -> str | None:
    item = first(element, name)
    if item is None or item.text is None or not item.text.strip():
        return None
    return item.text.strip()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def parse_report(path: Path) -> dict[str, object]:
    root = ET.parse(path).getroot()
    observation = first(root, "Observation")
    earthquake = first(root, "Earthquake")
    magnitude_text = text(earthquake, "Magnitude") if earthquake is not None else None

    try:
        magnitude = float(magnitude_text) if magnitude_text is not None else None
        if magnitude is not None and not math.isfinite(magnitude):
            magnitude = None
    except ValueError:
        magnitude = None

    return {
        "eventId": text(root, "EventID"),
        "infoType": text(root, "InfoType"),
        "serial": text(root, "Serial"),
        "maxIntensity": text(observation, "MaxInt") if observation is not None else None,
        "magnitude": magnitude,
        "hasHypocenterCoordinate": bool(
            earthquake is not None and descendants(earthquake, "Coordinate")
        ),
        "prefectureCount": len(descendants(observation, "Pref")) if observation is not None else 0,
        "areaCount": len(descendants(observation, "Area")) if observation is not None else 0,
        "cityCount": len(descendants(observation, "City")) if observation is not None else 0,
        "stationCount": len(descendants(observation, "IntensityStation")) if observation is not None else 0,
        "stationCodes": [
            text(station, "Code")
            for station in descendants(observation, "IntensityStation")
        ] if observation is not None else [],
        "areaCodes": [
            text(area, "Code") for area in descendants(observation, "Area")
        ] if observation is not None else [],
    }


def json_objects(parent: object, name: str) -> list[dict[str, object]]:
    if not isinstance(parent, dict):
        return []
    value = parent.get(name)
    if isinstance(value, dict):
        return [value]
    if isinstance(value, list):
        return [item for item in value if isinstance(item, dict)]
    return []


def parse_json_report(path: Path) -> dict[str, object]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    control = payload.get("Control", {})
    head = payload.get("Head", {})
    body = payload.get("Body", {})
    earthquake = body.get("Earthquake", {})
    area = earthquake.get("Hypocenter", {}).get("Area", {})
    observation = body.get("Intensity", {}).get("Observation", {})
    prefectures = json_objects(observation, "Pref")
    areas = [area for prefecture in prefectures for area in json_objects(prefecture, "Area")]
    cities = [city for area in areas for city in json_objects(area, "City")]
    stations = [
        station
        for city in cities
        for station in json_objects(city, "IntensityStation")
    ]
    magnitude_value = earthquake.get("Magnitude")
    try:
        magnitude = float(magnitude_value) if magnitude_value is not None else None
        if magnitude is not None and not math.isfinite(magnitude):
            magnitude = None
    except (TypeError, ValueError):
        magnitude = None

    return {
        "eventId": head.get("EventID"),
        "infoType": head.get("InfoType"),
        "serial": head.get("Serial") or None,
        "maxIntensity": observation.get("MaxInt") or None,
        "magnitude": magnitude,
        "hasHypocenterCoordinate": bool(
            area.get("Coordinate") or area.get("Coordinate_WGS")
        ),
        "prefectureCount": len(prefectures),
        "areaCount": len(areas),
        "cityCount": len(cities),
        "stationCount": len(stations),
        "controlStatus": control.get("Status"),
    }


def validate_manifest() -> tuple[dict[str, object], dict[str, dict[str, object]]]:
    manifest_path = DATA_ROOT / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    require(manifest["schemaVersion"] == 1, "manifest schemaVersion 必须为 1")

    reports: dict[str, dict[str, object]] = {}
    for fixture in manifest["fixtures"]:
        path = DATA_ROOT / fixture["path"]
        require(path.is_file(), f"缺少固定报文：{fixture['path']}")
        require(sha256(path) == fixture["sha256"], f"固定报文哈希不匹配：{fixture['path']}")
        actual = parse_report(path)
        for field, expected in fixture["expected"].items():
            if field == "unresolvedStationCodes":
                continue
            require(actual[field] == expected, f"{fixture['id']} 的 {field} 不匹配：{actual[field]!r}")
        reports[fixture["id"]] = actual

    for asset in manifest["assets"]:
        path = DATA_ROOT / asset["path"]
        require(path.is_file(), f"缺少固定数据：{asset['path']}")
        require(sha256(path) == asset["sha256"], f"固定数据哈希不匹配：{asset['path']}")

    return manifest, reports


def validate_event_chain(reports: dict[str, dict[str, object]]) -> None:
    chain = [reports[name] for name in ("official-vxse51", "official-vxse52", "official-vxse53")]
    require(len({item["eventId"] for item in chain}) == 1, "VXSE51/52/53 必须使用同一 EventID")
    correction = reports["synthetic-vxse53-correction"]
    require(correction["eventId"] == chain[0]["eventId"], "订正报必须属于同一事件")
    require(correction["infoType"] == "訂正", "订正报 InfoType 必须为訂正")


def validate_json_fixtures(manifest: dict[str, object]) -> None:
    fixtures = manifest.get("jsonFixtures", [])
    require(len(fixtures) == 7, "令和八年熊本事件必须包含 7 份官方 JSON 报文")
    reports = []
    for fixture in fixtures:
        path = DATA_ROOT / fixture["path"]
        require(path.is_file(), f"缺少固定 JSON 报文：{fixture['path']}")
        require(sha256(path) == fixture["sha256"], f"固定 JSON 哈希不匹配：{fixture['path']}")
        actual = parse_json_report(path)
        actual["reportCode"] = fixture["reportCode"]
        for field, expected in fixture["expected"].items():
            require(actual[field] == expected, f"{fixture['id']} 的 {field} 不匹配：{actual[field]!r}")
        reports.append(actual)

    require(
        {report["eventId"] for report in reports} == {"20260728162718"},
        "令和八年熊本 JSON 报文必须属于事件 20260728162718",
    )
    require(
        any(report["magnitude"] == 7.1 for report in reports),
        "令和八年熊本 JSON 报文必须包含 M7.1",
    )


def expected_asset_count(manifest: dict[str, object], asset_id: str) -> int:
    asset = next(item for item in manifest["assets"] if item["id"] == asset_id)
    return int(asset["expectedRecordCount"])


def validate_stations(manifest: dict[str, object], reports: dict[str, dict[str, object]]) -> None:
    with (DATA_ROOT / "JmaStations.csv").open(encoding="utf-8", newline="") as stream:
        rows = list(csv.DictReader(stream))

    expected_count = expected_asset_count(manifest, "jma-stations")
    require(len(rows) == expected_count, f"观测点坐标表必须包含 {expected_count} 条记录")
    codes = {row["station_code"] for row in rows}
    require(len(codes) == len(rows), "观测点编码必须唯一")
    require(set(reports["official-vxse53"]["stationCodes"]) == codes, "官方 VXSE53 观测点必须全部有坐标")

    for row in rows:
        latitude = float(row["latitude"])
        longitude = float(row["longitude"])
        require(-90 <= latitude <= 90 and -180 <= longitude <= 180, f"观测点坐标越界：{row['station_code']}")
        require(row["affiliation"] in {"JMA", "LocalGovernment", "NIED"}, f"观测点所属无效：{row['station_code']}")

    missing_fixture = next(
        fixture for fixture in manifest["fixtures"] if fixture["id"] == "synthetic-vxse53-missing-fields"
    )
    for code in missing_fixture["expected"]["unresolvedStationCodes"]:
        require(code not in codes, f"缺坐标测试站不应出现在坐标表：{code}")


def validate_formal_station_catalog() -> None:
    payload = json.loads(APP_STATION_CATALOG.read_text(encoding="utf-8"))
    require(payload["schemaVersion"] == 1, "正式观测点目录 schemaVersion 必须为 1")
    require(bool(payload["datasetVersion"]), "正式观测点目录必须记录数据版本")
    require(bool(payload["sourceUrl"]), "正式观测点目录必须记录来源 URL")
    stations = payload["stations"]
    require(len(stations) == 4368, "正式观测点目录必须包含 4,368 条记录")
    names = [normalize_station_name(str(station["name"])) for station in stations]
    require(len(set(names)) == len(names), "正式观测点目录的规范化名称必须唯一")
    require(len({str(station["prefectureCode"]) for station in stations}) == 47, "正式观测点目录必须覆盖 47 个都道府县")
    for station in stations:
        latitude = float(station["latitude"])
        longitude = float(station["longitude"])
        require(-90 <= latitude <= 90 and -180 <= longitude <= 180, f"正式观测点坐标越界：{station['name']}")


def normalize_station_name(value: str) -> str:
    return value.strip().rstrip("＊*").strip()


def validate_geojson(manifest: dict[str, object], reports: dict[str, dict[str, object]]) -> None:
    payload = json.loads((DATA_ROOT / "Gis" / "jma-area-test-envelopes.geojson").read_text(encoding="utf-8"))
    require(payload["type"] == "FeatureCollection", "GeoJSON 顶层必须为 FeatureCollection")
    expected_count = expected_asset_count(manifest, "jma-area-test-envelopes")
    require(len(payload["features"]) == expected_count, f"GeoJSON 必须包含 {expected_count} 个测试区域")

    feature_codes = set()
    for feature in payload["features"]:
        properties = feature["properties"]
        geometry = feature["geometry"]
        require(properties["officialBoundary"] is False, "测试包络不能标记为官方边界")
        require(geometry["type"] == "Polygon", "测试区域必须是 Polygon")
        ring = geometry["coordinates"][0]
        require(len(ring) >= 4 and ring[0] == ring[-1], "Polygon 外环必须闭合")
        for longitude, latitude in ring:
            require(-180 <= longitude <= 180 and -90 <= latitude <= 90, "GeoJSON 坐标越界")
        feature_codes.add(properties["areaCode"])

    require(feature_codes == set(reports["official-vxse53"]["areaCodes"]), "GeoJSON 区域编码与 VXSE53 不一致")


def validate_intensity_scale(manifest: dict[str, object]) -> None:
    payload = json.loads((DATA_ROOT / "Definitions" / "intensity-scale.json").read_text(encoding="utf-8"))
    levels = payload["levels"]
    expected_count = expected_asset_count(manifest, "intensity-scale")
    require(len(levels) == expected_count, f"震度定义必须包含 {expected_count} 条记录")
    require(tuple(level["code"] for level in levels) == EXPECTED_INTENSITY_CODES, "震度代码或顺序不正确")
    require([level["sortRank"] for level in levels] == list(range(10)), "震度排序值必须从 0 到 9")
    for level in levels:
        require(bool(re.fullmatch(r"#[0-9A-F]{6}", level["color"])), f"震度颜色无效：{level['code']}")
        require(bool(re.fullmatch(r"#[0-9A-F]{6}", level["textColor"])), f"震度文字颜色无效：{level['code']}")


def main() -> int:
    try:
        manifest, reports = validate_manifest()
        validate_json_fixtures(manifest)
        validate_event_chain(reports)
        validate_stations(manifest, reports)
        validate_formal_station_catalog()
        validate_geojson(manifest, reports)
        validate_intensity_scale(manifest)
    except (KeyError, OSError, ET.ParseError, ValueError, json.JSONDecodeError) as exc:
        print(f"固定测试数据校验失败：{exc}", file=sys.stderr)
        return 1

    print(
        "固定测试数据校验通过：5 份 XML、7 份 JMA JSON、75 个固定观测点、4,368 个正式观测点、"
        "7 个区域测试包络、10 个震度定义。"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
