# Freemium development rules (Turf Time)

**Status:** Mandatory for all human and AI contributors  
**Product model:** One store app. Everything free until adoption supports monetization; then freemium (paywall / IAP) in the **same** app—not a second download.

Read this before changing storage, teams, cloud, Firebase, billing, or feature availability.

Related:

- Feature catalog: [`FEATURES.md`](FEATURES.md)
- Agent entry point: [`../AGENTS.md`](../AGENTS.md)

---

## 1. Guiding principles

1. **One app, many capabilities.** Local and cloud are **features**, not separate products or long-lived git forks.
2. **Capability over implementation.** UI and game flow ask “is this feature allowed?” not “is Firebase configured?”
3. **Default free; gate later.** Product policy lives in one entitlement service. Do not scatter `isPremium` checks.
4. **Cloud is optional infrastructure.** The app must run fully offline with zero cloud success.
5. **Entitlements are data, not UI state.** Screens do not invent their own paid/free rules.
6. **Prefer add, don’t fork.** New paid behavior is modules + gates, not a duplicate “paid” page tree.
7. **Do not split store apps** for freemium unless product explicitly decides a second SKU.
8. **Challenge approaches; prefer best practice.** Humans and AI must not rubber-stamp product or architecture ideas. When a request conflicts with common mobile industry practice (or a clearly simpler maintainable design), **say so**, explain why, and **offer the standard alternative** before implementing. Example: dual free/paid store listings vs single-app freemium—prefer freemium unless product explicitly overrides after the trade-off discussion. Full agent behavior: see **R0** and root `AGENTS.md`.

---

## 2. Challenge and alternatives (AI and humans)

### R0 — Question requests; offer industry-standard alternatives

**Intent:** Develop to best practices. The product owner may not always know the norm. Blindly implementing a workable-but-suboptimal idea (e.g. two apps because dual listings “felt manageable”) is a process failure.

**For non-trivial requests, AI agents must:**

1. Restate the desired **outcome** (not only the proposed mechanism).  
2. Flag assumptions that raise cost, user friction, dual maintenance, or non-standard patterns.  
3. Present **at least one alternative** when industry practice or a lower-risk design differs—especially freemium-in-one-app, entitlement gates, adapter boundaries, single source of truth for local play.  
4. Compare trade-offs briefly (user conversion, engineering cost, store ops, long-term maintainability).  
5. Recommend the best-practice default; **implement the user’s choice** only after they have seen the alternative (or for trivial bugfixes/copy where challenge adds no value).  
6. Stay collaborative—not pedantic. Prefer short, concrete alternatives over lectures.

**Examples of challenges that should be raised:**

| Requested approach | Better / industry-common alternative |
|--------------------|--------------------------------------|
| Two store apps (free + paid) | One app, free core, IAP/subscription unlock |
| Long-lived `local` / `cloud` git branches | One mainline; feature flags / entitlements |
| Firebase calls from Pages/ViewModels | Interfaces + cloud adapters; DI |
| `Preferences["isPremium"]` in UI | `IEntitlementService` + feature ids |
| Remove free features when monetizing | Keep free value; sell additive cloud/admin |

---

## 3. Feature and entitlement rules

### R1 — Name every monetizable capability

Every user-facing capability that might ever be free vs paid gets an id in [`FEATURES.md`](FEATURES.md) (and later a `Feature` enum in code) **in the same PR** that introduces it.

### R2 — One entitlement API

```csharp
// Target shape (introduce when wiring; until then document intent)
public interface IEntitlementService
{
    bool IsEnabled(Feature feature);
}
```

**Today (everything free):** implementation returns `true` for all features (`FreeAllEntitlements`).

**Later:** store-backed implementation; UI gains paywall chrome; domain logic stays the same.

**Forbidden:** ad-hoc `Preferences` keys, build constants, or hard-coded booleans for product tier in Pages/ViewModels.

### R3 — Gate at the edge of the capability

| Good | Bad |
|------|-----|
| Before “create cloud team” / “enable sync” | Inside rotation / timer math |
| Before cloud write on a syncing team | Inside every Firestore REST helper |
| Navigation into cloud-admin flows | Random `#if` in converters |

Core game rules (timer, rotation, on-field UX) stay **entitlement-agnostic**.

### R4 — Paywall is UI; enforcement is service-level

- UI: upsell, restore purchases.
- Services: refuse paid actions if not entitled (even if UI is bypassed).

Never rely on hiding a button alone.

---

## 4. Architecture / dependency rules

### R5 — Ports and adapters for cloud

ViewModels and Pages depend on **interfaces**, never on concrete `CloudRosterService`, `FcmService`, or Firebase types.

| Port (interface) | Free / local behavior | Cloud behavior |
|------------------|----------------------|----------------|
| Roster store | Preferences / files only | Local + optional cloud sync |
| Session archive | Local history | Local + Firestore when entitled + cloud mode |
| Push registration | No-op | FCM |
| Team directory | Local list | Local + remote membership |

**Do not** `new CloudRosterService()` (or similar) in Pages. Use DI from `MauiProgram`.

### R6 — No Firebase types outside the cloud adapter layer

**Allowed** to reference Plugin.Firebase / Firestore REST / google-services config:

- Cloud-oriented services under `Services/` (prefer a clear `Services/Cloud/` home over time)
- Platform Firebase init in `MauiProgram` / lifecycle only

**Forbidden** in Pages, ViewModels, and domain Models:

- `using Plugin.Firebase…`
- Firestore wire DTOs as primary domain models

Domain models (`Player`, `GameSession`, …) stay **store-agnostic**. Cloud wire formats live in mappers beside adapters.

### R7 — Local path must work if cloud is dead

Offline, auth failure, missing plist → degrade to local; do not crash.  
Startup must not require network.

### R8 — Feature flags vs entitlements

| Mechanism | Purpose |
|-----------|---------|
| **Entitlement** | User free vs paid tier |
| **Remote/config flag** | Kill switch / gradual rollout |
| **Compile symbol** | Not for store tiers / freemium policy |

“Everyone free until we monetize” = free-all entitlements (+ optional remote kill switch). Not two app builds.

---

## 5. Data and model rules

### R9 — Device is source of truth for play

Local storage remains authoritative for active play. Cloud is a **replica / multi-device sync**, not a second game engine.

### R10 — Version persisted and exported data

Any JSON in Preferences, files, QR payload, or Firestore must include:

```text
schemaVersion: <int>
```

Loaders handle current and previous version where practical.

### R11 — Optional cloud fields, safe defaults

Cloud-only fields (`cloudTeamId`, roles, member lists, etc.) must be optional with safe defaults so local-only teams from older builds never throw on load.

### R12 — Team storage mode is explicit

Teams (or equivalent) declare mode, e.g. `LocalOnly` vs `CloudSynced`.  
Behavior branches on **mode + entitlement**, not on “Firebase worked once.”

---

## 6. UI / UX rules

### R13 — Centralize future paywall entry points

Cloud actions that may later show a paywall should go through one helper (e.g. `ICloudAccess.EnsureAllowedAsync()`):

- Create / join cloud team  
- Enable sync on a team  
- Admin invite / multi-device  
- Cloud-only session library  

No one-off “sorry paid only” dialogs invented per screen without that gate.

### R14 — Don’t remove free value when monetizing

Features that shipped free for adoption stay free unless product explicitly reclassifies them. Prefer monetizing **additive** cloud/admin value.

### R15 — Analytics use feature ids

Events such as paywall shown / feature blocked include the `Feature` id (even while everything is free, entitlements can log `entitled=true`).

---

## 7. DI / composition (`MauiProgram`)

### R16 — Composition root owns wiring

Only `MauiProgram` (or a dedicated registration helper) chooses implementations:

- `IEntitlementService`
- Roster / session / push interfaces  

Pages never choose cloud vs local implementations.

### R17 — Prefer decorator: always local, optionally cloud

Pattern: save local always; if entitled **and** `CloudSynced`, also sync.  
Local save is never skipped because cloud failed.

---

## 8. Testing rules

### R18 — Entitlement fixtures

Critical flows must pass under a **LocalOnly** entitlement set (all cloud features false) with no network.

### R19 — Adapter tests for cloud

Mappers and conflict resolution are unit-tested; rotation/reporting must not require a live Firebase device.

### R20 — Monetization PR checklist (when IAP lands)

- [ ] Restore purchases  
- [ ] Cached entitlement offline grace  
- [ ] LocalOnly still fully playable  
- [ ] Service-level deny if not entitled  
- [ ] No Firebase types in ViewModels/Pages  

---

## 9. Process rules

### R21 — PR checklist (storage / teams / cloud)

1. New **Feature** id in [`FEATURES.md`](FEATURES.md)?  
2. UI/services go through entitlements / cloud access helper?  
3. Domain free of Firebase?  
4. LocalOnly still works?  
5. Persisted data **schemaVersion** updated if format changed?  
6. Enforcement in **service**, not only the button?  

### R22 — Review reject patterns

- `Preferences.Get("isPremium")` (or similar) in a page  
- Direct `Plugin.Firebase` in ViewModel/Page  
- Cloud-only crash on startup  
- Duplicating entire pages for a “paid version”  
- Hard-coded “cloud always on” with no interface seam  
- Second store app / dual long-lived product branches for freemium  
- Implementing a major architecture/product request **without** noting a better industry-standard alternative when one clearly exists (violates R0)

### R23 — Keep docs current

- Update [`FEATURES.md`](FEATURES.md) in the same PR as new capabilities.  
- Update this file when policy or architecture rules change.

### R24 — No dual-app freemium without product decision

Do not introduce a second bundle id / Play applicationId for free vs paid freemium. Revisit only if product requires a separate SKU.

### R25 — Agents push back constructively

AI and reviewers should treat “the developer asked for it” as insufficient when the ask conflicts with documented product model or mobile best practice. Surface the alternative early; document the final decision if the owner overrides.

---

## 10. Expected change surface when monetizing

| Layer | Expected work |
|-------|----------------|
| Store-backed `IEntitlementService` | New |
| Paywall UI + restore | New |
| Gates on a few cloud entry points | Small |
| Cloud services checking `IsEnabled` | Already in place if rules followed |
| Timer / rotation / core game | None |
| Second app / Core–Local–Cloud split | Not required |

---

## 11. One-page summary

```text
0. Question non-trivial asks; offer industry-standard alternatives (R0).
1. Features have ids; entitlements answer IsEnabled(feature).
2. Today entitlements always return true (everything free).
3. UI and services both respect entitlements; services enforce.
4. Pages/VMs talk to interfaces only — never Firebase.
5. Local always works; cloud is optional and fail-soft.
6. Persist schemaVersion; optional cloud fields have defaults.
7. Team mode is explicit (LocalOnly vs CloudSynced).
8. Gate cloud entry points in one place (future paywall).
9. Review: reject premium flags and Firebase in UI layer.
10. One app forever unless product says otherwise.
```
