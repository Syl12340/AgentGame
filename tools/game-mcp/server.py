"""MCP stdio JSON-RPC tool server for persistent local game control.

Exposes game_status, game_observe, and game_act tools over standard I/O
conforming to the Model Context Protocol (MCP) specification.
"""

from __future__ import annotations

import argparse
import enum
import json
import math
import sys
import urllib.error
import urllib.parse
import urllib.request
from typing import Any

SUPPORTED_PROTOCOL_VERSIONS = [
    "2024-11-05",
    "2025-03-26",
    "2025-06-18",
    "2025-11-25",
]
LATEST_PROTOCOL_VERSION = "2025-11-25"

MAX_INPUT_BYTES = 1024 * 1024  # 1 MiB
MAX_HTTP_RESPONSE_BYTES = 2 * 1024 * 1024  # 2 MiB

DEFAULT_URL = "http://127.0.0.1:8765"
DEFAULT_TIMEOUT = 10.0


class ServerState(enum.Enum):
    UNINITIALIZED = "uninitialized"
    INITIALIZING = "initializing"
    INITIALIZED = "initialized"


def _reject_constant(val: str) -> None:
    raise ValueError(f"Non-finite numbers not allowed: {val}")


def _reject_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    keys = set()
    result = {}
    for k, v in pairs:
        if k in keys:
            raise ValueError(f"Duplicate key: {k}")
        keys.add(k)
        result[k] = v
    return result


def _finite_float(text: str) -> float:
    value = float(text)
    if not math.isfinite(value):
        raise ValueError("Non-finite JSON number")
    return value


def _valid_unicode(value: Any) -> None:
    if isinstance(value, str):
        value.encode("utf-8", errors="strict")
    elif isinstance(value, dict):
        for key, item in value.items():
            _valid_unicode(key)
            _valid_unicode(item)
    elif isinstance(value, list):
        for item in value:
            _valid_unicode(item)


def parse_json(text: str) -> Any:
    """Parse JSON with strict rejection of duplicate keys and non-finite numbers."""
    value = json.loads(
        text,
        parse_constant=_reject_constant,
        parse_float=_finite_float,
        object_pairs_hook=_reject_duplicates,
    )
    _valid_unicode(value)
    return value


def validate_url(url_str: str) -> str:
    """Validate and normalize game backend loopback URL."""
    if not isinstance(url_str, str) or not url_str.strip():
        raise ValueError("URL must be a non-empty string.")

    parsed = urllib.parse.urlsplit(url_str.strip())

    if parsed.scheme.lower() != "http":
        raise ValueError(f"Invalid URL scheme: '{parsed.scheme}'. Scheme must be http.")

    if parsed.username is not None or parsed.password is not None or "@" in parsed.netloc:
        raise ValueError("URL must not contain credentials.")

    if parsed.query:
        raise ValueError("URL must not contain query parameters.")

    if parsed.fragment:
        raise ValueError("URL must not contain a fragment.")

    if parsed.path not in ("", "/"):
        raise ValueError(f"Invalid URL path: '{parsed.path}'. Path must be empty or '/'.")

    host = parsed.hostname
    if not host:
        raise ValueError("URL must contain a host.")

    host_lower = host.lower()
    if host_lower not in ("127.0.0.1", "::1", "localhost"):
        raise ValueError(
            f"Invalid host: '{host}'. Host must be literal loopback (127.0.0.1, [::1], or localhost)."
        )

    try:
        port = parsed.port
    except ValueError:
        raise ValueError("Invalid URL port.")

    if port is None:
        raise ValueError("URL must explicitly specify a port.")

    if not (1 <= port <= 65535):
        raise ValueError(f"Port out of range (1-65535): {port}")

    if host_lower == "::1":
        return f"http://[{host}]:{port}"
    return f"http://{host_lower}:{port}"


def validate_timeout(timeout: Any) -> float:
    """Validate timeout value is a positive finite float."""
    try:
        val = float(timeout)
    except (ValueError, TypeError):
        raise ValueError(f"Timeout must be a numeric value, got: {timeout!r}")
    if not math.isfinite(val) or val <= 0:
        raise ValueError(f"Timeout must be a positive finite number, got: {val}")
    return val


class NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Disallows following HTTP redirects."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def build_opener() -> urllib.request.OpenerDirector:
    """Create a urllib opener with proxy resolution and redirects disabled."""
    proxy_handler = urllib.request.ProxyHandler({})
    redirect_handler = NoRedirectHandler()
    return urllib.request.build_opener(proxy_handler, redirect_handler)


class GameClient:
    """Low-level HTTP client communicating with the local game loopback server."""

    def __init__(self, base_url: str, timeout: float = DEFAULT_TIMEOUT):
        self.base_url = validate_url(base_url)
        self.timeout = validate_timeout(timeout)
        self.opener = build_opener()

    def _read_response(
        self, resp: Any, is_error: bool = False, default_code: int | None = None
    ) -> tuple[dict[str, Any], bool]:
        try:
            raw = resp.read(MAX_HTTP_RESPONSE_BYTES + 1)
        except Exception:
            return (
                {
                    "error": {
                        "code": "transport_error",
                        "detail": "Failed to read HTTP response.",
                    }
                },
                True,
            )

        if len(raw) > MAX_HTTP_RESPONSE_BYTES:
            return (
                {
                    "error": {
                        "code": "response_too_large",
                        "detail": "HTTP response exceeded 2 MiB limit.",
                    }
                },
                True,
            )

        try:
            text = raw.decode("utf-8")
            data = parse_json(text)
        except Exception:
            if is_error:
                code_val = default_code or getattr(resp, "code", 500)
                return (
                    {
                        "error": {
                            "code": f"http_{code_val}",
                            "detail": f"HTTP error {code_val}.",
                        }
                    },
                    True,
                )
            return (
                {
                    "error": {
                        "code": "invalid_json",
                        "detail": "HTTP response is not valid JSON.",
                    }
                },
                True,
            )

        if not isinstance(data, dict):
            return (
                {
                    "error": {
                        "code": "invalid_response",
                        "detail": "HTTP response must be a JSON object.",
                    }
                },
                True,
            )

        return (data, is_error)

    def _execute_request(self, req: urllib.request.Request) -> tuple[dict[str, Any], bool]:
        try:
            with self.opener.open(req, timeout=self.timeout) as resp:
                return self._read_response(resp, is_error=False)
        except urllib.error.HTTPError as e:
            with e:
                return self._read_response(e, is_error=True, default_code=e.code)
        except urllib.error.URLError:
            return (
                {
                    "error": {
                        "code": "transport_error",
                        "detail": "HTTP transport error.",
                    }
                },
                True,
            )
        except TimeoutError:
            return (
                {
                    "error": {
                        "code": "transport_error",
                        "detail": "HTTP request timed out.",
                    }
                },
                True,
            )
        except Exception:
            return (
                {
                    "error": {
                        "code": "transport_error",
                        "detail": "HTTP request failed.",
                    }
                },
                True,
            )

    def get_session(self) -> tuple[dict[str, Any], bool]:
        url = f"{self.base_url}/v1/session"
        req = urllib.request.Request(url, method="GET")
        req.add_header("Accept", "application/json")
        return self._execute_request(req)

    def get_observation(self) -> tuple[dict[str, Any], bool]:
        url = f"{self.base_url}/v1/observation"
        req = urllib.request.Request(url, method="GET")
        req.add_header("Accept", "application/json")
        return self._execute_request(req)

    def post_action(self, payload: dict[str, Any]) -> tuple[dict[str, Any], bool]:
        url = f"{self.base_url}/v1/actions"
        data_bytes = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        req = urllib.request.Request(url, data=data_bytes, method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Accept", "application/json")
        return self._execute_request(req)


class GameMCPServer:
    """MCP stdio JSON-RPC server implementing the game control tools."""

    def __init__(
        self,
        url: str | None = None,
        base_url: str | None = None,
        timeout: float = DEFAULT_TIMEOUT,
    ):
        target_url = (
            url
            if url is not None
            else (base_url if base_url is not None else DEFAULT_URL)
        )
        self.base_url = validate_url(target_url)
        self.timeout = validate_timeout(timeout)
        self.client = GameClient(self.base_url, self.timeout)
        self.state = ServerState.UNINITIALIZED

    def get_tools(self) -> list[dict[str, Any]]:
        return [
            {
                "name": "game_status",
                "description": (
                    "Get current game session metadata and status (session_id, state, tick, request_id, result, error) "
                    "for the current local game session. Read-only operation; observations and status checks do not advance ticks."
                ),
                "annotations": {
                    "readOnlyHint": True,
                },
                "inputSchema": {
                    "type": "object",
                    "properties": {},
                    "additionalProperties": False,
                },
            },
            {
                "name": "game_observe",
                "description": (
                    "Get current local game observation (position, tiles, inventory, mission, last_result) or episode end message. "
                    "Read-only operation for the current local session; observations do not advance ticks."
                ),
                "annotations": {
                    "readOnlyHint": True,
                },
                "inputSchema": {
                    "type": "object",
                    "properties": {},
                    "additionalProperties": False,
                },
            },
            {
                "name": "game_act",
                "description": (
                    "Execute one action in the current local game session. Coordinates: north=(0,-1), east=(1,0), south=(0,1), west=(-1,0). "
                    "to navigate the grid and achieve objectives (key -> door -> core -> exit). Observations do not advance ticks; "
                    "actions advance game ticks. Call exactly one action per observation using the observation's request_id."
                ),
                "annotations": {
                    "readOnlyHint": False,
                    "idempotentHint": False,
                },
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "request_id": {
                            "type": "string",
                            "minLength": 1,
                            "description": "Non-empty request ID from the current local observation message.",
                        },
                        "type": {
                            "type": "string",
                            "enum": ["move", "interact", "pickup", "wait"],
                            "description": "Action type: 'move' (cardinal coordinates), 'interact' (key/door/core/exit), 'pickup' (pick up item), or 'wait' (idle one tick).",
                        },
                        "direction": {
                            "type": "string",
                            "enum": ["north", "east", "south", "west"],
                            "description": "Cardinal direction. Required for move/interact; absent for pickup/wait.",
                        },
                    },
                    "required": ["request_id", "type"],
                    "additionalProperties": False,
                },
            },
        ]

    def _make_error(
        self, req_id: Any, code: int, message: str, data: Any = None
    ) -> dict[str, Any]:
        err_obj: dict[str, Any] = {
            "code": code,
            "message": message,
        }
        if data is not None:
            err_obj["data"] = data
        return {
            "jsonrpc": "2.0",
            "id": req_id,
            "error": err_obj,
        }

    def _make_result(self, req_id: Any, result: Any) -> dict[str, Any]:
        return {
            "jsonrpc": "2.0",
            "id": req_id,
            "result": result,
        }

    def _make_tool_result(
        self, req_id: Any, data: dict[str, Any], is_error: bool
    ) -> dict[str, Any]:
        structured = data if isinstance(data, dict) else {"data": data}
        text_content = json.dumps(
            structured, separators=(",", ":"), ensure_ascii=False
        )
        return {
            "jsonrpc": "2.0",
            "id": req_id,
            "result": {
                "content": [
                    {
                        "type": "text",
                        "text": text_content,
                    }
                ],
                "structuredContent": structured,
                "isError": is_error,
            },
        }

    def execute_tool(
        self, req_id: Any, tool_name: str, arguments: dict[str, Any]
    ) -> dict[str, Any]:
        if tool_name == "game_status":
            if arguments:
                return self._make_error(
                    req_id, -32602, "Invalid params: game_status takes no arguments"
                )
            data, is_error = self.client.get_session()
            return self._make_tool_result(req_id, data, is_error)

        elif tool_name == "game_observe":
            if arguments:
                return self._make_error(
                    req_id, -32602, "Invalid params: game_observe takes no arguments"
                )
            data, is_error = self.client.get_observation()
            return self._make_tool_result(req_id, data, is_error)

        elif tool_name == "game_act":
            allowed_keys = {"request_id", "type", "direction"}
            extra_keys = set(arguments.keys()) - allowed_keys
            if extra_keys:
                return self._make_error(
                    req_id,
                    -32602,
                    f"Invalid params: unexpected argument(s): {', '.join(sorted(extra_keys))}",
                )

            request_id = arguments.get("request_id")
            if not isinstance(request_id, str) or not request_id:
                return self._make_error(
                    req_id,
                    -32602,
                    "Invalid params: request_id must be a non-empty string",
                )

            action_type = arguments.get("type")
            valid_types = {"move", "interact", "pickup", "wait"}
            if not isinstance(action_type, str) or action_type not in valid_types:
                return self._make_error(
                    req_id,
                    -32602,
                    f"Invalid params: type must be one of: {', '.join(sorted(valid_types))}",
                )

            valid_directions = {"north", "east", "south", "west"}
            direction = arguments.get("direction")

            if action_type in ("move", "interact"):
                if not isinstance(direction, str) or direction not in valid_directions:
                    return self._make_error(
                        req_id,
                        -32602,
                        f"Invalid params: direction is required for '{action_type}' and must be one of: {', '.join(sorted(valid_directions))}",
                    )
                action_payload: dict[str, Any] = {
                    "type": action_type,
                    "direction": direction,
                }
            else:
                if "direction" in arguments:
                    return self._make_error(
                        req_id,
                        -32602,
                        f"Invalid params: direction must not be provided for '{action_type}'",
                    )
                action_payload = {"type": action_type}

            payload = {
                "type": "action",
                "request_id": request_id,
                "action": action_payload,
            }
            data, is_error = self.client.post_action(payload)
            return self._make_tool_result(req_id, data, is_error)

        else:
            return self._make_error(req_id, -32602, f"Unknown tool: {tool_name}")

    def handle_message(self, req: Any) -> dict[str, Any] | None:
        if isinstance(req, list):
            return self._make_error(
                None, -32600, "Invalid Request: Batches not supported"
            )
        if not isinstance(req, dict):
            return self._make_error(
                None, -32600, "Invalid Request: Request must be an object"
            )

        is_notification = "id" not in req
        req_id = req.get("id")

        if req.get("jsonrpc") != "2.0" or not isinstance(req.get("method"), str):
            return self._make_error(None, -32600, "Invalid JSON-RPC request")

        # Notifications MUST NOT receive any response
        if is_notification:
            if req.get("jsonrpc") == "2.0":
                method = req.get("method")
                if method == "notifications/initialized" and self.state == ServerState.INITIALIZING:
                    self.state = ServerState.INITIALIZED
            return None

        # Validate request ID
        if isinstance(req_id, bool) or not (
            isinstance(req_id, (str, int, float)) or req_id is None
        ):
            return self._make_error(
                None,
                -32600,
                "Invalid Request: id must be a string, number, or null",
            )

        if req.get("jsonrpc") != "2.0":
            return self._make_error(
                req_id, -32600, "Invalid Request: jsonrpc must be '2.0'"
            )

        method = req.get("method")
        if not isinstance(method, str):
            return self._make_error(
                req_id, -32600, "Invalid Request: method must be a string"
            )

        # Requests
        if method == "initialize":
            params = req.get("params")
            if self.state != ServerState.UNINITIALIZED:
                return self._make_error(req_id, -32600, "Server already initializing or initialized")
            if (not isinstance(params, dict)
                or not isinstance(params.get("protocolVersion"), str)
                or not isinstance(params.get("capabilities"), dict)
                or not isinstance(params.get("clientInfo"), dict)
                or not isinstance(params["clientInfo"].get("name"), str)
                or not isinstance(params["clientInfo"].get("version"), str)):
                return self._make_error(
                    req_id, -32602, "Invalid initialize params"
                )
            params = params or {}
            requested_proto = params.get("protocolVersion")
            if requested_proto in SUPPORTED_PROTOCOL_VERSIONS:
                proto = requested_proto
            else:
                proto = LATEST_PROTOCOL_VERSION
            self.state = ServerState.INITIALIZING
            return self._make_result(
                req_id,
                {
                    "protocolVersion": proto,
                    "capabilities": {
                        "tools": {},
                    },
                    "serverInfo": {
                        "name": "game-mcp",
                        "version": "1.0.0",
                    },
                    "instructions": (
                        "Control the persistent local game session via available tools. "
                        "Check game status with game_status. "
                        "Get observations with game_observe (observations do not advance ticks). "
                        "Send actions with game_act using the latest observation request_id. "
                        "Directions: north=(0,-1), east=(1,0), south=(0,1), west=(-1,0). "
                        "Pick up key/core on your current tile; interact towards an adjacent door with the key; carry core to exit. "
                        "Only currently visible tiles are given: remember explored places yourself. "
                        "On stale_request or transport errors, reobserve; never blindly retry an action."
                    ),
                },
            )

        elif method == "ping":
            return self._make_result(req_id, {})

        elif method == "tools/list":
            if self.state != ServerState.INITIALIZED:
                return self._make_error(req_id, -32002, "Server not initialized")
            params = req.get("params")
            if params is not None and not isinstance(params, dict):
                return self._make_error(
                    req_id, -32602, "Invalid params: params must be an object"
                )
            return self._make_result(req_id, {"tools": self.get_tools()})

        elif method == "tools/call":
            if self.state != ServerState.INITIALIZED:
                return self._make_error(req_id, -32002, "Server not initialized")
            params = req.get("params")
            if not isinstance(params, dict):
                return self._make_error(
                    req_id, -32602, "Invalid params: params must be an object"
                )
            tool_name = params.get("name")
            if not isinstance(tool_name, str):
                return self._make_error(
                    req_id, -32602, "Invalid params: name must be a string"
                )
            arguments = params.get("arguments", {})
            if not isinstance(arguments, dict):
                return self._make_error(
                    req_id, -32602, "Invalid params: arguments must be an object"
                )
            return self.execute_tool(req_id, tool_name, arguments)

        else:
            return self._make_error(req_id, -32601, f"Method not found: {method}")

    def process_line(self, line: bytes | str) -> dict[str, Any] | None:
        if isinstance(line, str):
            raw_bytes = line.encode("utf-8")
        else:
            raw_bytes = line

        if len(raw_bytes) > MAX_INPUT_BYTES:
            return self._make_error(
                None, -32700, "Parse error: input line exceeds 1 MiB limit"
            )

        stripped = raw_bytes.strip()
        if not stripped:
            return None

        try:
            text = stripped.decode("utf-8")
        except UnicodeDecodeError:
            return self._make_error(None, -32700, "Parse error: invalid UTF-8")

        try:
            req = parse_json(text)
        except Exception:
            return self._make_error(None, -32700, "Parse error")

        return self.handle_message(req)

    def _send_response(self, response_obj: dict[str, Any]) -> None:
        line = json.dumps(response_obj, separators=(",", ":"), ensure_ascii=False)
        sys.stdout.buffer.write(line.encode("utf-8") + b"\n")
        sys.stdout.buffer.flush()

    def run(self) -> None:
        while True:
            try:
                line = sys.stdin.buffer.readline(MAX_INPUT_BYTES + 1)
            except Exception:
                break

            if not line:
                # EOF reached, normal exit
                sys.exit(0)

            if len(line) > MAX_INPUT_BYTES:
                if not line.endswith(b"\n"):
                    while True:
                        chunk = sys.stdin.buffer.readline(MAX_INPUT_BYTES + 1)
                        if not chunk or chunk.endswith(b"\n"):
                            break
                resp = self._make_error(
                    None, -32700, "Parse error: input line exceeds 1 MiB limit"
                )
                self._send_response(resp)
                continue

            resp = self.process_line(line)
            if resp is not None:
                self._send_response(resp)


def parse_args(args: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="MCP stdio JSON-RPC tool server for local game control"
    )
    parser.add_argument(
        "--url",
        default=DEFAULT_URL,
        help=f"Target game HTTP loopback URL (default: {DEFAULT_URL})",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=DEFAULT_TIMEOUT,
        help=f"HTTP request timeout in seconds (default: {DEFAULT_TIMEOUT})",
    )
    return parser.parse_args(args)


def main() -> None:
    parsed = parse_args()
    try:
        base_url = validate_url(parsed.url)
        timeout = validate_timeout(parsed.timeout)
    except Exception as e:
        sys.stderr.write(f"Configuration error: {e}\n")
        sys.exit(2)

    server = GameMCPServer(base_url=base_url, timeout=timeout)
    server.run()


if __name__ == "__main__":
    main()
