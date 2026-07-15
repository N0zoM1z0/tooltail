# ADR 0002: Separate Window Leases from Resource Grants

- Status: Accepted
- Date: 2026-07-15

## Context

Dragging a pet onto a window is a powerful spatial metaphor. Users can reasonably interpret it as “pay attention here” or “work with this app.” It is not, by itself, an operating-system security boundary and cannot safely imply authority over every resource that the target process can reach.

Conflating visual placement with mutation permission would make consent ambiguous, make revocation incomplete, and allow a replaced or spoofed window to inherit authority.

## Decision

Model two independent capability objects:

1. `WindowLease` — short-lived context and presentation association with a verified top-level window/process identity.
2. `ResourceGrant` — explicit user approval for a closed action set over an exact canonical resource, initially one local folder root.

A window lease may influence where the companion sits, which adapter's status it presents, and what context is highlighted. It never authorizes file, UI, network, shell, or model effects.

A resource grant records:

- immutable canonical root identity;
- allowed action discriminators;
- issue, expiry/revocation state;
- user-visible label;
- policy constraints.

Execution additionally requires a validated skill version and approval bound to an exact plan fingerprint. Every effect passes through the `PermissionGateway` immediately before mutation.

## Invariants

- no lease implies a grant;
- no grant implies approval;
- no model output or UI state can create either object;
- window title is display-only untrusted text;
- HWND identity includes process identity and is revalidated;
- removing the pet revokes the lease immediately;
- revoking a grant prevents new planning and execution even if the lease remains;
- visible body state is a projection of committed state, never the source of authority.

## Consequences

Positive:

- consent and revocation are explainable and testable;
- the spatial metaphor remains useful without a false security claim;
- future app-specific capabilities can define their own grants;
- a stale/replaced target cannot inherit a lease solely through HWND reuse.

Costs:

- users sometimes perform two conceptually different consent actions;
- UI must clearly show the difference between “placed here” and “allowed to change this folder”;
- application services must carry and validate multiple typed IDs.

## Rejected alternatives

### Drag equals full application permission

Rejected because an application window does not enumerate or confine all authority associated with its process.

### One global automation toggle

Rejected because it is too broad to preview, reason about, revoke selectively, or bind to learned skills.

### Rely only on operating-system prompts

Rejected because local file operations may not produce a prompt and OS prompts do not express Tooltail's plan, skill version, or learned behavior.
