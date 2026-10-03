# CINO-AOS Windows Native

**Apache-2.0 开源预览 / Open-source preview. Not sponsored, signed or production certified.**

这是 CINO-AOS 的 Windows 原生核心：Host 后台服务代码、WPF 工作台、配置示例及隔离测试。这份独立源码快照已获授权，以 [Apache-2.0](LICENSE) 公开；不改变母仓库或第三方组件授权。

Repository: [TorofSkiy/cino-aos-windows-native](https://github.com/TorofSkiy/cino-aos-windows-native). Maintainer: [TorofSkiy](https://github.com/TorofSkiy).

CINO-AOS 在 Windows 提供的服务与用户会话中运行，Windows 内核、驱动和权限模型仍是基础。本源码包支持 Windows 11 x64 开发与本地验证；不能宣称已适配 macOS、Linux、Android、iOS、HarmonyOS 或实时操作系统。

## Scope and status

- Workspace: local text artifacts, editable copies, history, checksums, bounded file inventory and local inference integration.
- Host: loopback status, authenticated enrollment and typed task protocol, durable identity and receipts.
- Network protocol code is included; a compatible management backend, enrollment credentials and update broker are **not** included. A local health check does not prove two-machine operation.
- The private deployment installer, management platform, production keys, device identities, models, inference engine and previous Git history are excluded.
- The portable build does not install a Windows service, startup entry, driver or update task. Existing target-machine installation is unchanged.
- No public signing certificate or free sponsorship has been obtained. Do not bypass Windows security to run a blocked binary.

## Build from reviewed source

Windows x64 with the exact .NET SDK in `global.json` (8.0.424); PowerShell 5.1 or later.

```powershell
./scripts/Test-Source.ps1
./scripts/Build.ps1 -OutputRoot ./artifacts
```

Restores use the checked-in NuGet lockfiles and the sole source in `NuGet.Config`. Downloading dependencies requires Internet access; application builds and isolated tests do not need Azure or any signing account. `-UpdateLocks` is for an intentional dependency update, followed by review and regenerating the source manifest.

Output: `artifacts/portable/host`, `artifacts/portable/workbench`, dependency license copies, dependency inventory, and `artifacts/build-receipt.json`. CINO executables are unsigned. Microsoft's runtime binaries retain their upstream signatures.

The manual GitHub workflow only builds in a public repository on a standard runner; it does not publish releases, submit signing requests, create resources or deploy to devices. Artifact upload is off by default; enabling it requires an explicit input and the repository variable CINO_FREE_STORAGE_CONFIRMED=true after the owner verifies free storage and a zero spending budget. Check [Actions](https://github.com/TorofSkiy/cino-aos-windows-native/actions/workflows/build.yml) for actual run results; a checked-in workflow alone is not evidence of a successful build.

## Local use and removal

After your system permits the reviewed build under its normal policy, open `workbench/Cino.Workbench.exe`. Workspace data defaults to `%LOCALAPPDATA%/CINO-AOS/Workspace`, configuration to `%LOCALAPPDATA%/CINO-AOS/workbench.json`. Use `--config` with an isolated configuration for testing alongside an existing installation.

Local model generation additionally requires a compatible, independently verified llama.cpp engine with its original license files and integrity manifest, plus a model you are entitled to use. Neither is downloaded or bundled here. Without these, file inventory/history remain available but generation will not work.

Host can run as an ordinary foreground process with an explicit empty configuration, private state directory and unused loopback port. It remains unregistered until an operator configures enrollment. This preview does not provide a general-purpose service installer or authorize public network exposure.

To remove this portable preview: save work, close its workbench, stop only its own foreground Host process, then remove its extracted application folder. User configuration and artifacts are intentionally retained; delete those separately only after backing up data you want. Do not remove an existing CINO service or shared user-data folder as part of this preview's removal.

## Verification boundaries

Workspace tests use actual local files. Host tests start the actual executable on an ephemeral loopback port and check health and access boundaries. They do not exercise a physical target, GPU inference, service installation, network upgrades or restart recovery.

See [privacy](PRIVACY.md), [third-party notices](THIRD_PARTY_NOTICES.md), [signing policy](CODE_SIGNING_POLICY.md), and [release gates](RELEASE_CHECKLIST.md). The source manifest is a tamper-evident inventory, **not a trusted signature**.

