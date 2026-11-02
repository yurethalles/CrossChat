# CrossChat

CrossChat connects the global `/s` chat of two Valheim servers. A shout sent on either server is delivered to players connected to the other server through a direct authenticated TCP connection, without an external relay service.

Remote messages appear as:

```text
[Server 1] CharacterName: message
```

## Features

- Bidirectional `/s` chat between two Valheim servers.
- Direct TCP transport authenticated with HMAC-SHA256.
- Source server ID and original character name preserved in the displayed message.
- Works even when the sender never joined the destination server.
- Native Valheim chat support and optional Marketplace KG Chat integration.
- Configurable delivery delay for solo testing.
- Filters only the harmless internal `Server Bot_0` Character ID warning.

## Requirements

- Valheim with BepInEx 5.
- One additional TCP port allocated to each dedicated server.
- The same CrossChat version installed on both servers and every participating client.

Marketplace KG Chat is optional. When it is installed, CrossChat displays remote messages directly through it; otherwise, it uses the native Valheim chat.

## Installation

1. Copy `CrossChat.dll` to `BepInEx/plugins/CrossChat/` on both servers and every client.
2. Start both servers once to create `BepInEx/config/Dorn.CrossChat.cfg`.
3. Configure each server's listener, peer and shared secret.
4. Restart the servers after changing network settings.

Clients do not open a TCP listener; the `Peer` and `Listener` settings only apply to servers.

## Server configuration

`Peer.Port` must point to the other server's `Listener.Port`. These are additional TCP ports, not the normal Valheim game ports.

Server 1:

```ini
[Bridge]
Enabled = true
LocalServerId = Server 1

[Listener]
Port = 2473

[Peer]
Host = YOUR_SERVER_IP
Port = 2482
```

Server 2:

```ini
[Bridge]
Enabled = true
LocalServerId = Server 2

[Listener]
Port = 2482

[Peer]
Host = YOUR_SERVER_IP
Port = 2473
```

Allocate the listener ports in Pterodactyl and allow inbound TCP traffic to them.

Set the same long random value on both servers:

```ini
[Security]
SharedSecret = REPLACE_WITH_A_LONG_RANDOM_SECRET
```

For normal operation:

```ini
[Test]
DryRun = false
DeliveryDelaySeconds = 0
```

Only `/s` (`Shout`) messages are bridged. Say, whisper, ping and other chat modes remain local.

## Source

The complete plugin source is currently in [src/CrossChat.cs](src/CrossChat.cs). It includes the chat capture, direct TCP bridge, packet authentication, client display RPC, Marketplace integration and warning filter.

To compile it, reference the Valheim managed assemblies (`assembly_valheim.dll`, `assembly_utils.dll`, `Splatform.dll`, UnityEngine assemblies and `netstandard.dll`) plus BepInEx 5 (`BepInEx.dll` and `0Harmony.dll`). Those runtime files are intentionally not included in this repository.

## Updating from InterServerGlobalChat

CrossChat uses the plugin ID `Dorn.CrossChat` and creates `Dorn.CrossChat.cfg`. Copy the network ports, server IDs and shared secret from the old configuration, then remove `InterServerGlobalChat.dll` before installing CrossChat.
