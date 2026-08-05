"""Offline content compatibility checks for migrated Minecraft modpacks.

The analyzer intentionally accepts dictionaries or arbitrary objects instead of
depending on the GUI application's ``ModInfo`` class.  It only reports facts
present in mod, resource-pack, and shader-pack metadata.
"""

from __future__ import annotations

import gzip
import json
import os
import re
import struct
import threading
from dataclasses import dataclass, field
from pathlib import Path, PurePosixPath
from typing import Any, Iterable, Mapping, Sequence

try:
    import tomllib
except ImportError:  # Python 3.10 has no TOML parser in the standard library.
    tomllib = None  # type: ignore[assignment]


CONFIRMED = "confirmed"
HEURISTIC = "heuristic"
INCOMPLETE = "incomplete"

DEFAULT_MAX_CONFIG_BYTES = 5 * 1024 * 1024
DEFAULT_MAX_LEVEL_DAT_COMPRESSED_BYTES = 8 * 1024 * 1024
DEFAULT_MAX_LEVEL_DAT_BYTES = 32 * 1024 * 1024
CONFIG_SUFFIXES = {".json", ".toml", ".json5", ".cfg", ".conf", ".properties", ".yaml", ".yml"}


class AnalysisCancelled(RuntimeError):
    """Raised when a compatibility scan is cancelled by the UI."""


@dataclass(frozen=True)
class CompatibilityIssue:
    code: str
    severity: str
    message: str
    confidence: str = CONFIRMED
    scope: str = "general"
    item: str | None = None
    path: str | None = None
    evidence: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {
            "code": self.code,
            "severity": self.severity,
            "message": self.message,
            "confidence": self.confidence,
            "scope": self.scope,
            "item": self.item,
            "path": self.path,
            "evidence": dict(self.evidence),
        }


@dataclass
class CompatibilityReport:
    source_mc: str
    target_mc: str
    source_loader: str
    target_loader: str
    issues: list[CompatibilityIssue] = field(default_factory=list)
    limitations: list[str] = field(default_factory=list)
    stats: dict[str, int] = field(default_factory=dict)

    def add_issue(
        self,
        code: str,
        severity: str,
        message: str,
        *,
        confidence: str = CONFIRMED,
        scope: str = "general",
        item: str | None = None,
        path: str | None = None,
        evidence: Mapping[str, Any] | None = None,
    ) -> None:
        self.issues.append(
            CompatibilityIssue(
                code=code,
                severity=severity,
                message=message,
                confidence=confidence,
                scope=scope,
                item=item,
                path=path,
                evidence=dict(evidence or {}),
            )
        )

    @property
    def has_errors(self) -> bool:
        return any(issue.severity == "error" for issue in self.issues)

    @property
    def counts(self) -> dict[str, int]:
        result = {"error": 0, "warning": 0, "info": 0}
        for issue in self.issues:
            result[issue.severity] = result.get(issue.severity, 0) + 1
        return result

    def to_dict(self) -> dict[str, Any]:
        return {
            "source_mc": self.source_mc,
            "target_mc": self.target_mc,
            "source_loader": self.source_loader,
            "target_loader": self.target_loader,
            "has_errors": self.has_errors,
            "counts": self.counts,
            "stats": dict(self.stats),
            "limitations": list(self.limitations),
            "issues": [issue.to_dict() for issue in self.issues],
        }


@dataclass(frozen=True)
class _Relation:
    kind: str
    reference: str
    source: str
    reference_type: str = "project_id"
    exact_reference: str = ""


def _get(item: Any, *names: str, default: Any = None) -> Any:
    for name in names:
        if isinstance(item, Mapping) and name in item:
            return item[name]
        if hasattr(item, name):
            return getattr(item, name)
    return default


def _as_text(value: Any) -> str:
    return "" if value is None else str(value).strip()


def _normalize_reference(value: Any) -> str:
    return _as_text(value).casefold()


def _normalize_loader(value: str) -> str:
    normalized = re.sub(r"[^a-z0-9]+", "", value.casefold())
    aliases = {
        "fabricloader": "fabric",
        "quiltloader": "quilt",
        "neoforged": "neoforge",
        "neo": "neoforge",
    }
    return aliases.get(normalized, normalized)


def _item_label(mod: Any, index: int) -> str:
    return (
        _as_text(_get(mod, "name"))
        or _as_text(_get(mod, "target_file_name", "file_name"))
        or _as_text(_get(mod, "project_id", "projectId", "mod_id", "id"))
        or f"item #{index + 1}"
    )


def _item_scope(mod: Any) -> str:
    category = _normalize_reference(_get(mod, "category"))
    return {
        "resourcepack": "resourcepack",
        "resourcepacks": "resourcepack",
        "shader": "shaderpack",
        "shaderpack": "shaderpack",
        "shaderpacks": "shaderpack",
    }.get(category, "mod")


def _is_passthrough(mod: Any) -> bool:
    if bool(_get(mod, "passthrough", default=False)):
        return True
    category = _normalize_reference(_get(mod, "category"))
    return bool(category and category not in {
        "mod", "mods", "resourcepack", "resourcepacks", "shader", "shaderpack", "shaderpacks"
    })


def _is_disabled(mod: Any) -> bool:
    value = _get(mod, "disabled", default=False)
    if isinstance(value, str):
        return value.casefold() in {"1", "true", "yes", "disabled"}
    return bool(value)


def _is_excluded(mod: Any) -> bool:
    value = _get(mod, "excluded", default=False)
    if isinstance(value, str):
        return value.casefold() in {"1", "true", "yes", "excluded"}
    return bool(value)


def _has_scoped_environment(mod: Any) -> bool:
    environment = _get(mod, "environment", "env", default={})
    if not isinstance(environment, Mapping) or not environment:
        return False
    client = _normalize_reference(environment.get("client") or "required")
    server = _normalize_reference(environment.get("server") or "required")
    return client != "required" or server != "required"


def _is_unavailable(mod: Any) -> bool:
    status = _normalize_reference(_get(mod, "status"))
    return status in {"not_found", "not-found", "missing", "failed", "unresolved"}


def _identity_references(mod: Any) -> set[str]:
    result: set[str] = set()
    source = _normalize_reference(_get(mod, "source", "platform"))
    for field_names in (
        ("project_id", "projectId", "mod_id", "modId", "id"),
        ("target_version_id", "version_id", "versionId"),
        ("slug",),
        ("cf_slug",),
        ("mr_slug",),
        ("name",),
    ):
        value = _normalize_reference(_get(mod, *field_names))
        if value:
            result.add(value)
            if source:
                result.add(f"{source}:{value}")
    for name_field in ("target_file_name", "file_name"):
        filename = _as_text(_get(mod, name_field))
        if filename:
            basename = PurePosixPath(filename.replace("\\", "/")).name.casefold()
            for reference in (basename, _strip_known_suffixes(basename)):
                result.add(reference)
                if source:
                    result.add(f"{source}:{reference}")
    return {value for value in result if value}


def _strip_known_suffixes(filename: str) -> str:
    lowered = filename.casefold()
    for suffix in (".jar.disabled", ".disabled", ".jar", ".zip"):
        if lowered.endswith(suffix):
            return lowered[: -len(suffix)]
    return lowered


_REFERENCE_KEYS = (
    "project_id", "projectId", "mod_id", "modId", "id",
    "target_version_id", "version_id", "versionId", "file_name", "filename", "slug", "name",
)
_RELATION_TYPE_KEYS = ("dependency_type", "relation_type", "relationType", "type", "kind")


def _reference_type(key: str) -> str:
    if key in {"target_version_id", "version_id", "versionId"}:
        return "version_id"
    if key in {"file_name", "filename"}:
        return "file_name"
    if key == "slug":
        return "slug"
    if key == "name":
        return "name"
    return "project_id"


def _relation_kind(value: Any, default: str | None) -> str | None:
    if isinstance(value, int):
        return {3: "required", 5: "incompatible"}.get(value)
    normalized = re.sub(r"[^a-z0-9]+", "_", _as_text(value).casefold()).strip("_")
    if not normalized:
        return default
    if normalized in {"required", "required_dependency", "depends", "dependency"}:
        return "required"
    if normalized in {"incompatible", "conflict", "conflicts", "breaks"}:
        return "incompatible"
    if normalized in {"optional", "optional_dependency", "embedded", "include", "tool"}:
        return None
    return None


def _relation_from_value(value: Any, default_kind: str, owner_source: str) -> list[_Relation]:
    if value is None or value is False:
        return []
    if isinstance(value, (str, int)):
        exact_reference = _as_text(value)
        reference = _normalize_reference(value)
        return [
            _Relation(default_kind, reference, owner_source, "project_id", exact_reference)
        ] if reference else []
    if isinstance(value, Mapping):
        is_single = any(key in value for key in _REFERENCE_KEYS + _RELATION_TYPE_KEYS)
        if not is_single:
            result: list[_Relation] = []
            for key, specification in value.items():
                if specification is False or specification is None:
                    continue
                if isinstance(specification, Mapping):
                    merged = dict(specification)
                    if not any(ref_key in merged for ref_key in _REFERENCE_KEYS):
                        merged["id"] = key
                    result.extend(_relation_from_value(merged, default_kind, owner_source))
                else:
                    result.append(
                        _Relation(
                            default_kind,
                            _normalize_reference(key),
                            owner_source,
                            "project_id",
                            _as_text(key),
                        )
                    )
            return result

        kind_value = next((value[key] for key in _RELATION_TYPE_KEYS if key in value), None)
        kind = _relation_kind(kind_value, default_kind)
        if not kind:
            return []
        reference_key = next(
            (key for key in _REFERENCE_KEYS if value.get(key) not in (None, "")),
            "",
        )
        exact_reference = _as_text(value[reference_key]) if reference_key else ""
        reference = _normalize_reference(exact_reference)
        source = _normalize_reference(value.get("source") or value.get("platform") or owner_source)
        return [
            _Relation(
                kind,
                reference,
                source,
                _reference_type(reference_key),
                exact_reference,
            )
        ] if reference else []
    if isinstance(value, Sequence) and not isinstance(value, (bytes, bytearray)):
        result = []
        for entry in value:
            result.extend(_relation_from_value(entry, default_kind, owner_source))
        return result
    return []


def _relations_for_mod(mod: Any) -> tuple[list[_Relation], bool]:
    source = _normalize_reference(_get(mod, "source", "platform"))
    relations: list[_Relation] = []
    metadata_present = False
    relation_fields = (
        ("required_dependencies", "required"),
        ("target_dependencies", "required"),
        ("dependencies", "required"),
        ("relations", "required"),
        ("incompatible_with", "incompatible"),
        ("incompatibilities", "incompatible"),
        ("conflicts", "incompatible"),
    )
    explicit_availability = _get(mod, "dependency_metadata_available", "metadata_fetched")
    for field_name, default_kind in relation_fields:
        raw = _get(mod, field_name)
        if raw:
            metadata_present = True
            relations.extend(_relation_from_value(raw, default_kind, source))
    if explicit_availability is not None:
        metadata_present = metadata_present or bool(explicit_availability)
    explicit_incompatible = _get(mod, "incompatible")
    if explicit_incompatible is not None:
        metadata_present = True
        if explicit_incompatible is True:
            relations.append(_Relation("incompatible_self", "", source, "project_id"))
        elif explicit_incompatible not in (False, ""):
            relations.extend(_relation_from_value(explicit_incompatible, "incompatible", source))

    unique: dict[tuple[str, str, str, str], _Relation] = {}
    for relation in relations:
        unique[(relation.kind, relation.reference, relation.source, relation.reference_type)] = relation
    return list(unique.values()), metadata_present


def _project_key(mod: Any) -> str:
    project_id = _normalize_reference(_get(mod, "project_id", "projectId", "mod_id", "modId", "id"))
    if not project_id:
        return ""
    source = _normalize_reference(_get(mod, "source", "platform"))
    return f"{source}:{project_id}" if source else project_id


def _output_path(mod: Any) -> str:
    explicit = _as_text(_get(mod, "target_path", "output_path", "path"))
    if explicit:
        return explicit.replace("\\", "/")
    filename = _as_text(_get(mod, "target_file_name")) or _as_text(_get(mod, "file_name"))
    if not filename:
        return ""
    category = _normalize_reference(_get(mod, "category"))
    directory = {
        "resourcepack": "resourcepacks",
        "resourcepacks": "resourcepacks",
        "shaderpack": "shaderpacks",
        "shader": "shaderpacks",
        "shaderpacks": "shaderpacks",
    }.get(category, "mods")
    normalized_filename = filename.replace(chr(92), "/")
    if _is_disabled(mod) and not normalized_filename.casefold().endswith(".disabled"):
        normalized_filename += ".disabled"
    return f"{directory}/{normalized_filename}"


def _unsafe_archive_path(path: str) -> bool:
    normalized = path.replace("\\", "/")
    pure = PurePosixPath(normalized)
    return (
        not normalized
        or pure.is_absolute()
        or ".." in pure.parts
        or bool(re.match(r"^[a-zA-Z]:", normalized))
        or "\x00" in normalized
    )


def _version_tuple(version: str) -> tuple[int, ...] | None:
    match = re.match(r"^\s*(\d+)\.(\d+)(?:\.(\d+))?", version)
    if not match:
        return None
    return tuple(int(part) if part is not None else 0 for part in match.groups())


def _loader_pattern(loader: str) -> re.Pattern[str] | None:
    normalized = _normalize_loader(loader)
    if not normalized:
        return None
    return re.compile(rf"(?<![a-z0-9]){re.escape(normalized)}(?![a-z0-9])", re.IGNORECASE)


class NBTReadError(ValueError):
    """Raised when a level.dat file is malformed or exceeds safety limits."""


class _NBTReader:
    def __init__(
        self,
        data: bytes,
        *,
        max_depth: int = 64,
        max_nodes: int = 100_000,
        max_collection_items: int = 1_000_000,
    ) -> None:
        self._data = memoryview(data)
        self._position = 0
        self._max_depth = max_depth
        self._remaining_nodes = max_nodes
        self._max_collection_items = max_collection_items

    def _read(self, size: int) -> bytes:
        if size < 0 or self._position + size > len(self._data):
            raise NBTReadError("truncated NBT payload")
        start = self._position
        self._position += size
        return self._data[start : start + size].tobytes()

    def _unpack(self, fmt: str) -> Any:
        size = struct.calcsize(fmt)
        return struct.unpack(fmt, self._read(size))[0]

    def _u8(self) -> int:
        return self._unpack(">B")

    def _u16(self) -> int:
        return self._unpack(">H")

    def _i32(self) -> int:
        return self._unpack(">i")

    def _string(self) -> str:
        size = self._u16()
        try:
            return self._read(size).decode("utf-8")
        except UnicodeDecodeError as exc:
            raise NBTReadError("invalid UTF-8 in NBT name") from exc

    def _touch(self) -> None:
        self._remaining_nodes -= 1
        if self._remaining_nodes < 0:
            raise NBTReadError("NBT node limit exceeded")

    def _check_depth(self, depth: int) -> None:
        if depth > self._max_depth:
            raise NBTReadError("NBT nesting limit exceeded")

    def _collection_length(self) -> int:
        length = self._i32()
        if length < 0 or length > self._max_collection_items:
            raise NBTReadError("invalid or excessive NBT collection length")
        return length

    def read_data_version(self) -> int | None:
        if self._u8() != 10:
            raise NBTReadError("NBT root is not a compound")
        self._string()
        return self._scan_compound((), 0)

    def _scan_compound(self, path: tuple[str, ...], depth: int) -> int | None:
        self._check_depth(depth)
        while True:
            tag_id = self._u8()
            if tag_id == 0:
                return None
            self._touch()
            name = self._string()
            if tag_id == 3 and name == "DataVersion" and path in {(), ("Data",)}:
                return self._i32()
            if tag_id == 10:
                found = self._scan_compound(path + (name,), depth + 1)
                if found is not None:
                    return found
            else:
                self._skip_payload(tag_id, depth + 1)

    def _skip_payload(self, tag_id: int, depth: int) -> None:
        self._check_depth(depth)
        scalar_sizes = {1: 1, 2: 2, 3: 4, 4: 8, 5: 4, 6: 8}
        if tag_id in scalar_sizes:
            self._read(scalar_sizes[tag_id])
        elif tag_id == 7:
            self._read(self._collection_length())
        elif tag_id == 8:
            self._string()
        elif tag_id == 9:
            element_type = self._u8()
            length = self._collection_length()
            if element_type == 0 and length:
                raise NBTReadError("non-empty NBT list has TAG_End element type")
            for _ in range(length):
                self._touch()
                self._skip_payload(element_type, depth + 1)
        elif tag_id == 10:
            while True:
                child_type = self._u8()
                if child_type == 0:
                    break
                self._touch()
                self._string()
                self._skip_payload(child_type, depth + 1)
        elif tag_id == 11:
            self._read(self._collection_length() * 4)
        elif tag_id == 12:
            self._read(self._collection_length() * 8)
        else:
            raise NBTReadError(f"unknown NBT tag type {tag_id}")


def read_level_dat_data_version(
    path: str | os.PathLike[str],
    *,
    max_compressed_bytes: int = DEFAULT_MAX_LEVEL_DAT_COMPRESSED_BYTES,
    max_decompressed_bytes: int = DEFAULT_MAX_LEVEL_DAT_BYTES,
) -> int | None:
    """Read ``Data/DataVersion`` from a gzip-compressed level.dat safely."""

    level_path = Path(path)
    if level_path.is_symlink():
        raise NBTReadError("symbolic level.dat paths are not read")
    try:
        compressed_size = level_path.stat().st_size
    except OSError as exc:
        raise NBTReadError(f"cannot stat level.dat: {exc}") from exc
    if compressed_size > max_compressed_bytes:
        raise NBTReadError("compressed level.dat size limit exceeded")
    try:
        with gzip.open(level_path, "rb") as stream:
            data = stream.read(max_decompressed_bytes + 1)
    except (OSError, EOFError) as exc:
        raise NBTReadError(f"cannot decompress level.dat: {exc}") from exc
    if len(data) > max_decompressed_bytes:
        raise NBTReadError("decompressed level.dat size limit exceeded")
    try:
        return _NBTReader(data).read_data_version()
    except (struct.error, IndexError) as exc:
        raise NBTReadError("malformed level.dat NBT") from exc


class CompatibilityAnalyzer:
    def __init__(
        self,
        *,
        max_config_bytes: int = DEFAULT_MAX_CONFIG_BYTES,
        max_level_dat_compressed_bytes: int = DEFAULT_MAX_LEVEL_DAT_COMPRESSED_BYTES,
        max_level_dat_bytes: int = DEFAULT_MAX_LEVEL_DAT_BYTES,
        cancel_event: threading.Event | None = None,
    ) -> None:
        self.max_config_bytes = max_config_bytes
        self.max_level_dat_compressed_bytes = max_level_dat_compressed_bytes
        self.max_level_dat_bytes = max_level_dat_bytes
        self.cancel_event = cancel_event

    def _check_cancelled(self) -> None:
        if self.cancel_event is not None and self.cancel_event.is_set():
            raise AnalysisCancelled("compatibility analysis cancelled")

    def analyze(
        self,
        mods: Iterable[Any],
        overrides_dir: str | os.PathLike[str] | None,
        source_mc: str,
        target_mc: str,
        source_loader: str = "",
        target_loader: str = "",
        target_format: str = "",
        passthrough_paths: Iterable[str] | None = None,
    ) -> CompatibilityReport:
        self._check_cancelled()
        all_mods = list(mods)
        mod_entries = [
            (item_index, mod) for item_index, mod in enumerate(all_mods)
            if not _is_excluded(mod) and not _is_passthrough(mod)
        ]
        report = CompatibilityReport(
            source_mc=source_mc,
            target_mc=target_mc,
            source_loader=source_loader,
            target_loader=target_loader,
            stats={
                "content_items_checked": len(mod_entries),
                "mods_checked": len(mod_entries),
                "items_excluded": len(all_mods) - len(mod_entries),
                "dependency_relations_checked": 0,
            },
        )
        self._check_mods(
            mod_entries,
            report,
            target_format,
            passthrough_paths,
        )
        self._check_cancelled()
        report.limitations.extend(
            [
                "Static analysis cannot inspect mod bytecode, mixins, registries, datapacks, or runtime-only conflicts.",
                "Only recognized direct required/incompatible relations are checked; optional or unknown relation types, recursive dependencies, and cross-platform identity mapping are not verified.",
            ]
        )
        return report

    def _check_mods(
        self,
        mod_entries: list[tuple[int, Any]],
        report: CompatibilityReport,
        target_format: str = "",
        passthrough_paths: Iterable[str] | None = None,
    ) -> None:
        protected_paths = {
            str(PurePosixPath(str(path).replace("\\", "/"))).casefold()
            for path in (passthrough_paths or [])
            if str(path).strip()
        }
        active_references: set[str] = set()
        labels: dict[int, str] = {}
        for item_index, mod in mod_entries:
            self._check_cancelled()
            labels[item_index] = _item_label(mod, item_index)
            if not _is_disabled(mod) and not _is_unavailable(mod):
                active_references.update(_identity_references(mod))

        project_groups: dict[str, list[tuple[int, str]]] = {}
        output_groups: dict[str, list[tuple[int, str]]] = {}
        metadata_items = 0
        metadata_checkable_items = 0

        for item_index, mod in mod_entries:
            self._check_cancelled()
            label = labels[item_index]
            item_scope = _item_scope(mod)
            if _is_unavailable(mod):
                required = _get(mod, "required", default=True)
                report.add_issue(
                    "item_not_found",
                    "warning" if _is_disabled(mod) or required is False else "error",
                    "No target artifact was found for this item.",
                    scope=item_scope,
                    item=label,
                    evidence={"status": _as_text(_get(mod, "status")), "item_index": item_index},
                )
            source = _normalize_reference(_get(mod, "source", "platform"))
            if (
                target_format.casefold() == "modrinth"
                and source == "curseforge"
                and not _is_disabled(mod)
                and not _is_unavailable(mod)
                and not _as_text(_get(mod, "target_download_url", "download_url"))
            ):
                report.add_issue(
                    "required_embedded_download_unavailable",
                    "error",
                    "A CurseForge fallback item must be embedded in a Modrinth pack, but no download URL is available.",
                    scope="output",
                    item=label,
                    evidence={"item_index": item_index},
                )
            if (
                target_format.casefold() == "modrinth"
                and source == "curseforge"
                and not _is_disabled(mod)
                and not _is_unavailable(mod)
                and _has_scoped_environment(mod)
            ):
                report.add_issue(
                    "required_embedded_scope_unsupported",
                    "error",
                    "A CurseForge fallback item must be embedded, but its Modrinth env scope cannot be preserved safely.",
                    scope="output",
                    item=label,
                    evidence={"item_index": item_index},
                )

            project_key = _project_key(mod)
            if project_key:
                project_groups.setdefault(project_key, []).append((item_index, label))

            output_path = _output_path(mod)
            if output_path:
                if _unsafe_archive_path(output_path):
                    report.add_issue(
                        "unsafe_output_path",
                        "error",
                        "The target archive path is absolute or escapes its package directory.",
                        scope="output",
                        item=label,
                        path=output_path,
                        evidence={"item_index": item_index},
                    )
                else:
                    normalized = str(PurePosixPath(output_path)).casefold()
                    if normalized in protected_paths:
                        report.add_issue(
                            "override_output_collision",
                            "error",
                            "A migrated content item would overwrite an existing passthrough overrides file.",
                            scope="output",
                            item=label,
                            path=output_path,
                            evidence={"item_index": item_index},
                        )
                    output_groups.setdefault(normalized, []).append((item_index, label))

            relations, metadata_present = _relations_for_mod(mod)
            should_check_relations = (
                item_scope == "mod"
                and not _is_disabled(mod)
                and not _is_unavailable(mod)
            )
            if should_check_relations:
                metadata_checkable_items += 1
                if metadata_present:
                    metadata_items += 1
                report.stats["dependency_relations_checked"] += len(relations)
            else:
                relations = []
            for relation in relations:
                self._check_cancelled()
                if relation.kind == "incompatible_self":
                    report.add_issue(
                        "explicitly_incompatible_item",
                        "error",
                        "The supplied metadata explicitly marks this item as incompatible.",
                        scope=item_scope,
                        item=label,
                        evidence={"item_index": item_index},
                    )
                    continue
                lookup = relation.reference
                qualified = f"{relation.source}:{lookup}" if relation.source else ""
                present = qualified in active_references if relation.source else lookup in active_references
                if relation.kind == "required" and not present:
                    report.add_issue(
                        "missing_required_dependency",
                        "warning",
                        f"Required dependency '{relation.reference}' is not present as an active resolved item.",
                        scope="dependency",
                        item=label,
                        evidence={
                            "dependency": relation.reference,
                            "dependency_exact": relation.exact_reference or relation.reference,
                            "dependency_reference_type": relation.reference_type,
                            "source": relation.source,
                            "item_index": item_index,
                        },
                    )
                elif relation.kind == "incompatible" and present:
                    report.add_issue(
                        "explicit_incompatibility",
                        "error",
                        f"Explicitly incompatible item '{relation.reference}' is present.",
                        scope="dependency",
                        item=label,
                        evidence={
                            "incompatible_with": relation.reference,
                            "incompatible_with_exact": relation.exact_reference or relation.reference,
                            "incompatible_reference_type": relation.reference_type,
                            "source": relation.source,
                            "item_index": item_index,
                        },
                    )

        for project_key, group in project_groups.items():
            self._check_cancelled()
            if len(group) > 1:
                report.add_issue(
                    "duplicate_project",
                    "warning",
                    "The same platform project appears more than once.",
                    scope="content",
                    evidence={
                        "project": project_key,
                        "items": [label for _, label in group],
                        "item_indexes": [item_index for item_index, _ in group],
                    },
                )
        for output_path, group in output_groups.items():
            self._check_cancelled()
            if len(group) > 1:
                report.add_issue(
                    "duplicate_output_path",
                    "error",
                    "Multiple items resolve to the same case-insensitive archive path.",
                    scope="output",
                    path=output_path,
                    evidence={
                        "items": [label for _, label in group],
                        "item_indexes": [item_index for item_index, _ in group],
                    },
                )

        if metadata_items < metadata_checkable_items:
            report.limitations.append(
                f"Dependency/conflict metadata was absent for {metadata_checkable_items - metadata_items} "
                f"of {metadata_checkable_items} active resolved items; "
                "their required dependencies and explicit conflicts cannot be confirmed statically."
            )


    def _scan_overrides(
        self,
        overrides_dir: str | os.PathLike[str] | None,
        report: CompatibilityReport,
    ) -> None:
        if not overrides_dir:
            report.limitations.append("No overrides directory was supplied; configuration files and saves were not scanned.")
            return
        root = Path(overrides_dir)
        if not root.is_dir():
            report.add_issue(
                "overrides_unavailable",
                "warning",
                "The overrides directory does not exist or is not a directory.",
                confidence=INCOMPLETE,
                scope="overrides",
                path=str(root),
            )
            return

        config_files: list[Path] = []
        level_files: list[Path] = []
        skipped_symlinks = 0
        for current, directories, filenames in os.walk(root, followlinks=False):
            self._check_cancelled()
            current_path = Path(current)
            kept_directories = []
            for directory in directories:
                candidate = current_path / directory
                if candidate.is_symlink():
                    skipped_symlinks += 1
                else:
                    kept_directories.append(directory)
            directories[:] = kept_directories
            for filename in filenames:
                self._check_cancelled()
                candidate = current_path / filename
                if candidate.is_symlink():
                    skipped_symlinks += 1
                    continue
                suffix = candidate.suffix.casefold()
                if suffix in CONFIG_SUFFIXES:
                    config_files.append(candidate)
                relative_parts = [part.casefold() for part in candidate.relative_to(root).parts]
                if filename.casefold() == "level.dat" and "saves" in relative_parts[:-1]:
                    level_files.append(candidate)

        if skipped_symlinks:
            report.limitations.append(f"Skipped {skipped_symlinks} symbolic link(s) under overrides for safety.")
        self._check_configs(root, config_files, report)
        self._check_worlds(root, level_files, report)

    def _check_configs(self, root: Path, config_files: list[Path], report: CompatibilityReport) -> None:
        version_mentions: list[str] = []
        loader_mentions: list[str] = []
        syntax_unchecked: set[str] = set()
        source_loader = _normalize_loader(report.source_loader)
        target_loader = _normalize_loader(report.target_loader)
        loader_changed = bool(source_loader and target_loader and source_loader != target_loader)
        loader_pattern = _loader_pattern(report.source_loader) if loader_changed else None

        for config_path in config_files:
            self._check_cancelled()
            relative = config_path.relative_to(root).as_posix()
            try:
                size = config_path.stat().st_size
                if size > self.max_config_bytes:
                    report.add_issue(
                        "config_too_large",
                        "warning",
                        "Configuration file exceeded the analysis size limit and was not parsed.",
                        confidence=INCOMPLETE,
                        scope="config",
                        path=relative,
                        evidence={"bytes": size, "limit": self.max_config_bytes},
                    )
                    continue
                with config_path.open("rb") as stream:
                    raw = stream.read(self.max_config_bytes + 1)
                if len(raw) > self.max_config_bytes:
                    report.add_issue(
                        "config_too_large",
                        "warning",
                        "Configuration file exceeded the analysis size limit and was not parsed.",
                        confidence=INCOMPLETE,
                        scope="config",
                        path=relative,
                        evidence={"bytes": size, "limit": self.max_config_bytes},
                    )
                    continue
                text = raw.decode("utf-8-sig")
            except (OSError, UnicodeDecodeError) as exc:
                report.add_issue(
                    "config_unreadable",
                    "warning",
                    f"Configuration file could not be read as UTF-8: {exc}",
                    confidence=INCOMPLETE,
                    scope="config",
                    path=relative,
                )
                continue

            report.stats["config_files_checked"] += 1
            suffix = config_path.suffix.casefold()
            if suffix == ".json":
                try:
                    json.loads(text)
                except json.JSONDecodeError as exc:
                    report.add_issue(
                        "invalid_json_config",
                        "error",
                        f"Invalid JSON configuration at line {exc.lineno}, column {exc.colno}: {exc.msg}",
                        scope="config",
                        path=relative,
                    )
            elif suffix == ".toml":
                if tomllib is None:
                    report.add_issue(
                        "toml_parser_unavailable",
                        "info",
                        "TOML syntax was not checked because this Python has no standard-library tomllib module.",
                        confidence=INCOMPLETE,
                        scope="config",
                        path=relative,
                    )
                else:
                    try:
                        tomllib.loads(text)
                    except tomllib.TOMLDecodeError as exc:
                        report.add_issue(
                            "invalid_toml_config",
                            "error",
                            f"Invalid TOML configuration: {exc}",
                            scope="config",
                            path=relative,
                        )
            else:
                syntax_unchecked.add(suffix)

            if report.source_mc and report.source_mc != report.target_mc and report.source_mc in text:
                version_mentions.append(relative)
            if loader_pattern:
                if loader_pattern.search(relative) or loader_pattern.search(text):
                    loader_mentions.append(relative)

        checked = report.stats["config_files_checked"]
        if checked and report.source_mc and report.target_mc and report.source_mc != report.target_mc:
            report.add_issue(
                "config_version_migration_risk",
                "warning",
                "Configuration schemas or values may have changed between Minecraft versions.",
                confidence=HEURISTIC,
                scope="config",
                evidence={"files_checked": checked, "source_mc": report.source_mc, "target_mc": report.target_mc},
            )
        if checked and loader_changed:
            report.add_issue(
                "config_loader_migration_risk",
                "warning",
                "Configuration keys and defaults may differ after changing mod loaders.",
                confidence=HEURISTIC,
                scope="config",
                evidence={"files_checked": checked, "source_loader": report.source_loader, "target_loader": report.target_loader},
            )
        if version_mentions:
            report.add_issue(
                "config_mentions_source_version",
                "warning",
                "Configuration text still contains the source Minecraft version.",
                confidence=HEURISTIC,
                scope="config",
                evidence={"count": len(version_mentions), "paths": version_mentions[:20]},
            )
        if loader_mentions:
            report.add_issue(
                "config_mentions_source_loader",
                "warning",
                "Configuration path or text still contains the source loader name.",
                confidence=HEURISTIC,
                scope="config",
                evidence={"count": len(loader_mentions), "paths": loader_mentions[:20]},
            )
        if syntax_unchecked:
            report.limitations.append(
                "Configuration syntax was not parsed for these text formats: "
                + ", ".join(sorted(syntax_unchecked))
                + "; they were included only in migration heuristics."
            )

    def _check_worlds(self, root: Path, level_files: list[Path], report: CompatibilityReport) -> None:
        source_version = _version_tuple(report.source_mc)
        target_version = _version_tuple(report.target_mc)
        source_loader = _normalize_loader(report.source_loader)
        target_loader = _normalize_loader(report.target_loader)
        loader_changed = bool(source_loader and target_loader and source_loader != target_loader)

        for level_path in level_files:
            self._check_cancelled()
            relative = level_path.relative_to(root).as_posix()
            data_version: int | None = None
            try:
                data_version = read_level_dat_data_version(
                    level_path,
                    max_compressed_bytes=self.max_level_dat_compressed_bytes,
                    max_decompressed_bytes=self.max_level_dat_bytes,
                )
            except NBTReadError as exc:
                report.add_issue(
                    "level_dat_unreadable",
                    "warning",
                    f"The world DataVersion could not be read safely: {exc}",
                    confidence=INCOMPLETE,
                    scope="world",
                    path=relative,
                )
            report.stats["world_saves_checked"] += 1
            report.add_issue(
                "world_save_detected",
                "info",
                "A bundled world save was detected.",
                scope="world",
                path=relative,
                evidence={"data_version": data_version},
            )

            if source_version is None or target_version is None:
                report.add_issue(
                    "world_version_order_unknown",
                    "warning",
                    "Minecraft version order could not be determined; world upgrade/downgrade risk is unknown.",
                    confidence=INCOMPLETE,
                    scope="world",
                    path=relative,
                    evidence={"source_mc": report.source_mc, "target_mc": report.target_mc},
                )
            elif target_version < source_version:
                report.add_issue(
                    "world_downgrade_risk",
                    "error",
                    "Opening a world in an older Minecraft version is generally unsupported and can corrupt or remove data.",
                    confidence=HEURISTIC,
                    scope="world",
                    path=relative,
                    evidence={"source_mc": report.source_mc, "target_mc": report.target_mc, "data_version": data_version},
                )
            elif target_version > source_version:
                report.add_issue(
                    "world_upgrade_risk",
                    "warning",
                    "World upgrades are one-way in practice; back up the save and test it separately.",
                    confidence=HEURISTIC,
                    scope="world",
                    path=relative,
                    evidence={"source_mc": report.source_mc, "target_mc": report.target_mc, "data_version": data_version},
                )

            if loader_changed:
                report.add_issue(
                    "world_loader_change_risk",
                    "warning",
                    "Changing loaders can orphan modded blocks, entities, dimensions, or saved registry entries.",
                    confidence=HEURISTIC,
                    scope="world",
                    path=relative,
                    evidence={"source_loader": report.source_loader, "target_loader": report.target_loader},
                )


def analyze_compatibility(
    mods: Iterable[Any],
    overrides_dir: str | os.PathLike[str] | None,
    source_mc: str,
    target_mc: str,
    source_loader: str = "",
    target_loader: str = "",
    target_format: str = "",
    passthrough_paths: Iterable[str] | None = None,
    **analyzer_options: Any,
) -> CompatibilityReport:
    """Convenience wrapper around :class:`CompatibilityAnalyzer`."""

    return CompatibilityAnalyzer(**analyzer_options).analyze(
        mods,
        overrides_dir,
        source_mc,
        target_mc,
        source_loader,
        target_loader,
        target_format,
        passthrough_paths,
    )


__all__ = [
    "AnalysisCancelled",
    "CompatibilityAnalyzer",
    "CompatibilityIssue",
    "CompatibilityReport",
    "NBTReadError",
    "analyze_compatibility",
    "read_level_dat_data_version",
]
