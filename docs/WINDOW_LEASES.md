# Window Leases and Native Target Tracking

## Purpose and authority boundary

A `WindowLease` is a temporary association between one companion and one verified Windows top-level window/process instance. It may anchor the body, present run status, and identify the selected context to the user. It cannot authorize a file, UI, network, shell, model, or process effect.

The application window-binding code has no `ResourceGrant`, `PermissionGateway`, plan, approval, or executor dependency. A folder grant remains a separate explicit capability, and every file effect still passes through the existing permission gateway.

## Identity

The authority-relevant target tuple is:

```text
{ candidate HWND, root-owner HWND, process ID, process start UTC }
```

Application name and observed window title are bounded display metadata. A title or executable display name change does not transfer or invalidate a lease; a change to any member of the authority tuple does. This makes HWND reuse fail closed.

The native adapter obtains process creation time from a query-limited process handle and compares mandatory integrity levels. Tooltail-owned, hidden, cloaked, child-only, shell, transient tool, input-transparent, hung, empty, minimized-for-discovery, higher-integrity, and unverifiable targets cannot be selected. A visible secure/elevated or identity-unverifiable surface blocks discovery from selecting a different window behind it.

No target discovery code captures screenshots, reads UI content, sends input, changes foreground activation, or stores executable paths. It derives a bounded executable basename for display and discards the full path immediately.

## Explicit issue and lifecycle

The application service has separate operations for:

1. begin drag;
2. preview at a physical-pixel pointer position;
3. explicit drop or explicit keyboard selection;
4. target revalidation;
5. lease issue;
6. reconciliation and terminal revocation.

A preview contains no lease. Drop re-observes the HWND/process tuple before it issues a default 30-minute lease. Exactly one monitor belongs to the active lease.

An active lease becomes terminal on user removal, return home, explicit revoke, target destroy, process exit, identity mismatch, cloak/ineligibility, timeout, monitor failure, or application shutdown. At or after the deadline, every path records `expired` rather than a misleading later reason. Beginning a real drag revokes the prior lease before another preview can issue.

The immutable external projection remains `docs/schemas/scope-lease.schema.json`. The strict reader additionally rejects zero IDs/handles, invalid or non-UTC lifecycle times, a future process start, empty/duplicate/unknown context capabilities, and terminal state/revocation mismatches.

## Native event and reconciliation design

`SetWinEventHook` registrations are out-of-context and limited to the selected process for location, show/hide, cloak/uncloak, minimize, and destroy events. Foreground is the only desktop-wide event, and it carries no content. Production hooks skip Tooltail's own process.

The adapter owns a dedicated background message-loop thread because out-of-context callbacks are delivered through the registering thread's message queue. The callback delegate is rooted for every safe hook handle's lifetime. Disposal posts `WM_QUIT` only to that Tooltail-owned thread, deterministically unhooks Tooltail's registrations on the same thread, and never stops or mutates another process.

Native callbacks only classify and enqueue an HWND signal. A bounded 16-slot coalescing buffer keeps the latest signal of each kind; destroy/process-exit terminal signals clear lower-priority noise and cannot be displaced. Application reconciliation is serialized, and a one-second timer is a low-frequency fallback for process exit, missed events, remote-session changes, or hook drift. It performs one identity/eligibility observation of the current target, never a full-desktop poll.

## Coordinates

Win32 rectangles and pointer positions are explicitly typed as physical pixels. WPF-style coordinates are explicitly typed as device-independent units. `ICoordinateSpace` converts through a monitor reference containing both origins and independent X/Y DPI values.

Pure tests cover 96, 120, 144, and 192 DPI (100%, 125%, 150%, and 200%), negative monitor origins, independent rotated-axis scaling, exact rectangle conversion, and physical round trips. Native placement code must use this service or exact physical `SetWindowPos` operations; it must not mix unlabeled WPF and Win32 coordinates.

## Verification surfaces

Portable tests cover lifecycle transitions, explicit drop, keyboard selection bounds/deduplication, expiry, minimize/restore, destroy, process exit, cloak, monitor disconnect, HWND reuse, contract projection, eligibility precedence, callback burst bounds, and coordinate conversion.

Tagged Windows tests create and close only a Tooltail-owned synthetic no-activate HWND. They verify native enumeration, own-process exclusion, display-only title drift, process identity, move/destroy event delivery through the dedicated hook thread, and repeated registration/disposal. They do not send input, activate or close an existing user window, or terminate a host process.

The ambient `PetWindow`, click-through `TetherWindow`, accessible inspector/home controls, Per-Monitor V2 manifest, focus smoke, and manual multi-monitor matrix are the next M4 UI slice.
