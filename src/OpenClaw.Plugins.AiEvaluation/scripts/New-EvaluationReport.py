#!/usr/bin/env python3
"""
Generate a structured evaluation report for the target sandbox.

Usage:
    python New-EvaluationReport.py --ws-url ws://report:5050/chat \
        --scores '[{"dimension":"功能完整性","score":85,"max_score":100}]' \
        --output-path ./report.json
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


async def generate_report(ws_url: str, auth_token: str | None, scores: str,
                          trace_summary: str, test_results: str,
                          recommendations: str, overall_comment: str,
                          output_path: str | None, timeout: int):
    url = convert_ws_url(ws_url)

    async with websockets.connect(url, open_timeout=timeout) as ws:
        first_msg = await receive_message(ws)
        if first_msg.get("type") == "auth_required":
            token = resolve_token(auth_token)
            if not token:
                raise ValueError("Auth required but no token configured")
            await send_message(ws, {"type": "auth", "access_token": token})
            await receive_message(ws)

        prompt = (
            "Generate a structured evaluation report based on the following data. "
            "Return as JSON with 'report' key containing: "
            "report_id, evaluated_at, target_endpoint, scores array (each with dimension, score, max_score, comment), "
            "total_score, max_possible_score, overall_rating, strengths array, weaknesses array, "
            "suggestions array (each with area, suggestion, priority), and summary.\n\n"
            f"Scores: {scores}\n"
            f"Test Results: {test_results}\n"
            f"Trace Summary: {trace_summary}\n"
            f"Recommendations: {recommendations}\n"
            f"Overall Comment: {overall_comment}"
        )

        request_id = id(prompt) % 2147483647
        await send_message(ws, {"id": request_id, "type": "chat", "prompt": prompt})

        while True:
            msg = await receive_message(ws)
            if msg.get("type") == "result" and msg.get("id") == request_id:
                if msg.get("success"):
                    report_json = json.dumps(msg.get("result", {}), ensure_ascii=False)
                    if output_path:
                        with open(output_path, "w", encoding="utf-8") as f:
                            f.write(report_json)
                        print(json.dumps({"reportPath": output_path, "status": "saved"}, ensure_ascii=False))
                    else:
                        print(report_json)
                else:
                    raise ValueError(f"Report generation failed: {json.dumps(msg.get('error', {}))}")
                break


def main():
    parser = argparse.ArgumentParser(description="Generate evaluation report")
    parser.add_argument("--ws-url", required=True, help="Report generator WebSocket URL")
    parser.add_argument("--auth-token", default=None, help="Auth token")
    parser.add_argument("--scores", default="[]", help="Scores JSON string")
    parser.add_argument("--trace-summary", default="", help="Trace summary text")
    parser.add_argument("--test-results", default="{}", help="Test results JSON")
    parser.add_argument("--recommendations", default="[]", help="Recommendations JSON")
    parser.add_argument("--overall-comment", default="", help="Overall comment")
    parser.add_argument("--output-path", default=None, help="Report output file path")
    parser.add_argument("--timeout", type=int, default=120)
    args = parser.parse_args()

    try:
        asyncio.run(generate_report(args.ws_url, args.auth_token, args.scores,
                                    args.trace_summary, args.test_results,
                                    args.recommendations, args.overall_comment,
                                    args.output_path, args.timeout))
    except Exception as e:
        print(json.dumps({"error": str(e)}, ensure_ascii=False), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
