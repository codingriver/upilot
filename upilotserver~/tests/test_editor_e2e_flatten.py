"""Unit tests for editor E2E runner helpers (no Unity required)."""

from __future__ import annotations

from unitypilot_mcp.editor_e2e.runner import _flatten_pointer_sequences


def test_flatten_pointer_sequence_expands_events() -> None:
    items = [
        {
            "action": "uitoolkit.pointerSequence",
            "targetWindow": "W",
            "elementName": "box",
            "events": [
                {"eventType": "mousedown", "mouseButton": 0},
                {"eventType": "mouseup", "mouseButton": 0},
            ],
        }
    ]
    defaults: dict = {}
    out = _flatten_pointer_sequences(items, defaults)
    assert len(out) == 2
    assert out[0]["action"] == "uitoolkit.event"
    assert out[0]["eventType"] == "mousedown"
    assert out[0]["targetWindow"] == "W"
    assert out[0]["elementName"] == "box"
    assert out[1]["eventType"] == "mouseup"


def test_flatten_preserves_non_sequence_steps() -> None:
    items = [{"action": "wait", "ms": 10}]
    out = _flatten_pointer_sequences(items, {})
    assert out == items


def test_flatten_merges_defaults() -> None:
    items = [
        {
            "action": "uitoolkit.pointerSequence",
            "events": [{"eventType": "wheel", "wheelDeltaY": 5}],
        }
    ]
    defaults = {"targetWindow": "UnityPilot", "elementName": "sv"}
    out = _flatten_pointer_sequences(items, defaults)
    assert len(out) == 1
    assert out[0]["targetWindow"] == "UnityPilot"
    assert out[0]["elementName"] == "sv"
    assert out[0]["wheelDeltaY"] == 5
