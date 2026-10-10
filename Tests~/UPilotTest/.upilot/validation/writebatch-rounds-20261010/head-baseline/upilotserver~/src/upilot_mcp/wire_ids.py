from __future__ import annotations

from typing import Any, TypeAlias


WireIdInput: TypeAlias = str | int

MAX_SAFE_INTEGER = 9_007_199_254_740_991


def is_wire_id_key(key: str) -> bool:
    normalized = (key or "").strip().lower()
    return normalized in {
        "instanceid",
        "parentid",
        "gameobjectid",
        "gameobjectids",
        "sourcegameobjectid",
        "targetgameobjectid",
        "lookatinstanceid",
        "windowid",
    } or normalized.endswith("instanceid") \
        or normalized.endswith("gameobjectid") \
        or normalized.endswith("gameobjectids") \
        or normalized.endswith("windowid")


def parse_wire_id(value: Any, *, path: str = "wireId") -> int:
    if value is None or value == "":
        return 0
    if isinstance(value, bool):
        raise ValueError(f"{path} must be a decimal string or integer, not bool")
    if isinstance(value, int):
        if value < 0:
            raise ValueError(f"{path} must not be negative")
        if value > MAX_SAFE_INTEGER:
            raise ValueError(
                f"{path} integer exceeds JavaScript safe range; pass it as a decimal string"
            )
        return value
    if isinstance(value, str):
        text = value.strip()
        if not text or not text.isdecimal():
            raise ValueError(f"{path} must be an unsigned decimal string")
        parsed = int(text, 10)
        if parsed < 0 or parsed > 18_446_744_073_709_551_615:
            raise ValueError(f"{path} is outside the UInt64 wire ID range")
        return parsed
    raise ValueError(f"{path} must be a decimal string or integer")


def normalize_wire_ids_for_unity(value: Any, *, path: str = "payload", key: str = "") -> Any:
    if isinstance(value, dict):
        return {
            item_key: normalize_wire_ids_for_unity(
                item_value,
                path=f"{path}.{item_key}",
                key=str(item_key),
            )
            for item_key, item_value in value.items()
        }
    if isinstance(value, list):
        if is_wire_id_key(key):
            return [parse_wire_id(item, path=f"{path}[{index}]") for index, item in enumerate(value)]
        return [
            normalize_wire_ids_for_unity(item, path=f"{path}[{index}]", key=key)
            for index, item in enumerate(value)
        ]
    if is_wire_id_key(key):
        return parse_wire_id(value, path=path)
    return value


def stringify_wire_ids(value: Any, *, key: str = "") -> Any:
    if isinstance(value, dict):
        return {
            item_key: stringify_wire_ids(item_value, key=str(item_key))
            for item_key, item_value in value.items()
        }
    if isinstance(value, list):
        if is_wire_id_key(key):
            return [str(item) for item in value]
        return [stringify_wire_ids(item, key=key) for item in value]
    if is_wire_id_key(key) and isinstance(value, int) and not isinstance(value, bool):
        return str(value)
    return value
