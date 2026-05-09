#!/usr/bin/env python3
"""
Establish a WebSocket chat session with an AI sandbox.
Handles auth_required handshake and returns session info as JSON.

Usage:
    python Start-SandboxChat.py --ws-url ws://sandbox:8080/chat --auth-token env:MY_TOKEN
"""

import argparse
import asyncio
import json
import os
import sys
import uuid
from urllib.parse import urlparse

try:
    import websockets
except ImportError:
    print(json.dumps({"error": "websockets library required. Install: pip install websockets"}), file=sys.stderr)
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


async def start_chat(ws_url: str, auth_token: str | None, system_prompt: str, timeout: int):
    url = convert_ws_url(ws_url)

    async with websockets.connect(url, open_timeout=timeout) as ws:
        first_msg = await receive_message(ws)

        if first_msg.get("type") == "auth_required":
            token = resolve_token(auth_token)
            if not token:
                raise ValueError("Auth required but no token configured")

            await send_message(ws, {"type": "auth", "access_token": token})
            auth_reply = await receive_message(ws)
            if auth_reply.get("type") != "auth_ok":
                raise ValueError(f"Auth failed: {json.dumps(auth_reply)}")

        session_id = uuid.uuid4().hex[:8]
        result = {
            "sessionId": session_id,
            "wsUrl": url,
            "connected": True,
            "timestamp": __import__("datetime").datetime.utcnow().isoformat() + "Z"
        }
        print(json.dumps(result, ensure_ascii=False))


def main():
    parser = argparse.ArgumentParser(description="Start WebSocket chat session with AI sandbox")
    parser.add_argument("--ws-url", required=True, help="WebSocket URL")
    parser.add_argument("--auth-token", default=None, help="Auth token (env:VAR or raw:VALUE)")
    parser.add_argument("--system-prompt", default="", help="System prompt")
    parser.add_argument("--timeout", type=int, default=30, help="Connection timeout seconds")
    args = parser.parse_args()

    try:
        asyncio.run(start_chat(args.ws_url, args.auth_token, args.system_prompt, args.timeout))
    except Exception as e:
        error = {"error": str(e), "wsUrl": args.ws_url}
        print(json.dumps(error, ensure_ascii=False), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
