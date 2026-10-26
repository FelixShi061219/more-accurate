# Development and verification

Normal use needs Windows/.NET Framework; Codex is optional for browser/desktop linking. Development requires Windows PowerShell 5.1; Node is needed only for MCP tests. Optional icon rebuilding uses Python and Pillow (`python tools/build-icon.py`). `app/icons/app.svg` is the editable vector source. No fonts or user configuration belong in the repository.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File app/build.ps1
node tests/mcp-smoke.cjs
node tests/bridge-smoke.cjs
node tests/browser-fixture-smoke.cjs
powershell -NoProfile -ExecutionPolicy Bypass -File tests/multi-task-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/telemetry-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/telemetry-config-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/installer-smoke.ps1
```

MCP verifies JSON-RPC, UTF-8, understanding updates and invalid values. Telemetry tests exercise real loopback HTTP/protobuf/gzip, turn lifecycle, model metadata, measured latency, stale evidence and interleaved chats. Config tests verify preservation, section placement, privacy settings, idempotency and conflicting exporters. Native form tests verify startup None, understanding-only behavior, live animation, hidden completed strip, persisted language, topmost/alpha, shadow, water, switching and monitor bounds. Installer testing uses an isolated CODEX_HOME and temporary desktop folder, requiring an installed Codex CLI/Desktop. It does not replace the user's actual skill/configuration or shortcut. Its temporary directory path is printed for inspection.

Windows CI builds and runs MCP/native bridge/controlled browser fixtures and legacy telemetry/config tests. Multi-task tests verify empty startup, automatic first fresh task, four-task selection, contiguous sectors/independent arcs, monitor bounds and an owned accessibility fixture; preview images contain illustrative data. Live websites and actual AI clients require separate manual compatibility checks. Installer smoke additionally requires Codex. Tests use isolated data, never live scores. Do not claim CI covers full installation or interactive UI.

`form-refresh-smoke.ps1` is a retained legacy single-window harness. Its reflection targets and dimensions are outdated for the multi-task implementation; it currently fails and is not part of the supported verification commands above.

To reproduce the controlled README indicator examples, run `tests/multi-task-smoke.ps1 -PreviewDirectory <an empty output folder>`. It renders startup None, the task bar, 1/2/3/4 sectors and understanding values 20/40/65/82/96 using the app's drawing code. README connector images are real Windows captures verified in English and Chinese at the current desktop scale; they contain no chat content. Preserve screenshot provenance and distinguish illustrative scores from live measurements.

Before publishing, exclude app/data, .git, local backups/configs and development dependency caches. Keep PowerShell files encoded UTF-8 with BOM for Windows PowerShell 5.1. Build from the shipped source and include the matching executable. Source availability does not grant an open-source license; none has been selected.
