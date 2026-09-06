"""Inspect the local Unity MCP server, or run an explicitly targeted M0 smoke check.

Run with .local/bridge-venv/Scripts/python.exe. The default action only initializes
MCP, lists tools/resources and reads the actual instance list. Smoke must receive
the full name@hash returned by inspect and the expected saved scene path. The
returned projectRoot must equal this workspace's GoldCamp.Unity before any write.
It only creates/modifies the empty root object M0_BridgeProbe and saves that scene.
"""

from __future__ import annotations

import argparse
import asyncio
from datetime import datetime, timedelta, timezone
import importlib.metadata
import json
import math
import os
from pathlib import Path
import re
import sys
from typing import Any
import uuid

from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client


ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "GoldCamp.Unity"
EDITOR_VERSION = "6000.3.23f1"
BRIDGE_VERSION = "10.2.0"
URL = "http://127.0.0.1:8080/mcp"
PROBE_NAME = "M0_BridgeProbe"
SECRET_KEY = re.compile(r"(?i)password|secret|authorization|cookie|api[_-]?key|token|session[_-]?id")


def sanitize(value: Any) -> Any:
    if isinstance(value, dict):
        return {key: "[redacted]" if SECRET_KEY.search(str(key)) else sanitize(item)
                for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [sanitize(item) for item in value]
    if isinstance(value, str):
        text = re.sub(r"(?i)\bBearer\s+[^\s,;\"]+", "Bearer [redacted]", value)
        return re.sub(r"(?i)((?:password|secret|api[_-]?key|access[_-]?token|authorization|cookie)\s*[:=]\s*)[^\s,;\"]+",
                      r"\1[redacted]", text)
    return value


def payload(response: Any) -> dict[str, Any]:
    structured = getattr(response, "structuredContent", None)
    if isinstance(structured, dict):
        result = structured
    else:
        contents = getattr(response, "content", None) or getattr(response, "contents", [])
        texts = [item.text for item in contents if isinstance(getattr(item, "text", None), str)]
        if len(texts) != 1:
            raise RuntimeError("Expected exactly one JSON response block; refusing an ambiguous response.")
        result = json.loads(texts[0])
    if not isinstance(result, dict):
        raise RuntimeError("Expected a JSON object from the Unity bridge.")
    if getattr(response, "isError", False) or result.get("success") is not True:
        raise RuntimeError("Unity bridge operation failed: " + json.dumps(sanitize(result), ensure_ascii=False))
    return result


def require_data(result: dict[str, Any]) -> dict[str, Any]:
    data = result.get("data")
    if not isinstance(data, dict):
        raise RuntimeError("Expected object data in the bridge response.")
    return data


def validate_instance(instances: list[dict[str, Any]], requested: str) -> dict[str, Any]:
    matches = [item for item in instances if item.get("id") == requested]
    if len(matches) != 1:
        raise RuntimeError("The requested full instance ID is not uniquely present in the current server response.")
    item = matches[0]
    project_name = item.get("name")
    project_hash = item.get("hash")
    if not isinstance(project_name, str) or not project_name or not isinstance(project_hash, str) or not project_hash:
        raise RuntimeError("The returned instance must contain its actual nonempty name and hash.")
    if requested != f"{project_name}@{project_hash}":
        raise RuntimeError("Use the exact name@hash returned by inspect; prefixes/defaults are not allowed.")
    return item


def check_empty_probe(data: dict[str, Any], check_position: bool = False) -> None:
    if data.get("name") != PROBE_NAME or data.get("parent") is not None or data.get("children") != []:
        raise RuntimeError("Probe name/parent/children are unexpected; refusing to change an unrelated object.")
    if data.get("componentTypes") != ["Transform"]:
        raise RuntimeError("The probe is not an empty GameObject with only Transform; refusing to alter it.")
    if check_position:
        position = data.get("transform", {}).get("position", {})
        for axis, expected in {"x": 0.0, "y": -2.0, "z": 0.0}.items():
            observed = position.get(axis)
            if not isinstance(observed, (int, float)) or not math.isclose(observed, expected, abs_tol=0.0001):
                raise RuntimeError("The probe's observed world position is not (0, -2, 0).")


class Probe:
    def __init__(self, report: dict[str, Any], instance: str | None):
        self.report = report
        self.instance = instance
        self.session: ClientSession | None = None

    async def resource(self, uri: str) -> dict[str, Any]:
        assert self.session is not None
        response = await self.session.read_resource(uri)
        result = payload(response)
        self.report["operations"].append({"resource": uri, "result": sanitize(result)})
        return result

    async def tool(self, tool_name: str, **arguments: Any) -> dict[str, Any]:
        assert self.session is not None and self.instance is not None
        # Version 10.2 middleware consumes unity_instance before schema validation.
        # Resource routing also uses the explicitly selected session instance.
        arguments["unity_instance"] = self.instance
        response = await self.session.call_tool(tool_name, arguments, read_timeout_seconds=timedelta(seconds=30))
        result = payload(response)
        self.report["operations"].append({"tool": tool_name, "arguments": sanitize(arguments), "result": sanitize(result)})
        return result

    async def find_probe(self) -> list[dict[str, Any]]:
        data = require_data(await self.tool("find_gameobjects", search_term=PROBE_NAME,
                                            search_method="by_name", include_inactive=True, page_size=100))
        ids = data.get("instanceIDs")
        if not isinstance(ids, list) or data.get("hasMore") is not False or data.get("totalCount") != len(ids):
            raise RuntimeError("Probe search is incomplete or has an unexpected response shape; no guess is allowed.")
        matches = []
        for object_id in ids:
            if not isinstance(object_id, int):
                raise RuntimeError("Unexpected GameObject instance ID type.")
            detail = require_data(await self.resource(f"mcpforunity://scene/gameobject/{object_id}"))
            if detail.get("name") == PROBE_NAME:
                matches.append(detail)
        if len(matches) > 1:
            raise RuntimeError("Multiple M0_BridgeProbe objects already exist; refusing to create or delete duplicates.")
        return matches

    async def smoke(self, expected_scene: str) -> None:
        assert self.instance is not None
        selection = require_data(await self.tool("set_active_instance", instance=self.instance))
        if selection.get("instance") != self.instance:
            raise RuntimeError("Server did not select the exact requested instance.")
        project = require_data(await self.resource("mcpforunity://project/info"))
        observed_root = project.get("projectRoot")
        if not isinstance(observed_root, str) or os.path.normcase(os.path.normpath(observed_root)) != os.path.normcase(str(PROJECT)):
            raise RuntimeError("Connected project's actual root is not this workspace's GoldCamp.Unity.")
        if project.get("unityVersion") != EDITOR_VERSION:
            raise RuntimeError("Connected editor does not match the pinned version.")

        state = require_data(await self.resource("mcpforunity://editor/state"))
        play = (state.get("editor") or {}).get("play_mode") or {}
        if play.get("is_playing") is not False or play.get("is_changing") is not False:
            raise RuntimeError("Editor must be observed stopped and outside a play-mode transition.")
        compilation = state.get("compilation") or {}
        if compilation.get("is_compiling") is not False or compilation.get("is_domain_reload_pending") is not False:
            raise RuntimeError("Editor must be observed idle after compilation/domain reload.")

        active = require_data(await self.tool("manage_scene", action="get_active"))
        if active.get("path") != expected_scene or active.get("isLoaded") is not True or active.get("isDirty") is not False:
            raise RuntimeError("The expected saved M0 scene must be active, loaded and free of pre-existing unsaved changes.")
        loaded = require_data(await self.tool("manage_scene", action="get_loaded_scenes")).get("scenes")
        if not isinstance(loaded, list) or len(loaded) != 1 or loaded[0].get("path") != expected_scene:
            raise RuntimeError("Smoke requires only the expected M0 scene to be loaded.")
        await self.tool("manage_scene", action="get_hierarchy", page_size=100, max_nodes=100, include_transform=True)
        await self.tool("read_console", action="get", types=["all"], count=30, format="json", include_stacktrace=False)

        matches = await self.find_probe()
        if matches:
            check_empty_probe(matches[0])
            await self.tool("manage_gameobject", action="modify", target=str(matches[0]["instanceID"]),
                            search_method="by_id", position=[0.0, -2.0, 0.0])
            self.report["probe_change"] = "modified_existing_empty_probe"
        else:
            await self.tool("manage_gameobject", action="create", name=PROBE_NAME, position=[0.0, -2.0, 0.0])
            self.report["probe_change"] = "created_empty_probe"
        matches = await self.find_probe()
        if len(matches) != 1:
            raise RuntimeError("Mutation did not leave exactly one probe.")
        check_empty_probe(matches[0], check_position=True)
        active = require_data(await self.tool("manage_scene", action="get_active"))
        if active.get("path") != expected_scene:
            raise RuntimeError("Active scene changed during smoke; refusing to save.")
        saved = require_data(await self.tool("manage_scene", action="save"))
        if saved.get("path") != expected_scene:
            raise RuntimeError("The scene save response did not confirm the expected path.")
        final_scene = require_data(await self.tool("manage_scene", action="get_active"))
        if final_scene.get("path") != expected_scene or final_scene.get("isDirty") is not False:
            raise RuntimeError("The scene was not observed saved and clean after smoke.")
        final_probe = require_data(await self.resource(f"mcpforunity://scene/gameobject/{matches[0]['instanceID']}"))
        check_empty_probe(final_probe, check_position=True)
        await self.tool("read_console", action="get", types=["all"], count=30, format="json", include_stacktrace=False)
        self.report["smoke_verified"] = True

    async def run(self, action: str, scene_path: str | None) -> None:
        async with streamablehttp_client(URL, timeout=15, sse_read_timeout=35) as (read, write, _):
            async with ClientSession(read, write, read_timeout_seconds=timedelta(seconds=30)) as session:
                self.session = session
                initialized = await session.initialize()
                self.report["initialize_server_info"] = sanitize(initialized.serverInfo.model_dump(mode="json"))
                tools = await session.list_tools()
                resources = await session.list_resources()
                self.report["tools"] = [tool.name for tool in tools.tools]
                self.report["resources"] = [{"name": resource.name, "uri": str(resource.uri)} for resource in resources.resources]
                instance_resources = [resource for resource in resources.resources
                                      if resource.name == "unity_instances" and str(resource.uri) == "mcpforunity://instances"]
                if len(instance_resources) != 1:
                    raise RuntimeError("The advertised unity_instances resource was not found uniquely.")
                discovered = await self.resource(str(instance_resources[0].uri))
                instances = discovered.get("instances")
                if not isinstance(instances, list):
                    raise RuntimeError("Instance discovery did not return an instances list.")
                self.report["instance_count"] = len(instances)
                self.report["discovered_instance_ids"] = [item["id"] for item in instances
                                                          if item.get("id") and item.get("hash")]
                if action == "smoke":
                    assert self.instance is not None and scene_path is not None
                    self.report["selected_instance"] = sanitize(validate_instance(instances, self.instance))
                    required_tools = {"set_active_instance", "manage_scene", "find_gameobjects", "manage_gameobject", "read_console"}
                    if not required_tools.issubset(set(self.report["tools"])):
                        raise RuntimeError("Server did not advertise every required smoke tool.")
                    await self.smoke(scene_path)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--action", choices=("inspect", "instances", "smoke"), default="inspect")
    parser.add_argument("--unity-instance", help="Exact name@hash from the current instances response; never a prefix/default. Actual projectRoot must match GoldCamp.Unity.")
    parser.add_argument("--scene-path", help="For smoke, the already saved active scene path, e.g. Assets/GoldCamp/Scenes/M0_Graybox.unity.")
    args = parser.parse_args()
    if args.action == "smoke":
        if not args.unity_instance or not args.scene_path:
            parser.error("smoke requires --unity-instance and --scene-path after the instance and scene are ready")
        if "@" not in args.unity_instance or any(not part for part in args.unity_instance.rsplit("@", 1)):
            parser.error("Only the full actual name@hash instance ID is allowed")
        if args.scene_path != "Assets/GoldCamp/Scenes/M0_Graybox.unity":
            parser.error("This M0 probe only permits Assets/GoldCamp/Scenes/M0_Graybox.unity")
    elif args.unity_instance or args.scene_path:
        parser.error("inspect/instances do not select or change an editor; targeting arguments are smoke-only")

    report: dict[str, Any] = {"action": args.action, "started_utc": datetime.now(timezone.utc).isoformat(),
                              "url": URL, "smoke_verified": False, "operations": []}
    log_dir = ROOT / "Builds" / "Logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    report_path = log_dir / f"unity-bridge-probe-{stamp}-{args.action}-{uuid.uuid4().hex[:8]}.json"
    exit_code = 0
    try:
        installed = importlib.metadata.version("mcpforunityserver")
        report["bridge_distribution_version"] = installed
        report["mcp_sdk_version"] = importlib.metadata.version("mcp")
        if installed != BRIDGE_VERSION:
            raise RuntimeError(f"Expected mcpforunityserver {BRIDGE_VERSION}; found {installed}.")
        asyncio.run(asyncio.wait_for(Probe(report, args.unity_instance).run(args.action, args.scene_path), timeout=180))
        report["success"] = True
    except Exception as error:
        report["success"] = False
        report["error"] = sanitize(describe_error(error))
        exit_code = 1
    finally:
        report["finished_utc"] = datetime.now(timezone.utc).isoformat()
        report_path.write_text(json.dumps(sanitize(report), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    summary = {key: report.get(key) for key in ("action", "success", "instance_count", "discovered_instance_ids", "smoke_verified", "error")}
    summary["report_path"] = str(report_path)
    print(json.dumps(sanitize(summary), ensure_ascii=False, indent=2))
    return exit_code


def describe_error(error: BaseException) -> str:
    children = getattr(error, "exceptions", None)
    if children:
        return " | ".join(describe_error(child) for child in children)
    return f"{type(error).__name__}: {error}"


if __name__ == "__main__":
    sys.exit(main())
