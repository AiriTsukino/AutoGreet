# AutoGreet

AutoGreet is a Dalamud plugin for Final Fantasy XIV housing venues. It detects visitors in housing interiors and exteriors with use of custom regions, maintains per-venue visitor/session state, queues macro-style greetings, tracks greeted/ungreeted/skipped visitors and supports multiple venue profiles.

## Repo Link

```text
https://raw.githubusercontent.com/AiriTsukino/AutoGreet/main/pluginmaster.json
```

[Discord Server](https://discord.com/invite/HqyDz3SRbG)

## Features

- Housing-only visitor detection using Dalamud's object table.
- Multiple isolated venue profiles.
- Lifetime visitor database per venue.
- Manual, crash-persistent night/session tracking. Sessions never auto-reset.
- Auto-greet queue with start delay and per-player queue spacing.
- Greeting macros supporting:
  - `/tell <t> message`
  - `/dote <t>`
  - `/wait X`
- First-time, returning, VIP, and blacklist-aware greeting selection.
- Manual controls for greet now, skip, mark greeted, and blacklist.
- Modern tabbed UI with counters, settings, greeting config, venue profiles, queue, and analytics.
- `/autogreet` toggles the UI.

## First-time setup in game

1. Run `/autogreetsettings` to configure the plugin and `/autogreet` to open main window.
2. Create or rename a venue profile in **Venue Profiles**.
3. Configure greetings in **Greetings Config**.
4. Adjust delay and queue spacing in **Settings** **If delays are too short it will cut off your greetings depending how long they are**.
5. Enable **Auto-greet enabled** on the main tab to automatically greet guests with configured settings or manually press **Greet Now** button on visitors in ungreeted list.

