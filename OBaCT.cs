using BepInEx;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Erenshor
{
    [BepInPlugin("erenshor.obact", "OBaCT [Overhead Bars + Combat Text]", "1.7.4")]
    public class OBaCT : BaseUnityPlugin
    {
        public enum SuppressionMode
        {
            Off,
            Hard,
            Fade
        }

        public static OBaCT Instance;

        // Track last known states for flip logging
        private bool lastAuctionOpen, lastBankOpen, lastEscMenuOpen, lastGroupOpen, lastHelpOpen,
                     lastInvStatsOpen, lastItemInfoOpen, lastLootOpen, lastMapOpen, lastNpcDialogOpen,
                     lastOptionsOpen, lastQuestOpen, lastRestOpen, lastReviveOpen, lastRunOpen,
                     lastInspectOpen, lastSimTradeOpen, lastSmithOpen, lastSkillsOpen, lastTradeOpen,
                     lastTutOpen, lastVendorOpen, lastLocalLogOpen, lastChatOpen, lastCombatLogOpen;

        // === Globals ===
        private Camera cam;
        private float rescanTimer;
        private GUIStyle hpStyle, nameStyle, percentStyle;
        private int playerLevelCached = 1;
        private float playerLevelTimer;
        private Rect menuRect = new Rect(60f, 120f, 460f, 600f);
        private GUIStyle menuHeader;
        private GUIStyle headerStyle;
        private GUIStyle lineStyle;

        public static readonly List<Tracked> tracked = new List<Tracked>();
        public static readonly List<FloatingNum> floaters = new List<FloatingNum>();
        public static ConfigEntry<SuppressionMode> SuppressMode;
        public static ConfigEntry<bool> EnableBars, EnableDamageText, ShowName, ShowLevel, ShowHpNumbers, ShowHpPercent, ShowPortrait, ShowManaBar, FctShadow;
        public static ConfigEntry<float> MaxDistance, BarWidth, BarHeight, YOffset, DmgTextScale, DmgTextLifetime, FctRiseSpeed, FctJitter;
        public static ConfigEntry<int> NameFontSize, HpFontSize, PortraitSize;
        public static ConfigEntry<float> FadeAlpha;
        public static Font customFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
        public static Font wowFont;
        public static Font wowFontBold;
        public static ConfigEntry<string> CombatTextTheme;

        private static Texture2D hpGradientTex;
        private static RestWindow _restWin;
        private static RunWindow _runWin;
        private static GameObject _reviveWin;
        private static GameObject _npcDialog;
        private static GameObject _spellGo, _skillGo, _mainMenu, _minimapCanvas, _minimapPanel;
        private static float _uiRefetchCooldown = 1f, _uiRefetchTimer = 0f;
        private bool showMenu = false;
        private Vector2 scrollPos = Vector2.zero; // <-- stores scrollbar position

        private void Awake()
        {
            Instance = this;

            // === Config setup ===
            EnableBars = Config.Bind("General", "EnableBars", true, "Draw overhead bars for entities with Stats.");
            EnableDamageText = Config.Bind("General", "EnableDamageText", true, "Show floating damage numbers from HP deltas.");
            BarWidth = Config.Bind("Bars", "BarWidth", 180f, "Unit frame width (px).");
            BarHeight = Config.Bind("Bars", "BarHeight", 20f, "Health bar height (px).");
            YOffset = Config.Bind("Bars", "YOffset", 2.6f, "Vertical offset above head (meters).");
            MaxDistance = Config.Bind("Bars", "MaxDistance", 46f, "Max distance to render bars (meters).");

            ShowName = Config.Bind("Labels", "ShowName", true, "Show name strip above frame.");
            ShowLevel = Config.Bind("Labels", "ShowLevel", true, "Show level box next to the name.");
            ShowHpNumbers = Config.Bind("Labels", "ShowHpNumbers", true, "Show numeric HP inside bar.");
            ShowHpPercent = Config.Bind("Labels", "ShowHpPercent", true, "Show HP percent to the right of bar.");
            NameFontSize = Config.Bind("Labels", "NameFontSize", 14, "Name label font size.");
            HpFontSize = Config.Bind("Labels", "HpFontSize", 15, "HP text font size.");

            ShowPortrait = Config.Bind("Portrait", "ShowPortrait", true, "Show portrait box on the left.");
            PortraitSize = Config.Bind("Portrait", "PortraitSize", 42, "Portrait size in pixels.");

            ShowManaBar = Config.Bind("Mana", "ShowManaBar", true, "Show mana bar (blue) under the HP bar.");

            DmgTextScale = Config.Bind("DamageText", "Scale", 1.2f, "Base size multiplier for numbers.");
            DmgTextLifetime = Config.Bind("DamageText", "Lifetime", 1.15f, "Seconds numbers stay before fade.");
            FctRiseSpeed = Config.Bind("DamageText", "RisePerSec", 0.85f, "World-space rise speed.");
            FctJitter = Config.Bind("DamageText", "Jitter", 10f, "Horizontal wobble in pixels.");
            FctShadow = Config.Bind("DamageText", "Shadow", true, "Draw black shadow outline for readability.");

            SuppressMode = Config.Bind("Suppression", "Mode", SuppressionMode.Hard,
                "Choose how OBaCT handles suppression: Off = never suppress, Hard = hide bars fully, Fade = fade bars when overlapped.");
            FadeAlpha = Config.Bind("Suppression", "FadeAlpha", 0.25f,
                "Alpha level (0.0 = invisible, 1.0 = fully visible) when suppression mode is set to Fade.");

            // === New: Combat Text Theme ===
            CombatTextTheme = Config.Bind("DamageText", "Theme", "wow",
                "Floating combat text style: 'wow' = OG WoW style, 'spartan' = minimalist.");

            Logger.LogInfo("[OBaCT] Loaded 1.7.7 Occlusion Solution");
            Logger.LogInfo("[OBaCT] SuppressionMode set to: " + SuppressMode.Value);

            hpGradientTex = MakeHpGradient();

            // === Ensure assets folder exists ===
            string pluginDir = Path.GetDirectoryName(Info.Location);
            string assetDir = Path.Combine(pluginDir, "assets");
            if (!Directory.Exists(assetDir))
            {
                Directory.CreateDirectory(assetDir);
                Logger.LogInfo("[OBaCT] Created missing assets folder.");
            }

            // === Load WoW-style fonts from AssetBundle ===
            try
            {
                string bundlePath = Path.Combine(assetDir, "obactfonts");

                if (!File.Exists(bundlePath))
                {
                    Logger.LogWarning("[OBaCT] obactfonts AssetBundle not found. Using Arial fallback.");
                    wowFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    wowFontBold = wowFont;
                }
                else
                {
                    var bundle = UnityEngine.AssetBundle.LoadFromFile(bundlePath);
                    if (bundle != null)
                    {
                        wowFont = bundle.LoadAsset<Font>("QTFrizQuad");
                        wowFontBold = bundle.LoadAsset<Font>("QTFrizQuad-Bold");
                        Logger.LogInfo("[OBaCT] Loaded WoW fonts from AssetBundle: QTFrizQuad + Bold");
                    }
                    else
                    {
                        Logger.LogWarning("[OBaCT] Could not load obactfonts bundle, falling back to Arial.");
                        wowFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                        wowFontBold = wowFont;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[OBaCT] Font load failed: " + ex.Message);
                wowFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                wowFontBold = wowFont;
            }
        }



        private Texture2D MakeHpGradient()
        {
            var tex = new Texture2D(100, 1);
            for (int i = 0; i < tex.width; i++)
            {
                float pct = i / (float)(tex.width - 1);
                Color col = (pct >= 0.5f)
                    ? Color.Lerp(Color.yellow, Color.green, (pct - 0.5f) / 0.5f)
                    : Color.Lerp(Color.red, Color.yellow, pct / 0.5f);
                tex.SetPixel(i, 0, col);
            }
            tex.Apply();
            return tex;
        }



        private void Update()
        {
            if (cam == null)
                cam = Camera.main ?? Camera.current;
            if (cam == null) return;

            bool suppress = ShouldSuppressUI();

            // === Keybinds ===
            if (Input.GetKeyDown(KeyCode.F11))
                showMenu = !showMenu;

            if (Input.GetKeyDown(KeyCode.F10))
            {
                //DumpActiveUI();
                //DumpAllUIByKeywords("Spell", "Skill", "MainMenu", "Map", "Trade", "Help", "Tutorial", "Loot", "Item", "Minimap");
            }


            // === Rescan & floater tick ===
            rescanTimer -= Time.unscaledDeltaTime;
            if (rescanTimer <= 0f)
            {
                Rescan();
                rescanTimer = 3f;
            }

            for (int i = floaters.Count - 1; i >= 0; i--)
            {
                var f = floaters[i];
                f.t += Time.unscaledDeltaTime;
                if (f.t >= f.life) floaters.RemoveAt(i);
                else floaters[i] = f;
            }

            for (int j = tracked.Count - 1; j >= 0; j--)
            {
                if (!tracked[j].Valid()) tracked.RemoveAt(j);
                else tracked[j].TickForDamageFloaters();
            }

            playerLevelTimer -= Time.unscaledDeltaTime;
            if (playerLevelTimer <= 0f)
            {
                playerLevelCached = TryGetPlayerLevel(playerLevelCached);
                playerLevelTimer = 3f;
            }
        }

        private void OnGUI()
        {
            if (cam == null) return;

            // Check suppression state once
            bool suppress = ShouldSuppressUI();

            // Hard mode: vanish completely if suppression is active
            if (SuppressMode.Value == SuppressionMode.Hard && suppress)
                return;

            // Fade mode: don't return here, bars will dim inside DrawUnitFrame
            // Off mode: also keep drawing, no suppression at all

            if (hpStyle == null)
            {
                hpStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                hpStyle.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            hpStyle.fontSize = Mathf.Clamp(HpFontSize.Value, 8, 32);

            if (nameStyle == null)
            {
                nameStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                nameStyle.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            nameStyle.fontSize = Mathf.Clamp(NameFontSize.Value, 8, 32);

            if (percentStyle == null)
            {
                percentStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
                percentStyle.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            percentStyle.fontSize = Mathf.Clamp(HpFontSize.Value, 8, 32);

            if (EnableBars.Value)
            {
                for (int i = tracked.Count - 1; i >= 0; i--)
                    tracked[i].DrawUnitFrame(cam, hpStyle, nameStyle, percentStyle, playerLevelCached);
            }

            if (EnableDamageText.Value && floaters.Count > 0)
                DrawFloaters(cam);

            if (showMenu)
                menuRect = GUI.Window(1337, menuRect, SettingsWindow, "OBaCT");
        }


        private void SettingsWindow(int id)
        {
            // Lazy-init styles safely inside OnGUI context
            if (headerStyle == null)
            {
                headerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    fontStyle = FontStyle.Bold
                };
                headerStyle.normal.textColor = Color.cyan; // 🔵 section headers
            }

            if (lineStyle == null)
            {
                lineStyle = new GUIStyle(GUI.skin.box);
                lineStyle.normal.background = Texture2D.whiteTexture; // solid line
            }

            // 🔽 SCROLL VIEW STARTS HERE 🔽
            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Width(440f), GUILayout.Height(560f));

            GUILayout.BeginVertical();

            // === Bars Section ===
            GUILayout.Label("Frame Layout", headerStyle);
            GUILayout.Box(GUIContent.none, lineStyle, GUILayout.Height(1));
            GUILayout.Space(4);

            GUILayout.Label($"Offset Y: {YOffset.Value:F2}");
            YOffset.Value = GUILayout.HorizontalSlider(YOffset.Value, 0.5f, 4f);

            GUILayout.Label($"Width: {BarWidth.Value:F0} px");
            BarWidth.Value = GUILayout.HorizontalSlider(BarWidth.Value, 120f, 260f);

            GUILayout.Label($"Height: {BarHeight.Value:F0} px");
            BarHeight.Value = GUILayout.HorizontalSlider(BarHeight.Value, 14f, 30f);

            GUILayout.Label($"Max Distance: {MaxDistance.Value:F0} m");
            MaxDistance.Value = GUILayout.HorizontalSlider(MaxDistance.Value, 10f, 80f);

            GUILayout.Space(8);

            // === Labels Section ===
            GUILayout.Label("Labels", headerStyle);
            GUILayout.Box(GUIContent.none, lineStyle, GUILayout.Height(1));
            GUILayout.Space(4);

            GUILayout.BeginVertical("box");
            ShowName.Value = GUILayout.Toggle(ShowName.Value, " Show Name");
            ShowLevel.Value = GUILayout.Toggle(ShowLevel.Value, " Show Level");
            ShowHpNumbers.Value = GUILayout.Toggle(ShowHpNumbers.Value, " Show HP Numbers");
            ShowHpPercent.Value = GUILayout.Toggle(ShowHpPercent.Value, " Show HP Percent");
            GUILayout.EndVertical();

            GUILayout.Label($"Name Font Size: {NameFontSize.Value}");
            NameFontSize.Value = Mathf.RoundToInt(GUILayout.HorizontalSlider(NameFontSize.Value, 10f, 28f));

            GUILayout.Label($"HP Font Size: {HpFontSize.Value}");
            HpFontSize.Value = Mathf.RoundToInt(GUILayout.HorizontalSlider(HpFontSize.Value, 10f, 28f));

            GUILayout.Space(8);

            // === Portrait Section ===
            GUILayout.Label("Portrait", headerStyle);
            GUILayout.Box(GUIContent.none, lineStyle, GUILayout.Height(1));
            GUILayout.Space(4);
            ShowPortrait.Value = GUILayout.Toggle(ShowPortrait.Value, " Show Portrait");

            GUILayout.Space(8);

            // === Mana Section ===
            GUILayout.Label("Mana", headerStyle);
            GUILayout.Box(GUIContent.none, lineStyle, GUILayout.Height(1));
            GUILayout.Space(4);
            ShowManaBar.Value = GUILayout.Toggle(ShowManaBar.Value, " Show Mana Bar");

            GUILayout.Space(8);

            // === Suppression Section ===
            GUILayout.Label("Suppression", headerStyle);
            GUILayout.Box(GUIContent.none, lineStyle, GUILayout.Height(1));
            GUILayout.Space(4);

            if (GUILayout.Toggle(SuppressMode.Value == SuppressionMode.Off, " Off")) SuppressMode.Value = SuppressionMode.Off;
            if (GUILayout.Toggle(SuppressMode.Value == SuppressionMode.Hard, " Hard (hide bars)")) SuppressMode.Value = SuppressionMode.Hard;
            if (GUILayout.Toggle(SuppressMode.Value == SuppressionMode.Fade, " Fade (experimental)")) SuppressMode.Value = SuppressionMode.Fade;

            if (SuppressMode.Value == SuppressionMode.Fade)
            {
                GUILayout.Label($"Fade Alpha: {FadeAlpha.Value:F2}");
                FadeAlpha.Value = GUILayout.HorizontalSlider(FadeAlpha.Value, 0f, 1f);
            }

            GUILayout.Space(8);

            // === Damage Text Section ===
            GUILayout.Label("Combat Text", headerStyle);
            GUILayout.Box(GUIContent.none, lineStyle, GUILayout.Height(1));
            GUILayout.Space(4);

            GUILayout.Label($"Scale: {DmgTextScale.Value:F1}");
            DmgTextScale.Value = GUILayout.HorizontalSlider(DmgTextScale.Value, 0.5f, 3f);

            FctShadow.Value = GUILayout.Toggle(FctShadow.Value, " Enable Shadow");

            GUILayout.Space(4);
            GUILayout.Label("Theme:");
            if (GUILayout.Toggle(CombatTextTheme.Value == "wow", " WoW Style")) CombatTextTheme.Value = "wow";
            if (GUILayout.Toggle(CombatTextTheme.Value == "spartan", " Spartan Style")) CombatTextTheme.Value = "spartan";

            GUILayout.Space(12);

            // === Save Button ===
            if (GUILayout.Button("💾 Save Settings", GUILayout.Height(28)))
                Config.Save();

            GUILayout.EndVertical();

            GUILayout.EndScrollView();
        }




        private void DrawFloaters(Camera camRef)
        {
            for (int i = 0; i < floaters.Count; i++)
            {
                var f = floaters[i];
                Vector3 screen = camRef.WorldToScreenPoint(f.world);
                if (screen.z <= 0f) continue;

                float norm = f.t / f.life;
                float eased = Mathf.Sin(norm * Mathf.PI * 0.5f); // ease-out curve
                float rise = FctRiseSpeed.Value * eased * f.life;
                float alpha = Mathf.Clamp01(1f - norm);

                float x = screen.x + Mathf.Sin(f.seed + f.t * 2f) * FctJitter.Value;
                float y = Screen.height - screen.y - rise * 40f;

                int fontSize = Mathf.RoundToInt(20 * f.critScale * DmgTextScale.Value);
                fontSize = Mathf.Clamp(fontSize, 12, 64);

                GUIStyle style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = fontSize,
                    fontStyle = FontStyle.Bold,
                };

                // Pick theme font
                if (CombatTextTheme.Value == "wow")
                {
                    if (f.critScale > 1f && wowFontBold != null)
                        style.font = wowFontBold;   // crits = bold
                    else if (wowFont != null)
                        style.font = wowFont;
                }
                else if (customFont != null)
                    style.font = customFont;
                else
                    style.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

                // === Colors per theme ===
                Color col = Color.white;
                if (CombatTextTheme.Value == "wow")
                {
                    col = f.critScale > 1f
                        ? new Color(1f, 0.6f, 0.2f, alpha)  // crit = orange
                        : new Color(1f, 0.82f, 0.2f, alpha); // normal = golden yellow
                }
                else // spartan
                {
                    if (f.text.StartsWith("+"))
                        col = new Color(0.2f, 1f, 0.2f, alpha); // healing = green
                    else if (f.critScale > 1f)
                        col = new Color(1f, 0.4f, 0.2f, alpha); // crit = red-orange
                    else
                        col = new Color(1f, 1f, 1f, alpha);     // normal = white
                }

                // === Dynamic rect sizing ===
                float w = fontSize * 10f;
                float h = fontSize * 1.6f;
                Rect r = new Rect(x - w / 2f, y - h / 2f, w, h);

                // Shadow first
                if (FctShadow.Value)
                {
                    GUI.color = new Color(0, 0, 0, alpha);
                    GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), f.text, style);
                }

                // Main text
                GUI.color = col;
                GUI.Label(r, f.text, style);
                GUI.color = Color.white;
            }
        }




        private void Rescan()
        {
            try
            {
                tracked.Clear();

                HashSet<GameObject> seen = new HashSet<GameObject>();

                // Track all valid Stats
                foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                {
                    if (!root.activeInHierarchy) continue;
                    Stats[] comps = root.GetComponentsInChildren<Stats>(true);
                    foreach (var stats in comps)
                    {
                        if (stats == null || !stats.gameObject.activeInHierarchy) continue;
                        if (stats.CurrentMaxHP <= 1) continue;
                        seen.Add(stats.gameObject);

                        if (!tracked.Any(t => t.RefEquals(stats.gameObject)))
                            tracked.Add(new Tracked(stats.gameObject, stats));
                    }
                }

                // Skip player in Character Select
                string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (!string.Equals(sceneName, "LoadScene", StringComparison.OrdinalIgnoreCase))
                {
                    Stats myStats = GameData.PlayerControl?.Myself?.GetComponent<Stats>();
                    if (myStats != null && !tracked.Any(t => t.RefEquals(myStats.gameObject)))
                    {
                        tracked.Add(new Tracked(myStats.gameObject, myStats));
                        Logger.LogInfo("[OBaCT] Added player stats to tracked for incoming damage/heals.");
                    }
                    seen.Add(myStats?.gameObject);
                }

                tracked.RemoveAll(t => !t.Valid() || !seen.Contains(t.Go));
            }
            catch (Exception ex)
            {
                Logger.LogError("[OBaCT] Error in Rescan (handled): " + ex.Message);
            }
        }











        private static int TryGetPlayerLevel(int fallback) => Mathf.Max(1, fallback);

        // === Utility Methods ===
        private static void DrawBorder(Rect rect, int thickness, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture); // Top
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture); // Bottom
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture); // Left
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture); // Right
            GUI.color = Color.white;
        }

        private static void DrawGlowBorder(Rect rect, int thickness, Color color, int layers)
        {
            for (int i = 0; i < layers; i++)
            {
                float alpha = color.a * (1f - i * 0.4f);
                if (alpha <= 0f) break;
                Color layerColor = new Color(color.r, color.g, color.b, alpha);
                Rect expanded = new Rect(rect.x - i, rect.y - i, rect.width + i * 2, rect.height + i * 2);
                DrawBorder(expanded, thickness, layerColor);
            }
        }

        public class Tracked
        {

            public GameObject Go { get; private set; }
            public Transform Tf { get; private set; }
            public Stats stats;

            private int lastHP;

            public Tracked(GameObject go, Stats stats)
            {
                Go = go;
                Tf = go.transform;
                this.stats = stats;

                lastHP = stats.CurrentHP;   // <-- initialize baseline HP
            }
            public bool IsDead => stats != null && stats.CurrentHP <= 0;
            public bool RefEquals(GameObject go) => Go == go;
            public bool Valid() => Go && Tf && stats != null && Go.activeInHierarchy;
            public string SafeName() => !string.IsNullOrEmpty(stats.MyName) ? stats.MyName : Go ? Go.name : "Unknown";
            public bool TryGetLevel(out int level) { level = stats.Level; return true; }
            public void TickForDamageFloaters()
            {
                if (stats == null) return;

                int currentHP = stats.CurrentHP;
                int delta = currentHP - lastHP;

                if (delta != 0)
                {
                    FloatingNum f = new FloatingNum
                    {
                        world = Tf.position + Vector3.up * 2f,
                        text = delta < 0 ? (-delta).ToString() : "+" + delta.ToString(),
                        t = 0f,
                        life = OBaCT.DmgTextLifetime.Value,
                        critScale = Mathf.Abs(delta) > (stats.CurrentMaxHP * 0.1f) ? 1.4f : 1f, // crit if >10% of max HP
                        color = delta < 0 ? Color.red : Color.green,
                        seed = UnityEngine.Random.value * 10f
                    };
                    OBaCT.floaters.Add(f);
                }

                lastHP = currentHP;
            } // update baseline for next tick }

            private static Color HpColor(float pct)
            {
                pct = Mathf.Clamp01(pct);
                return pct >= 0.5f
                    ? Color.Lerp(Color.yellow, Color.green, (pct - 0.5f) / 0.5f)
                    : Color.Lerp(Color.red, Color.yellow, pct / 0.5f);
            }

            private static Color NameColor(Stats stats)
            {
                if (stats == null || stats.Myself == null)
                    return Color.white;

                Character c = stats.Myself;  // shorthand

                // === Player logic ===
                if (!c.isNPC) // Players are just non-NPCs
                {
                    if (c == GameData.PlayerControl.Myself)
                        return Color.cyan;   // You

                    if (GameData.GroupMember1?.MyAvatar == c ||
                        GameData.GroupMember2?.MyAvatar == c ||
                        GameData.GroupMember3?.MyAvatar == c)
                        return Color.green;  // Party

                    return Color.white;      // Other players
                }

                // === NPC logic ===
                if (c.isNPC && c.MyNPC != null)
                {
                    if (c.isVendor) return Color.yellow; // Vendor flag from Character

                    if (c.AggressiveTowards != null && c.AggressiveTowards.Contains(Character.Faction.Player))
                        return Color.red;   // Hostile

                    return Color.gray;      // Neutral (not targeting player)
                }

                return new Color(0.9f, 0.85f, 0.7f); // fallback beige
            }

            public void DrawUnitFrame(Camera cam, GUIStyle hpStyle, GUIStyle nameStyle, GUIStyle pctStyle, int playerLevel)
            {
                if (Tf == null || stats == null) return;

                float hp = stats.CurrentHP, max = stats.CurrentMaxHP;
                if (max <= 0f) return;

                Vector3 pos = Tf.position + Vector3.up * YOffset.Value;
                Vector3 screen = cam.WorldToScreenPoint(pos);
                if (screen.z <= 0f) return;
                if (Vector3.Distance(cam.transform.position, Tf.position) > MaxDistance.Value) return;

                float pct = Mathf.Clamp01(hp / max);
                float bw = BarWidth.Value, bh = BarHeight.Value;
                float x = screen.x - bw / 2f, y = Screen.height - screen.y - bh;

                // === Click-to-target full frame ===
                Rect frameRect = new Rect(x - 3, y - 26, bw + 6, bh + 36 + (OBaCT.ShowManaBar.Value ? 8f : 0f));
                if (Event.current.type == EventType.MouseDown && frameRect.Contains(Event.current.mousePosition))
                {
                    GameData.PlayerControl.CurrentTarget = stats.Myself;
                    Event.current.Use();
                }

                // === SuppressionMode Fade Handling ===
                float alpha = 1f;
                if (OBaCT.SuppressMode.Value == OBaCT.SuppressionMode.Fade && OBaCT.Instance.ShouldSuppressUI())
                    alpha = Mathf.Clamp01(OBaCT.FadeAlpha.Value);

                Color prevColor = GUI.color;
                GUI.color = new Color(prevColor.r, prevColor.g, prevColor.b, alpha);

                // === Name + Level Box ===
                if (OBaCT.ShowName.Value)
                {
                    string txt = SafeName();
                    int mobLevel = 0;
                    TryGetLevel(out mobLevel);

                    float nameStripWidth = bw - 38f;
                    Rect nameRect = new Rect(x, y - 22f, nameStripWidth, 18f);
                    GUI.color = Color.black;
                    GUI.DrawTexture(nameRect, Texture2D.whiteTexture);

                    // Dynamic name color
                    GUI.color = NameColor(stats);

                    // Custom font (defaults to Arial)
                    if (OBaCT.customFont != null)
                        nameStyle.font = OBaCT.customFont;

                    GUI.Label(nameRect, txt, nameStyle);

                    if (OBaCT.ShowLevel.Value && mobLevel > 0)
                    {
                        Rect lvlRect = new Rect(nameRect.xMax + 2f, nameRect.y, 34f, nameRect.height);
                        GUI.color = Color.black;
                        GUI.DrawTexture(lvlRect, Texture2D.whiteTexture);
                        DrawOutlinedLabel(lvlRect, mobLevel.ToString(), nameStyle, Color.yellow, Color.black);
                    }
                }

                // === DEAD BRANCH ===
                if (IsDead)
                {
                    // Frame + inner gray fill
                    Rect border = new Rect(x, y, bw, bh + 2f);
                    GUI.color = Color.black;
                    GUI.DrawTexture(border, Texture2D.whiteTexture);

                    Rect inner = new Rect(x + 2, y + 2, bw - 4, bh - 2);
                    GUI.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);
                    GUI.DrawTexture(inner, Texture2D.whiteTexture);

                    // Subtle "dead" text
                    GUIStyle deadStyle = new GUIStyle(hpStyle);
                    deadStyle.alignment = TextAnchor.MiddleCenter;
                    deadStyle.fontStyle = FontStyle.Italic;
                    deadStyle.normal.textColor = Color.gray;
                    if (OBaCT.customFont != null)
                        deadStyle.font = OBaCT.customFont;

                    GUI.Label(inner, "dead", deadStyle);

                    GUI.color = Color.white;
                    return; // stop here, don't draw the live HP elements
                }

                // === HP Bar ===
                Rect borderAlive = new Rect(x, y, bw, bh + 2f);
                GUI.color = Color.black;
                GUI.DrawTexture(borderAlive, Texture2D.whiteTexture);

                Rect innerAlive = new Rect(x + 2, y + 2, bw - 4, bh - 2);
                GUI.color = new Color(0.05f, 0.05f, 0.05f, 0.95f);
                GUI.DrawTexture(innerAlive, Texture2D.whiteTexture);

                float filledW = innerAlive.width * pct;
                Rect filled = new Rect(innerAlive.x, innerAlive.y, filledW, innerAlive.height);

                // WoW-style bar color
                GUI.color = HpColor(pct);
                GUI.DrawTexture(filled, Texture2D.whiteTexture);

                // Gloss overlay
                Rect gloss = new Rect(filled.x, filled.y, filled.width, filled.height / 3f);
                GUI.color = new Color(1f, 1f, 1f, 0.25f);
                GUI.DrawTexture(gloss, Texture2D.whiteTexture);

                // === HP Numbers ===
                if (OBaCT.ShowHpNumbers.Value)
                {
                    string txt = $"{Mathf.CeilToInt(hp)}/{Mathf.CeilToInt(max)}";
                    DrawOutlinedLabel(innerAlive, txt, hpStyle, Color.white, Color.black);
                }

                // === HP Percent ===
                if (OBaCT.ShowHpPercent.Value)
                {
                    string txt = $"{Mathf.RoundToInt(pct * 100f)}%";
                    Rect pctRect = new Rect(innerAlive.x + innerAlive.width + 6f, innerAlive.y, 48f, innerAlive.height);
                    DrawOutlinedLabel(pctRect, txt, pctStyle, Color.white, Color.black);
                }

                // === Mana Bar ===
                if (OBaCT.ShowManaBar.Value && stats.GetCurrentMaxMana() > 0)
                {
                    float manaPct = Mathf.Clamp01((float)stats.GetCurrentMana() / stats.GetCurrentMaxMana());
                    Rect manaRect = new Rect(x + 2, y + bh + 2f, bw - 4, Mathf.Max(6f, bh * 0.5f));
                    GUI.color = Color.black;
                    GUI.DrawTexture(new Rect(manaRect.x - 1, manaRect.y - 1, manaRect.width + 2, manaRect.height + 2), Texture2D.whiteTexture);
                    GUI.color = new Color(0.05f, 0.05f, 0.1f, 0.95f);
                    GUI.DrawTexture(manaRect, Texture2D.whiteTexture);
                    Rect manaFill = new Rect(manaRect.x, manaRect.y, manaRect.width * manaPct, manaRect.height);
                    GUI.color = Color.blue;
                    GUI.DrawTexture(manaFill, Texture2D.whiteTexture);
                }

                // === Glow Highlights ===
                bool isTarget = stats.Myself == GameData.PlayerControl.CurrentTarget;
                bool isAggro = stats.Myself.isNPC
                               && stats.Myself.MyNPC != null
                               && stats.Myself.MyNPC.CurrentAggroTarget == GameData.PlayerControl.Myself;

                if (isTarget)
                {
                    float pulse = 0.4f + Mathf.PingPong(Time.time * 0.75f, 0.4f);
                    DrawGlowBorder(frameRect, 2, new Color(1f, 1f, 0f, pulse), 3);
                }
                if (isAggro)
                {
                    float pulse = 0.7f + Mathf.PingPong(Time.time * 0.5f, 0.2f);
                    DrawGlowBorder(frameRect, 2, new Color(1f, 0f, 0f, pulse), 3);
                }

                GUI.color = Color.white;
            }

        }
        public struct FloatingNum
        {
            public Vector3 world;
            public string text;
            public float t;
            public float life;
            public float critScale;
            public Color color;
            public float seed;
        }
        // === Helper Methods ===
        private static void DrawOutlinedLabel(Rect rect, string text, GUIStyle style, Color textColor, Color outlineColor)
        {
            GUI.color = outlineColor;
            GUI.Label(new Rect(rect.x - 1, rect.y, rect.width, rect.height), text, style);
            GUI.Label(new Rect(rect.x + 1, rect.y, rect.width, rect.height), text, style);
            GUI.Label(new Rect(rect.x, rect.y - 1, rect.width, rect.height), text, style);
            GUI.Label(new Rect(rect.x, rect.y + 1, rect.width, rect.height), text, style);
            GUI.color = textColor;
            GUI.Label(rect, text, style);
        }

        private static Color HpGradient(float pct)
        {
            pct = Mathf.Clamp01(pct);
            return pct >= 0.5f
                ? Color.Lerp(Color.yellow, Color.green, (pct - 0.5f) / 0.5f)
                : Color.Lerp(Color.red, Color.yellow, pct / 0.5f);
        }

        private bool ShouldSuppressUI()
        {
            bool suppress = false;

            try
            {
                // === Use cached refs only (no GameObject.Find here) ===
                suppress |= GameData.AuctionWindowOpen || GameData.PlayerAuctionItemsOpen;
                suppress |= GameData.BankUI?.BankWindow != null && GameData.BankUI.BankWindow.activeSelf;
                suppress |= GameData.GM?.EscapeMenu != null && GameData.GM.EscapeMenu.activeSelf;
                suppress |= GameData.GroupBuilder?.gameObject != null && GameData.GroupBuilder.gameObject.activeSelf;
                suppress |= GameData.Misc?.HelpWindow != null && GameData.Misc.HelpWindow.activeSelf;
                suppress |= (GameData.PlayerInv?.InvWindow != null && GameData.PlayerInv.InvWindow.activeSelf)
                         || (GameData.PlayerInv?.StatWindow != null && GameData.PlayerInv.StatWindow.activeSelf);
                suppress |= GameData.ItemInfoWindow != null && GameData.ItemInfoWindow.isWindowActive();
                suppress |= GameData.LootWindow?.WindowParent != null && GameData.LootWindow.WindowParent.activeSelf;
                suppress |= GameData.HKMngr?.Map != null && GameData.HKMngr.Map.activeSelf;
                suppress |= GameData.HKMngr?.OptionsMenu != null && GameData.HKMngr.OptionsMenu.activeSelf;
                suppress |= GameData.VendorWindowOpen;
                suppress |= GameData.QuestLog?.QuestWindow != null && GameData.QuestLog.QuestWindow.activeSelf;
                suppress |= GameData.Smithing?.SmithingWindow != null && GameData.Smithing.SmithingWindow.activeSelf;

                // Spell / Skill books
                suppress |= _spellGo != null && _spellGo.activeSelf;
                suppress |= _skillGo != null && _skillGo.activeSelf;

                // Rest / Run / Revive
                suppress |= _restWin != null && _restWin.gameObject != null && _restWin.gameObject.activeSelf;
                suppress |= _runWin != null && _runWin.gameObject != null && _runWin.gameObject.activeSelf;
                suppress |= _reviveWin != null && _reviveWin.activeSelf;

                // NPC Dialog
                suppress |= _npcDialog != null && _npcDialog.activeSelf;
            }
            catch (Exception ex)
            {
                Logger.LogError("[OBaCT] Error in ShouldSuppressUI (handled): " + ex.Message);
            }

            // === Suppression Mode Handling ===
            if (SuppressMode.Value == SuppressionMode.Off) return false;
            if (SuppressMode.Value == SuppressionMode.Hard) return suppress;
            if (SuppressMode.Value == SuppressionMode.Fade) return false; // handled later

            return suppress;
        }


        private void TrackWindow(ref bool lastState, bool currentState, string name, List<string> list)
        {
            if (currentState) list.Add(name);

            if (currentState != lastState)
            {
                Logger.LogInfo($"[OBaCT] {name} active = {currentState}");
                lastState = currentState;
            }
        }


        // Returns true if a GameObject by name is found and visible (activeSelf OR activeInHierarchy)
        private static bool IsOpenByName(ref GameObject cache, string name)
        {
            if (cache == null) cache = GameObject.Find(name);
            if (cache != null) return cache.activeSelf || cache.activeInHierarchy;
            return false;
        }

        // Refreshes cached GameObject refs every ~1s and does a deeper sweep if still null
        private void RefreshUIRefsTick()
        {
            _uiRefetchTimer -= Time.unscaledDeltaTime;
            if (_uiRefetchTimer > 0f) return;
            _uiRefetchTimer = _uiRefetchCooldown;

            if (_spellGo == null) _spellGo = GameObject.Find("SpellBook");
            if (_skillGo == null) _skillGo = GameObject.Find("SkillBook");
            if (_mainMenu == null) _mainMenu = GameObject.Find("MainMenu");
            if (_minimapCanvas == null) _minimapCanvas = GameObject.Find("MinimapCanvas");
            if (_minimapPanel == null) _minimapPanel = GameObject.Find("MinimapPanel");

            if (_spellGo == null || _skillGo == null || _mainMenu == null || _minimapCanvas == null || _minimapPanel == null)
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    var n = t.name;
                    if (_spellGo == null && n.Equals("SpellBook", System.StringComparison.OrdinalIgnoreCase)) _spellGo = t.gameObject;
                    if (_skillGo == null && n.Equals("SkillBook", System.StringComparison.OrdinalIgnoreCase)) _skillGo = t.gameObject;
                    if (_mainMenu == null && n.Equals("MainMenu", System.StringComparison.OrdinalIgnoreCase)) _mainMenu = t.gameObject;
                    if (_minimapCanvas == null && n.Equals("MinimapCanvas", System.StringComparison.OrdinalIgnoreCase)) _minimapCanvas = t.gameObject;
                    if (_minimapPanel == null && n.Equals("MinimapPanel", System.StringComparison.OrdinalIgnoreCase)) _minimapPanel = t.gameObject;
                }
            }
        }
    }
}








