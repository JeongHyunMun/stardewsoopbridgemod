using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;

namespace SoopStardewBridge
{
    // 연결이 비정상으로 끊겼는데 서버가 그 플레이어를 아직 '접속 중'으로 잡고 있으면,
    // 그 사람이 다시 들어올 때 자기 캐릭터가 목록에 없어서 새 캐릭터를 만들게 된다 (게임 서버에서만 동작).
    // 같은 Steam 계정은 동시에 두 곳에서 게임할 수 없으므로, 같은 계정이 다시 접속하면 남아 있던 이전 연결을 정리해
    // 자기 캐릭터를 다시 고를 수 있게 한다.
    internal static class ReconnectFix
    {
        private static IMonitor? monitor;
        private static MethodInfo? kickMethod, disconnectMethod;

        public static void Apply(string harmonyId, IMonitor m)
        {
            monitor = m;
            kickMethod       = AccessTools.Method(typeof(GameServer), "kick", new[] { typeof(long) });
            disconnectMethod = AccessTools.Method(typeof(GameServer), "playerDisconnected", new[] { typeof(long) });
            MethodInfo? send  = AccessTools.Method(typeof(GameServer), "sendAvailableFarmhands");
            MethodInfo? check = AccessTools.Method(typeof(GameServer), nameof(GameServer.checkFarmhandRequest));
            if ((kickMethod == null && disconnectMethod == null) || (send == null && check == null))
            {
                m.Log("재접속 정리를 켜지 못했습니다 (게임 코드가 예상과 다름).", LogLevel.Warn);
                return;
            }
            var harmony = new Harmony(harmonyId + ".reconnect");
            // 다른 모드(JunimoServer)의 같은 처리보다 먼저: 캐릭터 목록을 보내기 전 / 캐릭터 요청을 검사하기 전에 정리한다.
            if (send != null)  harmony.Patch(send,  prefix: new HarmonyMethod(typeof(ReconnectFix), nameof(BeforeSendList)) { priority = Priority.First });
            if (check != null) harmony.Patch(check, prefix: new HarmonyMethod(typeof(ReconnectFix), nameof(BeforeCheck))    { priority = Priority.First + 1 });
        }

        private static void BeforeSendList(GameServer __instance, string userId) => DropStale(__instance, userId, "캐릭터 목록 요청");
        private static void BeforeCheck(GameServer __instance, string userId)    => DropStale(__instance, userId, "캐릭터 접속 요청");

        private static void DropStale(GameServer server, string userId, string when)
        {
            try
            {
                if (!Context.IsMainPlayer || string.IsNullOrWhiteSpace(userId)) return;
                foreach (long id in OwnedCharacters(userId))
                {
                    if (!Game1.otherFarmers.ContainsKey(id)) continue;   // 접속 중이 아니면 정리할 것 없음
                    string name = Game1.otherFarmers.TryGetValue(id, out Farmer f) ? f.Name : "";
                    monitor?.Log($"같은 계정({userId})이 다시 접속({when}): 끊긴 채 남아 있던 이전 연결 '{name}'(FarmerId={id})을 정리합니다.", LogLevel.Info);
                    try { kickMethod?.Invoke(server, new object[] { id }); } catch (Exception ex) { monitor?.Log($"  이전 연결 끊기 실패(계속 진행): {ex.InnerException?.Message ?? ex.Message}", LogLevel.Trace); }
                    if (Game1.otherFarmers.ContainsKey(id))
                    {
                        try { disconnectMethod?.Invoke(server, new object[] { id }); } catch (Exception ex) { monitor?.Log($"  접속 정보 정리 실패: {ex.InnerException?.Message ?? ex.Message}", LogLevel.Warn); }
                    }
                    if (Game1.otherFarmers.ContainsKey(id)) monitor?.Log($"  이전 연결(FarmerId={id})이 아직 남아 있습니다. 서버를 다시 시작해야 할 수 있습니다.", LogLevel.Warn);
                }
            }
            catch (Exception ex) { monitor?.Log($"재접속 정리 오류 (이번 접속은 그대로 진행): {ex.Message}", LogLevel.Warn); }
        }

        // 이 계정이 가진(꾸민) 캐릭터: 1) JunimoServer 주인 기록  2) 게임에 남은 계정 표시(userID)
        private static IEnumerable<long> OwnedCharacters(string userId)
        {
            var ids = new HashSet<long>();
            var farmhands = Game1.netWorldState.Value.farmhandData;
            string? saveDir = Constants.CurrentSavePath;
            string file = saveDir == null ? "" : Path.Combine(saveDir, "farmhand-ownership.json");
            if (file != "" && File.Exists(file))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("Farmhands", out JsonElement map) && map.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty entry in map.EnumerateObject())
                        if (long.TryParse(entry.Name, out long id) && entry.Value.TryGetProperty("Id", out JsonElement owner) && OneCharacterGate.SameAccount(owner.GetString(), userId))
                            ids.Add(id);
            }
            foreach (var pair in farmhands.Pairs)
                if (pair.Value != null && OneCharacterGate.SameAccount(pair.Value.userID.Value, userId)) ids.Add(pair.Key);
            return ids.Where(id => farmhands.TryGetValue(id, out Farmer f) && f != null && f.isCustomized.Value);
        }
    }
}
