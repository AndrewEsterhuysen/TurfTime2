# Turf Time — always-on freemium and architecture rules

**Rule type in Rider:** set this rule to **Always**  
(Settings → Tools → AI Assistant → Rules → this file → Rule type: Always)

These guidelines apply to every AI Assistant chat in this project.

## Before you change code

1. Read and obey `docs/FREEMIUM_RULES.md` and `docs/FEATURES.md`.  
2. Also follow root `AGENTS.md`.

## Always challenge and offer better alternatives (R0)

The developer wants **best practices**, not rubber-stamped ideas. They may not know the industry norm.

For every non-trivial request:

1. Restate the **outcome** they want.  
2. Question risky or non-standard assumptions.  
3. If industry practice or a lower-risk design differs, **offer that alternative before implementing**—with short trade-offs.  
4. Recommend the better default; only implement a suboptimal path if the user explicitly chooses it after seeing the alternative.  
5. Skip heavy pushback only for trivial fixes (typos, obvious bugs, pure copy).

**Example:** Dual free + paid store apps is a declining pattern; prefer **one app + freemium/IAP**. Say so before scaffolding two apps.

## Product model

- **One app** only (`com.andrewestherhuysen.turftime`).  
- **Everything free today**; design so a **paywall / IAP** can be added later in the **same** app.  
- **Do not** propose or implement a second free/paid store app, dual product git branches, or Core/App.Local/App.Cloud split for freemium unless the user explicitly asks **after** you have presented the freemium alternative.

## Non-negotiable coding rules

1. Pages and ViewModels: **interfaces only** — never `Plugin.Firebase`, never `new CloudRosterService()` / concrete cloud services.  
2. Product access control: only via feature ids + `IEntitlementService` (today: free-all / always true). **No** ad-hoc `isPremium` Preferences or `#if PAID` for store tier.  
3. Local play must work offline if cloud/Firebase fails; cloud is optional.  
4. Persist/export formats: include and respect `schemaVersion`.  
5. Team storage mode should be explicit (`LocalOnly` vs `CloudSynced`) when touching team persistence.  
6. Gate future paid cloud actions at a few entry points + **service enforcement**, not hidden buttons alone.  
7. New user-facing capabilities: update `docs/FEATURES.md` in the same change.  
8. Prefer small, focused diffs; do not drive-by refactor unrelated code.

## Reject these patterns in your own suggestions

- Second app ID for “TurfTimer+” freemium  
- Firebase imports in `Pages/` or `ViewModels/`  
- Duplicating entire screens for paid vs free  
- Hard dependency on network at startup  

## Full detail

- `docs/FREEMIUM_RULES.md` (R1–R24)  
- `docs/FEATURES.md`  
- `AGENTS.md`
