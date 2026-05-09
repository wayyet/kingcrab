#!/usr/bin/env python3
"""
Read the complete execution trace from a target AI sandbox.

Usage:
    python Read-SandboxTrace.py --ws-url ws://trace:7070/chat --session-id abc123
    python Read-SandboxTrace.py --ws-url ws://trace:7070/chat --trace-type tool_calls --max-entries 50
"""

import argparse
import asyncio
import json
import os
import sys
from urllib.parse import urlparse

try:
    import websockets
except ImportError:
    print(json.dumps({"error": "websockets library required"}), file=sys.stderr)
    sys.exit(1)


def resolve_token(token_ref: str | None) -> str | None:
    if not token_ref:
        return None
    if token_ref.startswith("env:"):
        return os.environ.get(token_ref[4:])
    if token_ref.startswith("raw:"):
        return token_ref[4:]
    return token_ref


def convert_ws_url(url: str) -> str:
    parsed = urlparse(url.rstrip("/"))
    scheme = "wss" if parsed.scheme == "https" else "ws"
    return f"{scheme}://{parsed.netloc}{parsed.path}"


async def receive_message(ws) -> dict:
    raw = await ws.recv()
    return json.loads(raw) if isinstance(raw, str) else json.loads(raw.decode())


async def send_message(ws, data: dict):
    await ws.send(json.dumps(data, ensure_ascii=False))


async def read_trace(ws_url: str, auth_token: str | None, session_id: str | None,
                     trace_type: str, max_entries: int, step_from: int | None,
                     step_to: int | None, timeout: int):
    url = convert_ws_url(ws_url)

    async with websockets.connect(url, open_timeout=timeout) as ws:
        first_msg = await receive_message(ws)

        if first_msg.get("type") == "auth_required":
            token = resolve_token(auth_token)
            if not token:
                raise ValueError("Auth required but no token configured")
            await send_message(ws, {"type": "auth", "access_token": token})
            await receive_message(ws)

        # Build trace query
        filters = []
        if session_id:
            filters.append(f"session_id={session_id}")
        if trace_type != "all":
            filters.append(f"type={trace_type}")
        if step_from is not None:
            filters.append(f"step_from={step_from}")
        if step_to is not None:
            filters.append(f"step_to={step_to}")

        filter_str = f" with filters: {', '.join(filters)}" if filters else ""
        prompt = (f"Read execution trace{filter_str}. Return up to {max_entries} entries "
                  f"as JSON with 'trace' key containing session_id, source, total_steps, "
                  f"and entries array. Each entry: step, type, content, tool_name, "
                  f"tool_arguments, timestamp.")

        request_id = id(prompt) % 2147483647
        await send_message(ws, {"id": request_id, "type": "chat", "prompt": prompt})

        while True:
            msg = await receive_message(ws)
            if msg.get("type") == "result" and msg.get("id") == request_id:
                if msg.get("success"):
                    print(json.dumps(msg.get("result", {}), ensure_ascii=False))
                else:
                    raise ValueError(f"Trace read failed: {json.dumps(msg.get('error', {}))}")
                break


def main():
    parser = argparse.ArgumentParser(description="Read execution trace from target sandbox")
    parser.add_argument("--ws-url", required=True, help="Trace WebSocket URL")
    parser.add_argument("--auth-token", default=None, help="Auth token")
    parser.add_argument("--session-id", default=None, help="Target session ID")
    parser.add_argument("--trace-type", default="all",
                        choices=["thinking", "tool_calls", "conversation", "all"])
    parser.add_argument("--max-entries", type=int, default=200)
    parser.add_argument("--step-from", type=int, default=None)
    parser.add_argument("--step-to", type=int, default=None)
    parser.add_argument("--timeout", type=int, default=120)
    args = parser.parse_args()

    try:
        asyncio.run(read_trace(args.ws_url, args.auth_token, args.session_id,
                               args.trace_type, args.max_entries,
                               args.step_from, args.step_to, args.timeout))
    except Exception as e:
        print(json.dumps({"error": str(e)}, ensure_ascii=False), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
