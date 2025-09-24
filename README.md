# SteamDB Alert Bot

Monitor Steam app depot changes using SteamKit2.

## Requirements

- .NET SDK 9.0+
- Steam account

## Setup

1. Install .NET: `sudo pacman -S dotnet-runtime dotnet-sdk`
2. Copy `.env.example` to `.env` and add your Steam credentials
3. Run: `dotnet run`

## Features

- Monitor manifest ID changes (app updates)
- Track depot size changes
- Detect new depots
- Works without Python steam library bugs