using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace SoopStardewBridge
{
    // 축제 메인 이벤트(달걀 찾기, 꽃춤, 수프 시식, 해파리 춤, 품평회, 얼음낚시)를 참가자 투표로 시작한다 (게임 서버에서만 동작).
    // JunimoServer는 5분 뒤 자동으로 시작하는데, 그 카운트다운을 멈추고 채팅 /축제시작 찬성이 과반수가 되면 시작시킨다.
    internal static class FestivalVote
    {
        private const string JunimoFestivalsType = "JunimoServer.Services.AlwaysOn.AlwaysOnServerFestivals";

        private static IMonitor? monitor;
        private static Func<bool> isEnabled = () => false;
        private static Func<double> ratio = () => 0.5;
        private static FieldInfo? eventCommandUsedField;
        // 안내는 플레이어 게임이 노란 글씨로 띄운다 (서버 채팅은 영어 글꼴이라 한글이 깨짐). (대상 null = 모두, 문구)
        private static Action<long?, string> notice = (_, _) => { };

        private static string currentKey = "";
        private static readonly HashSet<long> votes = new();
        private static bool passed, startRequested;
        private static DateTime lastReminder = DateTime.MinValue;
        private static DateTime lastCountdownTick = DateTime.MinValue;

        public static void Apply(string harmonyId, IMonitor m, Func<bool> enabled, Func<double> voteRatio, Action<long?, string> notify)
        {
            monitor = m;
            notice = notify;
            isEnabled = enabled;
            ratio = voteRatio;
            Type? festivals = AccessTools.TypeByName(JunimoFestivalsType);
            if (festivals == null) return;   // JunimoServer가 없는 곳(플레이어 게임)
            MethodInfo? target = AccessTools.Method(festivals, "RunMainEventCountdown");
            eventCommandUsedField = AccessTools.Field(festivals, "_eventCommandUsed");
            if (target == null || eventCommandUsedField == null)
            {
                m.Log("축제 투표를 켜지 못했습니다 (JunimoServer 코드가 예상과 다름). 기존처럼 자동으로 시작합니다.", LogLevel.Warn);
                return;
            }
            new Harmony(harmonyId + ".festivalvote").Patch(target, prefix: new HarmonyMethod(typeof(FestivalVote), nameof(Prefix)));
            m.Log("축제 메인 이벤트 투표 시작을 켰습니다 (/축제시작).", LogLevel.Info);
        }

        private static List<Farmer> Players()
            => Game1.getOnlineFarmers().Where(f => f.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID).ToList();

        private static int Required(int players)
            => Math.Max(1, (int)Math.Floor(players * Math.Clamp(ratio(), 0, 0.99)) + 1);

        private static int ValidVotes(List<Farmer> players)
            => votes.Count(id => players.Any(p => p.UniqueMultiplayerID == id));

        // JunimoServer가 매 프레임 부르는 카운트다운. 투표가 통과되기 전에는 실행하지 않는다.
        private static bool Prefix(object __instance)
        {
            try
            {
                if (!isEnabled()) return true;
                Event? ev = Game1.CurrentEvent;
                if (ev == null || !ev.isFestival) return true;

                string key = $"{Game1.Date.TotalDays}:{ev.id}";
                if (key != currentKey)
                {
                    currentKey = key;
                    votes.Clear();
                    passed = startRequested = false;
                    lastReminder = DateTime.MinValue;
                }
                lastCountdownTick = DateTime.UtcNow;

                List<Farmer> players = Players();
                if (!passed && players.Count > 0 && ValidVotes(players) >= Required(players.Count)) Pass(players);

                if (passed)
                {
                    // JunimoServer의 '!event'와 같은 방법으로 즉시 시작시킨다.
                    if (!startRequested) { eventCommandUsedField!.SetValue(__instance, true); startRequested = true; }
                    return true;
                }

                if ((DateTime.UtcNow - lastReminder).TotalSeconds >= 180 && players.Count > 0)
                {
                    lastReminder = DateTime.UtcNow;
                    Say($"축제 이벤트는 참가자 과반수가 찬성하면 시작합니다. 채팅에 /축제시작 을 입력하세요. (찬성 {ValidVotes(players)}/{Required(players.Count)})");
                }
                return false;
            }
            catch (Exception ex)
            {
                monitor?.Log($"축제 투표 오류, 기존 자동 시작으로 진행: {ex.Message}", LogLevel.Warn);
                return true;
            }
        }

        // 플레이어가 /축제시작 을 입력했을 때 (호스트에서 실행)
        public static void OnVote(long farmerId)
        {
            Farmer? who = Game1.getOnlineFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == farmerId);
            if (who == null) return;
            bool votingOpen = isEnabled() && Game1.CurrentEvent?.isFestival == true && (DateTime.UtcNow - lastCountdownTick).TotalSeconds < 5;
            if (!votingOpen) { Tell(farmerId, "지금은 투표로 시작할 축제 이벤트가 없습니다."); return; }
            if (passed) { Tell(farmerId, "이미 찬성이 모여 곧 시작합니다."); return; }
            if (!votes.Add(farmerId)) { Tell(farmerId, "이미 찬성했습니다."); return; }

            List<Farmer> players = Players();
            int have = ValidVotes(players), need = Required(players.Count);
            Say($"{who.Name}님이 축제 이벤트 시작에 찬성했습니다. (찬성 {have}/{need})");
            if (have >= need) Pass(players);
        }

        private static void Pass(List<Farmer> players)
        {
            passed = true;
            Say($"과반수 찬성! 축제 이벤트를 시작합니다. (찬성 {ValidVotes(players)}/{players.Count}명)");
            monitor?.Log($"축제 투표 통과: {ValidVotes(players)}/{players.Count}", LogLevel.Info);
        }

        private static void Say(string text)
        {
            notice(null, text);
        }

        private static void Tell(long farmerId, string text)
        {
            notice(farmerId, text);
        }
    }
}
