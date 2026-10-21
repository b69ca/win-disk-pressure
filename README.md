# win-disk-pressure

Find large files and disk pressure with a bounded scan that skips reparse points.

A read-only C# / .NET 10 CLI for Windows 11 and Windows Server 2022/2025. No third-party runtime packages. Live collection uses .NET and Windows APIs; no PowerShell collector is required.

## Run

Download `win-disk-pressure-win-x64` from a successful [GitHub Actions run](https://github.com/b69ca/win-disk-pressure/actions), extract the artifact, and run the executable from a terminal. The standalone artifact includes its .NET runtime. These initial binaries are unsigned; verify their source and CI provenance according to your normal software policy.

```powershell
.\win-disk-pressure.exe --path C:\ --top 20
.\win-disk-pressure.exe --path C:\ --top 20 --json > report.json
.\win-disk-pressure.exe --input report.json --json
```

Or build from source with the [.NET 10 SDK](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview):

```powershell
dotnet run --project WinDiskPressure.csproj -- --path C:\ --top 20
dotnet publish WinDiskPressure.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
```

The JSON output contains the evidence and can be replayed with `--input` on Windows or Linux. Live collection is Windows-only at the CLI boundary. A successful report can contain warnings or errors describing the diagnosed system; it is not a clean bill of health.

## Options

```text
--path DIRECTORY (default system-drive root)
--top N (1–100, default 20)
--max-entries N (1–2000000, default 200000)
--max-seconds N (1–600, default 60)
--json          Full versioned JSON report
--input FILE    Analyze saved evidence without live collection
--help          Usage
```

`--input` cannot be combined with live collection options. Unknown and duplicate flags are rejected. Snapshots must match this tool and schema version 1, contain evidence and issues, and be no larger than 8 MiB. `--help` performs no collection. Ctrl-C cancels an active collection.

Exit codes: **0** report produced, including findings or partial evidence; **2** argument/snapshot error; **3** collection failed; **4** live collection attempted outside Windows; **130** cancelled. Automations must inspect `status`, `issues`, and finding severity rather than treating exit 0 as healthy. CLI errors go to stderr; `--json` runtime collection failures emit a `status: failed` envelope.

## Evidence and findings

Uses .NET filesystem metadata and volume free-space information. The scanner counts logical file lengths, maintains the largest observed files, and groups bytes by the first directory under the selected root. Reparse points, including junctions, symbolic links, and many cloud placeholders, are skipped. The root itself cannot be a reparse point. The first 20 access/I/O messages are retained with a total error count. Conventional Windows/user temporary, browser-cache, update-download, Installer, and component-store locations are grouped when encountered in the scan. Their byte figures cover observed files only; this is not a cleanup recommendation, and managed stores must not be treated as disposable caches. Customized paths can be missed.

The low-space rule is less than 5 GiB available or less than 10% of volume size. This is a useful threshold, not an application-specific minimum.

Results cover only visited entries, especially if a budget or access restriction stops traversal. Hard links can be counted more than once. Sparse/compressed files, filesystem metadata, snapshots, reserved storage, and allocated clusters are not reconciled. Free-space figures apply to the whole volume. Time limits are checked between filesystem operations; a slow filesystem call can exceed the nominal budget. It does not classify caches as safe to delete.

Text reports show the main facts and actionable findings. JSON includes `schemaVersion`, `tool`, `version`, `collectedAtUtc`, `analyzedAtUtc`, `mode`, `platform`, `status`, `evidence`, `issues`, and `findings`. Replaying retains the original collection timestamp/platform when recorded. Finding IDs are stable within this initial schema. Issues and bounds are preserved, so unavailable data is not silently presented as a successful check.

## Permissions and operational behavior

Start as a normal user. Some service, event-log, process-path, registry, and filesystem evidence can require additional privileges; failures are reported as collection issues. Elevation may improve coverage but does not guarantee access to every protected resource.

The tool does not terminate processes, change services, modify PATH/hosts/registry/firewall, delete files, install updates, or restart the computer. Network tools make the explicitly requested DNS/TCP/TLS probes. Reports can contain local paths, account names, hosts, process/service names, event messages, and public certificate metadata; review a report before sharing it. There is no telemetry or report-upload feature.

## Test and develop

```powershell
dotnet build WinDiskPressure.csproj -c Release
dotnet run --project tests/Checks.csproj -c Release
dotnet run --project WinDiskPressure.csproj -- --input tests/fixture.json --json
```

The dependency-free test executable uses assertions and exits nonzero on failure. Run it with `dotnet run`, not `dotnet test`. Fixtures are synthetic and use reserved example addresses/names. CI builds and runs fixtures on Linux and Windows; Windows also exercises the live collector where applicable and publishes a standalone x64 executable. TLS tests use isolated loopback endpoints, reject an untrusted certificate, and check timeout behavior. Disk tests use temporary directories and check reparse-point skipping where link creation is permitted.

Each repository is independent. `Core.cs` provides strict option parsing, snapshot replay, reporting, cancellation, and a bounded collector launcher. `Tool.cs` owns the diagnostic rules and native collection. Where present, `Collector.ps1` is embedded into the executable, uses built-in Windows inventory commands, and receives validated parameters through environment variables rather than interpolating user input into script code. Its subprocess is capped at 90 seconds and 8 MiB per output stream. Execution policy is not changed or bypassed.

This is a focused initial release. CI smoke tests establish that collectors execute on the hosted Windows runner, not that every Windows policy, locale, domain, or protected application is covered. Findings identify evidence and next checks; configuration changes remain a separate administrative decision.

## License

MIT
