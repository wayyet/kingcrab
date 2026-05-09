#!/usr/bin/env python3
"""
Query multi-dimensional scoring criteria from ontology knowledge base.

Usage:
    python Get-ScoringCriteria.py --ws-url ws://ontology:6060/chat --domain "对话系统"
    python Get-ScoringCriteria.py --ws-url ws://ontology:6060/chat --dimensions "功能完整性" "交互质量"
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


async def query_criteria(ws_url: str, auth_token: str | None, domain: str,
                         dimensions: list[str], timeout: int):
    url = convert_ws_url(ws_url)

    async with websockets.connect(url, open_timeout=timeout) as ws:
        first_msg = await receive_message(ws)
        if first_msg.get("type") == "auth_required":
            token = resolve_token(auth_token)
            if not token:
                raise ValueError("Auth required but no token configured")
            await send_message(ws, {"type": "auth", "access_token": token})
            await receive_message(ws)

        domain_clause = f" for domain '{domain}'" if domain else ""
        dim_clause = f" covering dimensions: {', '.join(dimensions)}"
        prompt = (f"Query evaluation scoring criteria{domain_clause}{dim_clause}. "
                  f"Return as JSON with 'criteria' key containing domain, version, "
                  f"and dimensions array. Each dimension: name, description, max_score, "
                  f"indicators array, levels array (each level: label, range_min, range_max, description).")

        request_id = id(prompt) % 2147483647
        await send_message(ws, {"id": request_id, "type": "chat", "prompt": prompt})

        while True:
            msg = await receive_message(ws)
            if msg.get("type") == "result" and msg.get("id") == request_id:
                if msg.get("success"):
                    print(json.dumps(msg.get("result", {}), ensure_ascii=False))
                else:
                    raise ValueError(f"Query failed: {json.dumps(msg.get('error', {}))}")
                break


def main():
    parser = argparse.ArgumentParser(description="Query scoring criteria from ontology KB")
    parser.add_argument("--ws-url", required=True, help="Ontology WebSocket URL")
    parser.add_argument("--auth-token", default=None, help="Auth token")
    parser.add_argument("--domain", default="", help="Evaluation domain")
    parser.add_argument("--dimensions", nargs="*",
                        default=["功能完整性", "交互质量", "响应准确性", "效率性能"],
                        help="Scoring dimensions")
    parser.add_argument("--timeout", type=int, default=120)
    args = parser.parse_args()

    try:
        asyncio.run(query_criteria(args.ws_url, args.auth_token,
                                   args.domain, args.dimensions, args.timeout))
    except Exception as e:
        print(json.dumps({"error": str(e)}, ensure_ascii=False), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
