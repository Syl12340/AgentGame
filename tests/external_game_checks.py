"""Real loopback HTTP + stdio MCP checks. No model API, credentials or global configuration."""
import concurrent.futures
import importlib.util
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import unittest
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
CLI = ROOT / "src/AgentGame.Cli/bin/Release/net10.0/agent-game.dll"
MCP = ROOT / "tools/game-mcp/server.py"
SCENE = ROOT / "tests/Fixtures/Core/facility-small.json"
ROUTE = json.loads((ROOT / "tests/Fixtures/Core/facility-small.actions.json").read_text(encoding="utf-8"))
ARTIFACTS = ROOT / "artifacts/external-game-tests" / uuid.uuid4().hex
ARTIFACTS.mkdir(parents=True)
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def start_process(args):
    return subprocess.Popen(args, cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)


def read_lines(stream, output):
    for line in iter(stream.readline, b""):
        output.put(line)


def stop_process(process):
    if process.poll() is None:
        process.kill()
    process.wait(timeout=10)
    for stream in (process.stdin, process.stdout, process.stderr):
        stream.close()


class Game:
    def __init__(self, record=None):
        args = ["dotnet", str(CLI), "serve", "--scenario", str(SCENE), "--port", "0"]
        if record:
            args += ["--record", str(record)]
        self.process = start_process(args)
        self.lines = queue.Queue()
        threading.Thread(target=read_lines, args=(self.process.stderr, self.lines), daemon=True).start()
        try:
            self.ready = json.loads(self.lines.get(timeout=15))
            assert self.ready["format"] == "external-server/1", self.ready
            self.url = self.ready["url"]
        except Exception:
            stop_process(self.process)
            raise

    def __enter__(self):
        return self

    def __exit__(self, *_):
        stop_process(self.process)

    def request(self, path, payload=None, raw=None, headers=None, method=None):
        body = json.dumps(payload).encode("utf-8") if payload is not None else raw
        req = urllib.request.Request(self.url + path, data=body, method=method,
                                     headers=headers or ({"Content-Type": "application/json"} if body is not None else {}))
        try:
            response = OPENER.open(req, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            content = response.read()
            return response.status, json.loads(content) if content else None

    def observe(self):
        code, data = self.request("/v1/observation")
        assert code == 200, (code, data)
        return data

    def status(self):
        code, data = self.request("/v1/session")
        assert code == 200, (code, data)
        return data

    def action(self, request_id, action):
        return self.request("/v1/actions", {"type": "action", "request_id": request_id, "action": action})


class MCPClient:
    def __init__(self, url):
        self.process = start_process([sys.executable, "-u", str(MCP), "--url", url])
        self.lines = queue.Queue()
        self.id = 0
        threading.Thread(target=read_lines, args=(self.process.stdout, self.lines), daemon=True).start()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
            assert self.process.returncode == 0, self.process.stderr.read().decode()
        finally:
            # stdin already closed; closing twice is harmless.
            stop_process(self.process)

    def send(self, message):
        self.process.stdin.write(json.dumps(message).encode() + b"\n")
        self.process.stdin.flush()

    def call(self, method, params=None):
        self.id += 1
        msg = {"jsonrpc": "2.0", "id": self.id, "method": method}
        if params is not None:
            msg["params"] = params
        self.send(msg)
        reply = json.loads(self.lines.get(timeout=15))
        assert reply["id"] == self.id, reply
        return reply

    def initialize(self, version="2025-11-25"):
        response = self.call("initialize", {"protocolVersion": version, "capabilities": {},
                                            "clientInfo": {"name": "external-game-checks", "version": "1"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        return response

    def tool(self, name, arguments=None):
        return self.call("tools/call", {"name": name, "arguments": arguments or {}})


def load_mcp():
    spec = importlib.util.spec_from_file_location("game_mcp_checks", MCP)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ExternalGameChecks(unittest.TestCase):
    def test_http_idle_local_view_and_reconnect(self):
        with Game() as game:
            first = game.observe()
            time.sleep(0.1)
            self.assertEqual(first, game.observe())
            self.assertEqual(0, game.status()["tick"])
            self.assertEqual("waiting", game.status()["state"])
            self.assertNotIn("rows", first)
            self.assertNotIn("seed", game.status())
            self.assertNotIn("core_hash", game.status())
            self.assertTrue(all(tile["x"] <= 4 for tile in first["observation"]["tiles"]))
            self.assertFalse(any(tile.get("item") == "core" for tile in first["observation"]["tiles"]))

    def test_http_rejects_bad_actions_without_advancing(self):
        with Game() as game:
            id_ = game.observe()["request_id"]
            for action in ({"type": "move"}, {"type": "move", "direction": "right"},
                           {"type": "pickup", "direction": "east"}, {"type": "wait", "extra": True}):
                code, data = game.action(id_, action)
                self.assertEqual(400, code)
                self.assertEqual("invalid_action", data["error"]["code"])
            for raw in (b"no JSON", b"\xc3\x28", b'{"type":"action","type":"action"}', b"{}"):
                self.assertEqual(400, game.request("/v1/actions", raw=raw)[0])
            self.assertEqual(413, game.request("/v1/actions", raw=b" " * 65537)[0])
            self.assertEqual(415, game.request("/v1/actions", raw=b"{}", headers={"Content-Type": "text/plain"})[0])
            self.assertEqual(403, game.request("/v1/actions", payload={"type": "action", "request_id": id_, "action": {"type": "wait"}},
                                             headers={"Content-Type": "application/json", "Origin": "http://example.invalid"})[0])
            self.assertEqual(404, game.request("/unknown")[0])
            self.assertEqual(405, game.request("/v1/observation", raw=b"{}", method="POST")[0])
            self.assertEqual(0, game.status()["tick"])

    def test_http_concurrent_and_foreign_stale_requests(self):
        with Game() as game, Game() as other:
            old = game.observe()["request_id"]
            with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
                results = list(pool.map(lambda _: game.action(old, {"type": "wait"})[0], range(4)))
            self.assertEqual([200, 409, 409, 409], sorted(results))
            self.assertEqual(1, game.status()["tick"])
            self.assertEqual("stale_request", game.action(other.observe()["request_id"], {"type": "wait"})[1]["error"]["code"])
            self.assertEqual(1, game.status()["tick"])

    def test_http_real_route_and_record_verification_while_server_alive(self):
        record = ARTIFACTS / "http-success.jsonl"
        with Game(record) as game:
            observation = game.observe()
            for action in ROUTE:
                code, observation = game.action(observation["request_id"], action)
                self.assertEqual(200, code, observation)
            self.assertEqual("episode_end", observation["type"])
            self.assertEqual("success", observation["result"]["kind"])
            self.assertEqual(observation, game.observe())
            self.assertEqual("completed", game.status()["state"])
            self.assertEqual(13, game.status()["tick"])
            self.assertIsNone(game.status()["request_id"])
            self.assertEqual("episode_ended", game.action(observation["request_id"], {"type": "wait"})[1]["error"]["code"])
            verified = subprocess.run(["dotnet", str(CLI), "verify", str(record)], capture_output=True, timeout=15)
            self.assertEqual(0, verified.returncode, verified.stderr.decode())
            self.assertTrue(json.loads(verified.stdout)["valid"])
            self.assertEqual(1, sum(json.loads(line)["type"] == "run_footer" for line in record.read_text().splitlines()))
            self.assertIsNone(game.process.poll())

    def test_serve_cli_usage_and_no_overwrite(self):
        for args in ([], ["--port", "-1"], ["--scenario", str(SCENE), "--port", "65536"],
                     ["--scenario", str(SCENE), "--tui"], ["--scenario", str(SCENE), "--unknown"],
                     ["--scenario", str(SCENE), "--port", "0", "--port", "1"]):
            result = subprocess.run(["dotnet", str(CLI), "serve", *args], capture_output=True, timeout=10)
            self.assertEqual(2, result.returncode, (args, result.stderr.decode()))
            self.assertEqual(b"", result.stdout)
        record = ARTIFACTS / "existing.jsonl"
        record.write_text("preserve", encoding="utf-8")
        result = subprocess.run(["dotnet", str(CLI), "serve", "--scenario", str(SCENE), "--record", str(record)],
                                capture_output=True, timeout=10)
        self.assertEqual(1, result.returncode)
        self.assertEqual("preserve", record.read_text())

    def test_mcp_lifecycle_discovery_and_param_validation(self):
        with Game() as game, MCPClient(game.url) as client:
            self.assertEqual(-32002, client.call("tools/list")["error"]["code"])
            client.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            self.assertEqual(-32002, client.call("tools/list")["error"]["code"])
            self.assertEqual("2025-06-18", client.initialize("2025-06-18")["result"]["protocolVersion"])
            tools = client.call("tools/list")["result"]["tools"]
            self.assertEqual({"game_status", "game_observe", "game_act"}, {t["name"] for t in tools})
            act = next(t for t in tools if t["name"] == "game_act")
            self.assertEqual({"north", "east", "south", "west"}, set(act["inputSchema"]["properties"]["direction"]["enum"]))
            for args in ({"request_id": "x", "type": "move"}, {"request_id": "x", "type": "wait", "direction": None},
                         {"request_id": "x", "type": "wait", "extra": True}, {"type": "wait"}):
                self.assertEqual(-32602, client.tool("game_act", args)["error"]["code"])
            self.assertEqual(-32602, client.tool("game_status", {"extra": 1})["error"]["code"])
            self.assertEqual(-32601, client.call("unknown")["error"]["code"])
            self.assertEqual(0, game.status()["tick"])

    def test_mcp_real_game_and_authority_record(self):
        record = ARTIFACTS / "mcp-success.jsonl"
        with Game(record) as game, MCPClient(game.url) as client:
            client.initialize()
            observation = client.tool("game_observe")["result"]["structuredContent"]
            for action in ROUTE:
                reply = client.tool("game_act", {"request_id": observation["request_id"], **action})["result"]
                self.assertFalse(reply["isError"], reply)
                self.assertEqual(json.loads(reply["content"][0]["text"]), reply["structuredContent"])
                observation = reply["structuredContent"]
            self.assertEqual("success", observation["result"]["kind"])
            self.assertEqual(observation, client.tool("game_observe")["result"]["structuredContent"])
            self.assertEqual(13, client.tool("game_status")["result"]["structuredContent"]["tick"])
            error = client.tool("game_act", {"request_id": observation["request_id"], "type": "wait"})["result"]
            self.assertTrue(error["isError"])
            self.assertEqual("episode_ended", error["structuredContent"]["error"]["code"])
            verify = subprocess.run(["dotnet", str(CLI), "verify", str(record)], capture_output=True, timeout=15)
            self.assertEqual(0, verify.returncode, verify.stderr.decode())

    def test_mcp_disconnect_reconnect_and_no_stale_retry(self):
        with Game() as game:
            with MCPClient(game.url) as first:
                first.initialize()
                old = first.tool("game_observe")["result"]["structuredContent"]["request_id"]
                next_ = first.tool("game_act", {"request_id": old, "type": "wait"})["result"]["structuredContent"]
            with MCPClient(game.url) as second:
                second.initialize()
                self.assertEqual(next_, second.tool("game_observe")["result"]["structuredContent"])
                error = second.tool("game_act", {"request_id": old, "type": "wait"})["result"]
                self.assertTrue(error["isError"])
                self.assertEqual("stale_request", error["structuredContent"]["error"]["code"])
                self.assertEqual(1, game.status()["tick"])

    def test_mcp_strict_json_versions_and_target_configuration(self):
        module = load_mcp()
        for url in ("http://example.invalid:8765", "https://127.0.0.1:8765", "http://a:b@127.0.0.1:8765",
                    "http://127.0.0.1", "http://127.0.0.1:8765/other", "http://127.0.0.1:8765/?x=1"):
            with self.assertRaises(ValueError):
                module.validate_url(url)
        for timeout in (0, -1, float("nan"), float("inf")):
            with self.assertRaises(ValueError):
                module.validate_timeout(timeout)
        for version in module.SUPPORTED_PROTOCOL_VERSIONS + ["future"]:
            server = module.GameMCPServer()
            reply = server.handle_message({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
                "protocolVersion": version, "capabilities": {}, "clientInfo": {"name": "checks", "version": "1"}}})
            self.assertEqual(version if version != "future" else module.LATEST_PROTOCOL_VERSION, reply["result"]["protocolVersion"])
        server = module.GameMCPServer()
        for bad in (b"\xc3\x28", b'{"id":1,"id":2}', b'{"id":NaN}', b'{"id":1e999}', b'{"id":"\\ud800"}', b"x" * (module.MAX_INPUT_BYTES + 1)):
            self.assertEqual(-32700, server.process_line(bad)["error"]["code"])
        for bad in ([], False, {"jsonrpc": "2.0", "method": "ping", "id": True}, {"jsonrpc": "2.0"}):
            self.assertEqual(-32600, server.handle_message(bad)["error"]["code"])

    def test_mcp_transport_failure_is_tool_error(self):
        # Reserve then release a local port: no remote network or inference involved.
        import socket
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        with MCPClient(f"http://127.0.0.1:{port}") as client:
            client.initialize()
            result = client.tool("game_observe")["result"]
            self.assertTrue(result["isError"])
            self.assertEqual("transport_error", result["structuredContent"]["error"]["code"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
