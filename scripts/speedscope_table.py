#!/usr/bin/env python3
"""Print an aggregate function timing table from a Speedscope evented JSON file."""

from __future__ import annotations

import argparse
import json
import re
import shutil
import statistics
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable

 
SYNTHETIC_FRAMES = frozenset({"CPU_TIME", "UNMANAGED_CODE_TIME", "(Non-Activities)", "Threads"})
UNIT_SECONDS = {
    "seconds": 1.0,
    "milliseconds": 1e-3,
    "microseconds": 1e-6,
    "nanoseconds": 1e-9,
}


@dataclass
class FunctionStats:
    durations: list[float] = field(default_factory=list)

    @property
    def calls(self) -> int:
        return len(self.durations)

    @property
    def total(self) -> float:
        return sum(self.durations)

    @property
    def median(self) -> float:
        return statistics.median(self.durations)


@dataclass(frozen=True)
class OpenFrame:
    frame_index: int
    started_at: float


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Aggregate inclusive function-call durations from a Speedscope evented JSON profile "
            "and print them as a console table."
        )
    )
    parser.add_argument("trace", type=Path, help="Path to a .speedscope.json file")
    parser.add_argument(
        "--sort",
        choices=("total", "median", "calls", "name"),
        default="total",
        help="Column to sort by (default: total)",
    )
    parser.add_argument(
        "--limit",
        type=non_negative_int,
        default=50,
        help="Maximum rows to print; use 0 for all rows (default: 50)",
    )
    parser.add_argument(
        "--thread",
        action="append",
        default=[],
        metavar="TEXT",
        help="Include profiles whose name contains TEXT; repeat to match multiple threads",
    )
    parser.add_argument(
        "--include",
        type=compile_regex,
        metavar="REGEX",
        help="Include only function names matching REGEX",
    )
    parser.add_argument(
        "--exclude",
        type=compile_regex,
        metavar="REGEX",
        help="Exclude function names matching REGEX",
    )
    parser.add_argument(
        "--min-calls",
        type=positive_int,
        default=1,
        help="Only show functions called at least this many times (default: 1)",
    )
    parser.add_argument(
        "--synthetic",
        action="store_true",
        help="Include Speedscope timing, process, and thread container frames",
    )
    parser.add_argument(
        "--width",
        type=non_negative_int,
        default=0,
        metavar="COLUMNS",
        help="Table width; 0 uses the terminal width with a minimum of 120 columns (default: 0)",
    )
    parser.add_argument(
        "--no-color",
        action="store_true",
        help="Disable ANSI colors",
    )
    return parser.parse_args()


def non_negative_int(value: str) -> int:
    parsed = int(value)
    if parsed < 0:
        raise argparse.ArgumentTypeError("must be zero or greater")
    return parsed


def positive_int(value: str) -> int:
    parsed = int(value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("must be greater than zero")
    return parsed


def compile_regex(value: str) -> re.Pattern[str]:
    try:
        return re.compile(value, re.IGNORECASE)
    except re.error as error:
        raise argparse.ArgumentTypeError(f"invalid regular expression: {error}") from error


def load_trace(path: Path) -> dict[str, Any]:
    try:
        with path.open("r", encoding="utf-8") as trace_file:
            trace = json.load(trace_file)
    except FileNotFoundError as error:
        raise ValueError(f"trace file does not exist: {path}") from error
    except PermissionError as error:
        raise ValueError(f"cannot read trace file: {path}") from error
    except json.JSONDecodeError as error:
        raise ValueError(f"invalid JSON at line {error.lineno}, column {error.colno}: {error.msg}") from error

    if not isinstance(trace, dict):
        raise ValueError("the trace root must be a JSON object")
    return trace


def get_frame_names(trace: dict[str, Any]) -> list[str]:
    try:
        frames = trace["shared"]["frames"]
        return [str(frame["name"]) for frame in frames]
    except (KeyError, TypeError) as error:
        raise ValueError("missing or invalid shared.frames collection") from error


def selected_profiles(trace: dict[str, Any], thread_filters: list[str]) -> list[dict[str, Any]]:
    profiles = trace.get("profiles")
    if not isinstance(profiles, list):
        raise ValueError("missing or invalid profiles collection")

    evented_profiles = []
    unsupported_profiles = []
    filters = [value.casefold() for value in thread_filters]

    for profile in profiles:
        if not isinstance(profile, dict):
            continue
        name = str(profile.get("name", "<unnamed profile>"))
        if filters and not any(value in name.casefold() for value in filters):
            continue
        if profile.get("type") != "evented":
            unsupported_profiles.append(name)
            continue
        evented_profiles.append(profile)

    if evented_profiles:
        return evented_profiles
    if unsupported_profiles:
        joined = ", ".join(unsupported_profiles)
        raise ValueError(
            "selected profile(s) are not evented profiles, so individual call counts and medians "
            f"cannot be reconstructed: {joined}"
        )
    raise ValueError("no profiles matched the requested thread filter")


def is_synthetic_frame(name: str) -> bool:
    return name in SYNTHETIC_FRAMES or name.startswith("Process64 ") or name.startswith("Thread (")


def aggregate_profiles(
    profiles: Iterable[dict[str, Any]],
    frame_names: list[str],
    include_synthetic: bool,
) -> tuple[dict[str, FunctionStats], int]:
    stats: dict[str, FunctionStats] = defaultdict(FunctionStats)
    profile_count = 0

    for profile in profiles:
        profile_count += 1
        profile_name = str(profile.get("name", "<unnamed profile>"))
        events = profile.get("events")
        if not isinstance(events, list):
            raise ValueError(f"profile {profile_name!r} has no valid events collection")

        stack: list[OpenFrame] = []
        previous_at = float("-inf")

        for event_index, event in enumerate(events):
            try:
                event_type = event["type"]
                frame_index = int(event["frame"])
                at = float(event["at"])
                frame_name = frame_names[frame_index]
            except (KeyError, TypeError, ValueError, IndexError) as error:
                raise ValueError(
                    f"invalid event {event_index} in profile {profile_name!r}"
                ) from error

            if at < previous_at:
                raise ValueError(
                    f"events are not time-ordered at event {event_index} in profile {profile_name!r}"
                )
            previous_at = at

            if event_type == "O":
                stack.append(OpenFrame(frame_index, at))
                continue
            if event_type != "C":
                raise ValueError(
                    f"unknown event type {event_type!r} at event {event_index} in profile {profile_name!r}"
                )
            if not stack:
                raise ValueError(
                    f"close event without an open frame at event {event_index} in profile {profile_name!r}"
                )

            opened = stack.pop()
            if opened.frame_index != frame_index:
                expected = frame_names[opened.frame_index]
                raise ValueError(
                    f"mismatched close event at event {event_index} in profile {profile_name!r}: "
                    f"expected {expected!r}, got {frame_name!r}"
                )

            if include_synthetic or not is_synthetic_frame(frame_name):
                stats[frame_name].durations.append(at - opened.started_at)

        if stack:
            unclosed = frame_names[stack[-1].frame_index]
            raise ValueError(f"profile {profile_name!r} ends with an unclosed frame: {unclosed!r}")

    return dict(stats), profile_count


def filter_and_sort(
    stats: dict[str, FunctionStats],
    include: re.Pattern[str] | None,
    exclude: re.Pattern[str] | None,
    min_calls: int,
    sort_column: str,
) -> list[tuple[str, FunctionStats]]:
    rows = [
        (name, function_stats)
        for name, function_stats in stats.items()
        if function_stats.calls >= min_calls
        and (include is None or include.search(name))
        and (exclude is None or not exclude.search(name))
    ]

    if sort_column == "name":
        rows.sort(key=lambda row: row[0].casefold())
    else:
        key = {
            "total": lambda row: row[1].total,
            "median": lambda row: row[1].median,
            "calls": lambda row: row[1].calls,
        }[sort_column]
        rows.sort(key=lambda row: (key(row), row[0].casefold()), reverse=True)
    return rows


def format_duration(seconds: float) -> str:
    absolute = abs(seconds)
    if absolute >= 1.0:
        return f"{seconds:.3f} s"
    if absolute >= 1e-3:
        return f"{seconds * 1e3:.3f} ms"
    if absolute >= 1e-6:
        return f"{seconds * 1e6:.3f} us"
    return f"{seconds * 1e9:.3f} ns"


def truncate(value: str, width: int) -> str:
    if len(value) <= width:
        return value
    if width <= 1:
        return value[:width]
    return value[: width - 1] + "…"


def print_table(
    rows: list[tuple[str, FunctionStats]],
    trace_path: Path,
    profile_count: int,
    total_row_count: int,
    requested_width: int,
    use_color: bool,
) -> None:
    terminal_width = requested_width or max(120, shutil.get_terminal_size(fallback=(120, 24)).columns)
    total_width = 14
    median_width = 14
    calls_width = 10
    fixed_width = total_width + median_width + calls_width + 13
    name_width = max(28, terminal_width - fixed_width)

    cyan = "\033[36m" if use_color else ""
    bold = "\033[1m" if use_color else ""
    dim = "\033[2m" if use_color else ""
    reset = "\033[0m" if use_color else ""

    top = f"┌{'─' * total_width}┬{'─' * median_width}┬{'─' * calls_width}┬{'─' * name_width}┐"
    middle = f"├{'─' * total_width}┼{'─' * median_width}┼{'─' * calls_width}┼{'─' * name_width}┤"
    bottom = f"└{'─' * total_width}┴{'─' * median_width}┴{'─' * calls_width}┴{'─' * name_width}┘"

    print(f"{bold}{cyan}Speedscope function timings{reset}")
    print(f"{dim}{trace_path} · {profile_count} evented profile(s){reset}")
    print(top)
    print(
        f"│{bold}{'Total':>{total_width}}{reset}"
        f"│{bold}{'Median':>{median_width}}{reset}"
        f"│{bold}{'Calls':>{calls_width}}{reset}"
        f"│{bold}{'Function':<{name_width}}{reset}│"
    )
    print(middle)

    for name, function_stats in rows:
        total = format_duration(function_stats.total)
        median = format_duration(function_stats.median)
        function_name = truncate(name, name_width)
        print(
            f"│{total:>{total_width}}"
            f"│{median:>{median_width}}"
            f"│{function_stats.calls:>{calls_width},}"
            f"│{function_name:<{name_width}}│"
        )

    print(bottom)
    if len(rows) < total_row_count:
        print(f"{dim}Showing {len(rows)} of {total_row_count} matching functions. Use --limit 0 to show all.{reset}")
    else:
        print(f"{dim}{len(rows)} matching functions.{reset}")
    print(
        f"{dim}Times are inclusive elapsed call spans; parent and child totals overlap and are not CPU time.{reset}"
    )


def main() -> int:
    args = parse_args()
    try:
        trace = load_trace(args.trace)
        frame_names = get_frame_names(trace)
        profiles = selected_profiles(trace, args.thread)

        units = {str(profile.get("unit", "none")) for profile in profiles}
        unsupported_units = units.difference(UNIT_SECONDS)
        if unsupported_units:
            joined = ", ".join(sorted(unsupported_units))
            raise ValueError(f"unsupported profile time unit(s): {joined}")
        if len(units) != 1:
            joined = ", ".join(sorted(units))
            raise ValueError(f"selected profiles use different time units: {joined}")

        stats, profile_count = aggregate_profiles(profiles, frame_names, args.synthetic)
        unit_scale = UNIT_SECONDS[next(iter(units))]
        for function_stats in stats.values():
            function_stats.durations[:] = [duration * unit_scale for duration in function_stats.durations]

        rows = filter_and_sort(stats, args.include, args.exclude, args.min_calls, args.sort)
        total_row_count = len(rows)
        if args.limit:
            rows = rows[: args.limit]

        if not rows:
            print("No functions matched the requested filters.", file=sys.stderr)
            return 1

        print_table(
            rows,
            args.trace,
            profile_count,
            total_row_count,
            requested_width=args.width,
            use_color=sys.stdout.isatty() and not args.no_color,
        )
        return 0
    except (OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
