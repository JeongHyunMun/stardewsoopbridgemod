
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Objects;
using StardewValley.Menus;
using StardewValley.Monsters;

namespace SoopStardewBridge
{
    // ── 호스트 ↔ 플레이어 메시지 ───────────────────────────────────────────
    // 보상 대기열(state.json)은 호스트(Docker 서버)에만 있다. 플레이어 게임은 창을 띄우고
    // 자기 인벤토리에 아이템을 넣는 일만 하며, 필요한 정보는 SMAPI 멀티플레이 메시지로 주고받는다.
    internal sealed class PendingRequestMessage { public string Reason { get; set; } = "query"; }
    internal sealed class PendingMessage        { public int Chests { get; set; } public string Reason { get; set; } = "query"; }
    internal sealed class ClaimRequestMessage   { }
    internal sealed class GrantMessage          { public int Chests { get; set; } public int Remaining { get; set; } }
    internal sealed class ReturnMessage         { public int Chests { get; set; } }

    // ── 보상 수령 UI (플레이어 게임에서 표시) ──────────────────────────────
    internal sealed class RewardClaimMenu : IClickableMenu
    {
        private const int MenuWidth  = 520;
        private const int MenuHeight = 320;

        private readonly ModEntry _mod;
        private Rectangle _claimBtn;
        private ClickableTextureComponent _closeBtn = null!;

        public RewardClaimMenu(ModEntry mod) : base(
            (Game1.uiViewport.Width  - MenuWidth)  / 2,
            (Game1.uiViewport.Height - MenuHeight) / 2,
            MenuWidth, MenuHeight)
        {
            _mod = mod;
            RebuildButtons();
            Game1.playSound("shwip");
        }

        private void RebuildButtons()
        {
            _claimBtn = new Rectangle(xPositionOnScreen + width / 2 - 120, yPositionOnScreen + height - 96, 240, 60);
            _closeBtn = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 44, yPositionOnScreen - 4, 48, 48),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 4f);
        }

        private void DrawCentered(SpriteBatch b, string text, float y, Color color)
        {
            Vector2 size = Game1.dialogueFont.MeasureString(text);
            b.DrawString(Game1.dialogueFont, text, new Vector2(xPositionOnScreen + (width - size.X) / 2f, y), color);
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.4f);
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, false);

            DrawCentered(b, "SOOP 후원 보상", yPositionOnScreen + 28, Game1.textColor);
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + 80, width - 48, 2), Color.Gray * 0.5f);

            float midY = yPositionOnScreen + 120;
            int chests = _mod.ClientPendingChests;

            if (_mod.ClientWaiting)
            {
                bool timedOut = (DateTime.UtcNow - _mod.ClientWaitingSince).TotalSeconds > 6;
                DrawCentered(b, timedOut ? "서버가 응답하지 않습니다." : "보상 정보를 확인하는 중...", midY, Game1.textColor * 0.7f);
                if (timedOut)
                    DrawCentered(b, "서버에 모드가 설치되어 있는지 확인하세요.", midY + 44, Game1.textColor * 0.6f);
            }
            else if (chests <= 0)
            {
                DrawCentered(b, "수령할 보상이 없습니다.", midY, Game1.textColor * 0.6f);
            }
            else
            {
                DrawCentered(b, $"SOOP 보상 상자  ×  {chests}", midY, Game1.textColor);

                bool hover = _claimBtn.Contains(Game1.getMouseX(), Game1.getMouseY());
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                    _claimBtn.X, _claimBtn.Y, _claimBtn.Width, _claimBtn.Height, hover ? Color.Wheat : Color.White, 4f, false);
                Vector2 bs = Game1.dialogueFont.MeasureString("전체 수령");
                b.DrawString(Game1.dialogueFont, "전체 수령",
                    new Vector2(_claimBtn.X + (_claimBtn.Width - bs.X) / 2f, _claimBtn.Y + (_claimBtn.Height - bs.Y) / 2f + 4),
                    Game1.textColor);
            }

            _closeBtn.draw(b);
            drawMouse(b);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeBtn.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            if (!_mod.ClientWaiting && _mod.ClientPendingChests > 0 && _claimBtn.Contains(x, y))
                _mod.RequestClaim();
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape || Game1.options.doesInputListContain(Game1.options.menuButton, key))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
            }
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            xPositionOnScreen = (Game1.uiViewport.Width  - width)  / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            RebuildButtons();
        }
    }

    // ── 메인 모드 ─────────────────────────────────────────────────────────────
    internal sealed class ModEntry : Mod
    {
        private const string ChestItemId       = "SoopStardewBridge_RewardChest";
        private const string ChestTextureAsset = "SoopStardewBridge/reward_chest";
        private const int    ChestSpriteIndex  = 0;
        private const int    MaxGrantPerClaim  = 999;   // 한 칸에 쌓을 수 있는 최대 개수

        private const string MsgPendingRequest = "PendingRequest";  // 플레이어 → 호스트
        private const string MsgPending        = "Pending";         // 호스트 → 플레이어
        private const string MsgClaimRequest   = "ClaimRequest";    // 플레이어 → 호스트
        private const string MsgGrant          = "Grant";           // 호스트 → 플레이어
        private const string MsgReturn         = "Return";          // 플레이어 → 호스트 (인벤토리에 못 넣은 개수)

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly ConcurrentQueue<DonationEvent> incomingEvents      = new();
        private readonly ConcurrentQueue<CentralJob>    incomingCentralJobs = new();
        private readonly HashSet<long>                  loggedFarmerIds     = new();
        private readonly Random                         random              = new();
        private readonly HttpClient                     centralHttp         = new() { Timeout = TimeSpan.FromSeconds(12) };

        // 호스트: 마지막으로 보낸 지급 수량. 플레이어가 돌려보내는 양은 이 값을 넘을 수 없다.
        private readonly Dictionary<long, int> lastGrant = new();

        // 플레이어 게임 쪽 표시용 값 (호스트가 알려준 값)
        public int      ClientPendingChests;
        public bool     ClientWaiting;
        public DateTime ClientWaitingSince;

        // config/rules는 백그라운드 타이머가 교체하고 다른 스레드가 읽으므로 volatile로 둔다.
        private volatile BridgeConfig config = new();
        private volatile RulesFile    rules  = RulesFile.CreateDefault();
        public  BridgeState  State  = new();

        private const string ConfigFileName = "config.json";
        private const string RulesFileName  = "rules.json";
        private DateTime configWriteUtc;
        private DateTime rulesWriteUtc;
        private System.Threading.Timer? hotReloadTimer;

        private HttpListener?            listener;
        private CancellationTokenSource? listenerCancellation;
        private CancellationTokenSource? centralCancellation;

        // ── 초기화 ───────────────────────────────────────────────────────────

        public override void Entry(IModHelper helper)
        {
            config = Helper.ReadConfig<BridgeConfig>();

            if (string.IsNullOrWhiteSpace(config.AuthToken))
            {
                config.AuthToken = Guid.NewGuid().ToString("N");
                Helper.WriteConfig(config);
            }

            State = Helper.Data.ReadJsonFile<BridgeState>("state.json") ?? new BridgeState();

            configWriteUtc = StampOf(ConfigFileName);
            EnsureRulesFile();
            ReloadRules(force: true);

            Helper.Events.GameLoop.SaveLoaded             += OnSaveLoaded;
            Helper.Events.GameLoop.DayStarted             += OnDayStarted;
            Helper.Events.GameLoop.UpdateTicked           += OnUpdateTicked;
            Helper.Events.Input.ButtonPressed             += OnButtonPressed;
            Helper.Events.Content.AssetRequested          += OnAssetRequested;
            Helper.Events.Multiplayer.ModMessageReceived  += OnModMessageReceived;

            RegisterChatCommand();
            StartHttpListener();
            StartCentralPolling();

            // 2초마다 config.json / rules.json 수정 여부를 확인한다. 게임 루프와 무관하게 동작한다.
            hotReloadTimer = new System.Threading.Timer(_ => HotReload(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

            Monitor.Log("SOOP Stardew Bridge가 시작되었습니다.", LogLevel.Info);
            Monitor.Log($"로컬 후원 수신 주소: http://127.0.0.1:{config.Port}/event", LogLevel.Info);
            Monitor.Log(string.IsNullOrWhiteSpace(config.CentralServerUrl)
                ? "Central 서버 미설정. config.json에 CentralServerUrl과 AgentToken을 추가하면 관리자 보상을 받을 수 있습니다."
                : $"Central 서버 연결: {config.CentralServerUrl}", LogLevel.Info);
        }

        // 채팅창에 /우편함 을 입력하면 보상 창이 열린다. (/mailbox 도 같음)
        private void RegisterChatCommand()
        {
            try
            {
                ChatCommands.Register(
                    ModManifest.UniqueID + "_mailbox",
                    (command, chat) => OpenClaimMenu(),
                    name => "SOOP 후원 보상 수령 창을 엽니다.",
                    aliases: new[] { "우편함", "mailbox" });
                Monitor.Log("채팅 명령어 등록: /우편함, /mailbox", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"채팅 명령어 등록 실패 (F5는 사용 가능): {ex.Message}", LogLevel.Warn);
            }
        }

        // ── 이벤트 핸들러 ────────────────────────────────────────────────────

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            if (!Context.IsMainPlayer)
            {
                // 플레이어 게임: 접속하면 호스트에 내 대기 보상을 물어본다 (있으면 HUD 알림).
                ClientPendingChests = 0;
                ClientWaiting = false;
                SendToHost(MsgPendingRequest, new PendingRequestMessage { Reason = "login" });
                return;
            }

            State = Helper.Data.ReadJsonFile<BridgeState>("state.json") ?? State;
            loggedFarmerIds.Clear();
            SaveState();
            LogOnlineFarmers();
            ProcessPendingRewards();

            Monitor.Log(
                $"처리 이벤트: {State.ProcessedEventIds.Count}, " +
                $"대기 상자 대상: {State.PendingChests.Count}, " +
                $"대기 몬스터 대상: {State.PendingMonsters.Count}",
                LogLevel.Info);
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            if (Context.IsMainPlayer) ProcessPendingRewards();
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || !Context.IsMainPlayer) return;

            if (e.IsMultipleOf(60)) LogOnlineFarmers();

            while (incomingEvents.TryDequeue(out DonationEvent? d))
                if (d != null) ProcessDonation(d);

            while (incomingCentralJobs.TryDequeue(out CentralJob? j))
            {
                if (j != null)
                {
                    bool ok = ProcessCentralJob(j);
                    if (!string.IsNullOrWhiteSpace(j.Id))
                        _ = AcknowledgeCentralJobAsync(j.Id, ok);
                }
            }

            if (e.IsMultipleOf(60)) ProcessPendingRewards();
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            // F5: 보상 수령 창 열기
            if (e.Button == SButton.F5 && Context.IsPlayerFree)
            {
                OpenClaimMenu();
                Helper.Input.Suppress(e.Button);
            }
        }

        // ── 플레이어 게임 쪽 ─────────────────────────────────────────────────

        private void OpenClaimMenu()
        {
            if (!Context.IsWorldReady) return;
            if (Game1.activeClickableMenu is RewardClaimMenu) return;

            ClientWaiting = true;
            ClientWaitingSince = DateTime.UtcNow;
            Game1.activeClickableMenu = new RewardClaimMenu(this);
            SendToHost(MsgPendingRequest, new PendingRequestMessage { Reason = "query" });
        }

        public void RequestClaim()
        {
            if (ClientWaiting || ClientPendingChests <= 0) return;

            // 인벤토리가 꽉 찼으면 요청하지 않는다 (보상은 서버 대기열에 그대로 남는다).
            if (!Game1.player.couldInventoryAcceptThisItem(ItemRegistry.Create(ChestItemId, 1)))
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage("인벤토리에 공간이 없습니다. 한 칸 이상 비운 뒤 다시 수령하세요.", HUDMessage.error_type));
                return;
            }

            ClientWaiting = true;
            ClientWaitingSince = DateTime.UtcNow;
            SendToHost(MsgClaimRequest, new ClaimRequestMessage());
        }

        private void OnClientPending(PendingMessage msg)
        {
            ClientPendingChests = Math.Max(0, msg.Chests);
            ClientWaiting = false;

            // 접속 직후나 새 보상이 들어왔을 때만 알린다 (창을 열어 조회한 경우는 창에 표시됨).
            if (msg.Reason != "query" && ClientPendingChests > 0)
                Game1.addHUDMessage(new HUDMessage($"SOOP 보상 상자 {ClientPendingChests}개 대기 중! F5 또는 /우편함 으로 수령하세요.", HUDMessage.newQuest_type));
        }

        private void OnClientGrant(GrantMessage msg)
        {
            ClientWaiting = false;
            int granted = Math.Max(0, msg.Chests);
            int returned = 0;

            if (granted > 0)
            {
                Item stack = ItemRegistry.Create(ChestItemId, granted);
                Item? left = Game1.player.addItemToInventory(stack);
                returned = left?.Stack ?? 0;
                if (returned > 0)
                    SendToHost(MsgReturn, new ReturnMessage { Chests = returned });
            }

            int added = granted - returned;
            ClientPendingChests = Math.Max(0, msg.Remaining) + returned;

            if (added > 0)
            {
                Game1.playSound("coin");
                Game1.addHUDMessage(new HUDMessage(
                    returned > 0
                        ? $"보상 상자 {added}개를 받았습니다. 공간이 부족해 {returned}개는 대기 중입니다."
                        : $"보상 상자 {added}개를 받았습니다.",
                    HUDMessage.newQuest_type));
            }
            else
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(
                    granted > 0 ? "인벤토리에 공간이 없습니다. 한 칸 이상 비운 뒤 다시 수령하세요." : "수령할 보상이 없습니다.",
                    HUDMessage.error_type));
            }

            if (ClientPendingChests <= 0 && Game1.activeClickableMenu is RewardClaimMenu menu)
                menu.exitThisMenu();
        }

        // ── 호스트 쪽 ────────────────────────────────────────────────────────

        private void OnHostPendingRequest(long fromId, PendingRequestMessage msg)
        {
            State.PendingChests.TryGetValue(fromId, out int chests);
            SendToPlayer(fromId, MsgPending, new PendingMessage { Chests = chests, Reason = msg.Reason == "login" ? "login" : "query" });
        }

        private void OnHostClaimRequest(long fromId)
        {
            State.PendingChests.TryGetValue(fromId, out int pending);
            int give = Math.Min(Math.Max(0, pending), MaxGrantPerClaim);
            int remaining = pending - give;

            if (give > 0)
            {
                if (remaining > 0) State.PendingChests[fromId] = remaining;
                else State.PendingChests.Remove(fromId);
                lastGrant[fromId] = give;
                SaveState();
                Monitor.Log($"보상 상자 지급: FarmerId={fromId}, 지급={give}, 남은 대기={remaining}", LogLevel.Info);
            }

            SendToPlayer(fromId, MsgGrant, new GrantMessage { Chests = give, Remaining = Math.Max(0, remaining) });
        }

        private void OnHostReturn(long fromId, ReturnMessage msg)
        {
            lastGrant.TryGetValue(fromId, out int granted);
            int back = Math.Min(Math.Max(0, msg.Chests), granted);   // 방금 보낸 양보다 많이 돌려받지 않는다.
            lastGrant.Remove(fromId);
            if (back <= 0) return;

            AddPending(State.PendingChests, fromId, back);
            SaveState();
            Monitor.Log($"보상 상자 반환(인벤토리 부족): FarmerId={fromId}, 반환={back}", LogLevel.Info);
        }

        // 새 상자가 대기열에 들어오면 접속 중인 해당 플레이어에게 알린다.
        private void NotifyPending(long farmerId)
        {
            if (FindOnlineFarmer(farmerId) == null) return;
            State.PendingChests.TryGetValue(farmerId, out int chests);
            SendToPlayer(farmerId, MsgPending, new PendingMessage { Chests = chests, Reason = "notify" });
        }

        // ── 메시지 송수신 ────────────────────────────────────────────────────

        private void SendToHost<T>(string type, T message) where T : notnull
        {
            if (Context.IsMainPlayer)
            {
                // 호스트가 직접 플레이하는 경우 (전용 서버가 아닐 때)
                HandleHostMessage(type, Game1.player.UniqueMultiplayerID, message);
                return;
            }
            Helper.Multiplayer.SendMessage(message, type,
                modIDs: new[] { ModManifest.UniqueID },
                playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID });
        }

        private void SendToPlayer<T>(long farmerId, string type, T message) where T : notnull
        {
            if (farmerId == Game1.player.UniqueMultiplayerID)
            {
                HandleClientMessage(type, message);
                return;
            }
            Helper.Multiplayer.SendMessage(message, type,
                modIDs: new[] { ModManifest.UniqueID },
                playerIDs: new[] { farmerId });
        }

        private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != ModManifest.UniqueID) return;

            try
            {
                if (Context.IsMainPlayer)
                {
                    if (FindOnlineFarmer(e.FromPlayerID) == null) return;   // 접속 중인 플레이어만
                    switch (e.Type)
                    {
                        case MsgPendingRequest: HandleHostMessage(e.Type, e.FromPlayerID, e.ReadAs<PendingRequestMessage>()); break;
                        case MsgClaimRequest:   HandleHostMessage(e.Type, e.FromPlayerID, e.ReadAs<ClaimRequestMessage>());   break;
                        case MsgReturn:         HandleHostMessage(e.Type, e.FromPlayerID, e.ReadAs<ReturnMessage>());         break;
                    }
                }
                else
                {
                    if (e.FromPlayerID != Game1.MasterPlayer.UniqueMultiplayerID) return;   // 호스트가 보낸 것만
                    switch (e.Type)
                    {
                        case MsgPending: HandleClientMessage(e.Type, e.ReadAs<PendingMessage>()); break;
                        case MsgGrant:   HandleClientMessage(e.Type, e.ReadAs<GrantMessage>());   break;
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"모드 메시지 처리 오류 ({e.Type}): {ex.Message}", LogLevel.Warn);
            }
        }

        private void HandleHostMessage(string type, long fromId, object message)
        {
            switch (message)
            {
                case PendingRequestMessage m when type == MsgPendingRequest: OnHostPendingRequest(fromId, m); break;
                case ClaimRequestMessage   when type == MsgClaimRequest:     OnHostClaimRequest(fromId);      break;
                case ReturnMessage m       when type == MsgReturn:           OnHostReturn(fromId, m);         break;
            }
        }

        private void HandleClientMessage(string type, object message)
        {
            switch (message)
            {
                case PendingMessage m when type == MsgPending: OnClientPending(m); break;
                case GrantMessage m   when type == MsgGrant:   OnClientGrant(m);   break;
            }
        }

        // ── 몬스터 자동 소환 (호스트, 접속 중 플레이어) ──────────────────────
        // 상자는 플레이어가 창에서 직접 수령한다.

        private void ProcessPendingRewards()
        {
            if (!Context.IsWorldReady || !Context.IsMainPlayer) return;

            foreach (var entry in State.PendingMonsters.ToArray())
            {
                long farmerId = entry.Key;
                if (entry.Value <= 0) { State.PendingMonsters.Remove(farmerId); SaveState(); continue; }

                Farmer? farmer = FindOnlineFarmer(farmerId);
                if (farmer?.currentLocation == null) continue;

                for (int i = 0; i < entry.Value; i++)
                {
                    Vector2 tile  = farmer.Tile;
                    Vector2 spawn = new(tile.X + random.Next(-3, 4), tile.Y + random.Next(-3, 4));
                    farmer.currentLocation.characters.Add(new GreenSlime(spawn * 64f));
                }
                State.PendingMonsters.Remove(farmerId);
                Monitor.Log($"몬스터 소환 완료: FarmerId={farmerId}, 수량={entry.Value}", LogLevel.Info);
                SaveState();
            }
        }

        // ── 감사 로그 ────────────────────────────────────────────────────────

        private void LogOnlineFarmers()
        {
            foreach (Farmer farmer in Game1.getOnlineFarmers())
            {
                long fid = farmer.UniqueMultiplayerID;
                if (!loggedFarmerIds.Add(fid)) continue;
                Monitor.Log($"접속 플레이어 확인: Name={farmer.Name}, FarmerId={fid}", LogLevel.Info);
            }
        }

        // ── 에셋 ─────────────────────────────────────────────────────────────

        private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo(ChestTextureAsset))
            {
                e.LoadFromModFile<Texture2D>("assets/reward_chest.png", AssetLoadPriority.Exclusive);
                return;
            }

            if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, ObjectData>().Data;
                    data[ChestItemId] = new ObjectData
                    {
                        Name        = ChestItemId,
                        DisplayName = "SOOP 보상 상자",
                        Description = "SOOP 후원으로 받은 특별한 보상 상자.",
                        Type        = "Basic",
                        Category    = -8,
                        Price       = 0,
                        Texture     = ChestTextureAsset,
                        SpriteIndex = ChestSpriteIndex
                    };
                });
            }
        }

        // ── Central 서버 폴링 ────────────────────────────────────────────────

        private void StartCentralPolling()
        {
            centralCancellation = new CancellationTokenSource();
            _ = CentralPollingLoopAsync(centralCancellation.Token);
        }

        private async Task CentralPollingLoopAsync(CancellationToken ct)
        {
            string announced = "";
            while (!ct.IsCancellationRequested)
            {
                // 매 루프마다 최신 config를 읽는다. CentralServerUrl/AgentToken/PollIntervalSeconds를 고치면 바로 반영된다.
                BridgeConfig cfg = config;

                if (string.IsNullOrWhiteSpace(cfg.CentralServerUrl) || string.IsNullOrWhiteSpace(cfg.AgentToken))
                {
                    announced = "";
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                string baseUrl = cfg.CentralServerUrl.TrimEnd('/');
                if (announced != baseUrl)
                {
                    Monitor.Log($"Central 서버 폴링 중: {baseUrl}", LogLevel.Info);
                    announced = baseUrl;
                }

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/agent/poll");
                    req.Headers.Add("X-Agent-Token", cfg.AgentToken);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using HttpResponseMessage resp = await centralHttp.SendAsync(req, ct).ConfigureAwait(false);
                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var poll = JsonSerializer.Deserialize<CentralPollResponse>(json, JsonOptions);
                        if (poll?.Job != null) incomingCentralJobs.Enqueue(poll.Job);
                    }
                    else if ((int)resp.StatusCode == 401)
                    {
                        Monitor.Log("Central 서버 인증 실패: AgentToken 또는 방송 활성 상태를 확인하세요.", LogLevel.Error);
                        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { Monitor.Log($"Central 연결 오류(재시도 예정): {ex.Message}", LogLevel.Trace); }

                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, cfg.PollIntervalSeconds)), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task AcknowledgeCentralJobAsync(string jobId, bool processed)
        {
            try
            {
                string baseUrl = config.CentralServerUrl.TrimEnd('/');
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/agent/ack");
                req.Headers.Add("X-Agent-Token", config.AgentToken);
                req.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    jobId,
                    status  = processed ? "COMPLETED" : "REVIEW",
                    message = processed ? "SMAPI가 보상을 처리했습니다." : "FarmerId가 없거나 플레이어가 오프라인 상태입니다."
                }), Encoding.UTF8, "application/json");
                using HttpResponseMessage resp = await centralHttp.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    Monitor.Log($"Central ack 전송 실패: job={jobId}, HTTP={(int)resp.StatusCode}", LogLevel.Warn);
            }
            catch (Exception ex) { Monitor.Log($"Central ack 오류: job={jobId}, {ex.Message}", LogLevel.Warn); }
        }

        // ── 로컬 HTTP ────────────────────────────────────────────────────────

        private void StartHttpListener()
        {
            if (config.Port < 1 || config.Port > 65535)
            { Monitor.Log("config.json의 Port 값이 잘못되었습니다.", LogLevel.Error); return; }

            listenerCancellation = new CancellationTokenSource();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{config.Port}/");
            try
            {
                listener.Start();
                _ = ListenLoopAsync(listener, listenerCancellation.Token);
            }
            catch (Exception ex)
            { Monitor.Log($"HTTP 수신부 시작 실패: {ex}", LogLevel.Error); listener.Close(); listener = null; }
        }

        private async Task ListenLoopAsync(HttpListener httpListener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && httpListener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await httpListener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Monitor.Log($"HTTP 수신 오류: {ex.Message}", LogLevel.Warn); continue; }
                _ = HandleHttpRequestAsync(ctx);
            }
        }

        private async Task HandleHttpRequestAsync(HttpListenerContext context)
        {
            try
            {
                var req  = context.Request;
                var resp = context.Response;

                if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                { await WriteResponseAsync(resp, 405, "{\"error\":\"POST required\"}"); return; }

                if (!string.Equals(req.Url?.AbsolutePath, "/event", StringComparison.OrdinalIgnoreCase))
                { await WriteResponseAsync(resp, 404, "{\"error\":\"not found\"}"); return; }

                if (!FixedTimeEquals(req.Headers["X-SOOP-Token"] ?? "", config.AuthToken))
                { await WriteResponseAsync(resp, 401, "{\"error\":\"unauthorized\"}"); return; }

                if (req.ContentLength64 > 64 * 1024)
                { await WriteResponseAsync(resp, 413, "{\"error\":\"payload too large\"}"); return; }

                string body;
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);

                DonationEvent? donation;
                try { donation = JsonSerializer.Deserialize<DonationEvent>(body, JsonOptions); }
                catch (JsonException) { await WriteResponseAsync(resp, 400, "{\"error\":\"invalid json\"}"); return; }

                if (donation == null || string.IsNullOrWhiteSpace(donation.EventId) ||
                    string.IsNullOrWhiteSpace(donation.SoopId) || donation.Balloons <= 0)
                { await WriteResponseAsync(resp, 400, "{\"error\":\"eventId, soopId and positive balloons are required\"}"); return; }

                incomingEvents.Enqueue(donation);
                await WriteResponseAsync(resp, 202, "{\"accepted\":true}");
            }
            catch (Exception ex)
            {
                Monitor.Log($"HTTP 요청 처리 오류: {ex.Message}", LogLevel.Warn);
                try { await WriteResponseAsync(context.Response, 500, "{\"error\":\"internal error\"}"); } catch { }
            }
        }

        private static async Task WriteResponseAsync(HttpListenerResponse r, int code, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            r.StatusCode = code; r.ContentType = "application/json; charset=utf-8";
            r.ContentLength64 = bytes.Length;
            await r.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            r.Close();
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0;
        }

        // ── 보상 로직 ────────────────────────────────────────────────────────

        private void ProcessDonation(DonationEvent donation)
        {
            string eventId = donation.EventId.Trim();
            string soopId  = donation.SoopId.Trim();

            if (State.ProcessedEventIds.Contains(eventId))
            { Monitor.Log($"중복 이벤트 무시: {eventId}", LogLevel.Warn); return; }

            if (donation.Balloons <= 0)
            { Monitor.Log($"잘못된 후원 수량: {soopId}", LogLevel.Warn); return; }

            long? targetFarmerId = donation.FarmerId > 0 ? donation.FarmerId : null;
            if (targetFarmerId == null)
            { Monitor.Log($"FarmerId 없음 (soopId={soopId})", LogLevel.Warn); RememberEvent(eventId); return; }

            int balloons = donation.Balloons;
            var (chests, monsters) = ComputeRewards(rules, balloons);   // rules.json 기준

            if (chests == 0 && monsters == 0)
            { RememberEvent(eventId); Monitor.Log($"보상 기준 미달: {soopId}, {balloons}개", LogLevel.Info); return; }

            if (chests   > 0) AddPending(State.PendingChests,   targetFarmerId.Value, chests);
            if (monsters > 0) AddPending(State.PendingMonsters, targetFarmerId.Value, monsters);
            RememberEvent(eventId);

            Monitor.Log($"후원 이벤트 접수: {eventId}, {soopId}, {balloons}개 → 상자:{chests} 몬스터:{monsters}", LogLevel.Info);
            if (chests > 0) NotifyPending(targetFarmerId.Value);
            ProcessPendingRewards();
        }

        private bool ProcessCentralJob(CentralJob job)
        {
            string jobKey = "central:" + job.Id.Trim();
            if (State.ProcessedEventIds.Contains(jobKey))
            { Monitor.Log($"Central 중복 무시: {job.Id}", LogLevel.Warn); return true; }

            long? fid = job.GetFarmerIdLong();
            if (fid == null || fid <= 0)
            { Monitor.Log($"Central FarmerId 없음: {job.Id}", LogLevel.Warn); return false; }

            if      (job.RewardType == "chest")   AddPending(State.PendingChests,   fid.Value, job.Amount);
            else if (job.RewardType == "monster")  AddPending(State.PendingMonsters, fid.Value, job.Amount);
            else { Monitor.Log($"알 수 없는 보상 종류: {job.RewardType}", LogLevel.Warn); return false; }

            RememberEvent(jobKey);
            Monitor.Log($"Central 보상 접수: {job.Id}, {job.SoopId}, {job.RewardType}×{job.Amount}, FarmerId={fid}", LogLevel.Info);
            if (job.RewardType == "chest") NotifyPending(fid.Value);
            ProcessPendingRewards();
            return true;
        }

        private void RememberEvent(string id)
        {
            State.ProcessedEventIds.Add(id);
            while (State.ProcessedEventIds.Count > 50000) State.ProcessedEventIds.RemoveAt(0);
            SaveState();
        }

        private static void AddPending(Dictionary<long, int> dict, long fid, int count)
        {
            if (count <= 0) return;
            dict.TryGetValue(fid, out int ex);
            dict[fid] = ex + count;
        }

        private static Farmer? FindOnlineFarmer(long fid)
            => Game1.getOnlineFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == fid);

        public void SaveState() => Helper.Data.WriteJsonFile("state.json", State);

        // ── 설정 자동 재로드 (config.json / rules.json) ──────────────────────
        // DLL을 바꾸지 않아도 두 파일을 수정하면 2초 안에 반영된다.
        // 파일이 잘못되어 있으면 오류를 로그에 남기고 이전 값을 그대로 쓴다.

        private DateTime StampOf(string fileName)
        {
            string path = Path.Combine(Helper.DirectoryPath, fileName);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }

        private void HotReload()
        {
            try
            {
                ReloadConfig();
                ReloadRules(force: false);
            }
            catch (Exception ex)
            {
                Monitor.Log($"설정 자동 재로드 오류: {ex.Message}", LogLevel.Warn);
            }
        }

        private void ReloadConfig()
        {
            DateTime stamp = StampOf(ConfigFileName);
            if (stamp == configWriteUtc || stamp == DateTime.MinValue) return;
            configWriteUtc = stamp;

            try
            {
                // Helper.ReadConfig는 읽을 때마다 파일을 다시 써서 수정 시각이 바뀌므로
                // (그러면 자기 자신을 변경으로 감지해 무한 반복된다) 쓰기 없는 ReadJsonFile을 쓴다.
                BridgeConfig? loaded = Helper.Data.ReadJsonFile<BridgeConfig>(ConfigFileName);
                if (loaded == null)
                {
                    Monitor.Log("config.json이 비어 있어 이전 설정을 유지합니다.", LogLevel.Error);
                    return;
                }
                BridgeConfig current = config;

                if (string.IsNullOrWhiteSpace(loaded.AuthToken))
                    loaded.AuthToken = current.AuthToken;   // 토큰을 비워도 기존 값을 유지한다.
                if (loaded.Port != current.Port)
                {
                    Monitor.Log($"Port 변경({current.Port} → {loaded.Port})은 서버 재시작 후 적용됩니다. 지금은 {current.Port}를 계속 사용합니다.", LogLevel.Warn);
                    loaded.Port = current.Port;
                }

                config = loaded;
                Monitor.Log("config.json 변경을 반영했습니다.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"config.json 오류, 이전 설정을 유지합니다: {ex.Message}", LogLevel.Error);
            }
        }

        private void EnsureRulesFile()
        {
            if (File.Exists(Path.Combine(Helper.DirectoryPath, RulesFileName))) return;
            Helper.Data.WriteJsonFile(RulesFileName, RulesFile.CreateDefault());
            Monitor.Log("rules.json이 없어 기본 규칙으로 만들었습니다.", LogLevel.Info);
        }

        private void ReloadRules(bool force)
        {
            DateTime stamp = StampOf(RulesFileName);
            if (stamp == DateTime.MinValue) return;
            if (!force && stamp == rulesWriteUtc) return;
            rulesWriteUtc = stamp;

            try
            {
                RulesFile? loaded = Helper.Data.ReadJsonFile<RulesFile>(RulesFileName);
                if (loaded == null)
                {
                    Monitor.Log("rules.json이 비어 있어 이전 규칙을 유지합니다.", LogLevel.Error);
                    return;
                }

                string? error = ValidateRules(loaded);
                if (error != null)
                {
                    Monitor.Log($"rules.json 오류, 이전 규칙을 유지합니다: {error}", LogLevel.Error);
                    return;
                }

                rules = loaded;
                Monitor.Log($"보상 규칙을 반영했습니다: {loaded.Rules.Count}개", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"rules.json을 읽지 못해 이전 규칙을 유지합니다: {ex.Message}", LogLevel.Error);
            }
        }

        // 규칙 형식은 Central의 config/rules.json과 같다.
        private static string? ValidateRules(RulesFile file)
        {
            if (file.Rules == null || file.Rules.Count == 0)
                return "rules 배열이 비어 있습니다.";

            for (int i = 0; i < file.Rules.Count; i++)
            {
                RewardRule r = file.Rules[i];
                string at = $"rules[{i}]";

                if (r.Type != "chest" && r.Type != "monster") return $"{at}: type은 chest 또는 monster여야 합니다.";
                if (r.MinBalloons   is < 1) return $"{at}: minBalloons는 1 이상이어야 합니다.";
                if (r.MaxBalloons   is < 1) return $"{at}: maxBalloons는 1 이상이어야 합니다.";
                if (r.ExactBalloons is < 1) return $"{at}: exactBalloons는 1 이상이어야 합니다.";
                if (r.PerBalloons   is < 1) return $"{at}: perBalloons는 1 이상이어야 합니다.";
                if (r.Amount        is < 1) return $"{at}: amount는 1 이상이어야 합니다.";
                if (r.ExactBalloons.HasValue && (r.MinBalloons.HasValue || r.MaxBalloons.HasValue))
                    return $"{at}: exactBalloons는 minBalloons/maxBalloons와 함께 쓸 수 없습니다.";
                if (r.MinBalloons.HasValue && r.MaxBalloons.HasValue && r.MaxBalloons < r.MinBalloons)
                    return $"{at}: maxBalloons는 minBalloons보다 작을 수 없습니다.";
                if (r.PerBalloons.HasValue && r.Amount.HasValue)
                    return $"{at}: perBalloons와 amount는 함께 쓸 수 없습니다.";
            }

            return null;
        }

        // 조건에 맞는 모든 규칙의 수량을 type별로 합산한다.
        private static (int chests, int monsters) ComputeRewards(RulesFile file, int balloons)
        {
            const int MaxAmount = 1000;
            int chests = 0, monsters = 0;

            foreach (RewardRule r in file.Rules)
            {
                bool matches = r.ExactBalloons.HasValue
                    ? balloons == r.ExactBalloons.Value
                    : balloons >= (r.MinBalloons ?? 1) && balloons <= (r.MaxBalloons ?? int.MaxValue);
                if (!matches) continue;

                int amount = r.PerBalloons.HasValue ? balloons / r.PerBalloons.Value : (r.Amount ?? 1);
                if (amount < 1) continue;

                if (r.Type == "chest") chests += amount; else monsters += amount;
            }

            return (Math.Min(chests, MaxAmount), Math.Min(monsters, MaxAmount));
        }

        private sealed class RulesFile
        {
            public List<RewardRule> Rules { get; set; } = new();

            public static RulesFile CreateDefault() => new()
            {
                Rules = new List<RewardRule>
                {
                    new() { Name = "monster-33",    Type = "monster", MinBalloons = 33, Amount = 1 },
                    new() { Name = "chest-per-100", Type = "chest",   MinBalloons = 100, MaxBalloons = 999, PerBalloons = 100 },
                    new() { Name = "chest-1000",    Type = "chest",   ExactBalloons = 1000, Amount = 11 }
                }
            };
        }

        private sealed class RewardRule
        {
            public string Name          { get; set; } = "";
            public string Type          { get; set; } = "";
            public int?   MinBalloons   { get; set; }
            public int?   MaxBalloons   { get; set; }
            public int?   ExactBalloons { get; set; }
            public int?   PerBalloons   { get; set; }
            public int?   Amount        { get; set; }
        }

        // ── 데이터 클래스 ────────────────────────────────────────────────────

        private sealed class BridgeConfig
        {
            public int    Port             { get; set; } = 8765;
            public string AuthToken        { get; set; } = "";
            public string CentralServerUrl { get; set; } = "";
            public string AgentToken       { get; set; } = "";
            public int    PollIntervalSeconds { get; set; } = 8;
        }

        private sealed class DonationEvent
        {
            public string EventId  { get; set; } = "";
            public string SoopId   { get; set; } = "";
            public int    Balloons { get; set; }
            public long   FarmerId { get; set; }
        }

        private sealed class CentralPollResponse
        {
            public bool        Ok  { get; set; }
            public CentralJob? Job { get; set; }
        }

        private sealed class CentralJob
        {
            public string  Id         { get; set; } = "";
            public string  SoopId     { get; set; } = "";
            public string? FarmerId   { get; set; }
            public string  RewardType { get; set; } = "";
            public int     Amount     { get; set; }
            public string  Reason     { get; set; } = "";

            public long? GetFarmerIdLong()
                => long.TryParse(FarmerId, out long v) ? v : null;
        }

        public sealed class BridgeState
        {
            public List<string>          ProcessedEventIds { get; set; } = new();
            public Dictionary<long, int> PendingChests     { get; set; } = new();
            public Dictionary<long, int> PendingMonsters   { get; set; } = new();
        }
    }
}
