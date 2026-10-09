# Contributing

1. Fork the repo.
2. Create a feature branch (`git checkout -b feature/my-thing`).
3. Make your changes.
4. Test your script — each `.sh` should be self-contained and idempotent.
5. Run the regression suite (`tests/run.sh`) and the in-app self-tests (dashboard `Run self-tests`, or `ScriptSuite --self-test`).
6. Commit with a clear message (`git commit -m "Add feature X"`).
7. Push (`git push origin feature/my-thing`) and open a Pull Request.

## Style

- One script per folder under `scripts/`, plus `scripts/lib/common.sh` for shared helpers (`INFO:`/`WARN:`/`ERROR:`/`DRYRUN:` protocol, config readers, path expansion).
- Avoid external dependencies (nothing to install beyond the `.deb` `Depends:` line — `pkexec`, `polkitd`, `jq`, `xdg-user-dirs`, `systemd`, `libglib2.0-bin`).
- Every script takes `--config-path <json>`, supports `--dry-run` where its manifest sets `supportsDryRun: true`, and honors `--include-only-file <path>` for preview deselection.
- Use `warn`/`error` helpers for non-critical failures, never bare `exit 1` without an `ERROR:` line (the outcome mapping depends on it).
- Tests must use scratch dirs only (`$XDG_RUNTIME_DIR`, `mktemp -d`) — no hardcoded paths, no writes outside scratch, no `sudo`.

## App code

- C# mirrors `windows-scripts` `Views`/`ViewModels`/`Services` layout; port ViewModels verbatim where possible (paths/namespaces only) and record every Linux deviation in code comments *and* the README's known-limitations section.
- One concern per commit; never commit `bin/`, `obj/`, or build outputs (`**/bin/`, `**/obj/` are gitignored).
- No `sudo`, no `dpkg -i` / `apt install` in tests; no push without asking.
