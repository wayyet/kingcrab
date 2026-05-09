#!/usr/bin/env python3
"""
Send a message or test case to a target AI sandbox and get the response.

Usage:
    python Send-SandboxMessage.py --ws-url ws://target:9090/chat --message "Run this test"
    python Send-SandboxMessage.py --ws-url ws://target:9090/chat --testcase-file ./testcases/login.json
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


async def send_to_sandbox(ws_url: str, auth_token: str | None, message: str | None,
                          testcase_file: str | None, timeout: int):
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

        # Build prompt
        prompt = message or ""
        if testcase_file:
            with open(testcase_file, "r", encoding="utf-8") as f:
                tc_content = f.read()
            prompt += f"\nTestcase data: {tc_content}"

        if not prompt:
            raise ValueError("Either --message or --testcase-file must be provided")

        request_id = id(prompt) % 2147483647
        await send_message(ws, {"id": request_id, "type": "chat", "prompt": prompt})

        # Wait for result
        while True:
            msg = await receive_message(ws)
            if msg.get("type") == "result" and msg.get("id") == request_id:
                if msg.get("success"):
                    print(json.dumps(msg.get("result", {}), ensure_ascii=False))
                else:
                    raise ValueError(f"Sandbox error: {json.dumps(msg.get('error', {}))}")
                break
            if msg.get("type") == "error":
                raise ValueError(f"Sandbox error: {msg.get('message', 'unknown')}")


def main():
    parser = argparse.ArgumentParser(description="Send message/testcase to target AI sandbox")
    parser.add_argument("--ws-url", required=True, help="WebSocket URL")
    parser.add_argument("--auth-token", default=None, help="Auth token")
    parser.add_argument("--message", default=None, help="Message text to send")
    parser.add_argument("--testcase-file", default=None, help="Path to testcase JSON file")
    parser.add_argument("--timeout", type=int, default=120, help="Request timeout seconds")
    args = parser.parse_args()

    try:
        asyncio.run(send_to_sandbox(args.ws_url, args.auth_token,
                                    args.message, args.testcase_file, args.timeout))
    except Exception as e:
        error = {"error": str(e)}
        print(json.dumps(error, ensure_ascii=False), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
