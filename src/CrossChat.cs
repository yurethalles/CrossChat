using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Dorn.CrossChat
{
    [BepInPlugin("Dorn.CrossChat", "CrossChat", "1.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        internal ConfigEntry<bool> Enabled;
        internal ConfigEntry<string> LocalServerId;
        internal ConfigEntry<string> PeerHost;
        internal ConfigEntry<int> PeerPort;
        internal ConfigEntry<int> ListenPort;
        internal ConfigEntry<string> SharedSecret;
        internal ConfigEntry<bool> DryRun;
        internal ConfigEntry<int> DeliveryDelaySeconds;
        internal ConfigEntry<bool> SuppressServerBotWarnings;

        private readonly ConcurrentDictionary<string, DateTime> seen = new ConcurrentDictionary<string, DateTime>();
        private readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        private CancellationTokenSource cancellation;
        private TcpListener listener;
        private bool serverServicesStarted;
        private ZRoutedRpc registeredRpc;
        private static FieldInfo marketplaceChatField;
        private static FieldInfo hideTimerField;
        private static bool marketplaceLookupComplete;
        private const string SubmitRpcName = "Dorn_CrossChat_SubmitShout";
        private const string DisplayRpcName = "Dorn_CrossChat_DisplayRemote";

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("Bridge", "Enabled", true, "Enable the inter-server bridge.");
            LocalServerId = Config.Bind("Bridge", "LocalServerId", "server-1", "Unique ID for this server.");
            PeerHost = Config.Bind("Peer", "Host", "127.0.0.1", "DNS name or IP of the other server.");
            PeerPort = Config.Bind("Peer", "Port", 24590, "TCP port listened to by the other bridge.");
            ListenPort = Config.Bind("Listener", "Port", 24590, "Local TCP port for messages from the other bridge.");
            SharedSecret = Config.Bind("Security", "SharedSecret", "CHANGE-ME", "Same long secret on both servers. Change before enabling live use.");
            DryRun = Config.Bind("Test", "DryRun", false, "When true, receives and logs packets without showing them to players.");
            DeliveryDelaySeconds = Config.Bind("Test", "DeliveryDelaySeconds", 0, "Seconds to wait before publishing a received message. Use 120 for a solo visual test.");
            SuppressServerBotWarnings = Config.Bind("Log", "SuppressServerBotWarnings", true, "Hide only the harmless Character ID 0:0 warning for the internal Server Bot_0 peer.");

            new Harmony("Dorn.CrossChat").PatchAll();
            cancellation = new CancellationTokenSource();
            Logger.LogInfo("CrossChat 1.0.0 loaded. DryRun=" + DryRun.Value + ", Delay=" + DeliveryDelaySeconds.Value + "s");
        }

        private void OnDestroy()
        {
            if (cancellation != null) cancellation.Cancel();
            if (listener != null) listener.Stop();
        }

        private void Update()
        {
            if (!serverServicesStarted && ZNet.instance != null && ZNet.instance.IsServer())
            {
                serverServicesStarted = true;
                StartListener();
            }
            Action work;
            while (mainThread.TryDequeue(out work))
            {
                try { work(); }
                catch (Exception ex) { Logger.LogError("Main-thread bridge error: " + ex); }
            }
        }

        private void StartListener()
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, ListenPort.Value);
                listener.Start();
                Task.Run(() => AcceptLoop(cancellation.Token));
                Logger.LogInfo("Listening for bridge packets on TCP " + ListenPort.Value);
            }
            catch (Exception ex) { Logger.LogError("Could not start listener: " + ex); }
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    var backgroundRead = Task.Run(() => ReadClient(client, token));
                    GC.KeepAlive(backgroundRead);
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex) { Logger.LogWarning("Accept failed: " + ex.Message); }
            }
        }

        private async Task ReadClient(TcpClient client, CancellationToken token)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var lengthBytes = new byte[4];
                if (!await ReadExact(stream, lengthBytes, token)) return;
                int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes, 0));
                if (length < 1 || length > 8192) return;
                var body = new byte[length];
                if (!await ReadExact(stream, body, token)) return;
                var packet = Encoding.UTF8.GetString(body);
                HandlePacket(packet);
            }
        }

        private async Task<bool> ReadExact(NetworkStream stream, byte[] buffer, CancellationToken token)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, total, buffer.Length - total, token);
                if (read == 0) return false;
                total += read;
            }
            return true;
        }

        internal void RegisterSubmitRpc()
        {
            if (ZRoutedRpc.instance == null || registeredRpc == ZRoutedRpc.instance) return;
            ZRoutedRpc.instance.Register<UserInfo, string>(SubmitRpcName, ReceiveClientShout);
            ZRoutedRpc.instance.Register<string, string>(DisplayRpcName, ReceiveRemoteDisplay);
            registeredRpc = ZRoutedRpc.instance;
            Logger.LogInfo("Registered shout submission and remote display RPCs.");
        }

        internal void SubmitClientShout(string text)
        {
            if (!Enabled.Value || ZRoutedRpc.instance == null || string.IsNullOrWhiteSpace(text)) return;
            try
            {
                UserInfo user;
                string filteredText;
                Chat.GetChatMessageData(text, false, out user, out filteredText);
                var method = AccessTools.Method(typeof(ZRoutedRpc), "GetServerPeerID");
                if (method == null) throw new MissingMethodException("ZRoutedRpc.GetServerPeerID");
                long serverId = (long)method.Invoke(ZRoutedRpc.instance, null);
                ZRoutedRpc.instance.InvokeRoutedRPC(serverId, SubmitRpcName, user, filteredText);
                Logger.LogInfo("Submitted /s message to the local server.");
            }
            catch (Exception ex) { Logger.LogError("Could not submit /s message: " + ex); }
        }

        private void ReceiveClientShout(long senderPeerId, UserInfo user, string text)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !Enabled.Value) return;
            Logger.LogInfo("Received /s from client " + senderPeerId + " (" + user.Name + ").");
            ForwardShout(user, text);
        }

        internal void ForwardShout(UserInfo user, string text)
        {
            if (!Enabled.Value || string.IsNullOrWhiteSpace(text) || IsBridgeMessage(text)) return;
            var id = Guid.NewGuid().ToString("N");
            var userPackage = new ZPackage();
            user.Serialize(ref userPackage);
            var encodedUser = Convert.ToBase64String(userPackage.GetArray());
            var encodedText = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            var fields = id + "|" + LocalServerId.Value + "|" + encodedUser + "|" + encodedText;
            var signature = Sign(fields);
            Logger.LogInfo("Forwarding chat to " + PeerHost.Value + ":" + PeerPort.Value + ".");
            Task.Run(() => SendPacket(fields + "|" + signature));
        }

        private async Task SendPacket(string packet)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(PeerHost.Value, PeerPort.Value);
                    var data = Encoding.UTF8.GetBytes(packet);
                    var size = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length));
                    var stream = client.GetStream();
                    await stream.WriteAsync(size, 0, size.Length);
                    await stream.WriteAsync(data, 0, data.Length);
                    Logger.LogInfo("Chat packet sent to peer.");
                }
            }
            catch (Exception ex) { Logger.LogWarning("Peer send failed: " + ex.Message); }
        }

        private void HandlePacket(string packet)
        {
            var parts = packet.Split('|');
            if (parts.Length != 5 || !FixedEquals(Sign(parts[0] + "|" + parts[1] + "|" + parts[2] + "|" + parts[3]), parts[4])) return;
            if (parts[1] == LocalServerId.Value || !seen.TryAdd(parts[0], DateTime.UtcNow)) return;
            if (seen.Count > 1024) foreach (var entry in seen) { DateTime ignored; seen.TryRemove(entry.Key, out ignored); break; }
            UserInfo user;
            string text;
            try
            {
                var userPackage = new ZPackage(Convert.FromBase64String(parts[2]));
                user = new UserInfo();
                user.Deserialize(ref userPackage);
                text = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Rejected malformed bridge packet: " + ex.Message);
                return;
            }
            Logger.LogInfo("[CrossChat from " + parts[1] + "] " + text);
            if (!DryRun.Value) SchedulePublish(parts[1], user, text);
        }

        private void SchedulePublish(string origin, UserInfo user, string text)
        {
            int delaySeconds = Math.Max(0, DeliveryDelaySeconds.Value);
            if (delaySeconds == 0)
            {
                mainThread.Enqueue(() => PublishRemote(origin, user, text));
                return;
            }
            Logger.LogInfo("Remote chat scheduled for delivery in " + delaySeconds + " seconds.");
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delaySeconds * 1000, cancellation.Token);
                    if (!cancellation.IsCancellationRequested) mainThread.Enqueue(() => PublishRemote(origin, user, text));
                }
                catch (OperationCanceledException) { }
            });
        }

        private string Sign(string data)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SharedSecret.Value)))
                return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
        }

        private static bool FixedEquals(string a, string b)
        {
            var x = Encoding.UTF8.GetBytes(a); var y = Encoding.UTF8.GetBytes(b);
            if (x.Length != y.Length) return false;
            int difference = 0;
            for (int i = 0; i < x.Length; i++) difference |= x[i] ^ y[i];
            return difference == 0;
        }

        private void PublishRemote(string origin, UserInfo sender, string text)
        {
            try
            {
                if (ZRoutedRpc.instance == null)
                {
                    Logger.LogError("Could not publish remote message: Valheim routed RPC is unavailable.");
                    return;
                }
                var name = string.IsNullOrWhiteSpace(sender.Name) ? "Unknown" : sender.Name;
                var title = "[" + SanitizeForChat(origin) + "] " + SanitizeForChat(name);
                var safeText = SanitizeForChat(text);
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, DisplayRpcName, title, safeText);
                Logger.LogInfo("Published remote chat from " + origin + " through the direct-display RPC.");
            }
            catch (Exception ex) { Logger.LogError("Could not publish remote message: " + ex); }
        }

        private void ReceiveRemoteDisplay(long senderPeerId, string title, string text)
        {
            if (!Enabled.Value || Chat.instance == null || Player.m_localPlayer == null) return;
            try
            {
                var displayChat = GetDisplayChat();
                displayChat.AddString(title, text, Talker.Type.Shout, false);
                ShowDisplayChat(displayChat);
                Logger.LogInfo("Displayed remote chat line from " + title + " using " +
                    (displayChat == Chat.instance ? "Valheim chat" : "Marketplace KG Chat") + ".");
            }
            catch (Exception ex) { Logger.LogError("Could not display remote chat line: " + ex); }
        }

        private static Chat GetDisplayChat()
        {
            try
            {
                if (!marketplaceLookupComplete)
                {
                    var marketplaceType = AccessTools.TypeByName("Marketplace.Modules.KG_Chat.KG_Chat");
                    marketplaceChatField = marketplaceType == null ? null : AccessTools.Field(marketplaceType, "kgChat");
                    marketplaceLookupComplete = true;
                }
                var marketplaceChat = marketplaceChatField == null ? null : marketplaceChatField.GetValue(null) as Chat;
                if (marketplaceChat != null) return marketplaceChat;
            }
            catch { }
            return Chat.instance;
        }

        private static void ShowDisplayChat(Chat displayChat)
        {
            if (hideTimerField == null) hideTimerField = AccessTools.Field(typeof(Chat), "m_hideTimer");
            if (hideTimerField != null) hideTimerField.SetValue(displayChat, 0f);
        }

        private static string SanitizeForChat(string value)
        {
            return (value ?? string.Empty).Replace('<', ' ').Replace('>', ' ');
        }

        private static bool IsBridgeMessage(string text)
        {
            return text.StartsWith("[") && text.IndexOf("] ", StringComparison.Ordinal) > 1 && text.IndexOf("] ", StringComparison.Ordinal) < 48;
        }
    }

    [HarmonyPatch(typeof(Chat), "Awake")]
    internal static class RegisterBridgeRpcPatch
    {
        private static void Postfix()
        {
            var plugin = Plugin.Instance;
            if (plugin != null) plugin.RegisterSubmitRpc();
        }
    }

    [HarmonyPatch(typeof(Chat), "SendText")]
    internal static class ShoutCapturePatch
    {
        private static void Prefix(Talker.Type type, string text)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Enabled.Value || Player.m_localPlayer == null) return;
            if (type == Talker.Type.Shout && !string.IsNullOrWhiteSpace(text)) plugin.SubmitClientShout(text);
        }
    }

    [HarmonyPatch(typeof(Chat), "CheckPermissionsAndSendChatMessageRPCsAsync")]
    internal static class SuppressInternalServerBotWarningPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var warningMethod = AccessTools.Method(typeof(ZLog), "LogWarning", new Type[] { typeof(object) });
            var filteredWarningMethod = AccessTools.Method(typeof(SuppressInternalServerBotWarningPatch), "FilteredWarning");
            foreach (var instruction in instructions)
            {
                if (warningMethod != null && instruction.Calls(warningMethod))
                    instruction.operand = filteredWarningMethod;
                yield return instruction;
            }
        }

        private static void FilteredWarning(object obj)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || plugin.SuppressServerBotWarnings == null || !plugin.SuppressServerBotWarnings.Value)
            {
                ZLog.LogWarning(obj);
                return;
            }

            var message = obj == null ? string.Empty : obj.ToString();
            var isInternalServerBotWarning =
                message.IndexOf("Character ID for player (", StringComparison.Ordinal) >= 0 &&
                message.IndexOf("[Server, Bot_0", StringComparison.Ordinal) >= 0 &&
                message.IndexOf("was 0:0. Skipping.", StringComparison.Ordinal) >= 0;
            if (!isInternalServerBotWarning) ZLog.LogWarning(obj);
        }
    }
}
