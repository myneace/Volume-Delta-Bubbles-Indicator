# Contributing to Volume Delta Bubbles

Thank you for your interest in contributing! Here's everything you need to get started.

## Table of Contents

- [Reporting Bugs](#reporting-bugs)
- [Requesting Features](#requesting-features)
- [Development Setup](#development-setup)
- [Submitting a Pull Request](#submitting-a-pull-request)
- [Code Style](#code-style)
- [Commit Messages](#commit-messages)

---

## Reporting Bugs

Before opening an issue, please:

1. **Search existing issues** to avoid duplicates.
2. Confirm the bug occurs with a fresh Quantower restart and default settings.
3. Gather the **Quantower log output** (look for `[VolΔBubbles]` lines).

When opening an issue, include:

- Quantower version (e.g., `v1.146.18`)
- .NET SDK version (`dotnet --version`)
- Indicator parameter settings
- Steps to reproduce
- Actual vs. expected behavior
- Relevant log lines

---

## Requesting Features

Open a **Feature Request** issue and describe:

- The problem you're trying to solve
- The proposed solution
- Alternatives you have considered

---

## Development Setup

### Prerequisites

| Tool | Version |
|---|---|
| .NET SDK | 10.0 |
| Quantower | v1.146.18+ |
| Visual Studio 2022 / VS Code | Latest |

### Clone & Build

```powershell
git clone https://github.com/<your-username>/VolumeDeltaBubbles.git
cd VolumeDeltaBubbles
dotnet build VolumeDeltaBubbles\VolumeDeltaBubbles.csproj --configuration Debug
```

> [!IMPORTANT]
> The project references `TradingPlatform.BusinessLayer.dll` from your local Quantower installation.
> If Quantower is not installed at `C:\Quantower\`, update the `HintPath` in [`VolumeDeltaBubbles.csproj`](VolumeDeltaBubbles/VolumeDeltaBubbles.csproj).

---

## Submitting a Pull Request

1. Fork the repository and create a branch from `master`:
   ```powershell
   git checkout -b feat/my-feature
   ```
2. Make your changes.
3. Ensure the project builds without errors:
   ```powershell
   dotnet build VolumeDeltaBubbles\VolumeDeltaBubbles.csproj --configuration Release
   ```
4. Test the indicator in Quantower on a live symbol.
5. Push your branch and open a Pull Request against `master`.

Please describe **what** you changed and **why** in the PR description.

---

## Code Style

- Follow the existing C# style in the codebase.
- Keep partial classes in their appropriate file (`.Core.cs`, `.Drawing.cs`, etc.).
- Do not introduce unnecessary heap allocations in `OnPaintChart`.
- Do not block ThreadPool threads (use `async`/`await` or event-driven patterns).
- Document public and `internal` members with XML doc comments.

---

## Commit Messages

Use [Conventional Commits](https://www.conventionalcommits.org/):

```
feat: add logarithmic bubble scaling option
fix: prevent duplicate alerts on resubscribe
perf: eliminate per-frame List<T> allocation in OnPaintChart
docs: update README with Telegram setup guide
chore: bump NAudio to 2.2.1
```
