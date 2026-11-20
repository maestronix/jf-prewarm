# Jellyfin Episode Prewarm

A Jellyfin 12.4 plugin that preloads the beginning of the next episode when playback reaches a configurable progress threshold.

## How it works

- Listens to Jellyfin playback progress events.
- When an episode reaches the configured threshold (default: 90%), queues a background preload.
- Finds the next episode in the same series, including season transitions.
- Opens that episode's exact filesystem path inside the Jellyfin container and reads up to **1 MiB**.
- Applies a persistent **6-hour cooldown per target episode**. Different episodes and series are independent.
- Playback event handlers only enqueue work; filesystem access happens on a background worker.

The plugin does not use ffprobe, HTTP streaming, transcoding, or Jellyfin's media streaming pipeline.

## Requirements

- Jellyfin Server 12.4.x
- .NET 10 runtime (provided by Jellyfin 12)
- Media paths accessible inside the Jellyfin container

## Install from the Jellyfin plugin repository

Add this repository in **Dashboard → Plugins → Repositories**:

`https://raw.githubusercontent.com/maestronix/jf-prewarm/main/manifest.json`

Then install **Episode Prewarm** and restart Jellyfin.

## Configuration

The plugin configuration page exposes the playback threshold percentage. The read size is fixed at 1 MiB and the cooldown is fixed at 6 hours per target episode.

The same page exposes runtime counters (attempts, successful reads, failures, bytes read) and the last preload result.

## Development

```bash
dotnet restore
dotnet build Jellyfin.Plugin.JfPrewarm/Jellyfin.Plugin.JfPrewarm.csproj --configuration Release
```

## Release

Push a tag such as `v1.0.0.0`. GitHub Actions builds the plugin, creates the Jellyfin plugin ZIP, publishes a GitHub release, calculates its MD5 checksum, and prepends the release to `manifest.json`.

Manual release workflow dispatch is also supported; provide the version as the workflow input.
