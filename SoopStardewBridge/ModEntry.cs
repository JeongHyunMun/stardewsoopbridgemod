
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
    // ── 보상 수령 UI ──────────────────────────────────────────────────────────
    internal sealed class RewardClaimMenu : IClickableMenu
    {
        private const int MenuWidth  = 500;
        private const int MenuHeight = 340;

        private readonly ModEntry _mod;
        private readonly long     _farmerId;

        private ClickableTextureComponent _claimBtn = null!;
        private ClickableTextureComponent _closeBtn = null!;

        public RewardClaimMenu(ModEntry mod, Farmer farmer) : base(
            (Game1.uiViewport.Width  - MenuWidth)  / 2,
            (Game1.uiViewport.Height - MenuHeight) / 2,
            MenuWidth, MenuHeight, showUpperRightCloseButton: true)
        {
            _mod      = mod;
            _farmerId = farmer.UniqueMultiplayerID;
            RebuildButtons();
            Game1.playSound("shwip");
        }

        private void RebuildButtons()
        {
            int cx = xPositionOnScreen + width  / 2;
            int by = yPositionOnScreen + height - 90;

            _claimBtn = new ClickableTextureComponent(
                new Rectangle(cx - 120, by, 240, 60),
                Game1.mouseCursors,
                new Rectangle(128, 256, 64, 20), 3f)
            { myID = 100 };

            _closeBtn = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 44, yPositionOnScreen - 4, 48, 48),
                Game1.mouseCursors,
                new Rectangle(337, 494, 12, 12), 4f)
            { myID = 101 };

            populateClickableComponentList();
        }

        private (int chests, int monsters) GetPending()
        {
            _mod.State.PendingChests  .TryGetValue(_farmerId, out int c);
            _mod.State.PendingMonsters.TryGetValue(_farmerId, out int m);
            return (c, m);
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect,
                new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height),
                Color.Black * 0.4f);

            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, false);

            string title = "SOOP 후원 보상";
            Vector2 ts = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - ts.X) / 2f, yPositionOnScreen + 24),
                Game1.textColor);

            b.Draw(Game1.staminaRect,
                new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + 72, width - 48, 2),
                Color.Gray * 0.5f);

            var (chests, monsters) = GetPending();

            if (chests == 0 && monsters == 0)
            {
                string empty = "수령할 보상이 없습니다.";
                Vector2 es = Game1.dialogueFont.MeasureString(empty);
                b.DrawString(Game1.dialogueFont, empty,
                    new Vector2(xPositionOnScreen + (width - es.X) / 2f,
                                yPositionOnScreen + height / 2f - es.Y / 2f),
                    Game1.textColor * 0.6f);
            }
            else
            {
                int y  = yPositionOnScreen + 100;
                int lh = 48;

                if (chests > 0)
                {
                    b.Draw(Game1.objectSpriteSheet,
                        new Rectangle(xPositionOnScreen + 40, y, 40, 40),
                        Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, 220, 16, 16),
                        Color.White);
                    b.DrawString(Game1.dialogueFont,
                        $"SOOP 보상 상자  ×  {chests}",
                        new Vector2(xPositionOnScreen + 92, y + 4),
                        Game1.textColor);
                    y += lh;
                }

                if (monsters > 0)
                {
                    b.Draw(Game1.mouseCursors,
                        new Rectangle(xPositionOnScreen + 40, y, 36, 36),
                        new Rectangle(16, 592, 16, 16), Color.White);
                    b.DrawString(Game1.dialogueFont,
                        $"몬스터 소환  ×  {monsters}",
                        new Vector2(xPositionOnScreen + 92, y + 4),
                        new Color(220, 80, 80));
                }

                // 수령 버튼
                IClickableMenu.drawTextureBox(b,
                    _claimBtn.bounds.X - 4, _claimBtn.bounds.Y - 4,
                    _claimBtn.bounds.Width + 8, _claimBtn.bounds.Height + 8,
                    Color.White);
                string btnLabel = "전체 수령";
                Vector2 bs = Game1.dialogueFont.MeasureString(btnLabel);
                b.DrawString(Game1.dialogueFont, btnLabel,
                    new Vector2(
                        _claimBtn.bounds.X + (_claimBtn.bounds.Width  - bs.X) / 2f,
                        _claimBtn.bounds.Y + (_claimBtn.bounds.Height - bs.Y) / 2f),
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

            var (chests, monsters) = GetPending();
            if (chests == 0 && monsters == 0) return;

            if (_claimBtn.containsPoint(x, y))
            {
                bool claimed = _mod.ClaimRewardsForFarmer(_farmerId);
                if (claimed) Game1.playSound("coin");

                var (c2, m2) = GetPending();
                if (c2 == 0 && m2 == 0)
                    exitThisMenu();
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape || key == Keys.E)
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

        private BridgeConfig config = new();
        public  BridgeState  State  = new();

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

            Helper.Events.GameLoop.SaveLoaded    += OnSaveLoaded;
            Helper.Events.GameLoop.DayStarted    += OnDayStarted;
            Helper.Events.GameLoop.UpdateTicked  += OnUpdateTicked;
            Helper.Events.Input.ButtonPressed    += OnButtonPressed;
            Helper.Events.Content.AssetRequested += OnAssetRequested;

            StartHttpListener();
            StartCentralPolling();

            Monitor.Log("SOOP Stardew Bridge가 시작되었습니다.", LogLevel.Info);
            Monitor.Log($"로컬 후원 수신 주소: http://127.0.0.1:{config.Port}/event", LogLevel.Info);
            Monitor.Log(string.IsNullOrWhiteSpace(config.CentralServerUrl)
                ? "Central 서버 미설정. config.json에 CentralServerUrl과 AgentToken을 추가하면 관리자 보상을 받을 수 있습니다."
                : $"Central 서버 연결: {config.CentralServerUrl}", LogLevel.Info);
        }

        // ── 이벤트 핸들러 ────────────────────────────────────────────────────

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
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
            => ProcessPendingRewards();

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

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
            if (!Context.IsWorldReady) return;

            Farmer farmer = Game1.player;

            // F5: 보상 수령 창 열기
            if (e.Button == SButton.F5 && Game1.activeClickableMenu == null)
            {
                OpenClaimMenu(farmer);
                return;
            }

            // 우편함 앞 상호작용: 보상 있으면 수령 창 먼저 띄움
            if ((e.Button == SButton.MouseLeft || e.Button == SButton.ControllerA) &&
                Game1.activeClickableMenu == null &&
                farmer.currentLocation?.Name == "FarmHouse")
            {
                Vector2 tile = farmer.Tile;
                // 우편함 타일은 세이브마다 다를 수 있으므로 편지통 오브젝트 감지
                foreach (var obj in farmer.currentLocation.objects.Values)
                {
                    if (obj.Name == "Mailbox" && Vector2.Distance(obj.TileLocation, tile) <= 1.5f)
                    {
                        var (c, m) = GetPendingForFarmer(farmer.UniqueMultiplayerID);
                        if (c > 0 || m > 0)
                        {
                            OpenClaimMenu(farmer);
                            Helper.Input.Suppress(e.Button);
                        }
                        break;
                    }
                }
            }

            // F6 테스트: 상자 1개 추가
            if (e.Button == SButton.F6)
            {
                AddPending(State.PendingChests, farmer.UniqueMultiplayerID, 1);
                SaveState();
                Monitor.Log($"[F6 테스트] {farmer.Name}에게 보상 상자 1개 추가", LogLevel.Info);
                Game1.addHUDMessage(new HUDMessage("테스트: 보상 상자 1개 대기 추가 (F5로 수령)", HUDMessage.newQuest_type));
            }
        }

        private void OpenClaimMenu(Farmer farmer)
        {
            var (c, m) = GetPendingForFarmer(farmer.UniqueMultiplayerID);
            if (c == 0 && m == 0)
            {
                Game1.addHUDMessage(new HUDMessage("수령할 보상이 없습니다.", HUDMessage.error_type));
                return;
            }
            Game1.activeClickableMenu = new RewardClaimMenu(this, farmer);
        }

        private (int chests, int monsters) GetPendingForFarmer(long farmerId)
        {
            State.PendingChests  .TryGetValue(farmerId, out int c);
            State.PendingMonsters.TryGetValue(farmerId, out int m);
            return (c, m);
        }

        // ── 보상 수령 (메뉴에서 호출) ────────────────────────────────────────
        // 반환값: true = 일부라도 지급됨, false = 인벤토리 꽉 차서 아무것도 못 줌

        public bool ClaimRewardsForFarmer(long farmerId)
        {
            Farmer? farmer = FindOnlineFarmer(farmerId);
            if (farmer == null) return false;

            bool anyGiven = false;

            // 상자 지급
            if (State.PendingChests.TryGetValue(farmerId, out int chests) && chests > 0)
            {
                // 인벤토리 여유 확인
                int freeSlots = farmer.MaxItems - farmer.Items.Where(i => i != null).Count();

                if (freeSlots <= 0)
                {
                    Game1.addHUDMessage(new HUDMessage(
                        "인벤토리가 꽉 찼습니다. 공간을 확보한 뒤 다시 수령하세요.",
                        HUDMessage.error_type));
                    Monitor.Log($"보상 상자 수령 실패: 인벤토리 꽉 참 (FarmerId={farmerId}, 대기={chests})", LogLevel.Warn);
                    // 상자는 지급 안 하고 그대로 대기 유지
                }
                else
                {
                    int canGive  = Math.Min(chests, freeSlots);
                    int leftover = chests - canGive;

                    for (int i = 0; i < canGive; i++)
                    {
                        var item = ItemRegistry.Create(ChestItemId, 1);
                        farmer.addItemToInventoryBool(item);
                    }

                    if (leftover > 0)
                    {
                        State.PendingChests[farmerId] = leftover;
                        Game1.addHUDMessage(new HUDMessage(
                            $"상자 {canGive}개 지급. 인벤토리 부족으로 {leftover}개는 대기 중입니다.",
                            HUDMessage.newQuest_type));
                    }
                    else
                    {
                        State.PendingChests.Remove(farmerId);
                    }

                    Monitor.Log($"보상 상자 수령: FarmerId={farmerId}, 지급={canGive}, 잔여={leftover}", LogLevel.Info);
                    anyGiven = true;
                }
            }

            // 몬스터 소환
            if (State.PendingMonsters.TryGetValue(farmerId, out int monsters) && monsters > 0)
            {
                for (int i = 0; i < monsters; i++)
                {
                    Vector2 tile  = farmer.Tile;
                    Vector2 spawn = new(tile.X + random.Next(-3, 4), tile.Y + random.Next(-3, 4));
                    farmer.currentLocation.characters.Add(new GreenSlime(spawn * 64f));
                }
                State.PendingMonsters.Remove(farmerId);
                Monitor.Log($"몬스터 소환 완료: FarmerId={farmerId}, 수량={monsters}", LogLevel.Info);
                anyGiven = true;
            }

            SaveState();
            return anyGiven;
        }

        // ── 자동 처리 (접속 중 플레이어에게 보상 대기 알림) ─────────────────

        private void ProcessPendingRewards()
        {
            if (!Context.IsWorldReady) return;

            foreach (var entry in State.PendingChests.ToArray())
            {
                long farmerId = entry.Key;
                if (entry.Value <= 0) { State.PendingChests.Remove(farmerId); SaveState(); continue; }

                Farmer? farmer = FindOnlineFarmer(farmerId);
                if (farmer == null) continue;

                // 몬스터는 자동 소환, 상자는 HUD 알림 후 F5로 수령 유도
                Game1.addHUDMessage(new HUDMessage(
                    $"보상 상자 {entry.Value}개 대기 중! F5를 눌러 수령하세요.",
                    HUDMessage.newQuest_type));
            }

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
            if (string.IsNullOrWhiteSpace(config.CentralServerUrl) ||
                string.IsNullOrWhiteSpace(config.AgentToken)) return;

            centralCancellation = new CancellationTokenSource();
            _ = CentralPollingLoopAsync(centralCancellation.Token);
            Monitor.Log("Central 서버 폴링을 시작했습니다.", LogLevel.Info);
        }

        private async Task CentralPollingLoopAsync(CancellationToken ct)
        {
            string baseUrl = config.CentralServerUrl.TrimEnd('/');
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/agent/poll");
                    req.Headers.Add("X-Agent-Token", config.AgentToken);
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

                try { await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
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

            int balloons = donation.Balloons, chests = 0, monsters = 0;
            if (config.TestMode)
            {
                if (balloons == 1) monsters = 1;
                else if (balloons == 2) chests = 1;
                else if (balloons == 3) chests = 11;
            }
            else
            {
                if (balloons >= 33) monsters = 1;
                if (balloons == 1000) chests = 11;
                else if (balloons >= 100 && balloons < 1000) chests = balloons / 100;
            }

            if (chests == 0 && monsters == 0)
            { RememberEvent(eventId); Monitor.Log($"보상 기준 미달: {soopId}, {balloons}개", LogLevel.Info); return; }

            if (chests   > 0) AddPending(State.PendingChests,   targetFarmerId.Value, chests);
            if (monsters > 0) AddPending(State.PendingMonsters, targetFarmerId.Value, monsters);
            RememberEvent(eventId);

            Monitor.Log($"후원 이벤트 접수: {eventId}, {soopId}, {balloons}개 → 상자:{chests} 몬스터:{monsters}", LogLevel.Info);
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

        // ── 데이터 클래스 ────────────────────────────────────────────────────

        private sealed class BridgeConfig
        {
            public int    Port             { get; set; } = 8765;
            public string AuthToken        { get; set; } = "";
            public string CentralServerUrl { get; set; } = "";
            public string AgentToken       { get; set; } = "";
            public bool   TestMode         { get; set; } = true;
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
