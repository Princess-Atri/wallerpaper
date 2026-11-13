#!/usr/bin/env python3
"""Summarize VideoWallpaperHost stability samples from run-wallpaper.log."""
from __future__ import annotations

import argparse
import math
import re
import statistics
import sys
from pathlib import Path

STATS_RE = re.compile(
    r"\[child\] stats ws=(?P<ws>[0-9.]+)MB private=(?P<private>[0-9.]+)MB "
    r"(?:cpu=(?P<cpu>[0-9.]+)s threads=(?P<threads>\d+) handles=(?P<handles>\d+) "
    r"cache=(?P<cache>[0-9.]+)s )?"
    r"hwdec=(?P<hwdec>\S*) fps=(?P<fps>[0-9.]+) time=(?P<time>[0-9.]+)"
)


def slope_per_min(values: list[float], sample_seconds: float) -> float:
    n = len(values)
    if n < 2:
        return 0.0
    xs = [i * sample_seconds / 60.0 for i in range(n)]
    x_mean = sum(xs) / n
    y_mean = sum(values) / n
    num = sum((x - x_mean) * (y - y_mean) for x, y in zip(xs, values))
    den = sum((x - x_mean) ** 2 for x in xs)
    return num / den if den else 0.0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("log", nargs="?", default="run-wallpaper.log")
    ap.add_argument("--sample", type=float, default=15.0)
    ap.add_argument("--out", default="stability-summary.md")
    args = ap.parse_args()

    path = Path(args.log)
    if not path.exists():
        print(f"log not found: {path}", file=sys.stderr)
        return 2

    rows = []
    for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        m = STATS_RE.search(line)
        if m:
            rows.append(
                {
                    "ws": float(m.group("ws")),
                    "private": float(m.group("private")),
                    "hwdec": m.group("hwdec"),
                    "fps": float(m.group("fps")),
                    "time": float(m.group("time")),
                }
            )

    if not rows:
        print("no stats samples found", file=sys.stderr)
        return 3

    ws = [r["ws"] for r in rows]
    private = [r["private"] for r in rows]
    n = len(rows)
    duration_min = (n - 1) * args.sample / 60.0

    ws_slope = slope_per_min(ws, args.sample)
    private_slope = slope_per_min(private, args.sample)
    ws_delta = ws[-1] - ws[0]
    private_delta = private[-1] - private[0]
    max_ws_jump = max((b - a for a, b in zip(ws, ws[1:])), default=0.0)
    max_private_jump = max((b - a for a, b in zip(private, private[1:])), default=0.0)

    half = max(1, n // 2)
    first_ws = statistics.mean(ws[:half])
    last_ws = statistics.mean(ws[-half:])
    first_private = statistics.mean(private[:half])
    last_private = statistics.mean(private[-half:])

    hwdec_counts: dict[str, int] = {}
    for r in rows:
        key = r["hwdec"] or "(released/empty)"
        hwdec_counts[key] = hwdec_counts.get(key, 0) + 1

    lines = [
        "# Stability summary",
        "",
        f"- Log: `{path}`",
        f"- Samples: **{n}** (every {args.sample:g}s, span ~{duration_min:.1f} min)",
        f"- Video: 1920x1080, 10.0s loop, target 60fps",
        "",
        "## Working set (MB)",
        "",
        f"- first: {ws[0]:.1f}",
        f"- last: {ws[-1]:.1f}",
        f"- delta: {ws_delta:+.1f}",
        f"- min / mean / max: {min(ws):.1f} / {statistics.mean(ws):.1f} / {max(ws):.1f}",
        f"- first-half mean -> last-half mean: {first_ws:.1f} -> {last_ws:.1f}",
        f"- linear slope: {ws_slope:+.3f} MB/min",
        f"- largest single upward jump: {max_ws_jump:+.1f} MB",
        "",
        "## Private bytes (MB)",
        "",
        f"- first: {private[0]:.1f}",
        f"- last: {private[-1]:.1f}",
        f"- delta: {private_delta:+.1f}",
        f"- min / mean / max: {min(private):.1f} / {statistics.mean(private):.1f} / {max(private):.1f}",
        f"- first-half mean -> last-half mean: {first_private:.1f} -> {last_private:.1f}",
        f"- linear slope: {private_slope:+.3f} MB/min",
        f"- largest single upward jump: {max_private_jump:+.1f} MB",
        "",
        "## Hardware decode",
        "",
    ]
    for key, count in sorted(hwdec_counts.items(), key=lambda kv: -kv[1]):
        lines.append(f"- `{key}`: {count} samples")
    lines += [
        "",
        "## Verdict",
        "",
        f"- Working set slope: **{ws_slope:+.3f} MB/min**",
        f"- Private slope: **{private_slope:+.3f} MB/min**",
    ]

    out = Path(args.out)
    out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))
    print(f"\nwritten: {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
