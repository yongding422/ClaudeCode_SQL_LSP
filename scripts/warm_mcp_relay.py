"""Benchmark-only stdio relay to one owned, already initialized native MCP process.

The native plugin is unchanged. The relay never caches tool results or receives gold.
Its loopback endpoint is authenticated and is removed when the owned backend closes.
"""
import argparse
import hmac
import json
import os
from pathlib import Path
import queue
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
import time


LIMIT = 8 * 1024 * 1024


def frame(stream):
    line = stream.readline(LIMIT + 1)
    if len(line) > LIMIT:
        raise ValueError("Relay frame exceeds benchmark limit")
    return json.loads(line) if line else None


def send(stream, value):
    stream.write((json.dumps(value, separators=(",", ":")) + "\n").encode("utf-8"))
    stream.flush()


def payload(message):
    result = message.get("result", {})
    data = result.get("structuredContent")
    if data is None:
        data = next((json.loads(x["text"]) for x in result.get("content", []) if x.get("type") == "text"), {})
    if result.get("isError") or "error" in message:
        raise ValueError("Native MCP request failed")
    return data


class WarmBackend:
    def __init__(self, command, workspace, expected_files, environment=None):
        self.temporary = tempfile.TemporaryDirectory(prefix="sql-warm-relay-")
        self.stop = threading.Event()
        self.client_closed = threading.Event()
        self.messages = queue.Queue()
        self.observed = []
        self.error = None
        self.connection = None
        self.listener = None
        self.process = None
        self.stderr_lines = 0
        self.client_protocol = None
        self.preinitialize_methods = []
        self.endpoint = Path(self.temporary.name) / "endpoint.json"
        settings = Path(self.temporary.name) / "sql-settings.json"
        settings.write_text(json.dumps({"version": 1}), encoding="utf-8")
        started = time.perf_counter()
        try:
            self.process = subprocess.Popen(command + ["--stdio", "--workspace", str(workspace), "--settings", str(settings)],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                env=environment, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            self.pid = self.process.pid
            threading.Thread(target=self._read, daemon=True).start()
            threading.Thread(target=self._stderr, daemon=True).start()
            self.initial = self._call("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                "clientInfo": {"name": "sql-benchmark-prewarm", "version": "1"}}, 100000001)["result"]
            send(self.process.stdin, {"jsonrpc": "2.0", "method": "notifications/initialized"})
            arguments = {"query": "__benchmark_neutral_warmup_no_match__", "maxResults": 1}
            first = payload(self._call("tools/call", {"name": "find_procedures", "arguments": arguments}, 100000002))
            second = payload(self._call("tools/call", {"name": "find_procedures", "arguments": arguments}, 100000003))
            if first["indexedDocuments"] != expected_files or second["indexedDocuments"] != expected_files:
                raise ValueError("Warm backend inventory differs from frozen SQL")
            if first["timings"]["coldStart"] is not True or second["timings"]["coldStart"] is not False:
                raise ValueError("Native server did not demonstrate cold-to-warm reuse")
            self.warmup = {"backend_pid": self.pid, "elapsed_ms": (time.perf_counter() - started) * 1000,
                "prewarm_protocol_version": self.initial["protocolVersion"],
                "indexed_documents": second["indexedDocuments"], "first_timings": first["timings"],
                "verification_timings": second["timings"], "coverage": second.get("coverage"),
                "warmup_result_delivered_to_model": False}
            self.listener = socket.socket()
            self.listener.bind(("127.0.0.1", 0))
            self.listener.listen(1)
            self.listener.settimeout(.5)
            self.key = secrets.token_hex(32)
            self.endpoint.write_text(json.dumps({"port": self.listener.getsockname()[1], "key": self.key}), encoding="utf-8")
            threading.Thread(target=self._serve, daemon=True).start()
        except BaseException:
            self.close()
            raise

    def _read(self):
        try:
            while not self.stop.is_set():
                item = frame(self.process.stdout)
                self.messages.put(item)
                if item is None:
                    break
        except Exception:
            self.messages.put(None)

    def _stderr(self):
        for _ in self.process.stderr:
            self.stderr_lines += 1

    def _call(self, method, parameters, request_id):
        send(self.process.stdin, {"jsonrpc": "2.0", "id": request_id, "method": method, "params": parameters})
        deadline = time.monotonic() + 60
        while True:
            message = self.messages.get(timeout=max(.001, deadline - time.monotonic()))
            if message is None:
                raise ValueError("Owned backend exited")
            if message.get("id") == request_id:
                if "error" in message:
                    raise ValueError("Owned backend initialization failed")
                return message

    def _serve(self):
        try:
            while not self.stop.is_set():
                try:
                    self.connection, _ = self.listener.accept()
                    break
                except socket.timeout:
                    continue
            if self.stop.is_set():
                return
            self.connection.settimeout(15)
            stream = self.connection.makefile("rwb", buffering=0)
            auth = frame(stream)
            if not isinstance(auth, dict) or not hmac.compare_digest(str(auth.get("key", "")), self.key):
                raise ValueError("Relay authentication rejected")
            send(stream, {"relayReady": True})
            self.connection.settimeout(None)
            initial = frame(stream)
            # Current Claude clients probe modern discovery before initialize.
            # Forward the probe to the native backend, including its actual
            # MethodNotFound response, so the client can negotiate normally.
            while initial and initial.get("method") != "initialize":
                self.preinitialize_methods.append(initial.get("method"))
                send(self.process.stdin, initial)
                if "id" in initial:
                    deadline = time.monotonic() + 60
                    while True:
                        reply = self.messages.get(timeout=max(.001, deadline - time.monotonic()))
                        if reply is None:
                            raise ValueError("Backend exited during discovery negotiation")
                        send(stream, reply)
                        if reply.get("id") == initial["id"]:
                            break
                initial = frame(stream)
            if not initial or initial.get("method") != "initialize":
                raise ValueError("Expected client initialize")
            # MCP initialize negotiates a supported version. A newer client may
            # request a later version; return the backend's actual supported one.
            self.client_protocol = initial.get("params", {}).get("protocolVersion")
            send(stream, {"jsonrpc": "2.0", "id": initial["id"], "result": self.initial})
            pending = {}
            def replies():
                try:
                    while not self.stop.is_set():
                        try:
                            message = self.messages.get(timeout=.5)
                        except queue.Empty:
                            continue
                        if message is None:
                            raise ValueError("Owned backend exited during measured request")
                        name = pending.pop(message.get("id"), None)
                        if name:
                            try:
                                data = payload(message)
                                self.observed.append({"tool": name, "backend_pid": self.pid, "timings": data.get("timings")})
                            except (ValueError, TypeError):
                                self.observed.append({"tool": name, "backend_pid": self.pid, "timings": None, "native_error": True})
                        send(stream, message)
                except Exception as error:
                    if not self.stop.is_set() and not self.client_closed.is_set():
                        self.error = type(error).__name__
                        self.connection.close()
            threading.Thread(target=replies, daemon=True).start()
            while not self.stop.is_set():
                message = frame(stream)
                if message is None:
                    self.client_closed.set()
                    break
                if message.get("method") == "notifications/initialized":
                    continue  # The same backend was initialized once before warm-up.
                if message.get("method") == "tools/call":
                    pending[message["id"]] = message.get("params", {}).get("name")
                send(self.process.stdin, message)
        except (ConnectionResetError, ConnectionAbortedError):
            self.client_closed.set()
        except Exception as error:
            if not self.stop.is_set() and self.error is None:
                self.error = type(error).__name__ + ":" + str(error)

    def evidence(self):
        return {**self.warmup, "measured_calls": list(self.observed), "relay_error": self.error,
                "client_requested_protocol_version": self.client_protocol,
                "preinitialize_methods": list(self.preinitialize_methods),
                "same_backend_alive_after_client": self.process.poll() is None, "stderr_lines": self.stderr_lines}

    def close(self):
        self.stop.set()
        if self.connection:
            try:
                self.connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            self.connection.close()
        if self.listener:
            self.listener.close()
        if self.process:
            try:
                self.process.stdin.close()
                self.process.wait(timeout=5)
            except (OSError, subprocess.TimeoutExpired):
                self.process.kill()
                self.process.wait()
            self.process.stdout.close()
            self.process.stderr.close()
        self.temporary.cleanup()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def connect(endpoint):
    config = json.loads(endpoint.read_text(encoding="utf-8"))
    with socket.create_connection(("127.0.0.1", config["port"]), timeout=15) as connection:
        stream = connection.makefile("rwb", buffering=0)
        send(stream, {"key": config["key"]})
        if frame(stream) != {"relayReady": True}:
            raise ValueError("Relay authentication failed")
        connection.settimeout(None)
        def replies():
            while True:
                message = frame(stream)
                if message is None:
                    break
                send(sys.stdout.buffer, message)
        reader = threading.Thread(target=replies, daemon=True)
        reader.start()
        for line in sys.stdin.buffer:
            stream.write(line)
            stream.flush()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--connect", type=Path, required=True)
    connect(parser.parse_args().connect)
