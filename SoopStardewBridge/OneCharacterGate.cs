using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;

namespace SoopStardewBridge
{
    // 한 Steam 계정 = 캐릭터 하나 (게임 서버에서만 동작).
    // 이미 캐릭터를 만든 계정이 빈 칸(새 캐릭터 만들기)을 고르면 거절하고 캐릭터 목록을 다시 보낸다.
    // 기존 캐릭터로 들어가는 것은 그대로 두고, 남의 캐릭터를 막는 일은 JunimoServer(EnforceFarmhandOwnership)가 한다.
    internal static class OneCharacterGate
    {
        private static IMonitor? monitor;
        private static Func<bool>? isEnabled;
        private static MethodInfo? rejectMethod;

        public static void Apply(string harmonyId, IMonitor m, Func<bool> enabled)
        {
            monitor = m;
            isEnabled = enabled;
            rejectMethod = AccessTools.Method(typeof(GameServer), "rejectFarmhandRequest");
            MethodInfo? target = AccessTools.Method(typeof(GameServer), nameof(GameServer.checkFarmhandRequest));
            if (target == null || rejectMethod == null)
            {
                m.Log("캐릭터 1개 제한을 켜지 못했습니다 (게임 코드가 예상과 다름).", LogLevel.Warn);
                return;
            }
            // JunimoServer의 같은 검사보다 먼저 실행한다.
            new Harmony(harmonyId).Patch(target, prefix: new HarmonyMethod(typeof(OneCharacterGate), nameof(Prefix)) { priority = Priority.First });
        }

        private static bool Prefix(GameServer __instance, string userId, string connectionId, NetFarmerRoot farmer, Action<OutgoingMessage> sendMessage)
        {
            try
            {
                if (isEnabled?.Invoke() != true || rejectMethod == null) return true;
                if (farmer?.Value == null || string.IsNullOrWhiteSpace(userId)) return true;
                long uid = farmer.Value.UniqueMultiplayerID;
                if (!Game1.netWorldState.Value.farmhandData.TryGetValue(uid, out Farmer stored) || stored == null) return true;
                if (stored.isCustomized.Value) return true;   // 이미 만든 캐릭터로 접속하는 경우

                long? owned = FindOwnedCharacter(userId, uid, out string ownedName);
                if (owned == null)
                {
                    monitor?.Log($"새 캐릭터 만들기 허용: 계정 {userId} (이 계정의 기존 캐릭터 없음)", LogLevel.Info);
                    return true;
                }

                monitor?.Log($"새 캐릭터 만들기 거절: 계정 {userId}는 이미 캐릭터 '{ownedName}'(FarmerId={owned})가 있습니다. 캐릭터 목록을 다시 보냅니다.", LogLevel.Info);
                rejectMethod.Invoke(__instance, new object[] { userId, connectionId, farmer, sendMessage });
                return false;
            }
            catch (Exception ex)
            {
                monitor?.Log($"캐릭터 1개 제한 검사 오류 (이번 접속은 허용): {ex.Message}", LogLevel.Warn);
                return true;
            }
        }

        // 이 계정이 이미 만든(꾸민) 캐릭터를 찾는다. 1) JunimoServer 주인 기록  2) 게임에 남은 계정 표시(userID)
        // 같은 계정인지 비교. 연결 방식에 따라 "76561198…", "steam:76561198…"처럼 형식이 달라도 같은 Steam ID면 같다고 본다.
        private static string AccountKey(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(id, @"7656119\d{10}");
            return m.Success ? m.Value : id.Trim();
        }
        internal static bool SameAccount(string? a, string? b) { string ka = AccountKey(a); return ka != "" && ka == AccountKey(b); }

        private static long? FindOwnedCharacter(string userId, long except, out string name)
        {
            name = "";
            var farmhands = Game1.netWorldState.Value.farmhandData;

            string? saveDir = Constants.CurrentSavePath;
            string file = saveDir == null ? "" : Path.Combine(saveDir, "farmhand-ownership.json");
            if (file != "" && File.Exists(file))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("Farmhands", out JsonElement map) && map.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty entry in map.EnumerateObject())
                    {
                        if (!long.TryParse(entry.Name, out long id) || id == except) continue;
                        if (!entry.Value.TryGetProperty("Id", out JsonElement owner) || !SameAccount(owner.GetString(), userId)) continue;
                        if (farmhands.TryGetValue(id, out Farmer f) && f != null && f.isCustomized.Value) { name = f.Name; return id; }
                    }
                }
            }

            foreach (var pair in farmhands.Pairs)
            {
                Farmer f = pair.Value;
                if (f == null || pair.Key == except || !f.isCustomized.Value) continue;
                if (SameAccount(f.userID.Value, userId)) { name = f.Name; return pair.Key; }
            }
            return null;
        }
    }
}
