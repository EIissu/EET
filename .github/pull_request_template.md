## What this changes

<!-- One or two sentences. -->

## Checklist

- [ ] `python tools/eet.py verify` is green on every runtime I can build
- [ ] `cd runtimes/python && python -m unittest discover -s tests -t .` passes
- [ ] If I touched the spec, I updated the reference implementation and every other runtime
- [ ] If I regenerated `tests/conformance/golden/`, I said why below

<!--
A reminder, because it is the one rule that matters here: goldens are the pass/fail bar,
not something to adjust until the build goes green. If a golden changed, a deliberate
specification change should be the reason.
-->

## Golden changes

<!-- "None", or which programs changed and which spec section drove it. -->
