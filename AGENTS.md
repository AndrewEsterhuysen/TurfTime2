# Agent instructions (Turf Time / TurfTime2)

This file is the **primary instruction entry point** for coding agents (JetBrains agents, Claude Code, Cursor, Grok, Copilot workspace agents, etc.).

## Mandatory: read before changing the app

Before any non-trivial change to this repository—especially storage, teams, cloud, Firebase, navigation, DI, or feature availability—**read and follow**:

1. **[`docs/FREEMIUM_RULES.md`](docs/FREEMIUM_RULES.md)** — freemium architecture and coding rules (R1–R24)  
2. **[`docs/FEATURES.md`](docs/FEATURES.md)** — feature catalog and free vs candidate-paid ids  

If your change adds a user-facing capability, update `docs/FEATURES.md` in the same change.

## Mandatory: challenge requests and offer better alternatives

The developer may not always know industry best practice. **Do not implement a request blindly** when a better or more standard approach exists.

For **every** non-trivial request (architecture, product packaging, monetization, UX flow, data model, cloud design, tooling):

1. **Briefly restate** what was asked and the outcome they want.  
2. **Question assumptions** that could lead to avoidable cost, dual maintenance, poor conversion, or non-standard mobile patterns.  
3. **Offer at least one alternative** when the industry norm or a lower-risk design differs from the request—especially if the alternative is widely adopted.  
4. **Compare** trade-offs in plain language (effort, user friction, maintainability, store ops).  
5. **Recommend** a default when one option is clearly better practice; still proceed with the user’s explicit choice if they override after hearing the alternative.  
6. **Do not be obstructive** on trivial edits (typos, copy, obvious bugfixes)—challenge where judgment and standards matter.

**Canonical example:** “Two store apps (free local + paid cloud)” sounds manageable but is a **declining** pattern; industry default for this product type is **one app + freemium / IAP**. Agents must surface that kind of correction **before** scaffolding dual apps.

Apply the same habit to smaller choices (e.g. hard-coding premium flags vs entitlements, Firebase in ViewModels vs adapters, long-lived product branches vs feature gates).

## Product model (do not invent alternatives)

- **One** mobile app on App Store and Google Play (package/bundle: `com.andrewestherhuysen.turftime`).  
- **Everything is free today.** Monetization (paywall / IAP) will come **later in the same app**.  
- **Do not** create a second store app, dual long-lived product branches, or a Core/App.Local/App.Cloud split for freemium unless the product owner explicitly requests it.  
- Cloud/Firebase is **optional infrastructure**; local-only play must always work.

## Stack snapshot

- .NET MAUI (`TurfTime2.csproj`), TFMs: `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`  
- Local + cloud team/session features; Firebase / FCM used for cloud paths  
- UI: `Pages/`, logic: `ViewModels/`, `Services/`, `Helpers/`, `Models/`

## Hard constraints for agents

1. **No Firebase / Plugin.Firebase in Pages or ViewModels.** Depend on interfaces; keep cloud in service/adapter code.  
2. **No ad-hoc premium flags** (`Preferences` “isPremium”, compile `#if PAID` for store tier). Use / introduce `IEntitlementService` + feature ids.  
3. **Do not** `new` cloud services in UI; use DI (`MauiProgram`).  
4. **Fail soft:** cloud/auth/network failures must not break local play or crash startup.  
5. **Version persisted JSON** (`schemaVersion`) when changing on-disk or QR formats.  
6. **Match existing style**; avoid drive-by refactors and unsolicited markdown files.  
7. Prefer **interfaces + free-all entitlements** over dual apps when preparing for paywall.

## PR self-check (agents must apply)

When touching teams, storage, or cloud:

- [ ] Feature id recorded in `docs/FEATURES.md` if new capability  
- [ ] No Firebase types in UI layer  
- [ ] Local-only path still works  
- [ ] Service-level enforcement for any gated cloud action (not button-only)  
- [ ] Rules in `docs/FREEMIUM_RULES.md` not violated  

## Build notes

- Android signed release: `scripts/build/MAC-build-release-android.sh`  
- iOS signed release: `scripts/build/MAC-build-release-ios.sh`  
- Signing passwords: local `Directory.Build.props` (gitignored); never commit secrets  

## Definition of done

- Change solves the request without violating freemium rules.  
- App remains one product; free local path intact.  
- Docs updated when features or rules change.
