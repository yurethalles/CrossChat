# CrossChat

CrossChat links the `/s` global chat of two Valheim servers. A shout sent on either server is delivered to players on the other server through a direct authenticated TCP connection, without an external relay service.

Remote messages are displayed as:

```text
[Server 1] CharacterName: message
```

The original character name is carried inside the message, so the sender does not need to have visited the destination server.

## Features

- Bidirectional global `/s` chat between two Valheim servers.
- Direct server-to-server TCP transport with HMAC-SHA256 packet authentication.
- Preserves the source server ID and original character name.
- Does not depend on the remote Steam ID being known by the destination server.
- Native Valheim chat support.
- Optional automatic integration with Marketplace KG Chat.
- Configurable delivery delay for solo testing.
- Filters only the harmless internal `Server Bot_0` Character ID warning.
- No external bot, database or relay application required.

## Requirements

- Valheim with BepInEx 5.
- One additional TCP port allocated to each dedicated server.
- The same CrossChat version installed on both servers and every participating client.

Marketplace KG Chat is optional. When detected, CrossChat writes remote messages directly to its visible chat instance.

## Installation

1. Copy `CrossChat.dll` to `BepInEx/plugins/CrossChat/` on both servers.
2. Copy the same DLL to `BepInEx/plugins/CrossChat/` on every client.
3. Start both servers once to create `BepInEx/config/Dorn.CrossChat.cfg`.
4. Configure the listener and peer values on each server.
5. Restart both servers after changing the network settings.

Clients do not open a TCP listener. The `Peer` and `Listener` sections are only used when the game instance is running as a server.

## Server configuration

Each server listens on its own additional TCP port. `Peer.Port` must point to the other server's `Listener.Port`; it is not the normal Valheim game port.

Example with two Pterodactyl servers on the same public IP:

### Server 1

```ini
[Bridge]
Enabled = true
LocalServerId = Server 1

[Listener]
Port = 2473

[Peer]
Host = 162.43.190.115
Port = 2482
```

### Server 2

```ini
[Bridge]
Enabled = true
LocalServerId = Server 2

[Listener]
Port = 2482

[Peer]
Host = 162.43.190.115
Port = 2473
```

Allocate both listener ports in Pterodactyl and allow inbound TCP traffic to them. Different servers may use different listener ports, but each peer must target the exact listener port assigned to the other server.

## Shared secret

Set the same long random value on both servers:

```ini
[Security]
SharedSecret = REPLACE_WITH_A_LONG_RANDOM_SECRET
```

Do not leave the default value on an internet-accessible server.

## Normal operation

```ini
[Test]
DryRun = false
DeliveryDelaySeconds = 0
```

Only `/s` (`Shout`) messages are bridged. Normal say, whisper, ping and other chat modes remain local.

## Solo visual test

Set `DeliveryDelaySeconds = 120` on the receiving server, send an `/s` message on the first server and connect to the second server before the delay ends. Return the value to `0` after testing.

## Log settings

```ini
[Log]
SuppressServerBotWarnings = true
```

This hides only the harmless warning produced when Valheim finds its internal `Server Bot_0` peer with Character ID `0:0`. Other warnings and errors remain visible.

## Expected logs

Sender client:

```text
Submitted /s message to the local server.
```

Source server:

```text
Received /s from client ...
Chat packet sent to peer.
```

Destination server:

```text
[CrossChat from Server 1] ...
Published remote chat from Server 1 through the direct-display RPC.
```

Destination client:

```text
Displayed remote chat line from [Server 1] CharacterName using Marketplace KG Chat.
```

Without Marketplace, the final log ends with `using Valheim chat`.

## Updating from InterServerGlobalChat

CrossChat uses the new plugin ID `Dorn.CrossChat` and creates `Dorn.CrossChat.cfg`. Copy the network ports, server IDs and shared secret from the old configuration, then remove the old `InterServerGlobalChat.dll` to avoid loading both mods.

## Source

The plugin source is in `src/CrossChat.cs`. To compile it, reference the Valheim managed assemblies (`assembly_valheim.dll`, `assembly_utils.dll`, `Splatform.dll`, UnityEngine assemblies and `netstandard.dll`) plus BepInEx 5 (`BepInEx.dll` and `0Harmony.dll`). These game/runtime files are intentionally not included in this repository.

## Troubleshooting

- Confirm `CrossChat 1.0.0 loaded` on both servers and every client.
- Confirm each server logs `Listening for bridge packets on TCP ...`.
- Verify that `Peer.Port` targets the other server's additional listener port.
- Verify that both servers use exactly the same `SharedSecret`.
- Confirm that Pterodactyl allocated the listener port and that the firewall allows TCP traffic.
- Remove duplicate or older CrossChat/InterServerGlobalChat DLLs.
- Set `DryRun = true` only when testing transport through logs without displaying messages.
