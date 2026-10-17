"""Read-only server-stage probe: multiple requests in one actual MCP process, no model calls."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import tempfile
import threading
import time


def probe(args):
    with tempfile.TemporaryDirectory(prefix="sql-mcp-probe-") as temporary:
        settings = Path(temporary) / "settings.json"
        settings.write_text('{"version":1}', encoding="utf-8")
        command = [str(args.server)] + ([str(args.assembly)] if args.assembly else [])
        command += ["--stdio", "--workspace", str(args.workspace.resolve()), "--settings", str(settings)]
        environment = os.environ.copy()
        for key in ("SQLLSP_MCP_CONFIG", "SQLLSP_ANALYSIS_CACHE_DIR", "SQLLSP_METRICS_DIR"):
            environment.pop(key, None)
        process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   text=True, encoding="utf-8", env=environment,
                                   creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        messages = queue.Queue()
        errors = []
        def read():
            for line in process.stdout:
                messages.put(line)
            messages.put(None)
        def drain():
            for line in process.stderr:
                # Only a count; SQL/path/error values do not enter metrics.
                errors.append(1)
        reader = threading.Thread(target=read, daemon=True)
        reader.start()
        threading.Thread(target=drain, daemon=True).start()
        request_id = 0
        def send(value):
            process.stdin.write(json.dumps(value) + "\n")
            process.stdin.flush()
        def call(method, parameters):
            nonlocal request_id
            request_id += 1
            start = time.perf_counter()
            send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": parameters})
            deadline = start + 60
            while True:
                line = messages.get(timeout=max(.001, deadline - time.perf_counter()))
                if line is None:
                    raise RuntimeError("Server exited before response")
                message = json.loads(line)
                if message.get("id") == request_id:
                    return message, (time.perf_counter() - start) * 1000, len(line.encode("utf-8"))
        try:
            call("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "workspace-probe", "version": "1"}})
            send({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
            samples = []
            for _ in range(args.samples):
                wire, elapsed, length = call("tools/call", {"name": "find_procedures", "arguments": {"query": args.query, "maxResults": 1}})
                result = wire["result"]
                if result.get("isError"):
                    raise RuntimeError("Tool failed; values suppressed")
                data = result["structuredContent"]
                samples.append({"elapsed_ms": elapsed, "frame_utf8_bytes": length, "timings": data["timings"],
                                "coverage": data.get("coverage"), "indexed_documents": data["indexedDocuments"],
                                "generation": data.get("generation"), "total_matches": data["totalMatches"]})
                time.sleep(.3)  # Observe normal startup reconciliation without hiding its recorded work.
            binary = args.assembly or args.server
            output = {"mode": "same-live-process, saved source, no model/SQL calls", "server_sha256": hashlib.sha256(Path(binary).read_bytes()).hexdigest(),
                      "framework_dependent": args.assembly is not None, "stderr_lines": len(errors), "samples": samples,
                      "limits": "Wall times include competing machine load. These stage probes do not establish model latency, tokens or answer accuracy."}
            args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
            print(json.dumps({"requests": len(samples), "elapsed_ms": [round(s["elapsed_ms"], 2) for s in samples],
                              "parse_attempts": [s["coverage"].get("parseAttempts") if s["coverage"] else None for s in samples]}))
        finally:
            process.stdin.close()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--assembly", type=Path)
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--query", default="InsertCustomerOrders")
    parser.add_argument("--samples", type=int, choices=range(2, 21), default=5)
    probe(parser.parse_args())
