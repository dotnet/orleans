#!/usr/bin/env python3
"""Visualize JSONL stream from stdin with colors and formatting."""

import io
import json
import sys
from typing import Any

from rich.console import Console
from rich.style import Style

# Force UTF-8 encoding for stdin/stdout on Windows
if sys.platform == "win32":
    sys.stdin = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8", errors="replace")
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

console = Console(force_terminal=True)

# Color styles matching the Claude version
colors = {
    "reset": Style(),
    "bright": Style(bold=True),
    "dim": Style(dim=True),
    "red": Style(color="red"),
    "green": Style(color="green"),
    "yellow": Style(color="yellow"),
    "blue": Style(color="blue"),
    "magenta": Style(color="magenta"),
    "cyan": Style(color="cyan"),
}


def get_event_style(event_type: str) -> tuple[str, Style]:
    """Get icon and color for an opencode event type."""
    event_styles = {
        "tool_use": ("🔧", colors["cyan"]),
        "step_start": ("▶️", colors["blue"]),
        "step_finish": ("✅", colors["green"]),
        "text": ("💬", colors["reset"]),
        "error": ("❌", colors["red"]),
        "thinking": ("🤔", colors["magenta"]),
        "session_start": ("🚀", colors["bright"] + colors["green"]),
        "session_end": ("🏁", colors["bright"] + colors["blue"]),
    }
    return event_styles.get(event_type, ("⏺", colors["dim"]))


def safe_str(value: Any, max_len: int | None = None) -> str:
    """Safely convert any value to string and optionally truncate."""
    result = str(value) if not isinstance(value, str) else value
    if max_len is not None and len(result) > max_len:
        return result[:max_len] + "..."
    return result


def format_opencode_event(event: dict[str, Any], debug: bool = False) -> None:
    """Format and display an opencode JSON event."""
    event_type = event.get("type", "unknown")
    timestamp = event.get("timestamp", "")
    session_id = event.get("sessionID", "")

    # Extract data from the event based on type
    # OpenCode format uses 'part' for most events and 'error' for error events
    part = event.get("part", {})
    error_data = event.get("error", {})

    icon, style = get_event_style(event_type)

    # Show timestamp and session ID in debug mode
    if debug:
        debug_info = []
        if timestamp:
            debug_info.append(f"[{timestamp}]")
        if session_id:
            debug_info.append(f"(session: {session_id[:8]}...)")
        if debug_info:
            console.print(" ".join(debug_info), style=colors["dim"], end=" ")

    console.print(f"{icon} ", end="")
    console.print(event_type.replace("_", " ").title(), style=style, end="")

    # Format based on event type
    if event_type == "tool_use":
        # Extract tool information from part
        tool_name = part.get("tool", "unknown")
        tool_state = part.get("state", {})
        tool_title = tool_state.get("title", "")
        tool_input = tool_state.get("input", {})
        tool_status = tool_state.get("status", "")

        console.print(f" ({tool_name})", style=colors["cyan"], end="")

        if tool_status:
            status_color = colors["green"] if tool_status == "completed" else colors["yellow"]
            console.print(f" [{tool_status}]", style=status_color, end="")

        console.print()

        # Show title if available
        if tool_title:
            console.print("  ⎿  ", end="")
            console.print(f"Title: {tool_title}", style=colors["dim"])

        # Show key arguments from input
        if tool_input:
            for key in ["path", "file_path", "command", "query", "pattern", "url"]:
                if key in tool_input:
                    console.print("  ⎿  ", end="")
                    console.print(f"{key}: ", style=colors["dim"], end="")
                    console.print(
                        safe_str(tool_input[key], 80),
                        style=colors["green"],
                        markup=False,
                    )
                    break

    elif event_type == "step_start":
        step_type = part.get("type", "")
        if step_type:
            console.print(f" - {step_type}", style=colors["blue"])
        else:
            console.print()

    elif event_type == "step_finish":
        step_type = part.get("type", "")
        # Calculate duration if time info is available
        time_info = part.get("time", {})
        start_time = time_info.get("start")
        end_time = time_info.get("end")
        duration = None
        if start_time and end_time:
            duration = end_time - start_time

        if step_type:
            console.print(f" - {step_type}", style=colors["green"], end="")
        if duration:
            console.print(f" ({duration:.0f}ms)", style=colors["dim"], end="")
        console.print()

    elif event_type == "text":
        # Extract text content from part
        text_content = part.get("text", "")
        if text_content:
            console.print()
            lines = text_content.split("\n")
            # Show first 5 lines
            lines_to_show = min(5, len(lines))
            for i, line in enumerate(lines[:lines_to_show]):
                if line.strip() or i == 0:  # Show first line even if empty
                    console.print("  ⎿  ", end="")
                    line_style = colors["reset"] if i == 0 else colors["dim"]
                    console.print(line, style=line_style, markup=False)

            if len(lines) > lines_to_show:
                console.print("  ⎿  ", end="")
                console.print(f"... ({len(lines) - lines_to_show} more lines)", style=colors["dim"])
        else:
            console.print()

    elif event_type == "thinking":
        # Extract thinking content from part
        thinking_content = part.get("text", "")
        if thinking_content:
            console.print()
            lines = thinking_content.split("\n")
            # Show first 3 lines of thinking
            lines_to_show = min(3, len(lines))
            for _i, line in enumerate(lines[:lines_to_show]):
                if line.strip():
                    console.print("  ⎿  ", end="")
                    console.print(line, style=colors["magenta"], markup=False)

            if len(lines) > lines_to_show:
                console.print("  ⎿  ", end="")
                console.print(f"... ({len(lines) - lines_to_show} more lines)", style=colors["dim"])
        else:
            console.print()

    elif event_type == "error":
        error_name = error_data.get("name", "Error")
        error_msg = error_data.get("data", {}).get("message", str(error_data))

        console.print()
        console.print("  ⎿  ", end="")
        console.print(f"{error_name}: ", style=colors["red"] + colors["bright"], end="")
        console.print(safe_str(error_msg, 200), style=colors["red"], markup=False)

    elif event_type in ["session_start", "session_end"]:
        # Just show the event type
        console.print()

    else:
        # Unknown event type - show raw data summary
        data_to_show = part if part else error_data
        if data_to_show:
            console.print()
            console.print("  ⎿  ", end="")
            console.print(f"{safe_str(data_to_show, 100)}", style=colors["dim"], markup=False)
        else:
            console.print()

    console.print()


def process_stream() -> None:
    """Process JSONL stream from stdin."""
    debug_mode = "--debug" in sys.argv

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            event = json.loads(line)
            format_opencode_event(event, debug=debug_mode)

        except json.JSONDecodeError:
            console.print("⏺ ", end="")
            console.print("Parse Error", style=colors["red"])
            console.print(f"  ⎿  {line[:80]}...", style=colors["dim"], markup=False)
            console.print()


if __name__ == "__main__":
    try:
        process_stream()
    except KeyboardInterrupt:
        sys.exit(0)
    except Exception as e:
        console.print(f"Error: {e}", style=colors["red"], markup=False)
        sys.exit(1)
