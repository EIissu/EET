"""The runtime registry: how to build and how to invoke each implementation.

Adding a sixth language means adding one :class:`Runtime` here. Nothing else in the
tooling knows the difference between them.
"""

from __future__ import annotations

import os
import shutil
import subprocess
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, List, Optional, Sequence

ROOT = Path(__file__).resolve().parent.parent
BUILD = ROOT / "build"
IS_WINDOWS = os.name == "nt"
EXE = ".exe" if IS_WINDOWS else ""


def _which(*names: str) -> Optional[str]:
    for name in names:
        found = shutil.which(name)
        if found:
            return found
    return None


def java_home() -> Optional[Path]:
    """Locate a JDK: ``EET_JAVA_HOME``, then ``JAVA_HOME``, then whatever is on PATH."""
    for var in ("EET_JAVA_HOME", "JAVA_HOME"):
        value = os.environ.get(var)
        if value and (Path(value) / "bin" / f"javac{EXE}").exists():
            return Path(value)
    javac = _which("javac")
    if javac:
        return Path(javac).resolve().parent.parent
    return None


def _java_tool(name: str) -> Optional[str]:
    home = java_home()
    if home is None:
        return None
    return str(home / "bin" / f"{name}{EXE}")


@dataclass
class Runtime:
    """One implementation of the spec, plus the commands that build and run it."""

    key: str
    label: str
    language: str
    #: Directory holding the implementation, relative to the repository root.
    source: str
    #: Shell-free commands to build it, run in order from the repository root.
    build: Callable[[], List[Sequence[str]]]
    #: Given a ``.eetb`` path, the argv that runs it.
    run: Callable[[Path], List[str]]
    #: Why this runtime cannot be used right now, or None when it can.
    unavailable: Callable[[], Optional[str]] = field(default=lambda: None)
    #: Set for runtimes that are a second build of another one's source.
    variant_of: Optional[str] = None

    def available(self) -> bool:
        return self.unavailable() is None


# --- Python: the reference -------------------------------------------------------------


def _python_run(program: Path) -> List[str]:
    import sys

    return [sys.executable, "-m", "eetvm", "run", str(program)]


PYTHON = Runtime(
    key="python",
    label="Python",
    language="Python 3.9+",
    source="runtimes/python",
    build=lambda: [],
    run=_python_run,
    unavailable=lambda: None,
)


# --- Java ------------------------------------------------------------------------------


def _java_build() -> List[Sequence[str]]:
    javac = _java_tool("javac")
    assert javac is not None
    sources = sorted(str(p) for p in (ROOT / "runtimes/java/src").rglob("*.java"))
    out = BUILD / "java"
    return [
        [javac, "-encoding", "UTF-8", "--release", "17", "-d", str(out), *sources],
    ]


def _java_run(program: Path) -> List[str]:
    java = _java_tool("java")
    assert java is not None
    return [java, "-cp", str(BUILD / "java"), "eet.Main", "run", str(program)]


JAVA = Runtime(
    key="java",
    label="Java",
    language="Java 17+",
    source="runtimes/java",
    build=_java_build,
    run=_java_run,
    unavailable=lambda: None
    if java_home() is not None
    else "no JDK found (set JAVA_HOME or EET_JAVA_HOME)",
)


# --- C# on .NET ------------------------------------------------------------------------

_CSPROJ = "runtimes/dotnet/src/Eet.Cli/Eet.Cli.csproj"

# Not "dotnet". The SDK forwards `-o <path>` to MSBuild as a bare `PublishDir=<path>`
# token with no `--property:` prefix, and MSBuild then mis-parses a value whose final
# segment is literally `dotnet` as a second project, failing with MSB1008. Verified on
# SDK 10.0.400: `build/dotnetx`, `build/mydotnet` and `build/DOTNET` all publish fine,
# `build/dotnet` and `build/anything/dotnet` do not. The directory name is load bearing.
_PUBLISH_DIR = "csharp"


def _dotnet_build() -> List[Sequence[str]]:
    return [
        [
            "dotnet",
            "publish",
            str(ROOT / _CSPROJ),
            "-c",
            "Release",
            "-o",
            str(BUILD / _PUBLISH_DIR),
            "--nologo",
            "-v",
            "quiet",
        ]
    ]


def _dotnet_run(program: Path) -> List[str]:
    return [str(BUILD / _PUBLISH_DIR / f"eet{EXE}"), "run", str(program)]


DOTNET = Runtime(
    key="csharp",
    label="C# / .NET",
    language="C# 12 on .NET 8+",
    source="runtimes/dotnet",
    build=_dotnet_build,
    run=_dotnet_run,
    unavailable=lambda: None if _which("dotnet") else "dotnet SDK not on PATH",
)


def _aot_build() -> List[Sequence[str]]:
    return [
        [
            "dotnet",
            "publish",
            str(ROOT / _CSPROJ),
            "-c",
            "Release",
            "-p:PublishAot=true",
            "-o",
            str(BUILD / (_PUBLISH_DIR + "-aot")),
            "--nologo",
            "-v",
            "quiet",
        ]
    ]


def _aot_run(program: Path) -> List[str]:
    return [str(BUILD / (_PUBLISH_DIR + "-aot") / f"eet{EXE}"), "run", str(program)]


DOTNET_AOT = Runtime(
    key="csharp-aot",
    label="C# / NativeAOT",
    language="the same C#, compiled ahead of time",
    source="runtimes/dotnet",
    build=_aot_build,
    run=_aot_run,
    variant_of="csharp",
    unavailable=lambda: None
    if _which("dotnet") and os.environ.get("EET_ENABLE_AOT") == "1"
    else "set EET_ENABLE_AOT=1 and install the native toolchain to enable",
)


# --- C++ -------------------------------------------------------------------------------


def _cmake() -> Optional[str]:
    found = _which("cmake")
    if found:
        return found
    if IS_WINDOWS:
        # Visual Studio ships a CMake that is usually not on PATH.
        for edition in ("Community", "Professional", "Enterprise", "BuildTools"):
            for version in ("18", "2022", "2019"):
                candidate = Path(
                    f"C:/Program Files/Microsoft Visual Studio/{version}/{edition}"
                    "/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
                )
                if candidate.exists():
                    return str(candidate)
    return None


def _cpp_build() -> List[Sequence[str]]:
    cmake = _cmake()
    assert cmake is not None
    build_dir = BUILD / "cpp"
    return [
        [cmake, "-S", str(ROOT / "runtimes/cpp"), "-B", str(build_dir),
         "-DCMAKE_BUILD_TYPE=Release"],
        [cmake, "--build", str(build_dir), "--config", "Release", "--parallel"],
    ]


def _cpp_binary() -> Path:
    build_dir = BUILD / "cpp"
    # Single-config generators drop it in the root; multi-config ones use a subdirectory.
    for candidate in (
        build_dir / f"eet{EXE}",
        build_dir / "Release" / f"eet{EXE}",
        build_dir / "Debug" / f"eet{EXE}",
    ):
        if candidate.exists():
            return candidate
    return build_dir / f"eet{EXE}"


def _cpp_run(program: Path) -> List[str]:
    return [str(_cpp_binary()), "run", str(program)]


CPP = Runtime(
    key="cpp",
    label="C++",
    language="C++20",
    source="runtimes/cpp",
    build=_cpp_build,
    run=_cpp_run,
    unavailable=lambda: None if _cmake() else "cmake not found",
)


ALL: List[Runtime] = [PYTHON, JAVA, DOTNET, DOTNET_AOT, CPP]
BY_KEY = {r.key: r for r in ALL}

#: The runtime whose output defines the goldens.
REFERENCE = PYTHON


def env_for(runtime: Runtime) -> dict:
    """Environment for a subprocess running ``runtime``."""
    env = dict(os.environ)
    if runtime.key == "python":
        existing = env.get("PYTHONPATH", "")
        entry = str(ROOT / "runtimes" / "python")
        env["PYTHONPATH"] = entry + (os.pathsep + existing if existing else "")
    return env


def run_program(runtime: Runtime, program: Path, timeout: float = 120.0):
    """Execute one program and return ``(stdout, stderr, exit_status)`` as raw bytes."""
    proc = subprocess.run(
        runtime.run(program),
        capture_output=True,
        timeout=timeout,
        env=env_for(runtime),
        cwd=str(ROOT),
    )
    return proc.stdout, proc.stderr, proc.returncode
