# ADR 0001: Use Windows 11, WPF, and .NET 10 for the First Hypothesis Build

- Status: Accepted
- Date: 2026-07-15
- Decision owners: Product and engineering founders

## Context

Tooltail's first hypothesis depends on a companion that can be placed against a real desktop window, track that window without stealing focus, show scope and runtime state continuously, and expose an accessible inspector. The implementation also needs reliable local file APIs, a small native-integration boundary, and a headless core that is easy to test.

Linux is strategically relevant, but general Wayland window discovery, placement, screen observation, and remote input are mediated by compositor and portal behavior. Solving that variability before validating the learning loop would combine platform research with product research.

## Decision

The v0.1 supported platform is Windows 11 x64.

Use:

- .NET 10 LTS and C#;
- WPF for `PetWindow`, `TetherWindow`, `InspectorWindow`, and home/workbench UI;
- Per-Monitor V2 DPI awareness;
- Win32 and Windows UI Automation only behind `Tooltail.Platform.Windows`;
- portable domain, application, contracts, and file-skill projects wherever practical.

Do not introduce Electron, a browser runtime, WinUI migration work, Avalonia, Unity, or a custom renderer during the hypothesis build without replacing this ADR.

## Consequences

Positive:

- direct control over transparent/non-activating windows and HWND identity;
- one runtime for UI, domain services, file APIs, persistence, and tests;
- mature Windows interop and accessibility options;
- portable business logic remains testable on non-Windows CI.

Costs:

- the first build does not prove Linux or macOS feasibility;
- WPF presentation is Windows-specific;
- mixed-DPI, z-order, input transparency, native callback lifetime, and accessibility require explicit testing;
- native desktop integration cannot be fully validated on a normal headless CI runner.

## Revisit when

- M5 and the product research gates pass;
- an app-specific Linux vertical has a concrete user need;
- the Windows shell itself blocks distribution or accessibility;
- another UI technology demonstrates the required non-activation, transparency, accessibility, packaging, and native-window behavior with lower total risk.

## Rejected alternatives

### Linux first

Rejected for v0.1 because portal/compositor variability would obscure the product result. Linux remains a later app-specific target.

### Cross-platform UI framework first

Rejected because “cross-platform UI” does not make platform window authority, coordinates, hooks, portals, or accessibility cross-platform.

### Game engine

Rejected because animation richness is not the first hypothesis and a game runtime increases packaging, accessibility, resource, and desktop-integration work.
