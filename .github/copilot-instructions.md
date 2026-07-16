# GitHub Copilot instructions (Turf Time)

You are working in the Turf Time (.NET MAUI) repository.

## Required context

Before suggesting or editing code that affects features, storage, teams, cloud, or Firebase:

1. Follow **`docs/FREEMIUM_RULES.md`**  
2. Follow **`docs/FEATURES.md`**  
3. Follow root **`AGENTS.md`**

## Product model

- Single store app; everything free until later freemium in the same app.  
- Do not introduce a second paid app or dual product structure unless explicitly requested **after** the freemium alternative is presented.  
- Cloud/Firebase is optional; local-only must always work.

## Challenge requests (required)

Do not rubber-stamp non-trivial ideas. Prefer industry-standard approaches. If the user’s plan conflicts with common mobile practice (e.g. dual free/paid store apps vs one-app freemium), **say so**, explain briefly, and suggest the better alternative before implementing. Proceed with their explicit choice only after that.

## Code constraints

- No Firebase / Plugin.Firebase in Pages or ViewModels.  
- No ad-hoc premium flags; use feature ids + entitlement service (free-all today).  
- Prefer DI over `new` for cloud services.  
- Version persisted JSON with `schemaVersion`.  
- Fail soft when cloud is unavailable.
