#!/usr/bin/env python3
"""EET repository driver -- build the runtimes, run the programs, prove they agree.

    python tools/eet.py list                 what can be built on this machine
    python tools/eet.py build                build every available runtime
    python tools/eet.py asm                  assemble programs/ into build/programs/
    python tools/eet.py run programs/life.eet --runtime cpp
    python tools/eet.py golden               regenerate the goldens from the reference
    python tools/eet.py verify               the conformance matrix

``verify`` is the one that matters. Everything else exists to make it possible.
"""

from __future__ import annotations

import argparse
import difflib
import os
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Tuple

sys.path.insert(0, str(Path(__file__).resolve().parent))
import malformed  # noqa: E402
import runtimes as rt  # noqa: E402

ROOT = rt.ROOT
PROGRAMS = ROOT / "programs"
GOLDEN = ROOT / "tests" / "conformance" / "golden"
BUILT = rt.BUILD / "programs"
MALFORMED = rt.BUILD / "malformed"

#: Spec section 2: a rejected image says this and exits 65. The reason text is free-form.
BAD_BINARY_PREFIX = b"eet: bad binary:"

sys.path.insert(0, str(ROOT / "runtimes" / "python"))
from eetvm import AsmError, assemble  # noqa: E402

# --- pretty printing -------------------------------------------------------------------

_COLOR = sys.stdout.isatty()


def _paint(text: str, code: str) -> str:
    return f"\033[{code}m{text}\033[0m" if _COLOR else text


def green(t: str) -> str:
    return _paint(t, "32")


def red(t: str) -> str:
    return _paint(t, "31")


def yellow(t: str) -> str:
    return _paint(t, "33")


def dim(t: str) -> str:
    return _paint(t, "2")


def bold(t: str) -> str:
    return _paint(t, "1")


# --- program discovery -----------------------------------------------------------------


def program_sources() -> List[Path]:
    return sorted(PROGRAMS.glob("*.eet"))


def assemble_all(quiet: bool = False) -> List[Path]:
    """Assemble every ``programs/*.eet`` into ``build/programs/``."""
    BUILT.mkdir(parents=True, exist_ok=True)
    outputs = []
    for source in program_sources():
        try:
            module = assemble(source.read_text(encoding="utf-8"), str(source))
        except AsmError as error:
            print(red(f"assembly failed: {error}"), file=sys.stderr)
            raise SystemExit(1)
        target = BUILT / (source.stem + ".eetb")
        # Write through a temporary so a concurrent `verify` in another process can never
        # observe a half-written module.
        scratch = target.with_suffix(f".eetb.{os.getpid()}.tmp")
        scratch.write_bytes(module.to_bytes())
        os.replace(scratch, target)
        outputs.append(target)
        if not quiet:
            print(f"  {source.relative_to(ROOT)} -> {target.relative_to(ROOT)}")
    return outputs


# --- goldens ---------------------------------------------------------------------------


@dataclass
class Expected:
    stdout: bytes
    stderr: bytes
    status: int


def golden_paths(name: str) -> Tuple[Path, Path, Path]:
    return (
        GOLDEN / f"{name}.stdout",
        GOLDEN / f"{name}.stderr",
        GOLDEN / f"{name}.exit",
    )


def read_golden(name: str) -> Optional[Expected]:
    """Load the expected result. Absent ``.stderr``/``.exit`` mean empty and zero."""
    out_path, err_path, exit_path = golden_paths(name)
    if not out_path.exists():
        return None
    return Expected(
        stdout=out_path.read_bytes(),
        stderr=err_path.read_bytes() if err_path.exists() else b"",
        status=int(exit_path.read_bytes().strip()) if exit_path.exists() else 0,
    )


def write_golden(name: str, result: Expected) -> None:
    out_path, err_path, exit_path = golden_paths(name)
    GOLDEN.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(result.stdout)
    # Only keep the interesting files, so the golden directory stays readable.
    if result.stderr:
        err_path.write_bytes(result.stderr)
    elif err_path.exists():
        err_path.unlink()
    if result.status:
        # write_bytes, not write_text: on Windows the text path would translate the \n to
        # \r\n, and because goldens are stored verbatim that would make a Windows-recorded
        # golden differ from a Linux-recorded one for no reason at all.
        exit_path.write_bytes(f"{result.status}\n".encode("ascii"))
    elif exit_path.exists():
        exit_path.unlink()


# --- building --------------------------------------------------------------------------


def build_runtimes(keys: Sequence[str], quiet: bool = False) -> Dict[str, Optional[str]]:
    """Build the named runtimes. Returns ``key -> error message or None``."""
    results: Dict[str, Optional[str]] = {}
    for key in keys:
        runtime = rt.BY_KEY[key]
        reason = runtime.unavailable()
        if reason:
            results[key] = f"skipped: {reason}"
            if not quiet:
                print(f"  {yellow('skip')} {runtime.label}: {reason}")
            continue
        error = None
        for command in runtime.build():
            proc = subprocess.run(command, capture_output=True, cwd=str(ROOT))
            if proc.returncode != 0:
                error = (
                    proc.stderr.decode("utf-8", "replace").strip()
                    or proc.stdout.decode("utf-8", "replace").strip()
                    or f"exit {proc.returncode}"
                )
                break
        results[key] = error
        if not quiet:
            if error:
                print(f"  {red('FAIL')} {runtime.label}")
                print(dim("        " + error.replace("\n", "\n        ")[:1500]))
            else:
                print(f"  {green('ok')}   {runtime.label}")
    return results


# --- the malformed corpus --------------------------------------------------------------


def write_malformed():
    """Materialise the hostile images so runtimes can be handed a real file path."""
    MALFORMED.mkdir(parents=True, exist_ok=True)
    written = []
    for case in malformed.CASES:
        path = MALFORMED / f"{case.name}.eetb"
        path.write_bytes(case.blob)
        written.append((case, path))
    return written


def check_malformed(usable: Sequence) -> List[str]:
    """Every runtime must reject every malformed image with exit 65 and the right prefix.

    Nothing in ``programs/`` can test this: the assembler only emits valid modules. A
    runtime that skips its header checks passes the whole conformance matrix and still
    reads past the end of a buffer the first time it is handed a hostile file.
    """
    from eetvm.isa import EXIT_LOAD_ERROR

    failures: List[str] = []
    cases = write_malformed()
    width = max(len(c.name) for c, _ in cases) + 2

    print()
    print(bold("malformed input"))
    print(dim(" " * width + "".join(f"{r.label:<16}" for r in usable)))

    for case, path in cases:
        row = f"{case.name:<{width}}"
        for runtime in usable:
            try:
                stdout, stderr, status = rt.run_program(runtime, path)
            except (OSError, subprocess.TimeoutExpired) as error:
                row += f"{red('ERROR'):<16}"
                failures.append(f"{case.name} / {runtime.label}: {error}")
                continue

            problems = []
            if status != EXIT_LOAD_ERROR:
                problems.append(f"expected exit {EXIT_LOAD_ERROR}, got {status}")
            if not stderr.startswith(BAD_BINARY_PREFIX):
                shown = stderr[:120].decode("utf-8", "replace").strip() or "(nothing)"
                problems.append(
                    'stderr should start with "eet: bad binary:", got: ' + shown
                )
            if stdout:
                problems.append(f"wrote {len(stdout)} bytes to stdout; it should write none")

            if problems:
                row += f"{red('FAIL'):<16}"
                detail = "\n".join("      " + item for item in problems)
                failures.append(
                    f"{bold(case.name)} / {bold(runtime.label)}  ({case.why})\n" + detail
                )
            else:
                row += f"{green('ok'):<16}"
        print(row)
    return failures


# --- commands --------------------------------------------------------------------------


def cmd_list(_args) -> int:
    print(bold("EET runtimes"))
    for runtime in rt.ALL:
        reason = runtime.unavailable()
        mark = green("available") if reason is None else yellow("unavailable")
        note = "" if reason is None else dim(f"  ({reason})")
        variant = dim("  [variant of " + runtime.variant_of + "]") if runtime.variant_of else ""
        print(f"  {runtime.key:<12} {runtime.label:<16} {mark}{variant}{note}")
        print(f"  {'':<12} {dim(runtime.language)}")
    return 0


def cmd_asm(_args) -> int:
    print(bold("assembling programs"))
    assemble_all()
    return 0


def cmd_build(args) -> int:
    keys = args.runtime or [r.key for r in rt.ALL if r.unavailable() is None]
    print(bold("building runtimes"))
    results = build_runtimes(keys)
    failed = [k for k, v in results.items() if v and not v.startswith("skipped")]
    return 1 if failed else 0


def cmd_run(args) -> int:
    runtime = rt.BY_KEY[args.runtime]
    reason = runtime.unavailable()
    if reason:
        print(red(f"{runtime.label} is unavailable: {reason}"), file=sys.stderr)
        return 2
    program = Path(args.program)
    if program.suffix == ".eet":
        assemble_all(quiet=True)
        program = BUILT / (program.stem + ".eetb")
    stdout, stderr, status = rt.run_program(runtime, program)
    sys.stdout.buffer.write(stdout)
    sys.stdout.buffer.flush()
    sys.stderr.buffer.write(stderr)
    return status


def cmd_golden(_args) -> int:
    print(bold("assembling programs"))
    assemble_all()
    reference = rt.REFERENCE
    print(bold(f"recording goldens from {reference.label}"))
    for module in sorted(BUILT.glob("*.eetb")):
        stdout, stderr, status = rt.run_program(reference, module)
        write_golden(module.stem, Expected(stdout, stderr, status))
        detail = f"{len(stdout)} bytes out"
        if stderr:
            detail += f", {len(stderr)} bytes err"
        if status:
            detail += f", exit {status}"
        print(f"  {green('rec')}  {module.stem:<18} {dim(detail)}")
    return 0


def _diff(label: str, expected: bytes, actual: bytes) -> List[str]:
    """A readable diff of two byte streams, falling back to hex for binary content."""
    try:
        want = expected.decode("utf-8").splitlines(keepends=True)
        got = actual.decode("utf-8").splitlines(keepends=True)
    except UnicodeDecodeError:
        return [
            f"      {label}: expected {len(expected)} bytes, got {len(actual)} bytes",
            f"      expected {expected[:48].hex(' ')}",
            f"      actual   {actual[:48].hex(' ')}",
        ]
    lines = list(difflib.unified_diff(want, got, "expected", "actual", n=1, lineterm=""))
    out = [f"      {label}:"]
    for line in lines[:24]:
        out.append("      " + line.rstrip("\n"))
    if len(lines) > 24:
        out.append(dim(f"      ... {len(lines) - 24} more diff lines"))
    return out


def cmd_verify(args) -> int:
    keys = args.runtime or [r.key for r in rt.ALL]
    active = [rt.BY_KEY[k] for k in keys]

    print(bold("assembling programs"))
    assemble_all(quiet=True)
    modules = sorted(BUILT.glob("*.eetb"))
    print(f"  {len(modules)} programs")

    if not args.no_build:
        print(bold("building runtimes"))
        build_runtimes([r.key for r in active])

    usable = [r for r in active if r.unavailable() is None]
    skipped = [r for r in active if r.unavailable() is not None]

    print()
    print(bold("conformance matrix"))
    width = max((len(m.stem) for m in modules), default=10) + 2
    header = " " * width + "".join(f"{r.label:<16}" for r in usable)
    print(dim(header))

    failures: List[str] = []
    missing_golden: List[str] = []
    checks = 0

    for module in modules:
        expected = read_golden(module.stem)
        row = f"{module.stem:<{width}}"
        if expected is None:
            missing_golden.append(module.stem)
            print(row + yellow("no golden recorded"))
            continue
        for runtime in usable:
            try:
                stdout, stderr, status = rt.run_program(runtime, module)
            except subprocess.TimeoutExpired:
                row += f"{red('TIMEOUT'):<16}"
                failures.append(f"{module.stem} / {runtime.label}: timed out")
                continue
            except OSError as error:
                row += f"{red('NOT BUILT'):<16}"
                failures.append(f"{module.stem} / {runtime.label}: {error}")
                continue

            problems = []
            if stdout != expected.stdout:
                problems += _diff("stdout", expected.stdout, stdout)
            if stderr != expected.stderr:
                problems += _diff("stderr", expected.stderr, stderr)
            if status != expected.status:
                problems.append(
                    f"      exit: expected {expected.status}, got {status}"
                )

            if problems:
                row += f"{red('FAIL'):<16}"
                failures.append(
                    f"{bold(module.stem)} / {bold(runtime.label)}\n" + "\n".join(problems)
                )
            else:
                checks += 1
                row += f"{green('ok'):<16}"
        print(row)

    malformed_checks = 0
    if usable and not args.no_malformed:
        before = len(failures)
        failures.extend(check_malformed(usable))
        malformed_checks = (
            len(malformed.CASES) * len(usable) - (len(failures) - before)
        )

    for runtime in skipped:
        print(dim(f"  skipped {runtime.label}: {runtime.unavailable()}"))

    print()
    if failures:
        print(red(bold(f"{len(failures)} mismatch(es)")))
        for failure in failures:
            print()
            print(failure)
        return 1

    if missing_golden:
        # An unrecorded program is an unverified program; never let it look otherwise.
        print(red(bold(
            f"{len(missing_golden)} program(s) have no golden and were NOT checked: "
            + ", ".join(missing_golden)
        )))
        print("run: python tools/eet.py golden")
        return 1

    print(green(bold(
        f"all {checks + malformed_checks} checks passed across {len(usable)} runtime(s)"
        f"  ({checks} program, {malformed_checks} malformed)"
    )))
    return 0


def cmd_bench(args) -> int:
    """Time the same bytecode on every runtime.

    Wall time includes process startup, which is the honest thing to measure: it is what
    you actually wait for, and it is most of the story for the managed runtimes on short
    programs.
    """
    import time

    names = args.program or ["mandelbrot", "life", "sieve", "fib"]
    print(bold("assembling programs"))
    assemble_all(quiet=True)
    modules = [BUILT / f"{name}.eetb" for name in names]
    for module in modules:
        if not module.exists():
            print(red(f"no such program: {module.stem}"), file=sys.stderr)
            return 2

    usable = [r for r in rt.ALL if r.unavailable() is None]
    if not args.no_build:
        print(bold("building runtimes"))
        build_runtimes([r.key for r in usable])
        usable = [r for r in usable if r.unavailable() is None]

    print()
    print(bold(f"best of {args.repeat} runs, wall time in milliseconds"))
    label_width = max(len(r.label) for r in usable) + 2
    print(dim(" " * label_width + "".join(f"{m.stem:>14}" for m in modules)))

    timings: Dict[str, Dict[str, float]] = {}
    for runtime in usable:
        row = f"{runtime.label:<{label_width}}"
        timings[runtime.key] = {}
        for module in modules:
            best = None
            for _ in range(args.repeat):
                start = time.perf_counter()
                try:
                    _, _, status = rt.run_program(runtime, module)
                except (OSError, subprocess.TimeoutExpired):
                    best = None
                    break
                elapsed = (time.perf_counter() - start) * 1000.0
                # A program that trapped on purpose still counts; a crash does not.
                if status not in (0, 70):
                    best = None
                    break
                best = elapsed if best is None else min(best, elapsed)
            if best is None:
                row += f"{'--':>14}"
            else:
                timings[runtime.key][module.stem] = best
                row += f"{best:>13.1f}"
        print(row)

    # A relative column is easier to read than four columns of milliseconds.
    fastest = {
        m.stem: min(
            (t[m.stem] for t in timings.values() if m.stem in t), default=None
        )
        for m in modules
    }
    print()
    print(dim("relative to the fastest runtime for each program"))
    print(dim(" " * label_width + "".join(f"{m.stem:>14}" for m in modules)))
    for runtime in usable:
        row = f"{runtime.label:<{label_width}}"
        for module in modules:
            mine = timings[runtime.key].get(module.stem)
            best = fastest[module.stem]
            row += f"{'--':>14}" if mine is None or not best else f"{mine / best:>13.1f}x"
        print(row)
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(prog="eet", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("list", help="show runtimes and whether they can be built here")
    sub.add_parser("asm", help="assemble every program into build/programs")

    p_build = sub.add_parser("build", help="build runtimes")
    p_build.add_argument("-r", "--runtime", action="append", choices=list(rt.BY_KEY))

    p_run = sub.add_parser("run", help="run one program on one runtime")
    p_run.add_argument("program")
    p_run.add_argument("-r", "--runtime", default="python", choices=list(rt.BY_KEY))

    sub.add_parser("golden", help="regenerate goldens from the reference runtime")

    p_verify = sub.add_parser("verify", help="run the conformance matrix")
    p_verify.add_argument("-r", "--runtime", action="append", choices=list(rt.BY_KEY))
    p_verify.add_argument("--no-build", action="store_true",
                          help="assume the runtimes are already built")
    p_verify.add_argument("--no-malformed", action="store_true",
                          help="skip the malformed-input corpus")

    p_bench = sub.add_parser("bench", help="time the same bytecode on every runtime")
    p_bench.add_argument("program", nargs="*", help="defaults to a representative set")
    p_bench.add_argument("-n", "--repeat", type=int, default=3)
    p_bench.add_argument("--no-build", action="store_true")

    args = parser.parse_args(argv)
    return {
        "list": cmd_list,
        "asm": cmd_asm,
        "build": cmd_build,
        "run": cmd_run,
        "golden": cmd_golden,
        "verify": cmd_verify,
        "bench": cmd_bench,
    }[args.command](args)


if __name__ == "__main__":
    raise SystemExit(main())
