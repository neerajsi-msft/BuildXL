#!/usr/bin/env python3
"""Regenerate the checked-in ckati v3 corpus without executing recipes."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys

fixture = Path(__file__).resolve().parent
repo = fixture.parents[6]
work = repo / "Out" / "directgraph-conformance-generator"
work.mkdir(parents=True, exist_ok=True)
payloads = {
    "DG_TABS": "\tleading\tand trailing\t",
    "DG_CR_LF": "left\rright\n\tcontinuation\n",
    "DG_SPACE": " ",
    "DG_DOLLAR": "$HOME $$ $(not-expanded) ' \" \\",
    "DG_C1": "\u0080\u0085\u009f",
    "DG_EMPTY": "",
}
makefile = """SHELL := /bin/bash --noprofile '' ' '
all: install/alpha install/beta output/long
install/alpha: input
\t@printf '%s' "$(shell printf 'left\\nright\\n')" > $@
install/beta: input
\t@printf '%s' 'literal $$ dollar' > $@
output/long: input
\t@printf '%s' '' ' ' 'LONG' > $@
"""
makefile = makefile.replace("'LONG'", "'" + "chunk " * 2048 + "end'")
(work / "Makefile").write_text(makefile, encoding="utf-8")
(work / "input").write_text("source\n", encoding="utf-8")
config = (
    "buildxlConfig 1\n"
    + "path 0 " + json.dumps(str(work / "install")) + "\n"
    + "path 1 " + json.dumps(str(work / "membership.json")) + "\n"
    + "sealDirectory installation $p0 $p1\n"
    + "inheritEnvironment " + " ".join(payloads) + "\n"
)
(work / "policy.config").write_text(config, encoding="utf-8")
ckati = Path(sys.argv[1]).resolve()
subprocess.run(
    [str(ckati), "--buildxl=golden.dg", "--buildxl_config=policy.config", "all"],
    cwd=work, env={"PATH": "/usr/bin:/bin", "LC_ALL": "C", **payloads}, check=True,
)
graph = (work / "golden.dg").read_bytes().replace(
    os.fsencode(work), b"/directgraph-conformance"
)
# Stable fixture names independent of the checkout path used in ckati's hashes.
names = {}
graph = re.sub(
    rb"action_[0-9a-f]{16}",
    lambda m: names.setdefault(m[0], ("action_%016x" % len(names)).encode()),
    graph,
)
(fixture / "golden.dg").write_bytes(graph)
(fixture / "expected.json").write_text(
    json.dumps(payloads, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
)
