using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Audio;

namespace SoopStardewBridge
{
    // 게임 서버(소리 장치 없음)에서는 소리를 틀면 cue가 null로 돌아온다.
    // 동굴 파리(Fly)처럼 그 cue를 바로 쓰는 몬스터가 있으면 매 프레임 오류가 나서 시간, 이벤트, 잠자기가 멈춘다.
    // 그래서 null 대신 아무 소리도 안 나는 DummyCue를 돌려준다. 소리가 나는 플레이어 게임에는 영향이 없다.
    internal static class HeadlessSoundFix
    {
        public static void Apply(string harmonyId, IMonitor monitor)
        {
            var target = AccessTools.Method(typeof(SoundsHelper), nameof(SoundsHelper.PlayLocal));
            if (target == null) { monitor.Log("소리 오류 방지 패치를 적용하지 못했습니다 (게임 코드가 예상과 다름).", LogLevel.Warn); return; }
            new Harmony(harmonyId + ".sound").Patch(target, postfix: new HarmonyMethod(typeof(HeadlessSoundFix), nameof(Postfix)));
        }

        private static void Postfix(ref ICue cue)
        {
            if (cue == null) cue = new DummyCue();
        }
    }
}
