using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using System.Reflection; // for reflecting InventoryMapManager private fields
using TeamCherry.Localization;
using GlobalEnums;

namespace ChairTeleport
{
    [BepInPlugin("chairteleport", "Chair Teleport", "1.0.0")]
    public class ChairTeleportPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;

        // 配置
        private ConfigEntry<KeyboardShortcut> _openUiKey;   // F1 进入/确认
        private ConfigEntry<KeyboardShortcut> _quickRestKey; // F2 随地恢复+保存
        private ConfigEntry<KeyboardShortcut> _confirmTeleportKey; // UI 内确认/传送键（默认 Enter）
        private ConfigEntry<KeyboardShortcut> _renameKey; // 重命名键（默认 Space）
        private ConfigEntry<string> _benchesPersist;          // 持久化：scene|marker;scene|marker;...
        private ConfigEntry<bool> _debugLog;   // 调试日志开关

        // 手柄配置
        private ConfigEntry<bool> _enableGamepadSupport;
        private ConfigEntry<string> _gamepadOpenUI;
        private ConfigEntry<string> _gamepadQuickRest;
        private ConfigEntry<float> _gamepadOpenHoldSeconds;

        // 地图预览快捷键（键盘与手柄）
        private ConfigEntry<KeyboardShortcut> _showMapKey; // 默认 F3 长按
        private ConfigEntry<string> _gamepadShowMap;       // 默认 X（PS 可用 SQUARE/CROSS）

        // 难度配置
        private ConfigEntry<bool> _requireOnBenchToOpenUI;

        // 语言配置
        private ConfigEntry<string> _uiLanguage;

        // UI缩放配置
        private ConfigEntry<float> _uiScale;
        private ConfigEntry<bool> _autoScale;

        // F2 覆盖：记录脚下重定位
        private string _repositionScene;
        private Vector3 _repositionPos;
        private string _repositionMarker;
        private Vector2 _repositionLocalOffset;
        private bool _f2OverrideActive;
        private bool _playerJustRespawned; // 标记玩家是否刚刚复活
        private bool _didInitialCleanup = false; // 首次进游戏清理无效场景的椅子是否已执行




        // UI tip
        private string _tipText;
        private float _tipUntil;

        // Top-center HUD (e.g., OK/NO)
        private string _hudText;
        private float _hudUntil;

        // 运行态
        private readonly List<BenchRef> _benches = new List<BenchRef>();
        private int _benchAddedOrderCounter = 0;

        private bool _uiOpen;
        private int _selected;
        private float _lastNavTime;
        // 打开UI时待选中的“最近新增椅子”的键（scene|marker）
        private string _pendingSelectKey;

        private const float NavCooldown = 0.08f; // 导航节流

        // UI 关闭保护时长：与“手柄打开UI的延时”同开同关同时长
        private float _uiOpenedAt = 0f;
        private float CloseGraceSeconds => Mathf.Clamp(_gamepadOpenHoldSeconds.Value, 0f, 1f);

        // 长按快速导航相关（键盘）
        private float _keyHoldStartTime;
        private KeyCode _currentHeldKey = KeyCode.None;
        private const float HoldThreshold = 0.5f; // 长按阈值（秒）
        private const float FastNavInterval = 0.08f; // 快速导航间隔（秒）

        // 手柄长按快速导航相关
        private float _gamepadHoldStartTime;
        private string _currentHeldGamepadDirection = null; // "Up", "Down", null
        private float _lastGamepadNavTime;

        // 空格键长按删除选中椅子支持
        private float _spaceKeyDownAt = -1f;
        private bool _spaceDeleteTriggered = false;
        private const float SpaceDeleteHoldSeconds = 2.0f;

        // Y 键（手柄Y或键盘Y）长按删除/短按重命名支持
        private float _yButtonDownAt = -1f;
        private bool _yDeleteTriggered = false;

        // 按键防抖
        private float _lastF1PressTime;
        private float _lastF2PressTime;
        // UI 右侧区域地图预览（长按触发）
        // 手柄组合：F2（QuickRest）组合的“上一次是否处于按住”标志（用于边沿触发）
        private bool _padQuickRestHeldPrev = false;

        private float _showMapKeyDownAt = -1f;
        private float _showMapPadDownAt = -1f;
        private bool _showMapActive = false;

        // —— Native GameMap preview (rendered by the game’s own map camera) ——

        private int _showMapIndex = -1;
        private bool _mapPreviewShown = false; // 标记地图预览是否已经显示

        // 地图预览状态（仅使用官方背包地图，全屏）
        private Coroutine _mapPreviewRoutine;
        private bool _inventoryMapOpenForPreview = false;

        // 选中椅子在地图上的高亮（脉冲放大）
        private MapPin _highlightedPin = null;
        private Vector3 _highlightedPinOrigScale = Vector3.one;
        private float _highlightPulseStart = 0f;

        // 通过取消键关闭UI时，应用短暂输入屏蔽的标志（防止B键回血）
        private bool _uiClosedByCancelNext = false;


        private const float KeyCooldown = 0.2f; // 按键防抖时间

        // 手柄打开UI可设置按住延时（范围 0~1s；默认 0s；同时作为 UI 关闭保护时长）
        private float _gamepadHoldStartForOpen = -1f;

        // 简化的重命名功能
        private int _editingIndex = -1;
        private string _editingText = "";
        private bool _editingFocusSet = false;
        // 二级菜单重命名：只允许编辑子条目显示部分（锁定前缀为分类名）
        private string _editingLockedPrefix = null;
        private string _editingSuffix = "";

        private bool _editingParentOnly = false;


        // 父类目重命名编辑状态
        private int _editingCategoryIndex = -1;
        private string _editingCategoryText = "";
        private bool _editingCategoryFocusSet = false;


        // 行内“震动”反馈：当尝试删除收藏的椅子时，短暂抖动该行以提示不可删除
        private string _shakeRowKey = null;     // scene|marker
        private float _shakeUntil = -1f;        // Time.unscaledTime 截止

        // 翻页支持
        private int _currentPage = 0;
        private int ItemsPerPage => CalculateItemsPerPage();

        // Cached singletons (avoid per-frame object lookups)
        private GameManager _gm;
        private HeroController _hc;
        private PlayerData _pd;
        private InputHandler _ih;
        private float _refreshedAt = -1f;

        // Cached gamepad combos and hold seconds
        private KeyCode[] _comboOpenKeys = Array.Empty<KeyCode>();
        private KeyCode[] _comboRestKeys = Array.Empty<KeyCode>();
        private float _cachedHoldSeconds = 0f;



        // GUI style cache
        private GUIStyle _stHud, _stTitle, _stInfo, _stIndicator, _stIndicatorSel, _stName, _stNameSel, _stScene, _stMarker, _stInput, _stEmpty, _stHeart;
        private bool _stylesBuilt = false;
        // UI font scale (1.0 = default). Increase slightly per user request.
        private float _fontScale = 1.1f;


        // UI 预热控制
        private bool _uiPrewarmed = false;          // 是否已在进入存档后进行过一次UI预热
        private bool _uiPrewarming = false;         // 当前是否处于预热帧（用于隐藏一次绘制）
        private bool _bypassBenchRequirementOnce = false; // 下次OpenUI时忽略“必须在椅子上”限制（仅一次）

        // 进入存档后的“一次性规范化”标记（回到主菜单后重置）
        private bool _postEnterNormalized = false;



        // Scene caches to avoid repeated full scans and LINQ allocs
        private Dictionary<string, RespawnMarker> _markerByName;
        private List<RespawnMarker> _markerList;
        private List<TransitionPoint> _tpList;

        // Known bench markers from SceneTeleportMap (scene -> set of marker names)
        private Dictionary<string, HashSet<string>> _knownBenchByScene;


        // Cached UI strings
        private string _infoTextCache;
        private int _infoLastCount = -1, _infoLastSelected = -1, _infoLastPage = -1;


        // 二级菜单状态（分类 -> 条目）
        private bool _inCategoryView = true;
        private bool _groupsDirty = true;
        private List<string> _catNames = new List<string>();
        private Dictionary<string, List<int>> _catToIndices = new Dictionary<string, List<int>>(StringComparer.CurrentCultureIgnoreCase);
        private int _selectedCategory = 0;
        private int _categoryPage = 0;
        private int _selectedInCategory = 0;
        private int _itemPageInCategory = 0;


        // 收藏功能与双击→检测
        private ConfigEntry<string> _favoriteScenesPersist; // 收藏的“分类/场景名”列表；分号分隔
        private readonly List<string> _favoriteScenes = new List<string>();
        private int _benchFavoriteOrderCounter = 0; // 子条目收藏顺序计数器（越早收藏序号越小/越靠前）
        private readonly HashSet<string> _favoriteScenesSet = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        private readonly Dictionary<string, int> _favoriteScenesOrder = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);


        // 配置：进入存档时导入全部椅子
        private ConfigEntry<bool> _importAllOnEnter;
        private bool _importedAllOnce = false;


        // 区域 -> 父类目重命名 映射（可持久化）
        private ConfigEntry<string> _zoneRenamePersist; // 形如：远野=远野1111;王国边缘=王国边缘·改
        private readonly Dictionary<string, string> _zoneRenameMap = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);



        private bool _lastRightWasOnCategory = false;
        // 子条目上双击空格/Y键 -> 直接编辑父类目 名称 支持
        private float _lastSpaceOnChildTime = -1f;
        private int _lastSpaceChildIndex = -1;
        private const float DoubleSpaceEditSeconds = 0.2f;
        private bool _pendingSpaceSingle = false;
        private int _pendingSpaceIndex = -1;
        private float _pendingSpaceStart = -1f;
        private Coroutine _pendingSpaceCo = null;

        // 手柄Y键双击支持
        private bool _pendingYSingle = false;
        private int _pendingYIndex = -1;
        private float _pendingYStart = -1f;
        private Coroutine _pendingYCo = null;


        private int _lastRightCategoryIndex = -1;
        private int _lastRightChildBenchIndex = -1;
        private bool _pendingEnterOnRight = false;
        private float _pendingEnterDeadline = 0f;

        private const float DoubleTapWindow = 0.20f; // 双击→判定窗口（调低以降低进入延迟）

        private const float F2DoubleTapWindow = 0.35f; // F2 双击→判定窗口（稍宽松，避免误判单击）


        // F2 双击处理状态
        private bool _pendingF2Single = false;
        private float _pendingF2Start = -1f;
        private Coroutine _pendingF2Co;



        // 语言支持
        private bool _isChineseLanguage = true;

        // 延迟中文本地化刷新标记
        private bool _needsLocalizedRefresh = true;

        // 延迟刷新协程运行标记，防止重复开启
        private bool _refreshRoutineRunning = false;

        // F3地图预览功能状态标记（避免与自动放大功能冲突）
        private bool _isF3MapPreviewActive = false;


        private static ChairTeleportPlugin _instance;
        internal static ChairTeleportPlugin Instance => _instance;

        private void Awake()
        {
            // 防止多个实例
            if (_instance != null && _instance != this)
            {
                Logger.LogWarning("检测到多个ChairTeleportPlugin实例，销毁重复实例");
                Destroy(this.gameObject); // 销毁整个GameObject而不是组件
                return;
            }

            _instance = this;
            // 默认无选中，打开UI应停留在父级分类列表
            _selected = -1;
            _harmony = new Harmony("chairteleport");
            _debugLog = Config.Bind("Debug", "EnableDebugLog", false, "是否启用调试日志");

            // 检测游戏语言
            DetectLanguage();

            SceneManager.activeSceneChanged += OnActiveSceneChanged;

            _harmony.PatchAll(typeof(Hooks));

            _showMapKey = Config.Bind("Keyboard", "ShowMapPreview", new KeyboardShortcut(KeyCode.F3),
                "长按强制打开背包地图，放大并居中当前选中椅子；松开关闭地图 | Long-press to open the inventory map fullscreen, zoom to scene and center on the selected bench; release to close");

            _openUiKey = Config.Bind("Keyboard", "OpenTeleportUI", new KeyboardShortcut(KeyCode.F1),
                "打开/关闭椅子传送 UI 的按键 (键盘，UI界面下F1关闭UI，Enter传送) | Open/Close Chair Teleport UI (Keyboard, F1 closes UI, Enter teleports in UI)\n" +
                "支持组合键，如 Ctrl+F1, Alt+F2 等 | Supports key combinations like Ctrl+F1, Alt+F2, etc.");
            _quickRestKey = Config.Bind("Keyboard", "QuickRestAndSave", new KeyboardShortcut(KeyCode.F2),
                "随地恢复并保存，并将复活点设为当前场景最近的合法点 (键盘，UI界面时不响应) | Quick Rest and Save (Keyboard, disabled when UI is open)\n" +
                "支持组合键，如 Ctrl+F2, Shift+F2 等 | Supports key combinations like Ctrl+F2, Shift+F2, etc.");
            _confirmTeleportKey = Config.Bind("Keyboard", "ConfirmTeleport", new KeyboardShortcut(KeyCode.Return),
                "在 UI 内确认/传送的按键（默认 Enter）。支持组合键；始终允许数字小键盘 Enter | Keyboard confirm/teleport key inside UI (default Enter). Supports combos; Keypad Enter always allowed.");
            _renameKey = Config.Bind("Keyboard", "RenameKey", new KeyboardShortcut(KeyCode.Space),
                "重命名椅子的按键（默认 Space）。单击重命名椅子，双击编辑父类目。支持组合键 | Rename bench key (default Space). Single click to rename bench, double click to edit parent category. Supports key combinations.");
            _zoneRenamePersist = Config.Bind("Data", "ZoneRenameMap", string.Empty,
                "区域到父类目重命名映射，形如 Zone=Renamed;Zone2=Renamed2");
            LoadZoneRenameMap();

            _benchesPersist = Config.Bind("Data", "ActivatedBenches", "Tut_01|Death Respawn Marker Init",
                "已激活椅子列表：以分号分隔的 scene|marker 形式");
            _favoriteScenesPersist = Config.Bind("Data", "FavoritedScenes", string.Empty,
                "收藏的分类/场景名（显示用的组名），分号分隔，按收藏时间顺序排列");


            _importAllOnEnter = Config.Bind("Data", "ImportAllBenchesOnEnter", false,
                "进入存档时：将地图中的所有椅子写入配置（来自 SceneTeleportMap 的白名单）。默认关闭 | On entering a save: write all benches from map (SceneTeleportMap whitelist) into config. Default: off");

            // 手柄配置
            _gamepadShowMap = Config.Bind("Gamepad", "ShowMapPreviewButton", "X",
                "长按强制打开背包地图，放大并居中当前选中椅子；松开关闭地图 | Long-press to open the inventory map fullscreen, zoom to scene and center on the selected bench; release to close\nXbox: X | PS: SQUARE or CROSS. 支持别名：A/B/X/Y, SQUARE/CROSS/CIRCLE/TRIANGLE, LB/RB/L1/R1, SELECT/START/SHARE/OPTIONS");
            _enableGamepadSupport = Config.Bind("Gamepad", "EnableGamepadSupport", true,
                "启用手柄支持 | Enable gamepad support");
            _gamepadOpenUI = Config.Bind("Gamepad", "OpenUIButtons", "LB+RB+X",
                "打开椅子传送界面的手柄按键组合 | Gamepad buttons to open chair teleport UI\n" +
                "可选值 | Available options: LB+RB+X, LT+RT+X, LB+RB+Y, SELECT+START, etc.\n" +
                "Xbox按键 | Xbox buttons: LB, RB, LT, RT, A, B, X, Y, SELECT, START, UP, DOWN, LEFT, RIGHT\n" +
                "PS按键别名 | PS button aliases: L1, R1, L2, R2, CROSS, CIRCLE, SQUARE, TRIANGLE, SHARE, OPTIONS");
            _gamepadQuickRest = Config.Bind("Gamepad", "QuickRestButtons", "RB+Y",
                "随地复活的手柄按键组合 | Gamepad buttons for quick rest\n" +
                "可选值 | Available options: RB+Y, LB+RB+Y, LT+RT+Y, LB+RB+X, SELECT+START, etc.\n" +
                "Xbox按键 | Xbox buttons: LB, RB, LT, RT, A, B, X, Y, SELECT, START, UP, DOWN, LEFT, RIGHT\n" +
                "PS按键别名 | PS button aliases: L1, R1, L2, R2, CROSS, CIRCLE, SQUARE, TRIANGLE, SHARE, OPTIONS");

            _gamepadOpenHoldSeconds = Config.Bind("Gamepad", "OpenUIHoldSeconds", 0.0f,
                "手柄打开UI所需的按住时长（秒），范围 0~1；默认 0 | Hold duration in seconds required to open UI with gamepad (0 to 1).\n" +
                "同时作为 UI 关闭保护时长；设为 0 则两者均关闭 | Also used as UI close-protection duration; set to 0 to disable both.");

            // 语言配置
            _uiLanguage = Config.Bind("General", "UILanguage", "Chinese",
                "界面语言 | UI Language\n" +
                "可选值 | Available options: Chinese, English, Auto");

            // 难度选项：必须坐在椅子上才能打开UI（默认关闭）
            _requireOnBenchToOpenUI = Config.Bind("Difficulty", "RequireOnBenchToOpenUI", false,
                "玩家必须坐在椅子上才能打开UI | Player must be at a bench to open the UI");

            // UI缩放配置
            _uiScale = Config.Bind("UI", "UIScale", 1.0f,
                "界面缩放倍数 | UI Scale Factor\n" +
                "推荐值 | Recommended values: 1.0 (1080p), 1.5 (1440p), 2.0 (4K)\n" +
                "范围 | Range: 0.5 - 3.0");
            _autoScale = Config.Bind("UI", "AutoScale", true,
                "自动根据屏幕分辨率调整界面大小 | Auto scale UI based on screen resolution\n" +
                "启用后会根据屏幕分辨率自动调整，禁用后使用手动设置的缩放值 | When enabled, automatically adjusts based on resolution");

            LoadPersistedBenches();
            SeedCurrentBenchIfEmpty();

            LoadFavoriteScenes();

            // Watch for config changes to re-parse combos
            _gamepadOpenUI.SettingChanged += (_, __) => ParsePadCombos();
            _gamepadQuickRest.SettingChanged += (_, __) => ParsePadCombos();
            _gamepadOpenHoldSeconds.SettingChanged += (_, __) => ParsePadCombos();

        }

        private void DetectLanguage()
        {
            try
            {
                var langSetting = _uiLanguage?.Value ?? "Chinese";

                switch (langSetting.ToLower())
                {
                    case "chinese":
                    case "中文":
                        _isChineseLanguage = true;
                        break;

                    case "english":
                    case "英文":
                        _isChineseLanguage = false;
                        break;

                    case "auto":
                    case "自动":
                    default:
                        // 自动检测游戏语言
                        _isChineseLanguage = true; // 默认中文

                        // 检查游戏的语言设置（避免过早访问 GameManager.instance 触发日志）
                        var gameManager = UnityEngine.Object.FindFirstObjectByType<GameManager>();
                        if (gameManager != null)
                        {
                            try
                            {
                                var langField = typeof(GameManager).GetField("gameSettings",
                                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (langField != null)
                                {
                                    var gameSettings = langField.GetValue(gameManager);
                                    if (gameSettings != null)
                                    {
                                        var langProp = gameSettings.GetType().GetProperty("language");
                                        if (langProp != null)
                                        {
                                            var langValue = langProp.GetValue(gameSettings);
                                            if (langValue != null)
                                            {
                                                var langStr = langValue.ToString();
                                                _isChineseLanguage = !langStr.Contains("EN");
                                            }
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // 反射失败，检查系统语言
                                var systemLang = Application.systemLanguage;
                                _isChineseLanguage = systemLang == SystemLanguage.Chinese ||
                                                   systemLang == SystemLanguage.ChineseSimplified ||
                                                   systemLang == SystemLanguage.ChineseTraditional;
                            }
                        }
                        else
                        {
                            // GM 尚未就绪：使用系统语言作为兜底，避免产生 "Couldn't find a Game Manager" 日志
                            var systemLang = Application.systemLanguage;
                            _isChineseLanguage = systemLang == SystemLanguage.Chinese ||
                                               systemLang == SystemLanguage.ChineseSimplified ||
                                               systemLang == SystemLanguage.ChineseTraditional;
                        }
                        break;
                }
            }
            catch
            {
                // 默认使用中文
                _isChineseLanguage = true;
            }
        }

        private void OnDestroy()
        {
            // 只有当前实例是正确的实例时才清理Hook
            if (_instance == this)
            {
                try { SceneManager.activeSceneChanged -= OnActiveSceneChanged; } catch { }
                try { _harmony?.UnpatchSelf(); } catch { /* ignore */ }

                // 强制清理UI状态，防止残留
                if (_uiOpen)
                {
                    _uiOpen = false;
                    try
                    {
                        var hc = HeroController.SilentInstance;
                        if (hc != null) hc.RemoveInputBlocker(this);
                    }
                    catch { /* ignore */ }
                }

                // 清理编辑状态
                _editingIndex = -1;
                _editingText = "";
                _editingFocusSet = false;

                _instance = null;
                Logger.LogInfo("ChairTeleportPlugin实例已正确清理");
            }
        }

        private void Update()
        {
            // 首次进入游戏时清理不在游戏场景内的椅子记录
            if (!_didInitialCleanup)
            {
                int removed = CleanInvalidSceneBenches();
                _didInitialCleanup = true;
                if (removed > 0)
                {
                    D($"初始清理：移除 {removed} 条无效场景的椅子");
                    SavePersistedBenches();
                }
            }

            RefreshRefs();
            if (_gm == null || _pd == null) return;

            var ih = _ih;
            var currentTime = Time.unscaledTime;

            // 游戏背包/地图是否打开（打开时禁止打开椅子UI）
            bool inventoryOpen = false;
            try { var pdInv = PlayerData.instance; inventoryOpen = (pdInv != null && pdInv.isInventoryOpen); } catch { }


            // F2随地恢复 (键盘) 或 自定义手柄按键 - 但在UI界面时不响应，支持双击：双击传送到复活点，单击设置复活点
            bool f2KeyDown = _quickRestKey.Value.IsDown();

            // 手柄组合使用“边沿触发”：从未按住 -> 按住 才算一次按下
            bool f2PadHeld = _enableGamepadSupport.Value && IsGamepadButtonPressed(_gamepadQuickRest?.Value, ih);
            bool f2PadDown = f2PadHeld && !_padQuickRestHeldPrev;

            bool f2WithinDouble = _pendingF2Single && (Time.unscaledTime - _pendingF2Start) <= F2DoubleTapWindow;
            bool f2CooldownOk = (currentTime - _lastF2PressTime > KeyCooldown) || f2WithinDouble;
            bool f2AnyPressed = (f2KeyDown || f2PadDown) && !_uiOpen && f2CooldownOk;

            // 更新手柄状态（在处理完按键逻辑后）
            _padQuickRestHeldPrev = f2PadHeld;

            if (f2AnyPressed)
            {
                _lastF2PressTime = currentTime;
                D($"F2 pressed: key={f2KeyDown}, pad={f2PadDown}, pending={_pendingF2Single}, timeSinceStart={(_pendingF2Single ? Time.unscaledTime - _pendingF2Start : -1f)}");

                // 检查是否为双击
                if (_pendingF2Single && (Time.unscaledTime - _pendingF2Start) <= F2DoubleTapWindow)
                {
                    // 已经是双击：取消待定的单击动作，执行传送到复活点
                    D("F2 double-tap detected, teleporting to respawn point");
                    if (_pendingF2Co != null) { try { StopCoroutine(_pendingF2Co); } catch { } _pendingF2Co = null; }
                    _pendingF2Single = false;
                    TryTeleportToSetRespawnPoint();
                }
                else
                {
                    // 启动“延迟的单击”候选，在双击窗口内等待第二次按下
                    D("F2 single-tap candidate, starting delay timer");
                    _pendingF2Single = true;
                    _pendingF2Start = Time.unscaledTime;
                    if (_pendingF2Co != null) { try { StopCoroutine(_pendingF2Co); } catch { } _pendingF2Co = null; }
                    _pendingF2Co = StartCoroutine(SingleF2AfterDelay());
                }
            }

            // F1打开椅子传送UI (键盘) 或 自定义手柄按键，添加防抖
            bool f1Pressed = _openUiKey.Value.IsDown() && (currentTime - _lastF1PressTime > KeyCooldown);

            // 手柄打开UI需长按1秒，仅在 UI 关闭时生效；关闭UI仍立即响应
            bool gamepadOpenUIPressed = false;
            bool gamepadOpenRaw = _enableGamepadSupport.Value && IsGamepadButtonPressed(_gamepadOpenUI?.Value, ih);
            if (!_uiOpen)
            {
                if (gamepadOpenRaw)
                {
                    if (_gamepadHoldStartForOpen < 0f) _gamepadHoldStartForOpen = currentTime;
                    if ((currentTime - _gamepadHoldStartForOpen) >= _cachedHoldSeconds && (currentTime - _lastF1PressTime > KeyCooldown))
                    {
                        gamepadOpenUIPressed = true;
                        _gamepadHoldStartForOpen = float.PositiveInfinity; // 防止长按连触发，直到释放
                    }
                }
                else
                {
                    _gamepadHoldStartForOpen = -1f; // 未按住，重置计时
                }
            }
            else
            {
                // UI 已打开：按下即可关闭（无需长按）
                gamepadOpenUIPressed = gamepadOpenRaw && (currentTime - _lastF1PressTime > KeyCooldown);
            }

            if (f1Pressed || gamepadOpenUIPressed)
            {
                _lastF1PressTime = currentTime;

                if (!_uiOpen)
                {
                    // 若游戏背包/地图已打开，则不允许打开椅子UI
                    if (inventoryOpen) { D("OpenUI blocked: game inventory is open"); return; }

                    // 难度选项：必须坐在椅子上才能打开UI
                    if (_requireOnBenchToOpenUI != null && _requireOnBenchToOpenUI.Value)
                    {
                        var pdOpen = PlayerData.instance;
                        if (pdOpen == null || !pdOpen.atBench)
                        {
                            return;
                        }
                    }
                    OpenUI();
                }
                else
                {
                    // 达到保护时长后允许关闭
                    if (Time.unscaledTime - _uiOpenedAt >= CloseGraceSeconds)
                    {
                        CloseUI();
                    }
                    // else: 忽略关闭请求
                }
            }

            if (_uiOpen)
            {
                // 如果正在编辑（子条目或父类目），不处理导航和其他按键，让OnGUI处理
                if (_editingIndex >= 0 || _editingCategoryIndex >= 0)
                {
                    // 编辑模式下只处理ESC关闭UI（如果ESC没有被编辑逻辑消费）
                    return;
                }

                // 在F3地图预览激活时，禁止导航和传送操作
                if (!_showMapActive)
                {
                    HandleUiNavigation();
                }

                // ESC 关闭整个 UI；B键：分类层关闭UI，子条目层返回父条目；Enter：分类层进入子条目；条目层执行传送
                // 在F3地图预览激活时，禁止传送操作
                var actions = ih != null ? ih.inputActions : null;
                bool escPressed = Input.GetKeyDown(KeyCode.Escape);
                bool cancelPressed = (actions != null && actions.MenuCancel.WasPressed);
                bool submitPressed = !_showMapActive && ((_confirmTeleportKey != null && _confirmTeleportKey.Value.IsDown()) || Input.GetKeyDown(KeyCode.KeypadEnter) || (actions != null && actions.MenuSubmit.WasPressed));

                if ((Time.unscaledTime - _uiOpenedAt) >= CloseGraceSeconds && escPressed)
                {
                    CloseUI();
                }
                else if ((Time.unscaledTime - _uiOpenedAt) >= CloseGraceSeconds && cancelPressed)
                {
                    // B键逻辑：在分类视图中关闭UI，在子条目视图中返回父条目
                    if (_inCategoryView)
                    {
                        _uiClosedByCancelNext = true; // 标记为“取消键关闭”，避免误触发回血
                        CloseUI();
                    }
                    else
                    {
                        BackToCategories();
                        _lastNavTime = Time.unscaledTime;
                    }
                }
                else if (submitPressed)
                {
                    if (_inCategoryView) EnterSelectedCategory();
                    else ConfirmTeleport();
                }
                // 空格/Y 重命名与删除（仅在条目层生效，且需要存在可选项）
                // 在F3地图预览激活时，禁止重命名和删除操作
                else if (!_showMapActive && !_inCategoryView && _benches.Count > 0)
                {
                    // 记录按下时间（重命名键）
                    if (_renameKey.Value.IsDown())
                    {
                        _spaceKeyDownAt = Time.unscaledTime;
                        _spaceDeleteTriggered = false;
                    }
                    // 长按检测（重命名键）：达到阈值后执行删除（只触发一次，直到松开）
                    else if (_renameKey.Value.IsPressed() && _spaceKeyDownAt > 0f && !_spaceDeleteTriggered)
                    {
                        if (Time.unscaledTime - _spaceKeyDownAt >= SpaceDeleteHoldSeconds)
                        {
                            RemoveBenchAtIndex(_selected);
                            _spaceDeleteTriggered = true;
                            _lastNavTime = Time.unscaledTime;
                        }
                    }
                    // 松开（重命名键）：若未触发长按删除，则视为短按 -> 开始编辑
                    else if (_renameKey.Value.IsUp())
                    {
                        if (!_spaceDeleteTriggered && Time.unscaledTime - _spaceKeyDownAt < SpaceDeleteHoldSeconds && Time.unscaledTime - _lastNavTime > NavCooldown)
                        {
                            bool isDouble = (_pendingSpaceSingle && _pendingSpaceIndex == _selected && (Time.unscaledTime - _pendingSpaceStart) <= DoubleSpaceEditSeconds);
                            if (isDouble)
                            {
                                // 识别为双击：取消待定的单击编辑，改为编辑父类目（仅当前条目）
                                if (_pendingSpaceCo != null) { try { StopCoroutine(_pendingSpaceCo); } catch { } _pendingSpaceCo = null; }
                                _pendingSpaceSingle = false;
                                StartEditingCategoryFromChild();
                            }
                            else
                            {
                                // 启动“延迟的单击编辑”候选，等待 DoubleSpaceEditSeconds，若期间未发生第二次空格，则执行 StartEditing()
                                _pendingSpaceSingle = true;
                                _pendingSpaceIndex = _selected;
                                _pendingSpaceStart = Time.unscaledTime;
                                if (_pendingSpaceCo != null) { try { StopCoroutine(_pendingSpaceCo); } catch { } _pendingSpaceCo = null; }
                                _pendingSpaceCo = StartCoroutine(SingleSpaceEditAfterDelay());
                            }
                            _lastSpaceOnChildTime = Time.unscaledTime;
                            _lastSpaceChildIndex = _selected;
                        }
                        _spaceKeyDownAt = -1f;
                        _spaceDeleteTriggered = false;
                    }

                    // 手柄“Cast”（Xbox: Y, PS: Triangle）—— 短按重命名，长按(>=2s)删除
                    var actionsY = ih != null ? ih.inputActions : null;
                    if (actionsY != null)
                    {
                        if (actionsY.DreamNail.WasPressed)
                        {
                            _yButtonDownAt = Time.unscaledTime;
                            _yDeleteTriggered = false;
                        }
                        else if (actionsY.DreamNail.IsPressed && _yButtonDownAt > 0f && !_yDeleteTriggered)
                        {
                            if (Time.unscaledTime - _yButtonDownAt >= SpaceDeleteHoldSeconds)
                            {
                                RemoveBenchAtIndex(_selected);
                                _yDeleteTriggered = true;
                                _lastNavTime = Time.unscaledTime;
                            }
                        }
                        else if (actionsY.DreamNail.WasReleased)
                        {
                            if (!_yDeleteTriggered && Time.unscaledTime - _yButtonDownAt < SpaceDeleteHoldSeconds && Time.unscaledTime - _lastNavTime > NavCooldown)
                            {
                                // 检查是否为双击Y键
                                bool isDoubleY = (_pendingYSingle && _pendingYIndex == _selected && (Time.unscaledTime - _pendingYStart) <= DoubleSpaceEditSeconds);
                                if (isDoubleY)
                                {
                                    // 识别为双击：取消待定的单击编辑，改为编辑父类目（仅当前条目）
                                    if (_pendingYCo != null) { try { StopCoroutine(_pendingYCo); } catch { } _pendingYCo = null; }
                                    _pendingYSingle = false;
                                    StartEditingCategoryFromChild();
                                }
                                else
                                {
                                    // 启动"延迟的单击编辑"候选，等待 DoubleSpaceEditSeconds，若期间未发生第二次Y键，则执行 StartEditing()
                                    _pendingYSingle = true;
                                    _pendingYIndex = _selected;
                                    _pendingYStart = Time.unscaledTime;
                                    if (_pendingYCo != null) { try { StopCoroutine(_pendingYCo); } catch { } }
                                    _pendingYCo = StartCoroutine(SingleYEditAfterDelay());
                                }
                                _lastNavTime = Time.unscaledTime;
                            }
                            _yButtonDownAt = -1f;
                            _yDeleteTriggered = false;
                        }
                    }
                }
                // 右侧地图预览：仅在条目层且有选中项时可用（按住才显示，松开立即取消）
                if (!_inCategoryView && _benches.Count > 0 && _selected >= 0)
                {
                    float now = Time.unscaledTime;

                    // 键盘：检测是否按住（兼容 KeyboardShortcut 与物理键）
                    bool kbHeld = false;
                    if (_showMapKey != null)
                    {
                        try { kbHeld = _showMapKey.Value.IsPressed(); } catch { }
                        try { kbHeld = kbHeld || Input.GetKey(_showMapKey.Value.MainKey); } catch { }


                    }


                    if (kbHeld)
                    {
                        if (_showMapKeyDownAt < 0f)
                        {
                            _showMapKeyDownAt = now;
                        }
                    }
                    else
                    {
                        _showMapKeyDownAt = -1f;
                    }

                    // 手柄：检测是否按住（默认 X；PS 可用 SQUARE/CROSS 别名）
                    bool padHeld = _enableGamepadSupport.Value && IsGamepadButtonPressed(_gamepadShowMap?.Value, ih);
                    if (padHeld)
                    {
                        if (_showMapPadDownAt < 0f)
                        {
                            _showMapPadDownAt = now;
                        }
                    }
                    else
                    {
                        _showMapPadDownAt = -1f;
                    }

                    bool anyHeldLong = (kbHeld && _showMapKeyDownAt >= 0f && (now - _showMapKeyDownAt) >= HoldThreshold)
                                     || (padHeld && _showMapPadDownAt >= 0f && (now - _showMapPadDownAt) >= HoldThreshold);

                    if (anyHeldLong)
                    {
                        if (!_showMapActive)
                        {
                            _mapPreviewShown = false; // 重置标记，准备显示新的地图预览
                        }
                        _showMapActive = true;
                        _showMapIndex = _selected; // 跟随当前选中项
                    }
                    else
                    {
                        _showMapActive = false; // 没有按住或未到阈值，隐藏
                        _mapPreviewShown = false; // 重置标记
                        StopInventoryMapPreview();
                    }
                }
                else
                {
                    _showMapActive = false;
                    _showMapKeyDownAt = -1f;
                    _showMapPadDownAt = -1f;
                    _mapPreviewShown = false; // 重置标记
                    StopInventoryMapPreview();
                }

                // 在F3地图预览激活时，禁止分类重命名操作
                if (!_showMapActive && _inCategoryView && _catNames.Count > 0)
                {
                    // 父类目：重命名键/Y 短按 -> 开始重命名
                    if (_renameKey.Value.IsUp() && Time.unscaledTime - _lastNavTime > NavCooldown)
                    {
                        StartEditingCategory();
                        _lastNavTime = Time.unscaledTime;
                    }
                    var actionsY2 = ih != null ? ih.inputActions : null;
                    if (actionsY2 != null && actionsY2.DreamNail.WasReleased && Time.unscaledTime - _lastNavTime > NavCooldown)
                    {
                        StartEditingCategory();
                        _lastNavTime = Time.unscaledTime;
                    }
                }
            }

            // 当全屏地图显示时：对选中椅子的 MapPin 做脉冲放大以便更显眼
            if (_showMapActive && _highlightedPin != null)
            {
                try
                {
                    float tPulse = (Time.unscaledTime - _highlightPulseStart);
                    float s = 1f + 0.35f * (0.5f + 0.5f * Mathf.Sin(tPulse * 2f * Mathf.PI * 1.5f)); // 增强效果：更大幅度，更快频率
                    _highlightedPin.transform.localScale = _highlightedPinOrigScale * s;
                }
                catch { }
            }
            else if (!_showMapActive && _highlightedPin != null)
            {
                try { _highlightedPin.transform.localScale = _highlightedPinOrigScale; } catch { }
                _highlightedPin = null;
            }

        }

        // 屏幕提示
        private void ShowTip(string text, float seconds = 2.0f)
        {
            _tipText = text;
            _tipUntil = Time.unscaledTime + seconds;
        }

        private void ShowHud(string text, float seconds = 1.2f)
        {
            _hudText = text;
            _hudUntil = Time.unscaledTime + seconds;
        }

        private void Start()
        {
            // 游戏完全加载后重新检测语言
            DetectLanguage();

            // 不在启动时触发任何会写配置或打开UI的操作；这些将延后到“进入存档后”的钩子中执行


            // Parse gamepad combos and refresh refs once
            ParsePadCombos();
            RefreshRefs(true);
            InvalidateSceneCaches();
            // Note: BuildGuiStyles() will be called in OnGUI when GUI.skin is available

        }

        // Refresh cached singletons occasionally or when forced
        private void RefreshRefs(bool force = false)
        {
            if (!force && Time.unscaledTime - _refreshedAt < 0.5f) return;
            _refreshedAt = Time.unscaledTime;
            try { var gm = GameManager.SilentInstance; if (gm != null) _gm = gm; } catch { }
            try { var hc = HeroController.SilentInstance; if (hc != null) _hc = hc; } catch { }
            try { if (PlayerData.HasInstance) _pd = PlayerData.instance; } catch { }
            try { var ih = InputHandler.Instance; if (ih != null) _ih = ih; } catch { }
        }

        private void InvalidateSceneCaches()
        {
            _markerByName = null; _markerList = null; _tpList = null;
        }

        private void BuildMarkerCache()
        {
            if (_markerByName != null && _markerList != null) return;
            var list = RespawnMarker.Markers;
            if (list == null || list.Count == 0)
            {
                _markerByName = null; _markerList = null; return;
            }
            var dict = new Dictionary<string, RespawnMarker>(list.Count);
            var copy = new List<RespawnMarker>(list.Count);
            foreach (var m in list)
            {
                if (!m) continue;
                copy.Add(m);
                if (!dict.ContainsKey(m.name)) dict[m.name] = m; // first wins
            }
            _markerByName = dict; _markerList = copy;
        }

        private void BuildTransitionCache()
        {
            if (_tpList != null) return;
            var list = TransitionPoint.TransitionPoints;
            _tpList = (list != null && list.Count > 0) ? new List<TransitionPoint>(list) : null;
        }

        // Build known bench marker map from SceneTeleportMap
        private void BuildKnownBenchMap()
        {
            if (_knownBenchByScene != null && _knownBenchByScene.Count > 0) return;
            try
            {
                _knownBenchByScene = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                var map = SceneTeleportMap.GetTeleportMap();
                if (map == null) return;
                foreach (var kv in map)
                {
                    var points = kv.Value?.RespawnPoints;
                    if (points == null) continue;
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < points.Count; i++)
                    {
                        var name = points[i];
                        if (string.IsNullOrEmpty(name)) continue;
                        // 包含 RestBench 但排除 CustomRestBench(Clone) 等自定义椅子
                        if (name.Contains("RestBench") && !name.Contains("CustomRestBench(Clone)"))
                        {
                            set.Add(name);
                        }
                    }
                    if (set.Count > 0) _knownBenchByScene[kv.Key] = set;
                }
            }
            catch { /* ignore */ }
        }

        // Check if a marker is a known bench for a given scene
        internal bool IsKnownBenchMarker(string scene, string marker)
        {
            if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(marker)) return false;
            try
            {
                BuildKnownBenchMap();
                if (_knownBenchByScene != null && _knownBenchByScene.TryGetValue(scene, out var set))
                {
                    if (set.Contains(marker)) return true;
                }
            }
            catch { }
            // 仅使用白名单判断（不再使用名称模式回退）
            return false;
        }

        // Import all benches from SceneTeleportMap whitelist into our config/persisted list (one-shot when enabled)
        internal void ImportAllBenchesFromMap()
        {
            try
            {
                BuildKnownBenchMap();
                if (_knownBenchByScene == null || _knownBenchByScene.Count == 0) return;
                int before = _benches.Count;
                foreach (var kv in _knownBenchByScene)
                {
                    var scene = kv.Key;
                    var set = kv.Value;
                    if (set == null || set.Count == 0) continue;
                    foreach (var marker in set)
                    {
                        AddBench(scene, marker);
                    }
                }
                SavePersistedBenches();
                D($"ImportAllBenchesFromMap: imported {_benches.Count - before} new benches; total={_benches.Count}");
            }
            catch (Exception e)
            {
                Logger.LogError($"ImportAllBenchesFromMap error: {e}");
            }
        }



        private string GetInfoTextCached()
        {
            int count = _benches.Count;
            int totalPages = count > 0 ? Mathf.CeilToInt((float)count / ItemsPerPage) : 1;


            int selectedOneBased = _selected + 1;
            if (count != _infoLastCount || _currentPage != _infoLastPage || selectedOneBased != _infoLastSelected)
            {
                _infoLastCount = count; _infoLastPage = _currentPage; _infoLastSelected = selectedOneBased;
                _infoTextCache = GetText(
                    $"椅子数：{count} | 选中：{selectedOneBased} | 第 {_currentPage + 1} 页 / 共 {totalPages} 页",
                    $"Benches: {count} | Selected: {selectedOneBased} | Page {_currentPage + 1} / {totalPages}");
            }
            return _infoTextCache ?? string.Empty;
        }

        // Parse and cache gamepad combos and hold seconds
        private void ParsePadCombos()
        {
            _comboOpenKeys = ParseCombo(_gamepadOpenUI?.Value);
            _comboRestKeys = ParseCombo(_gamepadQuickRest?.Value);
            _cachedHoldSeconds = Mathf.Clamp(_gamepadOpenHoldSeconds?.Value ?? 0f, 0f, 1f);
        }

        private static KeyCode[] ParseCombo(string combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return Array.Empty<KeyCode>();
            var parts = combo.ToUpperInvariant().Split('+');
            var list = new List<KeyCode>(parts.Length);
            foreach (var p in parts)
            {
                switch (p.Trim())
                {
                    case "A": list.Add(KeyCode.JoystickButton0); break;
                    case "B": list.Add(KeyCode.JoystickButton1); break;
                    case "X": list.Add(KeyCode.JoystickButton2); break;
                    case "Y": list.Add(KeyCode.JoystickButton3); break;
                    // PS aliases (Unity common mapping: 0=Square,1=Cross,2=Circle,3=Triangle)
                    case "SQUARE": list.Add(KeyCode.JoystickButton0); break;
                    case "CROSS": list.Add(KeyCode.JoystickButton1); break;
                    case "CIRCLE": list.Add(KeyCode.JoystickButton2); break;
                    case "TRIANGLE": list.Add(KeyCode.JoystickButton3); break;
                    case "LB":
                    case "L1": list.Add(KeyCode.JoystickButton4); break;
                    case "RB":
                    case "R1": list.Add(KeyCode.JoystickButton5); break;
                    case "SELECT":
                    case "SHARE": list.Add(KeyCode.JoystickButton6); break;
                    case "START":
                    case "OPTIONS": list.Add(KeyCode.JoystickButton7); break;
                    default: break; // ignore unsupported inputs like triggers here
                }
            }
            return list.ToArray();
        }

        private static bool AllPressed(KeyCode[] keys)
        {
            if (keys == null || keys.Length == 0) return false;
            for (int i = 0; i < keys.Length; i++)
                if (!Input.GetKey(keys[i])) return false;
            return true;
        }

        private void BuildGuiStyles()
        {
            if (_stylesBuilt) return;
            try
            {
                int S(int baseSize) => Mathf.RoundToInt(baseSize * _fontScale);
                _stHud = new GUIStyle(GUI.skin.label) { fontSize = S(28), fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, normal = { textColor = new Color(1f, 1f, 1f, 0.95f) } };
                _stTitle = new GUIStyle(GUI.skin.label) { fontSize = S(20), fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.8f, 0.9f, 1f) } };
                _stInfo = new GUIStyle(GUI.skin.label) { fontSize = S(12), normal = { textColor = new Color(0.7f, 0.8f, 0.9f) } };
                _stIndicator = new GUIStyle(GUI.skin.label) { fontSize = S(16), normal = { textColor = new Color(0.5f, 0.5f, 0.6f) } };
                _stIndicatorSel = new GUIStyle(GUI.skin.label) { fontSize = S(16), normal = { textColor = new Color(1f, 0.8f, 0.3f) } };
                _stName = new GUIStyle(GUI.skin.label) { fontSize = S(14), normal = { textColor = new Color(0.9f, 0.9f, 0.95f) } };
                _stNameSel = new GUIStyle(GUI.skin.label) { fontSize = S(14), fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
                _stScene = new GUIStyle(GUI.skin.label) { fontSize = S(10), fontStyle = FontStyle.Italic, normal = { textColor = new Color(0.6f, 0.7f, 0.8f) } };
                _stMarker = new GUIStyle(GUI.skin.label) { fontSize = S(12), fontStyle = FontStyle.Normal, normal = { textColor = new Color(0.75f, 0.85f, 0.95f) } };
                _stInput = new GUIStyle(GUI.skin.textField) { fontSize = S(14), normal = { textColor = Color.white } };
                _stEmpty = new GUIStyle(GUI.skin.label) { fontSize = S(14), fontStyle = FontStyle.Italic, normal = { textColor = new Color(0.6f, 0.6f, 0.7f) } };
                _stHeart = new GUIStyle(GUI.skin.label) { fontSize = S(14), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.9f, 0.8f, 0.3f) } };
                _stylesBuilt = true;
            }
            catch
            {
                // GUI.skin not available yet, will retry in next OnGUI call
            }
        }



        // 判断是否进入可游玩阶段（避免在菜单/加载早期访问语言系统）
        private bool IsGameplayReady()
        {
            try
            {
                // 使用 SilentInstance，避免触发 GameManager.instance 的报错日志，也避免反射级查找
                var gm = GameManager.SilentInstance;
                var hc = HeroController.SilentInstance;
                return gm != null && hc != null && hc.acceptingInput;
            }
            catch { return false; }
        }

        // 进入存档后首帧预热UI：打开一次并立刻关闭，移除第一次打开的卡顿
        private IEnumerator PrewarmUIRoutine()
        {
            if (_uiPrewarmed) yield break;

            // 等待进入可游玩阶段，最多等待 15 秒
            float deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline && !IsGameplayReady())
            {
                yield return new WaitForSecondsRealtime(0.2f);
            }

            // 再等一帧，确保UI与输入系统稳定
            yield return null;

            try
            {
                // 预先创建绘制所需的小纹理与渐变纹理，避免首次分配卡顿
                try { CTP_EnsureTex1x1(); CTP_EnsureGradient(new Color(0.08f, 0.09f, 0.12f, 0.96f), new Color(0.05f, 0.06f, 0.09f, 0.96f)); } catch { }

                // 预先构建 GUI 样式（若 GUI.skin 可用），减少首次可见时的样式创建
                try { BuildGuiStyles(); } catch { }

                _uiPrewarming = true;
                _bypassBenchRequirementOnce = true; // 预热时忽略“必须在椅子上”限制
                OpenUI();

                // 等待至少一次重绘（Layout/Repaint）
                yield return null;
                yield return new WaitForEndOfFrame();

                CloseUI();
            }
            finally
            {
                _uiPrewarming = false;
                _bypassBenchRequirementOnce = false;
                _uiPrewarmed = true;
            }
        }

        // 进入存档后：规范命名（仅此时才会更改配置文件）
        private IEnumerator PostEnterNormalizeRoutine()
        {
            // 等待进入可游玩阶段，最多等待 15 秒
            float deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline && !IsGameplayReady())
            {
                yield return new WaitForSecondsRealtime(0.2f);
            }
            if (!IsGameplayReady()) yield break;

            bool changed = false;
            try { changed = ConvertOldFormatBenchNames(); } catch { changed = false; }

            // 在旧格式迁移之后，若为中文玩家，则为区划构建默认映射并本地化父类目字段
            bool mapChanged = false, parentChanged = false;
            try { EnsureDefaultZoneRenameMapAndLocalizeParents(out mapChanged, out parentChanged); } catch { mapChanged = parentChanged = false; }

            if (changed || mapChanged || parentChanged)
            {
                // 仅在进入存档后才保存
                try { SaveZoneRenameMap(); } catch { }
                try { SavePersistedBenches(); } catch { }
                _groupsDirty = true;
                try { RebuildGroups(); } catch { }
            }
        }

        // 为中文玩家在语言就绪后：
        // 1) 自动填充 ZoneRenameMap（枚举名 -> 语言表中文名）
        // 2) 将仍为“枚举名”的父类目字段改为中文名
        private void EnsureDefaultZoneRenameMapAndLocalizeParents(out bool mapChanged, out bool parentChanged)
        {
            mapChanged = false; parentChanged = false;
            if (!_isChineseLanguage) return; // 仅中文环境需要默认映射
            if (!IsGameplayReady()) return;

            var map = SceneTeleportMap.GetTeleportMap();
            if (map == null) return;

            // 先补齐映射（仅当 UILanguage 设置为 Chinese 时）
            if (_isChineseLanguage)
            {
                try
                {
                    var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
                    for (int i = 0; i < _benches.Count; i++)
                    {
                        var b = _benches[i]; if (string.IsNullOrEmpty(b?.Scene)) continue;
                        if (!map.TryGetValue(b.Scene, out var info)) continue;
                        var zoneKey = info.MapZone.ToString();
                        if (string.IsNullOrEmpty(zoneKey)) continue;
                        if (!seen.Add(zoneKey)) continue;
                        string zh = null;
                        try { zh = Language.Get(zoneKey, "Map Zones"); } catch { zh = null; }
                        if (!string.IsNullOrEmpty(zh) && !string.Equals(zh, zoneKey, StringComparison.CurrentCulture))
                        {
                            if (!_zoneRenameMap.ContainsKey(zoneKey)) { _zoneRenameMap[zoneKey] = zh; mapChanged = true; }
                        }
                    }
                }
                catch { }
            }

            // 不再修改 ActivatedBenches 内已存的父类目字段；统一存储为英文（枚举键）由保存/读取时保证
            // 此处仅补齐 ZoneRenameMap 的默认映射，parentChanged 始终为 false
        }


        // 尝试为某个场景获取官方本地化的“区域名”（中文环境下返回中文）
        private string TryGetLocalizedZoneNameForScene(string scene)
        {
            try
            {
                if (!IsGameplayReady()) return null; // 避免过早触发语言系统
                var map = SceneTeleportMap.GetTeleportMap();
                if (map != null && map.TryGetValue(scene, out var info))
                {
                    var key = info.MapZone.ToString();
                    try
                    {
                        var text = Language.Get(key, "Map Zones");
                        if (!string.IsNullOrEmpty(text)) return text;
                    }
                    catch { }
                }
            }
            catch { /* 语言系统或资源未就绪，稍后重试 */ }
            return null;
        }

        // 单次刷新：为所有“未重命名”的椅子，按 AddedOrder 顺序，改为“#编号”（子类目只保留编号）
        private bool TryRefreshLocalizedNamesOnce()
        {

            bool anySuccess = false;
            bool changed = false;
            var list = _benches.Where(b => !b.IsRenamed).OrderBy(b => b.AddedOrder).ToList();
            // 构建每个父类目下已占用编号的集合（包含已重命名与未重命名的条目），避免重复编号
            var usedMap = new Dictionary<string, HashSet<int>>(StringComparer.CurrentCulture);
            for (int i = 0; i < _benches.Count; i++)
            {
                var ob = _benches[i];
                var cat = GetCategoryKey(ob);
                if (string.IsNullOrEmpty(cat)) continue;
                var dn = ob.DisplayName ?? string.Empty;
                int p = dn.LastIndexOf('#');
                if (p >= 0 && p + 1 < dn.Length)
                {
                    var numStr = dn.Substring(p + 1).Trim();
                    bool digitsOnly = numStr.Length > 0;
                    for (int k = 0; k < numStr.Length; k++) { if (!char.IsDigit(numStr[k])) { digitsOnly = false; break; } }
                    if (digitsOnly && int.TryParse(numStr, out var n) && n > 0)
                    {
                        if (!usedMap.TryGetValue(cat, out var set)) { set = new HashSet<int>(); usedMap[cat] = set; }
                        set.Add(n);
                    }
                }
            }

            foreach (var b in list)
            {
                var cat = GetCategoryKey(b);
                if (string.IsNullOrEmpty(cat)) continue;
                anySuccess = true;
                if (!usedMap.TryGetValue(cat, out var used)) { used = new HashSet<int>(); usedMap[cat] = used; }
                int idx = 1;
                while (used.Contains(idx)) idx++;
                used.Add(idx);
                var newName = $"#{idx}";
                if (!string.Equals(b.DisplayName, newName, StringComparison.Ordinal))
                {
                    b.DisplayName = newName;
                    changed = true;
                }
            }

            if (changed)
            {
                _benches.Sort(BenchRef.Comparer);
                try { SavePersistedBenches(); } catch { }
            }

            return anySuccess;
        }
        private void RequestLocalizedRefresh()
        {
            _needsLocalizedRefresh = true;
            if (!_refreshRoutineRunning)
            {
                try { StartCoroutine(DelayedLocalizedNameRefreshRoutine()); } catch { }
            }
        }


        // ===== Two-level grouping helpers =====
        private string FriendlyFromScene(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return GetText("未知区域", "Unknown");
            try
            {
                // 优先使用 SceneTeleportMap 中的 MapZone + 语言表，保证与地图分类一致
                var map = SceneTeleportMap.GetTeleportMap();
                if (map != null && map.TryGetValue(scene, out var info))
                {
                    var key = info.MapZone.ToString();
                    if (IsGameplayReady())
                    {
                        try
                        {
                            var text = Language.Get(key, "Map Zones");
                            if (!string.IsNullOrEmpty(text)) return text;
                        }
                        catch { }
                    }
                    return key; // 语言未就绪或未进入可玩阶段，返回枚举名
                }
            }
            catch { /* ignore */ }
            // 无回退：无法从 MapZone 获取时，直接返回 Unknown
            return GetText("未知区域", "Unknown");
        }


        // 根据场景解析应使用的父类目名（照搬“所有椅子mod”的区域分类：使用 MapZone）
        // 优先级：用户重命名映射 > 语言表(Map Zones)文本 > MapZone 枚举名 > 场景前缀兜底
        private string ResolveCategoryNameForScene(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return GetText("未知区域", "Unknown");

            string zoneKey = null;     // MapZone.ToString()
            string localized = null;   // Language.Get(zoneKey, "Map Zones")
            try
            {
                var map = SceneTeleportMap.GetTeleportMap();
                if (map != null && map.TryGetValue(scene, out var info))
                {
                    zoneKey = info.MapZone.ToString();
                    if (IsGameplayReady())
                    {
                        try { localized = Language.Get(zoneKey, "Map Zones"); } catch { localized = null; }
                    }
                    else localized = null;
                }
            }
            catch { /* ignore */ }

            // 1) 用户映射优先（键允许用枚举名或本地化名）
            if (_zoneRenameMap != null)
            {
                if (!string.IsNullOrEmpty(zoneKey) && _zoneRenameMap.TryGetValue(zoneKey, out var m1) && !string.IsNullOrEmpty(m1))
                    return m1;
                if (!string.IsNullOrEmpty(localized) && _zoneRenameMap.TryGetValue(localized, out var m2) && !string.IsNullOrEmpty(m2))
                    return m2;
            }
            // 额外兼容：若用户在 ZoneRenameMap 中使用了去下划线的键（例如 BONETOWN），也尝试匹配
            if (!string.IsNullOrEmpty(zoneKey))
            {
                var alt = zoneKey.Replace("_", string.Empty);
                if (!string.Equals(alt, zoneKey, StringComparison.Ordinal) && _zoneRenameMap.TryGetValue(alt, out var m3) && !string.IsNullOrEmpty(m3))
                    return m3;
            }


            // 2) 首选语言表文本；中文环境会得到中文，其它语言会得到对应语言/英文
            if (!string.IsNullOrEmpty(localized)) return localized;

            // 3) 次选 MapZone 枚举名
            if (!string.IsNullOrEmpty(zoneKey)) return zoneKey;

            // 无回退：无法从 MapZone 获取时，直接返回 Unknown
            return GetText("未知区域", "Unknown");
        }

        private string GetCategoryKey(BenchRef b)
        {
            // 优先：使用已存父类目；显示前先用 ZoneRenameMap 做一次映射
            var parent = b?.ParentCategory;
            if (!string.IsNullOrEmpty(parent))
            {
                if (_zoneRenameMap != null && _zoneRenameMap.TryGetValue(parent, out var mapped) && !string.IsNullOrEmpty(mapped))
                    return mapped;

                // 进入存档后的自动中文化：
                // 若父类目是 MapZone 枚举键或其英文本地化名，则在中文环境下显示为中文
                if (_isChineseLanguage)
                {
                    try
                    {
                        var map = SceneTeleportMap.GetTeleportMap();
                        if (map != null && map.TryGetValue(b?.Scene ?? string.Empty, out var info))
                        {
                            var zoneKey = info.MapZone.ToString();
                            // 枚举键 → 中文
                            if (!string.IsNullOrEmpty(zoneKey) && string.Equals(parent, zoneKey, StringComparison.CurrentCultureIgnoreCase))
                            {
                                var zh = TryGetLocalizedZoneNameForScene(b.Scene);
                                if (!string.IsNullOrEmpty(zh)) return zh;
                            }
                            else
                            {
                                // 英文本地化名 → 中文（仅当与该场景的区域名匹配时）
                                var loc = TryGetLocalizedZoneNameForScene(b.Scene);
                                if (!string.IsNullOrEmpty(loc) && string.Equals(parent, loc, StringComparison.CurrentCultureIgnoreCase))
                                    return loc;
                            }
                        }
                    }
                    catch { /* ignore */ }
                }

                // 无映射/非枚举/自定义名称：原样返回
                return parent;
            }
            // 若旧数据缺少父类目，则回退到根据场景推断的友好名（含语言映射）
            return ResolveCategoryNameForScene(b?.Scene);
        }

        private void RebuildGroups()
        {
            _catNames.Clear();
            _catToIndices.Clear();
            for (int i = 0; i < _benches.Count; i++)
            {
                var b = _benches[i];
                var key = GetCategoryKey(b);
                if (!_catToIndices.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    _catToIndices[key] = list;
                    _catNames.Add(key);
                }
                list.Add(i);
            }
            _catNames.Sort((a, b) =>
            {
                bool af = IsCategoryFavorited(a);
                bool bf = IsCategoryFavorited(b);
                if (af != bf) return af ? -1 : 1;
                if (af && bf)
                {
                    int ia = _favoriteScenesOrder.TryGetValue(a, out var ia0) ? ia0 : int.MaxValue;
                    int ib = _favoriteScenesOrder.TryGetValue(b, out var ib0) ? ib0 : int.MaxValue;
                    int c = ia.CompareTo(ib);
                    if (c != 0) return c;
                }
                return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            });

            // 子条目排序：收藏优先，越早收藏越靠前，其次沿用原有名称排序


            for (int i = 0; i < _catNames.Count; i++)
            {
                var key = _catNames[i];
                if (_catToIndices.TryGetValue(key, out var li) && li != null && li.Count > 1)
                {
                    li.Sort((a, b) =>
                    {
                        var x = _benches[a]; var y = _benches[b];
                        if (x == null || y == null) return 0;
                        if (x.IsFavorite != y.IsFavorite) return x.IsFavorite ? -1 : 1;
                        if (x.IsFavorite && y.IsFavorite)
                        {
                            int c = x.FavoriteOrder.CompareTo(y.FavoriteOrder);
                            if (c != 0) return c;
                        }
                        return BenchRef.Comparer.Compare(x, y);
                    });
                }
            }

            _selectedCategory = Mathf.Clamp(_selectedCategory, 0, Mathf.Max(0, _catNames.Count - 1));
            if (!_inCategoryView && _catNames.Count > 0)
            {
                var cat = _catNames[_selectedCategory];
                var list = _catToIndices[cat];
                _selectedInCategory = Mathf.Clamp(_selectedInCategory, 0, Mathf.Max(0, list.Count - 1));
                if (list.Count > 0) _selected = list[_selectedInCategory];
            }
            _groupsDirty = false;
        }

        private string GetInfoTextTwoLevel()
        {
            if (_benches.Count == 0)
                return GetInfoTextCached();
            if (_groupsDirty) RebuildGroups();
            if (_inCategoryView)
            {
                int n = _catNames.Count;
                int idx = _catNames.Count > 0 ? (_selectedCategory + 1) : 0;
                return GetText($"分类：{n} | 选中：{idx}", $"Groups: {n} | Selected: {idx}");
            }
            else
            {
                var cat = (_catNames.Count > 0) ? _catNames[_selectedCategory] : "";
                var list = _catToIndices.TryGetValue(cat, out var li) ? li : null;
                int m = list?.Count ?? 0;
                int j = m > 0 ? (_selectedInCategory + 1) : 0;
                return GetText($"分类：{cat} | 项目：{j}/{m}", $"Group: {cat} | Items: {j}/{m}");
            }
        }

        // 区域->父类目重命名 映射的加载/保存（可持久化）
        private void LoadZoneRenameMap()
        {
            try
            {
                _zoneRenameMap.Clear();
                var raw = _zoneRenamePersist != null ? (_zoneRenamePersist.Value ?? string.Empty) : string.Empty;
                foreach (var tok in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = tok.IndexOf('=');
                    if (eq > 0)
                    {
                        var k = tok.Substring(0, eq).Trim();
                        var v = tok.Substring(eq + 1).Trim();
                        if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v)) _zoneRenameMap[k] = v;
                    }
                }
            }
            catch { /* ignore */ }
        }

        private void SaveZoneRenameMap()
        {
            try
            {
                var sb = new StringBuilder();
                bool first = true;
                foreach (var kv in _zoneRenameMap)
                {
                    if (!first) sb.Append(';');
                    first = false;
                    var k = (kv.Key ?? string.Empty).Replace(";", string.Empty).Replace("=", string.Empty);
                    var v = (kv.Value ?? string.Empty).Replace(";", string.Empty).Replace("=", string.Empty);
                    sb.Append(k).Append('=').Append(v);
                }
                if (_zoneRenamePersist != null) _zoneRenamePersist.Value = sb.ToString();
                Config.Save();
            }
            catch { /* ignore */ }
        }


        private string GetInfoTextChild()
        {
            if (_benches.Count == 0) return GetInfoTextCached();
            if (_groupsDirty) RebuildGroups();
            if (_catNames.Count == 0) return GetInfoTextCached();
            var cat = _catNames[Mathf.Clamp(_selectedCategory, 0, _catNames.Count - 1)];
            var list = _catToIndices.TryGetValue(cat, out var li) ? li : null;
            int m = list?.Count ?? 0;
            int j = m > 0 ? (_selectedInCategory + 1) : 0;
            int totalPages = m > 0 ? Mathf.CeilToInt((float)m / ItemsPerPage) : 1;
            int page = m > 0 ? (_itemPageInCategory + 1) : 1;
            return GetText($"椅子数：{m} | 选中：{j} | 第 {page} 页 / 共 {totalPages} 页",
                           $"Benches: {m} | Selected: {j} | Page {page} / {totalPages}");
        }

        private string GetInfoTextParent()
        {
            if (_groupsDirty) RebuildGroups();
            int totalBenches = _benches.Count;
            int catCount = _catNames.Count;
            int sel = catCount > 0 ? (_selectedCategory + 1) : 0;
            int totalPages = catCount > 0 ? Mathf.CeilToInt((float)catCount / ItemsPerPage) : 1;
            int page = catCount > 0 ? (_categoryPage + 1) : 1;
            return GetText($"椅子数：{totalBenches} | 选中：{sel} | 第 {page} 页 / 共 {totalPages} 页",
                           $"Benches: {totalBenches} | Selected: {sel} | Page {page} / {totalPages}");
        }


        private void EnterSelectedCategory()
        {
            if (_groupsDirty) RebuildGroups();
            if (_catNames.Count == 0) return;
            var cat = _catNames[_selectedCategory];
            var list = _catToIndices[cat];
            if (list.Count == 0) return;
            _selectedInCategory = Mathf.Clamp(_selectedInCategory, 0, list.Count - 1);
            _selected = list[_selectedInCategory];
            _itemPageInCategory = _selectedInCategory / ItemsPerPage;
            UpdateCurrentPage();
            _inCategoryView = false;
        }

        private void BackToCategories()
        {
            _inCategoryView = true;
        }

        private void DrawTwoLevelMenu()
        {
            if (_groupsDirty) RebuildGroups();
            if (_catNames.Count == 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(GetText("尚未记录任何椅子", "No benches recorded yet"), _stylesBuilt ? _stEmpty : GUI.skin.label);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(10);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(GetText("坐一次椅子即可自动记录", "Sit on a bench to record it"), _stylesBuilt ? _stEmpty : GUI.skin.label);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                return;
            }

            float wheel = Input.GetAxis("Mouse ScrollWheel");

            if (_inCategoryView)
            {
                int total = _catNames.Count;
                int totalPages = Mathf.CeilToInt((float)total / ItemsPerPage);
                _categoryPage = Mathf.Clamp(_categoryPage, 0, Mathf.Max(0, totalPages - 1));
                int start = _categoryPage * ItemsPerPage;
                int end = Mathf.Min(start + ItemsPerPage, total);

                if (Mathf.Abs(wheel) > 0.01f)
                {
                    _selectedCategory = Mathf.Clamp(_selectedCategory + (wheel > 0 ? -1 : +1), 0, total - 1);
                    _categoryPage = _selectedCategory / ItemsPerPage;
                }

                for (int i = start; i < end; i++)
                {
                    bool isSel = (i == _selectedCategory);
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(20);
                    GUILayout.Label(isSel ? "►" : "•", _stylesBuilt ? (isSel ? _stIndicatorSel : _stIndicator) : GUI.skin.label, GUILayout.Width(25));
                    var cat = _catNames[i];
                    int count = _catToIndices.TryGetValue(cat, out var li) ? li.Count : 0;

                    if (_editingCategoryIndex == i)
                    {
                        // 父类目重命名输入
                        GUI.SetNextControlName("EditCatField");
                        if (Event.current.type == EventType.KeyDown)
                        {
                            if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                            {
                                ConfirmEditCategory();
                                Event.current.Use();
                                GUILayout.EndHorizontal();
                                return; // 使用 return 而不是 break，避免GUI布局不匹配
                            }
                            else if (Event.current.keyCode == KeyCode.Escape)
                            {
                                CancelEditCategory();
                                Event.current.Use();
                                GUILayout.EndHorizontal();
                                return; // 使用 return 而不是 break，避免GUI布局不匹配
                            }
                        }
                        var actionsEdit = _ih != null ? _ih.inputActions : null;
                        if (actionsEdit != null)
                        {
                            if (actionsEdit.MenuSubmit.WasPressed)
                            {
                                ConfirmEditCategory();
                                GUI.FocusControl(null);
                                GUILayout.EndHorizontal();
                                return; // 使用 return 而不是 break，避免GUI布局不匹配
                            }
                            else if (actionsEdit.MenuCancel.WasPressed)
                            {
                                CancelEditCategory();
                                GUI.FocusControl(null);
                                GUILayout.EndHorizontal();
                                return; // 使用 return 而不是 break，避免GUI布局不匹配
                            }
                        }
                        _editingCategoryText = GUILayout.TextField(_editingCategoryText, 40, _stylesBuilt ? _stInput : GUI.skin.textField, GUILayout.Width(300));
                        if (!_editingCategoryFocusSet && Event.current.type == EventType.Repaint)
                        {
                            GUI.FocusControl("EditCatField");
                            _editingCategoryFocusSet = true;
                        }
                        // 显示数量
                        GUILayout.Label($" ({count})", _stylesBuilt ? _stMarker : GUI.skin.label, GUILayout.Width(80));
                    }
                    else
                    {
                        string label = $"{cat} ({count})";
                        GUILayout.Label(label, _stylesBuilt ? (isSel ? _stNameSel : _stName) : GUI.skin.label, GUILayout.Width(400));
                    }
                    GUILayout.FlexibleSpace();
                    if (IsCategoryFavorited(cat)) { GUILayout.Label("✦", _stylesBuilt ? _stHeart : GUI.skin.label); GUILayout.Space(Mathf.RoundToInt(32 * _fontScale)); }
                    GUILayout.EndHorizontal();
                }
            }
            else
            {
                var cat = _catNames[_selectedCategory];
                var list = _catToIndices[cat];


                int total = list.Count;
                int totalPages = Mathf.CeilToInt((float)total / ItemsPerPage);
                _itemPageInCategory = Mathf.Clamp(_itemPageInCategory, 0, Mathf.Max(0, totalPages - 1));
                int start = _itemPageInCategory * ItemsPerPage;
                int end = Mathf.Min(start + ItemsPerPage, total);

                if (Mathf.Abs(wheel) > 0.01f)
                {
                    _selectedInCategory = Mathf.Clamp(_selectedInCategory + (wheel > 0 ? -1 : +1), 0, total - 1);
                    _itemPageInCategory = _selectedInCategory / ItemsPerPage;
                    _selected = list[_selectedInCategory];
                    UpdateCurrentPage();
                }

                for (int j = start; j < end; j++)
                {
                    int benchIndex = list[j];
                    var bench = _benches[benchIndex];
                    bool isSel = (j == _selectedInCategory);
                    string dn = bench.DisplayName ?? string.Empty;
                    string textToShow = dn;
                    // 在分类内始终尝试去掉分类前缀，只显示后缀部分
                    if (dn.StartsWith(cat + " ", StringComparison.CurrentCultureIgnoreCase))
                    {
                        textToShow = dn.Substring(cat.Length + 1).Trim(); // 去掉 "分类名 " 前缀
                        if (string.IsNullOrEmpty(textToShow))
                        {
                            textToShow = cat; // 如果后缀为空，显示分类名
                        }
                    }
                    else
                    {
                        // 如果不是标准格式，尝试从显示名中提取后缀部分
                        int spaceIndex = dn.IndexOf(' ');
                        if (spaceIndex > 0)
                        {
                            string prefix = dn.Substring(0, spaceIndex).Trim();
                            // 检查前缀是否与当前分类匹配（考虑重命名情况）
                            if (string.Equals(prefix, cat, StringComparison.CurrentCultureIgnoreCase))
                            {
                                textToShow = dn.Substring(spaceIndex + 1).Trim();
                                if (string.IsNullOrEmpty(textToShow))
                                {
                                    textToShow = cat;
                                }
                            }
                            else
                            {
                                // 前缀不匹配，显示完整名称
                                textToShow = dn;
                            }
                        }
                        else
                        {
                            // 没有空格，直接显示完整名称
                            textToShow = dn;
                        }
                    }

                    GUILayout.BeginHorizontal();
                    // 行抖动反馈：若该行处于“不可删除”的震动窗口内，则在左侧额外插入动态空白
                    float __extraShake = 0f;
                    if (!string.IsNullOrEmpty(_shakeRowKey) && Time.unscaledTime < _shakeUntil)
                    {
                        string __k = (bench.Scene ?? string.Empty) + "|" + (bench.Marker ?? string.Empty);
                        if (string.Equals(__k, _shakeRowKey, StringComparison.CurrentCultureIgnoreCase))
                            __extraShake = Mathf.RoundToInt(8f * _fontScale * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 50f)));
                    }
                    GUILayout.Space(20 + __extraShake);
                    GUILayout.Label(isSel ? "►" : "•", _stylesBuilt ? (isSel ? _stIndicatorSel : _stIndicator) : GUI.skin.label, GUILayout.Width(25));

                    if (_editingIndex == benchIndex)
                    {
                        // 编辑模式：仅显示并编辑“子条目显示部分”（锁定前缀为分类名）
                        GUI.SetNextControlName("EditField");

                        // 处理键盘输入事件
                        if (Event.current.type == EventType.KeyDown)
                        {
                            if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                            {
                                ConfirmEdit();
                                Event.current.Use();
                                GUILayout.EndHorizontal();
                                return; // 使用 return 而不是 break，避免GUI布局不匹配
                            }
                            else if (Event.current.keyCode == KeyCode.Escape)
                            {
                                CancelEdit();
                                Event.current.Use();
                                GUILayout.EndHorizontal();
                                return; // 使用 return 而不是 break，避免GUI布局不匹配
                            }
                        }

                        // 手柄支持：A 确认，B 取消
                        var ihForEdit = _ih;
                        var actionsEdit = ihForEdit != null ? ihForEdit.inputActions : null;
                        if (actionsEdit != null)
                        {
                            if (actionsEdit.MenuSubmit.WasPressed)
                            {
                                ConfirmEdit();
                                GUI.FocusControl(null);
                                GUILayout.EndHorizontal();
                                return; // 使用 return 而不是 break，避免GUI布局不匹配
                            }
                            else if (actionsEdit.MenuCancel.WasPressed)
                            {
                                CancelEdit();
                                GUI.FocusControl(null);
                                GUILayout.EndHorizontal();
                                return; // 使用 return 而不是 break，避免GUI布局不匹配
                            }
                        }

                        // 绘制输入框：仅编辑“子条目显示部分”（例如 “#1”）
                        _editingText = GUILayout.TextField(_editingText, 30, _stylesBuilt ? _stInput : GUI.skin.textField, GUILayout.Width(400));
                        if (!_editingFocusSet && Event.current.type == EventType.Repaint)
                        {
                            GUI.FocusControl("EditField");
                            _editingFocusSet = true;
                        }

                        GUILayout.FlexibleSpace();
                        GUILayout.Label($"({bench.Scene})", _stylesBuilt ? _stMarker : GUI.skin.label, GUILayout.Width(200));
                        GUILayout.EndHorizontal();
                    }
                    else
                    {
                        // 非编辑：显示只读文本
                        GUILayout.Label(textToShow, _stylesBuilt ? (isSel ? _stNameSel : _stName) : GUI.skin.label, GUILayout.Width(400));
                        GUILayout.FlexibleSpace();
                        if (!bench.IsFavorite)
                            GUILayout.Label($"({bench.Scene})", _stylesBuilt ? _stMarker : GUI.skin.label, GUILayout.Width(200));
                        if (bench.IsFavorite) { GUILayout.Label("✦", _stylesBuilt ? _stHeart : GUI.skin.label); GUILayout.Space(Mathf.RoundToInt(32 * _fontScale)); }
                        GUILayout.EndHorizontal();
                    }
                }
            }
        }

        // 在列表发生增删或重命名后，刷新分组并尽量保持当前选择稳定
        private void RefreshGroupsAndSelectionAfterMutation()
        {
            try
            {
                // 记录重排前的“目标椅子”索引，防止重建分组时被覆盖
                int pinnedSelected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _benches.Count - 1));
                _groupsDirty = true;
                RebuildGroups();
                // 恢复目标索引，以便后续根据它定位分类与子索引，实现“选中跟随”
                _selected = Mathf.Clamp(pinnedSelected, 0, Mathf.Max(0, _benches.Count - 1));

                if (_benches.Count == 0)
                {
                    _selected = -1;
                    _inCategoryView = true;
                    _selectedCategory = 0;
                    _selectedInCategory = 0;
                    _categoryPage = 0;
                    _itemPageInCategory = 0;
                    _currentPage = 0;
                    return;
                }

                _selected = Mathf.Clamp(_selected, 0, _benches.Count - 1);
                var sel = _selected >= 0 ? _benches[_selected] : null;
                var key = sel != null ? GetCategoryKey(sel) : null;

                if (!string.IsNullOrEmpty(key) && _catToIndices.TryGetValue(key, out var list) && list != null && list.Count > 0)
                {
                    int catIdx = _catNames.FindIndex(n => string.Equals(n, key, StringComparison.CurrentCultureIgnoreCase));
                    _selectedCategory = Mathf.Clamp(catIdx < 0 ? 0 : catIdx, 0, Math.Max(0, _catNames.Count - 1));
                    _categoryPage = _selectedCategory / ItemsPerPage;

                    int inIdx = list.IndexOf(_selected);
                    if (inIdx < 0)
                    {
                        // 若原选中项已被删除，则尽量选择同分类中相邻项
                        inIdx = Mathf.Clamp(_selectedInCategory, 0, list.Count - 1);
                        if (inIdx >= 0) _selected = list[inIdx];
                    }

                    _selectedInCategory = Mathf.Clamp(inIdx, 0, list.Count - 1);
                    if (!_inCategoryView)
                    {
                        _itemPageInCategory = _selectedInCategory / ItemsPerPage;
                    }
                }
                else
                {
                    // 找不到对应分类，回到分类层
                    _inCategoryView = true;
                    _selectedCategory = Mathf.Clamp(_selectedCategory, 0, Math.Max(0, _catNames.Count - 1));
                    _categoryPage = _selectedCategory / ItemsPerPage;
                    _selectedInCategory = 0;
                    _itemPageInCategory = 0;
                }

                UpdateCurrentPage();
            }
            catch { /* ignore UI-only refresh errors */ }
        }


        // 协程：等待进入可游玩阶段后再尝试刷新，失败则小憩后重试，最多尝试数次
        private IEnumerator DelayedLocalizedNameRefreshRoutine()
        {
            _refreshRoutineRunning = true;
            int attempts = 0;
            while (_needsLocalizedRefresh && attempts < 6)
            {
                attempts++;
                // 等待进入可游玩阶段，最多等待 15s
                float deadline = Time.realtimeSinceStartup + 15f;
                while (Time.realtimeSinceStartup < deadline && !IsGameplayReady())
                {
                    yield return new WaitForSecondsRealtime(0.5f);
                }

                if (!IsGameplayReady())
                {
                    // 再等等，下一轮再试
                    yield return new WaitForSecondsRealtime(2f);
                    continue;
                }

                bool ok = false;
                try { ok = TryRefreshLocalizedNamesOnce(); }
                catch { ok = false; }

                if (ok)
                {
                    _needsLocalizedRefresh = false;
                    D("延迟刷新：已将未重命名椅子改为官方中文区域名");
                    _refreshRoutineRunning = false;
                    yield break;
                }

                // 未成功（例如资源未就绪），稍后重试
                yield return new WaitForSecondsRealtime(3f);
            }
            _refreshRoutineRunning = false;
        }

        private void D(string msg)
        {
            try
            {
                if (!string.IsNullOrEmpty(msg) && msg.StartsWith("地图预览"))
                {
                    // 地图预览相关日志总是输出为 Info，便于现场排查
                    Logger.LogInfo(msg);
                }
                else if (_debugLog != null && _debugLog.Value)
                {
                    Logger.Log(LogLevel.Debug, "[DBG] " + msg);
                }
            }
            catch { }
        }

        // 获取本地化文本
        private string GetText(string chineseText, string englishText)
        {
            return _isChineseLanguage ? chineseText : englishText;
        }

        // 计算UI缩放倍数
        private float GetUIScale()
        {
            if (!_autoScale.Value)
            {
                return Mathf.Clamp(_uiScale.Value, 0.5f, 3.0f);
            }

            // 自动缩放：根据屏幕分辨率计算
            var screenHeight = Screen.height;

            // 基准分辨率：1080p
            if (screenHeight <= 1080)
                return 1.0f;
            // 1440p
            else if (screenHeight <= 1440)
                return 1.5f;
            // 4K及以上
            else if (screenHeight <= 2160)
                return 2.0f;
            // 8K等超高分辨率
            else
                return 2.5f;
        }

        // Scaled heights based on font scale
        private float GetItemHeight() => 28f * _fontScale;
        private float GetTitleHeight() => 45f * _fontScale;
        private float GetBottomHeight() => 45f * _fontScale;

        // 计算每页显示的椅子数量，根据可用空间动态调整
        private int CalculateItemsPerPage()
        {
            // UI面板总高度：500px
            // 标题区域：标题(~25px) + Space(15) = 40px
            // 信息区域：信息文字(~15px) + Space(15) + Space(15) = 45px
            // 可用于列表的高度：500 - 40 - 45 = 415px

            var totalPanelHeight = 500f;
            var availableHeight = totalPanelHeight - GetTitleHeight() - GetBottomHeight();
            var itemHeight = GetItemHeight();

            // 计算理论上能显示多少个椅子项
            var theoreticalMaxItems = Mathf.FloorToInt(availableHeight / itemHeight);

            // 由于实际渲染时可能存在细微高度差异，减少1个以避免超出
            var practicalMaxItems = Mathf.Max(0, theoreticalMaxItems - 1);

            // 确保至少显示5个，最多显示20个
            return Mathf.Clamp(practicalMaxItems, 5, 20);
        }

        // 计算滚动视图的可用高度（不考虑UI缩放，因为在缩放后的坐标系中计算）
        private float GetScrollViewHeight()
        {
            // 精确计算滚动视图高度，基于实际的UI布局
            // 注意：这个计算是在UI缩放后的坐标系中进行的
            // 总面板高度：500px（缩放后的逻辑像素）
            // 标题区域：标题文字(~25px) + GUILayout.Space(20) = 45px
            // 底部区域：GUILayout.Space(20) + 椅子数量文字(~15px) + GUILayout.Space(10) = 45px
            // 滚动视图可用高度：500 - 45 - 45 = 410px

            var totalPanelHeight = 500f; // UI面板总高度（逻辑像素）
            var titleHeight = GetTitleHeight();
            var bottomHeight = GetBottomHeight();
            var availableHeight = totalPanelHeight - titleHeight - bottomHeight;

            // 确保最小高度，保证可用性
            var result = Mathf.Max(200f, availableHeight);

            // 调试信息：输出计算的高度
            D($"GetScrollViewHeight: titleHeight={titleHeight}, bottomHeight={bottomHeight}");
            D($"GetScrollViewHeight: totalPanel={totalPanelHeight}, available={availableHeight}, result={result}");

            return result;
        }


        // InControl 反射缓存，避免每帧扫描程序集造成性能开销
        private static bool _inCtrlResolved = false;
        private static System.Type _inCtrlInputManagerType;
        private static System.Reflection.PropertyInfo _inCtrlActiveDeviceProp;
        private static readonly Dictionary<string, string> _inCtrlTokenMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"LB", "LeftBumper"},{"L1", "LeftBumper"},
            {"RB", "RightBumper"},{"R1", "RightBumper"},
            // Triggers (support Xbox LT/RT and PS L2/R2 via InControl)
            {"LT", "LeftTrigger"},{"L2", "LeftTrigger"},
            {"RT", "RightTrigger"},{"R2", "RightTrigger"},
            {"Y", "Action4"},{"TRIANGLE", "Action4"},
            {"X", "Action3"},{"SQUARE", "Action3"},
            {"A", "Action1"},{"CROSS", "Action1"},
            {"B", "Action2"},{"CIRCLE", "Action2"},
            {"UP", "DPadUp"},{"DOWN", "DPadDown"},
            {"LEFT", "DPadLeft"},{"RIGHT", "DPadRight"},
        };
        private static void ResolveInControl()
        {
            if (_inCtrlResolved) return;
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var inCtrlAsm = assemblies.FirstOrDefault(a => a.GetType("InControl.InputManager") != null);
                _inCtrlInputManagerType = inCtrlAsm?.GetType("InControl.InputManager");
                _inCtrlActiveDeviceProp = _inCtrlInputManagerType?.GetProperty("ActiveDevice", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            }
            catch { /* ignore */ }
            finally { _inCtrlResolved = true; }
        }


        // 检测手柄按键组合（支持Unity按键与 InControl 设备的双通道检测）
        private bool IsGamepadButtonPressed(string buttonCombo, InputHandler ih)
        {
            if (!_enableGamepadSupport.Value || string.IsNullOrEmpty(buttonCombo))
                return false;

            // Unity 物理按键检测，避免被游戏内自定义键位影响
            bool IsPhysical(string token)
            {
                switch (token)
                {
                    case "A": return Input.GetKey(KeyCode.JoystickButton0);
                    case "B": return Input.GetKey(KeyCode.JoystickButton1);
                    case "X": return Input.GetKey(KeyCode.JoystickButton2);
                    case "Y": return Input.GetKey(KeyCode.JoystickButton3);
                    case "LB":
                    case "L1": return Input.GetKey(KeyCode.JoystickButton4);
                    case "RB":
                    case "R1": return Input.GetKey(KeyCode.JoystickButton5);
                    case "SELECT":
                    case "SHARE": return Input.GetKey(KeyCode.JoystickButton6);
                    case "START":
                    case "OPTIONS": return Input.GetKey(KeyCode.JoystickButton7);
                    // PS 别名（常见映射：0=Square, 1=Cross, 2=Circle, 3=Triangle）
                    case "CROSS": return Input.GetKey(KeyCode.JoystickButton1);
                    case "CIRCLE": return Input.GetKey(KeyCode.JoystickButton2);
                    case "SQUARE": return Input.GetKey(KeyCode.JoystickButton0);
                    case "TRIANGLE": return Input.GetKey(KeyCode.JoystickButton3);
                    case "UP": return Input.GetKey(KeyCode.JoystickButton13); // 一些驱动会把方向映射为按钮，尽量兼容
                    case "DOWN": return Input.GetKey(KeyCode.JoystickButton14);
                    case "LEFT": return Input.GetKey(KeyCode.JoystickButton11);
                    case "RIGHT": return Input.GetKey(KeyCode.JoystickButton12);
                    default: return false; // LT/RT等扳机不参与本插件组合
                }
            }

            // InControl 设备检测（使用反射避免编译期依赖）
            bool IsInControlPressed(string token)
            {
                try
                {
                    ResolveInControl();
                    if (_inCtrlActiveDeviceProp == null) return false;
                    var device = _inCtrlActiveDeviceProp.GetValue(null);
                    if (device == null) return false;

                    bool CheckButton(string propName)
                    {
                        var prop = device.GetType().GetProperty(propName);
                        var control = prop?.GetValue(device);
                        if (control == null) return false;
                        var isPressedProp = control.GetType().GetProperty("IsPressed");
                        if (isPressedProp == null) return false;
                        var val = isPressedProp.GetValue(control);
                        return val is bool b && b;
                    }

                    if (_inCtrlTokenMap.TryGetValue(token, out var propName))
                        return CheckButton(propName);
                    return false;
                }
                catch { return false; }
            }

            var buttons = buttonCombo.ToUpper().Split('+');
            foreach (var button in buttons)
            {
                var trimmed = button.Trim();
                if (!IsPhysical(trimmed) && !IsInControlPressed(trimmed)) return false;
            }
            return true;
        }

        // ===== UI Styling Helpers (Silksong-inspired ornate panel) =====
        private static Texture2D _ctpTex1x1;
        private static Texture2D _ctpGradTex;
        private static Color _ctpGradTop, _ctpGradBottom;

        private static void CTP_EnsureTex1x1()
        {
            if (_ctpTex1x1 == null)
            {
                _ctpTex1x1 = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _ctpTex1x1.wrapMode = TextureWrapMode.Clamp;
                _ctpTex1x1.filterMode = FilterMode.Bilinear;
                _ctpTex1x1.SetPixel(0, 0, Color.white);
                _ctpTex1x1.Apply();
            }
        }

        private static void CTP_EnsureGradient(Color top, Color bottom)
        {
            if (_ctpGradTex != null && _ctpGradTop == top && _ctpGradBottom == bottom) return;
            if (_ctpGradTex != null) UnityEngine.Object.Destroy(_ctpGradTex);
            _ctpGradTop = top; _ctpGradBottom = bottom;
            _ctpGradTex = new Texture2D(1, 64, TextureFormat.RGBA32, false);
            _ctpGradTex.wrapMode = TextureWrapMode.Clamp;
            _ctpGradTex.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < _ctpGradTex.height; y++)
            {
                float t = (float)y / (_ctpGradTex.height - 1);
                _ctpGradTex.SetPixel(0, y, Color.Lerp(top, bottom, t));
            }
            _ctpGradTex.Apply();
        }

        private static void CTP_DrawFill(Rect r, Color c)
        {
            CTP_EnsureTex1x1();
            var old = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, _ctpTex1x1);
            GUI.color = old;
        }

        private static void CTP_DrawGradient(Rect r, Color top, Color bottom)
        {
            CTP_EnsureGradient(top, bottom);
            GUI.DrawTexture(r, _ctpGradTex, ScaleMode.StretchToFill);
        }

        private static void CTP_DrawLine(Rect r, Color c)
        {
            CTP_DrawFill(r, c);
        }

        private static void CTP_DrawOrnateFrame(Rect r)
        {
            // Palette tuned to Silksong-like UI
            Color gold = new Color(0.94f, 0.85f, 0.55f, 1f);
            Color goldSoft = new Color(0.94f, 0.85f, 0.55f, 0.35f);
            Color borderDark = new Color(0.12f, 0.15f, 0.22f, 1f);
            float outer = 2f;
            float inner = 1f;

            // Outer border (gold)
            CTP_DrawLine(new Rect(r.x, r.y, r.width, outer), gold);
            CTP_DrawLine(new Rect(r.x, r.yMax - outer, r.width, outer), gold);
            CTP_DrawLine(new Rect(r.x, r.y, outer, r.height), gold);
            CTP_DrawLine(new Rect(r.xMax - outer, r.y, outer, r.height), gold);

            // Inner border (dark)
            Rect rIn = new Rect(r.x + outer + 1, r.y + outer + 1, r.width - (outer + 1) * 2, r.height - (outer + 1) * 2);
            CTP_DrawLine(new Rect(rIn.x, rIn.y, rIn.width, inner), borderDark);
            CTP_DrawLine(new Rect(rIn.x, rIn.yMax - inner, rIn.width, inner), borderDark);
            CTP_DrawLine(new Rect(rIn.x, rIn.y, inner, rIn.height), borderDark);
            CTP_DrawLine(new Rect(rIn.xMax - inner, rIn.y, inner, rIn.height), borderDark);

            // Corner studs
            float stud = 4f;
            CTP_DrawFill(new Rect(r.x + 6, r.y + 6, stud, stud), gold);
            CTP_DrawFill(new Rect(r.xMax - 6 - stud, r.y + 6, stud, stud), gold);
            CTP_DrawFill(new Rect(r.x + 6, r.yMax - 6 - stud, stud, stud), gold);
            CTP_DrawFill(new Rect(r.xMax - 6 - stud, r.yMax - 6 - stud, stud, stud), gold);

            // Top and bottom flourishes (simple bars + gem)
            float cx = r.x + r.width * 0.5f;
            float yTop = r.y + 8f;
            float yBot = r.yMax - 10f;
            // Top
            CTP_DrawLine(new Rect(cx - 24f, yTop, 48f, 2f), gold);
            CTP_DrawLine(new Rect(cx - 12f, yTop + 4f, 24f, 2f), goldSoft);
            CTP_DrawFill(new Rect(cx - 2f, yTop - 2f, 4f, 4f), gold);
            // Bottom
            CTP_DrawLine(new Rect(cx - 24f, yBot, 48f, 2f), gold);
            CTP_DrawLine(new Rect(cx - 12f, yBot - 4f, 24f, 2f), goldSoft);
            CTP_DrawFill(new Rect(cx - 2f, yBot - 2f, 4f, 4f), gold);
        }


        private void OnGUI()
        {
            // Build styles on first OnGUI call when GUI.skin is available
            if (!_stylesBuilt)
            {
                BuildGuiStyles();
            }

            // 预热帧：以透明颜色绘制一次，预热字体/布局/贴图，避免首次可见卡顿
            bool __prewarmInvisible = false;
            Color __prevGuiColor = default;
            if (_uiPrewarming)
            {
                __prewarmInvisible = true;
                __prevGuiColor = GUI.color;
                GUI.color = new Color(__prevGuiColor.r, __prevGuiColor.g, __prevGuiColor.b, 0f);
            }

            var isRepaint = Event.current != null && Event.current.type == EventType.Repaint;

            // 顶部中央 HUD：用于显示 OK/NO 等简短状态
            if (Time.unscaledTime < _hudUntil && !string.IsNullOrEmpty(_hudText))
            {


                var rect = new Rect(0, 20, Screen.width, 40);
                if (isRepaint) GUI.Label(rect, _hudText, _stylesBuilt ? _stHud : GUI.skin.label);
            }

            if (!_uiOpen) return;

            // 获取UI缩放倍数
            var uiScale = GetUIScale();

            // 保存原始GUI矩阵
            var originalMatrix = GUI.matrix;

            // 长按地图时淡化UI
            float uiAlpha = _showMapActive ? 0.3f : 1.0f;
            GUI.color = new Color(GUI.color.r, GUI.color.g, GUI.color.b, GUI.color.a * uiAlpha);

            // 当需要地图预览时，先触发显示协程（只触发一次）
            if (_showMapActive && !_inCategoryView && _showMapIndex >= 0 && _showMapIndex < _benches.Count && !_mapPreviewShown)
            {
                var bench = _benches[_showMapIndex];
                StartInventoryMapPreview(bench);
                _mapPreviewShown = true;
            }
            // 若正在展示地图预览：
            // - 全屏模式：不绘制面板（避免覆盖官方地图）
            if (_showMapActive)
            {
                // 地图全屏显示时不再绘制本插件面板，避免覆盖官方地图
                GUI.matrix = originalMatrix;
                if (__prewarmInvisible) GUI.color = __prevGuiColor;
                return;
            }
            // 应用缩放
            GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));

            var screenW = Screen.width / uiScale;
            var screenH = Screen.height / uiScale;
            var boxW = 600f;
            var maxBoxH = 500f; // 最大面板高度
            var boxH = maxBoxH; // 固定面板高度，使用滚动视图
            var boxX = (screenW - boxW) * 0.5f;
            var boxY = (screenH - boxH) * 0.5f;


            // 移除全屏黑色背景，保持游戏画面可见

            // 主面板背景与装饰边框（Silksong风格）
            var panelRect = new Rect(boxX - 12, boxY - 14, boxW + 24, boxH + 28);
            if (isRepaint)
            {
                var bgAlpha = uiAlpha * 0.96f;
                var frameAlpha = uiAlpha;
                CTP_DrawGradient(panelRect, new Color(0.08f, 0.09f, 0.12f, bgAlpha), new Color(0.05f, 0.06f, 0.09f, bgAlpha));

                // Apply alpha to frame drawing
                var oldColor = GUI.color;
                GUI.color = new Color(oldColor.r, oldColor.g, oldColor.b, oldColor.a * frameAlpha);
                CTP_DrawOrnateFrame(panelRect);
                GUI.color = oldColor;
            }

            GUILayout.BeginArea(new Rect(boxX, boxY, boxW, boxH));
            {
                // 标题
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                string __title = _inCategoryView ? GetText("椅子传送", "Chair Teleport") : (_catNames.Count > 0 ? _catNames[Mathf.Clamp(_selectedCategory, 0, _catNames.Count - 1)] : GetText("椅子传送", "Chair Teleport"));
                GUILayout.Label(__title, _stylesBuilt ? _stTitle : GUI.skin.label);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(15);

                // 椅子数量和分页信息显示（移动到标题下方）
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();

                // 显示椅子数量和分页信息（缓存字符串，减少GC）
                GUILayout.Label(_inCategoryView ? GetInfoTextParent() : GetInfoTextChild(), _stylesBuilt ? _stInfo : GUI.skin.label);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(15);

                // 椅子列表（使用二级菜单显示）
                DrawTwoLevelMenu();
            }
            GUILayout.EndArea();



            // 恢复原始GUI矩阵
            GUI.matrix = originalMatrix;

            // 恢复预热时设置的透明颜色
            if (__prewarmInvisible)
            {
                GUI.color = __prevGuiColor;
            }
        }



        private void DrawBenchListWithPagination()
        {
            var isRepaint = Event.current != null && Event.current.type == EventType.Repaint;
            if (_benches.Count == 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(GetText("尚未记录任何椅子", "No benches recorded yet"), _stylesBuilt ? _stEmpty : GUI.skin.label);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(10);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(GetText("坐一次椅子即可自动记录", "Sit on a bench to record it"), _stylesBuilt ? _stEmpty : GUI.skin.label);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                return;
            }

            // 处理鼠标滚轮事件 - 控制选中项移动并自动翻页
            HandleMouseWheelScroll();

            // 计算分页信息
            int totalPages = Mathf.CeilToInt((float)_benches.Count / ItemsPerPage);
            _currentPage = Mathf.Clamp(_currentPage, 0, totalPages - 1);

            int startIndex = _currentPage * ItemsPerPage;
            int endIndex = Mathf.Min(startIndex + ItemsPerPage, _benches.Count);



            // 显示当前页的椅子列表
            for (int i = startIndex; i < endIndex; i++)
            {
                var bench = _benches[i];
                var isSelected = (i == _selected);

                GUILayout.BeginHorizontal();



                // 左侧空白，用于居中对齐
                GUILayout.Space(20);

                // 选中背景
                if (isSelected)
                {
                    GUI.backgroundColor = new Color(0.3f, 0.5f, 0.8f, 0.6f);
                }

                // 选中指示器
                if (_stylesBuilt)
                {
                    GUILayout.Label(isSelected ? "►" : "•", isSelected ? _stIndicatorSel : _stIndicator, GUILayout.Width(25));
                }
                else
                {
                    GUILayout.Label(isSelected ? "►" : "•", GUI.skin.label, GUILayout.Width(25));
                }

                // 椅子名称 - 如果正在编辑则显示输入框，否则显示标签
                if (_editingIndex == i)
                {
                    // 编辑模式：显示输入框
                    GUI.SetNextControlName("EditField");
                    // use cached text field style

                    // 处理键盘输入事件（在TextField之前处理，确保优先级）
                    if (Event.current.type == EventType.KeyDown)
                    {
                        if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                        {
                            ConfirmEdit();
                            Event.current.Use();

                            // 若右侧预览未激活或不在子项视图，确保拆除原生地图预览
                            if (!(_showMapActive && !_inCategoryView && _showMapIndex >= 0 && _showMapIndex < _benches.Count))
                            {
                                StopInventoryMapPreview();
                            }

                            return; // 立即返回，避免继续处理
                        }
                        else if (Event.current.keyCode == KeyCode.Escape)
                        {
                            CancelEdit();
                            Event.current.Use();
                            return; // 立即返回，避免继续处理
                        }
                    }

                    // 手柄支持：A 确认，B 取消（编辑模式下）
                    var ihForEdit = _ih;
                    var actionsEdit = ihForEdit != null ? ihForEdit.inputActions : null;
                    if (actionsEdit != null)
                    {
                        if (actionsEdit.MenuSubmit.WasPressed)
                        {
                            ConfirmEdit();
                            GUI.FocusControl(null);
                            return;
                        }
                        else if (actionsEdit.MenuCancel.WasPressed)
                        {
                            CancelEdit();
                            GUI.FocusControl(null);
                            return;
                        }
                    }

                    // 绘制输入框，限制宽度以保持居中
                    _editingText = GUILayout.TextField(_editingText, 30, _stylesBuilt ? _stInput : GUI.skin.textField, GUILayout.Width(400));

                    // 只在第一次进入编辑模式时设置焦点
                    if (!_editingFocusSet && Event.current.type == EventType.Repaint)
                    {
                        GUI.FocusControl("EditField");
                        _editingFocusSet = true;
                    }
                }
                else
                {
                    // 正常模式：显示标签，限制宽度以保持居中
                    if (_stylesBuilt)
                    {
                        GUILayout.Label(bench.DisplayName, isSelected ? _stNameSel : _stName, GUILayout.Width(400));
                    }
                    else
                    {
                        GUILayout.Label(bench.DisplayName, GUI.skin.label, GUILayout.Width(400));
                    }
                }

                // 场景信息
                GUILayout.Label($"({bench.Scene})", _stylesBuilt ? _stScene : GUI.skin.label, GUILayout.Width(130));

                // 右侧空白，用于居中对齐
                GUILayout.Space(20);

                GUI.backgroundColor = Color.white;
                GUILayout.EndHorizontal();

                // 行分隔线（轻微）
                {
                    var lastRect = GUILayoutUtility.GetLastRect();
                    if (isRepaint)
                    {
                        GUI.color = new Color(1f, 1f, 1f, 0.06f);
                        GUI.DrawTexture(new Rect(0f, lastRect.yMax + 1f, 600f, 1f), Texture2D.whiteTexture);
                        GUI.color = Color.white;
                    }
                }

                GUILayout.Space(3);
            }

            // 填充空白行，保持UI高度一致
            int currentPageItems = endIndex - startIndex;
            int maxItemsPerPage = ItemsPerPage;
            int emptyRows = maxItemsPerPage - currentPageItems;
            for (int i = 0; i < emptyRows; i++)
            {
                GUILayout.Space(GetItemHeight()); // 每行的高度（随字体缩放）
            }
        }



        #region UI Flow

        // Ensure the game's own inventory/map UI is closed to avoid freeze after teleport
        private void EnsureInventoryClosed()
        {
            try
            {
                var gm = GameManager.instance;
                var pd = PlayerData.instance;
                if (gm != null && pd != null)
                {
                    if (pd.isInventoryOpen)
                    {
                        gm.SetIsInventoryOpen(false);
                        D("EnsureInventoryClosed: Closed in-game inventory before opening Chair UI");
                    }
                    else
                    {

                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"EnsureInventoryClosed error: {e}");
            }
        }

        private void OpenUI()
        {
            if (_uiOpen)
            {
                Logger.LogWarning("UI已经打开，忽略重复的OpenUI调用");
                return;
            }

            // 难度选项：必须坐在椅子上才能打开UI（可被一次性预热绕过）
            if (_requireOnBenchToOpenUI != null && _requireOnBenchToOpenUI.Value && !_bypassBenchRequirementOnce)
            {
                var pdOpenCheck = PlayerData.instance;
                if (pdOpenCheck == null || !pdOpenCheck.atBench)
                {
                    return;
                }
            }
            // 重置一次性绕过标记（若本次使用了预热绕过）
            _bypassBenchRequirementOnce = false;

            // 若游戏背包/地图打开则不打开本UI（在上层逻辑已阻止）
            _uiOpen = true;
            _uiOpenedAt = Time.unscaledTime;
            _lastNavTime = 0f;
            // 预热阶段：尽量避免任何可见/可感的副作用
            bool prewarming = _uiPrewarming;

            // 打开UI时，自动清理当前场景内“一个屏幕宽度”内的重复椅子，只保留一个（预热时跳过以减少工作量）
            if (!prewarming)
                PruneNearbyDuplicatesInActiveScene();

            // 若存在“最近新增”的待选项，优先选中它（预热时跳过）
            if (!prewarming && !string.IsNullOrEmpty(_pendingSelectKey))
            {
                var parts = _pendingSelectKey.Split('|');
                if (parts.Length == 2)
                {
                    int idx = _benches.FindIndex(b => b.Scene == parts[0] && b.Marker == parts[1]);
                    if (idx >= 0) _selected = idx;
                }
                _pendingSelectKey = null;
            }

            if (!prewarming) Logger.LogInfo($"打开椅子传送UI，当前椅子数量: {_benches.Count}");

            // 记录打开UI时是否已有有效选中项（包括“最近新增”的待选项）
            bool hadSelection = !prewarming && _selected >= 0;
            // 确保选择索引在有效范围内：仅在“已有选中项”时才进行夹取与翻页
            if (_benches.Count > 0)
            {
                if (hadSelection)
                {
                    _selected = Mathf.Clamp(_selected, 0, _benches.Count - 1);
                    UpdateCurrentPage();
                }
                else
                {
                    _selected = -1; // 保持无选中
                }
            }

            if (!prewarming)
            {
                // 初始化二级菜单分组与选中状态（默认进入父级分类）
                _groupsDirty = true;
                _inCategoryView = true;
                _categoryPage = 0;
                _itemPageInCategory = 0;
                _selectedCategory = 0;
                _selectedInCategory = 0;
                RebuildGroups();
                // 仅在“打开时已有选中项”时自动进入该分类的子条目
                if (hadSelection && _benches.Count > 0 && _selected >= 0 && _selected < _benches.Count && _catNames.Count > 0)
                {
                    var key = GetCategoryKey(_benches[_selected]);
                    int catIdx = _catNames.FindIndex(n => string.Equals(n, key, StringComparison.CurrentCultureIgnoreCase));
                    if (catIdx >= 0)
                    {
                        _selectedCategory = catIdx;
                        _categoryPage = _selectedCategory / ItemsPerPage;
                        var list = _catToIndices[key];
                        int inIdx = list.IndexOf(_selected);
                        if (inIdx >= 0)
                        {
                            _selectedInCategory = inIdx;
                            _itemPageInCategory = _selectedInCategory / ItemsPerPage;
                            _inCategoryView = false; // 仅在已有选中项时自动进入子级
                        }
                    }
                }
            }


            // 屏蔽角色输入，避免方向键移动角色（预热时不屏蔽，避免肉眼可见的交互状态变化）
            if (!prewarming)
            {
                var hc = HeroController.instance;
                if (hc != null) hc.AddInputBlocker(this);
            }
        }

        private void CloseUI()
        {
            if (!_uiOpen)
            {
                Logger.LogWarning("UI已经关闭，忽略重复的CloseUI调用");
                return;
            }

            _uiOpen = false;
            Logger.LogInfo("关闭椅子传送UI");

            // 安全清理输入阻塞器
            try
            {
                var hc = HeroController.instance;
                if (hc != null) hc.RemoveInputBlocker(this);
            }
            catch (Exception e)
            {
                Logger.LogError($"CloseUI: 移除输入阻塞器时出错: {e}");
            }

            // 若由取消键关闭，短暂继续屏蔽输入，避免B键回血
            if (_uiClosedByCancelNext)
            {
                _uiClosedByCancelNext = false;
                try { StartCoroutine(PostCloseCancelGrace()); } catch { /* ignore */ }
            }


            // 强制清除GUI焦点，防止焦点卡死
            try
            {
                GUI.FocusControl(null);
                GUI.UnfocusWindow();
            }
            catch (Exception e)
            {
                Logger.LogError($"CloseUI: 清除GUI焦点时出错: {e}");
            }

            // 重置编辑状态
            _editingIndex = -1;
            _editingText = "";
            _editingFocusSet = false;
        }

        private void HandleUiNavigation()
        {
            if (_benches.Count == 0) return;

            int delta = 0;
            bool isInitialPress = false;
            bool leftPressed = false, rightPressed = false;

            // 键盘：上下带长按；左/右用于切层（主：右进入；子：左返回）
            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                delta = -1; isInitialPress = true; _currentHeldKey = KeyCode.UpArrow; _keyHoldStartTime = Time.unscaledTime;
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                delta = +1; isInitialPress = true; _currentHeldKey = KeyCode.DownArrow; _keyHoldStartTime = Time.unscaledTime;
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                leftPressed = true; _currentHeldKey = KeyCode.None;
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                rightPressed = true; _currentHeldKey = KeyCode.None;
            }
            else if (_currentHeldKey != KeyCode.None && Input.GetKey(_currentHeldKey))
            {
                float holdTime = Time.unscaledTime - _keyHoldStartTime;
                if (holdTime > HoldThreshold && Time.unscaledTime - _lastNavTime >= FastNavInterval)
                {
                    if (_currentHeldKey == KeyCode.UpArrow) delta = -1;
                    else if (_currentHeldKey == KeyCode.DownArrow) delta = +1;
                }
            }
            else if (_currentHeldKey != KeyCode.None && !Input.GetKey(_currentHeldKey))
            {
                _currentHeldKey = KeyCode.None;
            }

            if (isInitialPress && Time.unscaledTime - _lastNavTime < NavCooldown) return;

            // 手柄：左/右用于切层，上下支持长按快速导航
            var ih = _ih;
            if (ih != null)
            {
                var act = ih.inputActions;

                // 手柄上下键长按快速导航
                if (act.Up.WasPressed)
                {
                    delta = -1; isInitialPress = true;
                    _currentHeldGamepadDirection = "Up";
                    _gamepadHoldStartTime = Time.unscaledTime;
                }
                else if (act.Down.WasPressed)
                {
                    delta = +1; isInitialPress = true;
                    _currentHeldGamepadDirection = "Down";
                    _gamepadHoldStartTime = Time.unscaledTime;
                }
                else if (act.Left.WasPressed)
                {
                    leftPressed = true; _currentHeldGamepadDirection = null;
                }
                else if (act.Right.WasPressed)
                {
                    rightPressed = true; _currentHeldGamepadDirection = null;
                }
                // 检查手柄长按状态
                else if (_currentHeldGamepadDirection != null)
                {
                    bool stillHeld = (_currentHeldGamepadDirection == "Up" && act.Up.IsPressed) ||
                                   (_currentHeldGamepadDirection == "Down" && act.Down.IsPressed);

                    if (stillHeld)
                    {
                        float holdTime = Time.unscaledTime - _gamepadHoldStartTime;
                        if (holdTime > HoldThreshold && Time.unscaledTime - _lastGamepadNavTime >= FastNavInterval)
                        {
                            if (_currentHeldGamepadDirection == "Up") delta = -1;
                            else if (_currentHeldGamepadDirection == "Down") delta = +1;
                            _lastGamepadNavTime = Time.unscaledTime;
                        }
                    }
                    else
                    {
                        _currentHeldGamepadDirection = null;
                    }
                }
            }
            if (delta != 0 || leftPressed) _pendingEnterOnRight = false;


            // 右键双击检测：双击→为收藏/取消收藏；单击→在分类视图进入子列表
            if (rightPressed)
            {
                float now = Time.unscaledTime;
                bool sameContext = (_lastRightWasOnCategory == _inCategoryView) &&
                                   (_lastRightCategoryIndex == _selectedCategory) &&
                                   (_inCategoryView || _lastRightChildBenchIndex == _selectedInCategory);
                if (_pendingEnterOnRight && now <= _pendingEnterDeadline && sameContext)
                {
                    // 双击：切换收藏状态
                    if (_inCategoryView)
                    {
                        if (_catNames.Count > 0)
                        {
                            var cat = _catNames[_selectedCategory];
                            ToggleFavoriteCategory(cat);
                        }
                    }
                    else
                    {
                        if (_catNames.Count > 0)
                        {
                            var list = _catToIndices[_catNames[_selectedCategory]];
                            if (list.Count > 0)
                            {
                                int benchIndex = list[_selectedInCategory];
                                ToggleFavoriteBench(benchIndex);
                            }
                        }
                    }
                    _pendingEnterOnRight = false;
                    return; // 本帧已处理
                }
                // 否则启动单击等待窗口
                _pendingEnterOnRight = true;
                _pendingEnterDeadline = now + DoubleTapWindow;
                _lastRightWasOnCategory = _inCategoryView;
                _lastRightCategoryIndex = _selectedCategory;
                _lastRightChildBenchIndex = _selectedInCategory;
            }

            if (_inCategoryView)
            {
                if (_groupsDirty) RebuildGroups();
                if (delta != 0 && _catNames.Count > 0)
                {
                    int n = _catNames.Count;
                    _selectedCategory = ((_selectedCategory + delta) % n + n) % n;
                    _categoryPage = _selectedCategory / ItemsPerPage;
                    _lastNavTime = Time.unscaledTime;
                }
                // o:  
                if (_pendingEnterOnRight && _lastRightWasOnCategory && Time.unscaledTime > _pendingEnterDeadline && _selectedCategory == _lastRightCategoryIndex)
                {
                    _pendingEnterOnRight = false;
                    EnterSelectedCategory();
                    _lastNavTime = Time.unscaledTime;
                }
                // 左键在主条目无作用
            }
            else
            {
                if (_groupsDirty) RebuildGroups();
                if (_catNames.Count == 0) return;
                var list = _catToIndices[_catNames[_selectedCategory]];
                if (delta != 0 && list.Count > 0)
                {
                    int m = list.Count;
                    _selectedInCategory = ((_selectedInCategory + delta) % m + m) % m;
                    _itemPageInCategory = _selectedInCategory / ItemsPerPage;
                    _selected = list[_selectedInCategory];
                    UpdateCurrentPage();
                    _lastNavTime = Time.unscaledTime;
                }
                if (leftPressed)
                {
                    BackToCategories();
                    _lastNavTime = Time.unscaledTime;
                }
                // 右键在子条目无作用
            }
        }

        // 更新当前页面，确保选中项在当前页面中
        private void UpdateCurrentPage()
        {
            if (_benches.Count == 0) return;

            int targetPage = _selected / ItemsPerPage;
            _currentPage = targetPage;

            D($"UpdateCurrentPage: selected={_selected}, targetPage={targetPage}");
        }

        // 处理鼠标滚轮 - 控制选中项移动并自动翻页
        private void HandleMouseWheelScroll()
        {
            if (_benches.Count == 0) return;

            // 检测鼠标滚轮输入
            float scrollDelta = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scrollDelta) > 0.01f)
            {
                // 根据滚轮方向移动选中项
                int delta = 0;
                if (scrollDelta > 0) delta = -1; // 向上滚动，选中项向上移动
                else if (scrollDelta < 0) delta = +1; // 向下滚动，选中项向下移动

                if (delta != 0)
                {
                    int oldSelected = _selected;
                    _selected = Mathf.Clamp(_selected + delta, 0, _benches.Count - 1);

                    // 如果选择发生了变化，更新当前页面
                    if (_selected != oldSelected)




                    {
                        UpdateCurrentPage();
                        D($"MouseWheel: moved selection from {oldSelected} to {_selected}");
                    }
                }
            }
        }


        private void StartEditingCategory()
        {

            if (_catNames.Count == 0) return;
            _editingCategoryIndex = Mathf.Clamp(_selectedCategory, 0, _catNames.Count - 1);
            _editingCategoryText = _catNames[_editingCategoryIndex] ?? string.Empty;
            _editingCategoryFocusSet = false;

            // 立即设置焦点到输入框
            GUI.FocusControl("EditCatField");
        }

        private void ConfirmEditCategory()
        {
            if (_editingCategoryIndex < 0 || _editingCategoryIndex >= _catNames.Count) { CancelEditCategory(); return; }
            var oldCat = _catNames[_editingCategoryIndex];
            var newCat = (_editingCategoryText ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(newCat) || string.Equals(oldCat, newCat, StringComparison.CurrentCultureIgnoreCase)) { CancelEditCategory(); return; }
            if (!_catToIndices.TryGetValue(oldCat, out var list) || list == null || list.Count == 0) { CancelEditCategory(); return; }

            // 不再批量改动子项显示名；仅更新父类目重命名映射，分组将依据 ResolveCategoryNameForScene(scene) 生效
            // 现有 DisplayName 全视为“子类目名”，无需处理

            // 策略：
            // - 若 newCat 命中 ZoneRenameMap 的某个“显示值”（等号右侧），则进行“全局映射更新”到该值；
            // - 否则视为自定义名字：不动 ZoneRenameMap，直接把该分类下的椅子父类目写入 ActivatedBenches。
            // 先检查当前分类 oldCat 是否在 ZoneRenameMap 中有映射
            bool oldCatHasMapping = false;
            string oldCatMappingKey = null;

            if (_zoneRenameMap != null && _zoneRenameMap.Count > 0)
            {
                foreach (var kv in _zoneRenameMap)
                {
                    var v = (kv.Value ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(v) && string.Equals(v, oldCat, StringComparison.CurrentCultureIgnoreCase))
                    { oldCatMappingKey = kv.Key; oldCatHasMapping = true; break; }
                }
            }

            // 如果当前分类没有映射，再检查 newCat 是否命中现有映射值
            string canonicalKey = null;
            bool mapsToExistingValue = false;
            if (!oldCatHasMapping && _zoneRenameMap != null && _zoneRenameMap.Count > 0)
            {
                foreach (var kv in _zoneRenameMap)
                {
                    var v = (kv.Value ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(v) && string.Equals(v, newCat, StringComparison.CurrentCultureIgnoreCase))
                    { canonicalKey = kv.Key; mapsToExistingValue = true; break; }
                }
            }

            if (oldCatHasMapping)
            {
                // 当前分类有映射：直接修改 ZoneRenameMap 中的映射值
                try
                {
                    if (!string.IsNullOrEmpty(oldCatMappingKey))
                    {
                        _zoneRenameMap[oldCatMappingKey] = newCat;
                        SaveZoneRenameMap();
                    }
                }
                catch { /* ignore */ }
            }
            else if (mapsToExistingValue)
            {
                // newCat 命中现有映射值：全局映射更新
                try
                {
                    if (list != null)
                    {
                        for (int __i = 0; __i < list.Count; __i++)
                        {
                            int __idx = list[__i]; if (__idx < 0 || __idx >= _benches.Count) continue;
                            var __b = _benches[__idx];
                            __b.ParentCategory = canonicalKey;
                            try
                            {
                                var map = SceneTeleportMap.GetTeleportMap();
                                if (map != null && map.TryGetValue(__b.Scene, out var info))
                                {
                                    var zoneKey = info.MapZone.ToString();
                                    if (!string.IsNullOrEmpty(zoneKey))
                                    {
                                        _zoneRenameMap[zoneKey] = newCat; // 修改的是“枚举键”，而不是本地化显示名键
                                        // 清理可能存在的本地化键，避免产生“苔穴=xxx;MOSS_CAVE=yyy;”的重复键
                                        try
                                        {
                                            var localized = TryGetLocalizedZoneNameForScene(__b.Scene);
                                            if (!string.IsNullOrEmpty(localized)) _zoneRenameMap.Remove(localized);
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                        SaveZoneRenameMap();
                    }
                }
                catch { /* ignore */ }
            }
            else
            {
                // 自定义名字：逐条写入 ActivatedBenches 父类目
                try
                {
                    if (list != null)
                    {
                        for (int __i = 0; __i < list.Count; __i++)
                        {
                            int __idx = list[__i]; if (__idx < 0 || __idx >= _benches.Count) continue;
                            var __b = _benches[__idx];
                            __b.ParentCategory = newCat;
                        }
                        SavePersistedBenches();
                    }
                }
                catch { /* ignore */ }
            }

            // 如旧分类被收藏，则将收藏项名称同步为新分类名，保持收藏与顺序
            if (_favoriteScenesSet.Contains(oldCat))
            {
                int order = -1;
                for (int i = 0; i < _favoriteScenes.Count; i++) { if (string.Equals(_favoriteScenes[i], oldCat, StringComparison.CurrentCultureIgnoreCase)) { order = i; break; } }
                _favoriteScenes.RemoveAll(s => string.Equals(s, oldCat, StringComparison.CurrentCultureIgnoreCase));
                if (order < 0 || order > _favoriteScenes.Count) order = _favoriteScenes.Count;
                // 若已存在同名收藏，避免重复；否则插入到原位置
                if (!_favoriteScenesSet.Contains(newCat))
                {
                    _favoriteScenes.Insert(order, newCat);
                }
                // 重建 set & order
                _favoriteScenesSet.Clear();
                _favoriteScenesOrder.Clear();
                for (int i = 0; i < _favoriteScenes.Count; i++) { var n = _favoriteScenes[i]; _favoriteScenesSet.Add(n); _favoriteScenesOrder[n] = i; }
                SaveFavoriteScenes();
            }

            _benches.Sort(BenchRef.Comparer);
            SavePersistedBenches();
            _groupsDirty = true; RebuildGroups();

            // 选中停留在新分类；若存在同名分类将自动合并
            int catIdx = _catNames.FindIndex(n => string.Equals(n, newCat, StringComparison.CurrentCultureIgnoreCase));
            if (catIdx >= 0)
            {
                _inCategoryView = true;
                _selectedCategory = catIdx;
                _categoryPage = _selectedCategory / ItemsPerPage;
            }

            CancelEditCategory();
        }


        /// <summary>
        /// 判断父类目是否是自定义的（不是 MapZone 枚举键）
        /// </summary>
        private bool IsCustomParentCategory(string parentCategory, string sceneName)
        {
            if (string.IsNullOrEmpty(parentCategory)) return false;

            try
            {
                // 检查是否是 MapZone 枚举键
                var map = SceneTeleportMap.GetTeleportMap();
                if (map != null && map.TryGetValue(sceneName, out var info))
                {
                    var zoneKey = info.MapZone.ToString();
                    // 如果父类目等于该场景的 MapZone 键，则不是自定义的
                    if (string.Equals(parentCategory, zoneKey, StringComparison.Ordinal)) return false;

                    // 检查去下划线的变体
                    var alt = zoneKey.Replace("_", string.Empty);
                    if (string.Equals(parentCategory, alt, StringComparison.Ordinal)) return false;
                }

                // 检查是否在 ZoneRenameMap 的键中（如果在键中，说明是 MapZone 枚举键）
                if (_zoneRenameMap != null && _zoneRenameMap.ContainsKey(parentCategory)) return false;

                // 检查去下划线后是否在 ZoneRenameMap 的键中
                var parentAlt = parentCategory.Replace("_", string.Empty);
                if (_zoneRenameMap != null && _zoneRenameMap.ContainsKey(parentAlt)) return false;

                // 如果都不匹配，则认为是自定义的
                return true;
            }
            catch
            {
                // 出错时保守处理，认为是自定义的
                return true;
            }
        }

        private void StartEditingCategoryFromChild()
        {
            // 仅修改“当前选中椅子”的父类目，不做全局更名
            if (_benches.Count == 0) return;
            if (_selected < 0 || _selected >= _benches.Count) return;
            _editingIndex = _selected;
            var bench = _benches[_editingIndex];
            _editingParentOnly = true;
            // 获取当前父类目
            var currentParent = bench.ParentCategory;
            if (string.IsNullOrEmpty(currentParent)) currentParent = ResolveCategoryNameForScene(bench.Scene);

            var key = (currentParent ?? string.Empty).Trim();

            // 先检查父类目是否是自定义的（不是 MapZone 枚举键）
            bool isCustomParent = IsCustomParentCategory(key, bench.Scene);

            if (isCustomParent)
            {
                // 如果是自定义父类目，直接显示
                _editingText = key;
            }
            else
            {
                // 如果不是自定义的，按原逻辑：检查映射并显示映射值
                string mapped = null;
                bool set = false;
                if (!string.IsNullOrEmpty(key) && _zoneRenameMap != null)
                {
                    if (_zoneRenameMap.TryGetValue(key, out mapped) && !string.IsNullOrEmpty(mapped))
                    {
                        _editingText = mapped; set = true;
                    }
                    else
                    {
                        var alt = key.Replace("_", string.Empty);
                        if (!string.Equals(alt, key, StringComparison.Ordinal) && _zoneRenameMap.TryGetValue(alt, out mapped) && !string.IsNullOrEmpty(mapped))
                        { _editingText = mapped; set = true; }
                    }
                }
                // 备用：使用该椅子的 MapZone 键做映射（例如 BONETOWN），保证能看到当前区域的映射值
                if (!set)
                {
                    try
                    {
                        var map = SceneTeleportMap.GetTeleportMap();
                        if (map != null && map.TryGetValue(bench.Scene, out var info))
                        {
                            var zoneKey = info.MapZone.ToString();
                            if (!string.IsNullOrEmpty(zoneKey) && _zoneRenameMap != null)
                            {
                                if (_zoneRenameMap.TryGetValue(zoneKey, out mapped) && !string.IsNullOrEmpty(mapped))
                                { _editingText = mapped; set = true; }
                                else
                                {
                                    var alt2 = zoneKey.Replace("_", string.Empty);
                                    if (!string.Equals(alt2, zoneKey, StringComparison.Ordinal) && _zoneRenameMap.TryGetValue(alt2, out mapped) && !string.IsNullOrEmpty(mapped))
                                    { _editingText = mapped; set = true; }
                                }
                            }
                        }
                    }
                    catch { }
                }
                if (!set) _editingText = key;
            }
            // 关闭“前缀锁定”逻辑，按独立父类目编辑
            _editingLockedPrefix = null;
            _editingSuffix = string.Empty;
            _editingFocusSet = false;
            _lastNavTime = Time.unscaledTime;

            // 立即设置焦点到输入框
            GUI.FocusControl("EditField");
        }

        private void CancelEditCategory()
        {
            _editingCategoryIndex = -1;
            _editingCategoryText = string.Empty;
            _editingCategoryFocusSet = false;
        }

        private void StartEditing()
        {
            if (_selected >= 0 && _selected < _benches.Count)
            {
                _editingIndex = _selected;
                _editingParentOnly = false;
                var bench = _benches[_selected];
                var dn = bench.DisplayName ?? string.Empty;

                if (!_inCategoryView)
                {
                    // 二级菜单下：锁定前缀为当前分类名，只允许编辑子条目显示部分
                    string cat = ResolveCategoryNameForScene(bench.Scene);
                    string suffix = dn;
                    int h = dn.IndexOf(' ');
                    if (h > 0)
                    {
                        string prefix = dn.Substring(0, h).Trim();


                        if (string.Equals(prefix, cat, StringComparison.CurrentCultureIgnoreCase))
                        {
                            suffix = dn.Substring(h).Trim(); // 去掉“分类名 ”前缀后作为可编辑内容
                        }
                    }
                    _editingLockedPrefix = cat;
                    _editingSuffix = suffix;
                    _editingText = suffix; // 用于输入框
                }
                else
                {
                    // 父级列表理论上不进入编辑；兜底为完整名
                    _editingLockedPrefix = null;
                    _editingSuffix = string.Empty;
                    _editingText = dn;
                }
                _editingFocusSet = false; // 重置焦点状态
                _lastNavTime = Time.unscaledTime;

                // 立即设置焦点到输入框
                GUI.FocusControl("EditField");
            }
        }

        private void ConfirmEdit()
        {
            if (_editingIndex >= 0 && _editingIndex < _benches.Count)
            {
                var bench = _benches[_editingIndex];
                // 记录当前编辑的椅子，用于重命名后保持选中
                string benchKey = bench.Scene + "|" + bench.Marker;

                // 模式一：仅修改该条目的父类目（不影响其它条目）
                if (_editingParentOnly)
                {
                    var newParent = (_editingText ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(newParent))
                    {
                        // 反查 ZoneRenameMap：若等号右侧等于用户输入，则写入左侧“键”；否则直接写入用户输入
                        string toWrite = newParent;
                        if (_zoneRenameMap != null && _zoneRenameMap.Count > 0)
                        {
                            foreach (var kv in _zoneRenameMap)
                            {
                                var val = (kv.Value ?? string.Empty).Trim();
                                if (!string.IsNullOrEmpty(val) && string.Equals(val, newParent, StringComparison.CurrentCulture))
                                { toWrite = kv.Key; break; }
                            }
                        }
                        bench.ParentCategory = toWrite;
                        SavePersistedBenches();
                        RefreshGroupsAndSelectionAfterMutation();
                        // 重命名后恢复选中到原椅子
                        RestoreSelectionToBench(benchKey);
                    }
                }
                else
                {
                    // 模式二：修改该条目的显示名
                    string finalName = null;
                    if (!_inCategoryView && !string.IsNullOrEmpty(_editingLockedPrefix))
                    {
                        // 只允许编辑子条目显示部分（例如 “#1” 或 “我的椅子”）；保存时仅保留后缀，不再写入父类目前缀
                        var suffix = (_editingText ?? string.Empty).Trim();
                        finalName = string.IsNullOrEmpty(suffix) ? bench.DisplayName : suffix;
                    }
                    else
                    {
                        // 备用路径：保留原名或采用完整编辑值
                        finalName = (_editingText ?? string.Empty).Trim();
                        if (string.IsNullOrEmpty(finalName)) finalName = bench.DisplayName;
                    }

                    bench.DisplayName = finalName;
                    bench.IsRenamed = true;
                    _benches.Sort(BenchRef.Comparer);
                    SavePersistedBenches();
                    RefreshGroupsAndSelectionAfterMutation();
                    // 重命名后恢复选中到原椅子
                    RestoreSelectionToBench(benchKey);
                }
            }

            // 重置编辑状态
            _editingIndex = -1;
            _editingText = "";
            _editingSuffix = "";
            _editingLockedPrefix = null;
            _editingFocusSet = false;
            _lastNavTime = Time.unscaledTime;

            // 清除GUI焦点
            GUI.FocusControl(null);
        }

        private void RestoreSelectionToBench(string benchKey)
        {
            // 根据椅子的唯一标识（scene|marker）恢复选中状态
            if (string.IsNullOrEmpty(benchKey)) return;

            for (int i = 0; i < _benches.Count; i++)
            {
                var bench = _benches[i];
                string key = bench.Scene + "|" + bench.Marker;
                if (string.Equals(key, benchKey, StringComparison.CurrentCulture))
                {
                    _selected = i;
                    // 更新分类视图的选中状态
                    var catKey = GetCategoryKey(bench);
                    if (!string.IsNullOrEmpty(catKey) && _catToIndices.TryGetValue(catKey, out var list) && list != null)
                    {
                        int catIdx = _catNames.FindIndex(n => string.Equals(n, catKey, StringComparison.CurrentCultureIgnoreCase));
                        if (catIdx >= 0)
                        {
                            _selectedCategory = catIdx;
                            _categoryPage = _selectedCategory / ItemsPerPage;

                            int inIdx = list.IndexOf(i);
                            if (inIdx >= 0)
                            {
                                _selectedInCategory = inIdx;
                                if (!_inCategoryView)
                                {
                                    _itemPageInCategory = _selectedInCategory / ItemsPerPage;
                                }
                            }
                        }
                    }
                    break;
                }
            }
        }

        private void CancelEdit()
        {
            // 重置编辑状态
            _editingIndex = -1;
            _editingText = "";
            _editingFocusSet = false;
            _lastNavTime = Time.unscaledTime;

            // 清除GUI焦点
            GUI.FocusControl(null);
        }




        private IEnumerator SingleSpaceEditAfterDelay()
        {
            // 等待双击时间窗口；若期间未出现第二次空格，则执行“单击编辑”
            float end = Time.unscaledTime + DoubleSpaceEditSeconds;
            while (Time.unscaledTime < end)


            {
                yield return null;
            }
            if (_pendingSpaceSingle)
            {
                _pendingSpaceSingle = false;
                // 确保仍是原选中项
                if (_selected == _pendingSpaceIndex)
                {
                    StartEditing();
                    _lastNavTime = Time.unscaledTime;
                }
            }
            _pendingSpaceCo = null;
        }

        private IEnumerator SingleYEditAfterDelay()
        {
            // 等待双击时间窗口；若期间未出现第二次Y键，则执行"单击编辑"
            float end = Time.unscaledTime + DoubleSpaceEditSeconds;
            while (Time.unscaledTime < end)
            {
                yield return null;
            }
            if (_pendingYSingle)
            {
                _pendingYSingle = false;
                // 确保仍是原选中项
                if (_selected == _pendingYIndex)
                {
                    StartEditing();
                    _lastNavTime = Time.unscaledTime;
                }
            }
            _pendingYCo = null;
        }


        private IEnumerator SingleF2AfterDelay()
        {
            // 等待双击时间窗口；若期间未出现第二次F2，则执行“单击”逻辑（设置复活点）
            float end = Time.unscaledTime + F2DoubleTapWindow;
            while (Time.unscaledTime < end)
            {
                yield return null;
                if (!_pendingF2Single) { _pendingF2Co = null; yield break; }
            }
            if (_pendingF2Single)
            {
                _pendingF2Single = false;
                TryQuickRestAndSave();
            }
            _pendingF2Co = null;
        }

        /// <summary>
        /// 清除F2标记
        /// </summary>
        private void ClearF2Marker()
        {

#if false

		private IEnumerator SingleSpaceEditAfterDelay()
		{
			// 等待双击时间窗口；若期间未出现第二次空格，则执行“单击编辑”
			float end = Time.unscaledTime + DoubleSpaceEditSeconds;
			while (Time.unscaledTime < end)
			{
				yield return null;
			}
			if (_pendingSpaceSingle)
			{
				_pendingSpaceSingle = false;
				// 确保仍是原选中项
				if (_selected == _pendingSpaceIndex)
				{
					StartEditing();
					_lastNavTime = Time.unscaledTime;
				}
			}


			_pendingSpaceCo = null;
		}
#endif


            _f2OverrideActive = false;
            _repositionScene = null;
            _repositionPos = Vector3.zero;
            _repositionMarker = null;
            _repositionLocalOffset = Vector2.zero;
            _playerJustRespawned = false; // 重置复活标记

            ShowHud("NO", 1.2f);
            D("F2 marker cleared by user");
        }

        private void ConfirmTeleport()
        {
            if (_benches.Count > 0 && _selected >= 0 && _selected < _benches.Count)
            {
                var target = _benches[_selected];



                CloseUI();

                // 确保UI状态完全清理后再开始传送
                StartCoroutine(SafeTeleportToBenchRoutine(target));
            }
        }

        /// <summary>
        /// 安全的传送协程，确保UI状态完全清理
        /// </summary>
        private IEnumerator SafeTeleportToBenchRoutine(BenchRef target)
        {
            // 等待一帧确保UI完全关闭
            yield return null;

            // 强制确保输入状态正常
            bool needWait = false;
            try
            {
                var ih = InputHandler.Instance;
                if (ih != null)
                {
                    // 确保输入系统处于正常状态
                    if (!ih.acceptingInput)
                    {
                        D("SafeTeleportToBenchRoutine: 输入系统未就绪，等待恢复");
                        needWait = true;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"SafeTeleportToBenchRoutine: 检查输入状态时出错: {e}");
            }

            if (needWait)
            {
                yield return new WaitForSeconds(0.1f);
            }

            // 开始实际传送
            yield return StartCoroutine(TeleportToBenchRoutine(target));
        }



        private void ConfirmTeleportSelection()
        {
            if (_benches.Count == 0) { CloseUI(); return; }
            var target = _benches[_selected];
            CloseUI();
            StartCoroutine(TeleportToBenchRoutine(target));
        }
        #endregion

        #region Actions
        private System.Collections.IEnumerator TeleportToBenchRoutine(BenchRef target)
        {
            var gm = GameManager.instance;
            var pd = PlayerData.instance;
            if (gm == null || pd == null) yield break;

            D($"TeleportToBenchRoutine: start -> {target.Scene}|{target.Marker}");

            // 检查并处理椅子状态
            if (pd.atBench)
            {
                D("F1传送：玩家在椅子上，先让其起来");
                ForceGetOffBench();
                yield return null; // 等待一帧让状态更新
            }

            // 获取椅子位置
            Vector3 benchPosition = GetBenchPosition(target.Scene, target.Marker);
            if (benchPosition == Vector3.zero)
            {
                Logger.LogWarning($"无法找到椅子位置: {target.Scene}|{target.Marker}");
                yield break;
            }

            // 检查是否需要跨场景传送
            string currentScene = SceneManager.GetActiveScene().name;
            if (currentScene == target.Scene)
            {
                // 同场景传送：直接传送到椅子位置
                D($"同场景传送到椅子: {benchPosition}");
                PerformDirectTeleport(benchPosition);

            }
            else
            {
                // 跨场景传送：传递椅子标记名称，在目标场景中重新查找位置
                D($"跨场景传送: {currentScene} -> {target.Scene}");
                yield return StartCoroutine(TeleportWithSceneChangeToMarker(target.Scene, target.Marker));
            }
        }

        private void TryQuickRestAndSave()
        {
            try
            {
                var hc = HeroController.instance;
                var pd = PlayerData.instance;
                var gm = GameManager.instance;

                if (hc == null || pd == null || gm == null)
                {
                    // Logger.LogWarning("F2: 游戏组件未就绪");
                    return;
                }

                // 如果已经有F2标记，再按一次F2表示清除标记
                if (_f2OverrideActive)
                {
                    ClearF2Marker();
                    return;
                }

                // 记录当前位置信息
                _repositionScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                _repositionPos = hc.transform.position;

                // 寻找最近的 RespawnMarker 作为锚点（如果有的话）
                RespawnMarker nearest = null;
                float bestSqr = float.MaxValue;
                foreach (var rm in RespawnMarker.Markers)
                {
                    if (!rm) continue;
                    float sq = (rm.transform.position - _repositionPos).sqrMagnitude;
                    if (sq < bestSqr) { bestSqr = sq; nearest = rm; }
                }

                if (nearest != null)
                {
                    // 有 RespawnMarker：使用相对偏移定位
                    _repositionMarker = nearest.name;
                    _repositionLocalOffset = (Vector2)(_repositionPos - nearest.transform.position);
                    D($"Found nearest marker: {_repositionMarker}, offset={_repositionLocalOffset}");
                }
                else
                {
                    // 无 RespawnMarker：使用绝对位置定位
                    _repositionMarker = null;
                    _repositionLocalOffset = Vector2.zero;


                    D("No RespawnMarker found, using absolute position");
                }

                // 开启 F2 覆盖模式
                _f2OverrideActive = true;

                // 自动保存游戏
                try
                {
                    gm.SaveGame((success) =>
                    {
                        if (success)
                        {
                            D("F2: Auto-saved game after setting custom respawn point");
                        }
                        else
                        {
                            D("F2: Failed to auto-save game");
                        }
                    });
                }
                catch (Exception saveEx)
                {
                    D($"F2: Failed to auto-save game: {saveEx}");
                }

                // Logger.LogInfo($"已记录脚下重定位：{_repositionScene} @ ({_repositionPos.x:F2}, {_repositionPos.y:F2})");
                D($"Recorded ground reposition: scene={_repositionScene}, pos={_repositionPos}, marker={_repositionMarker ?? "none"}");
                ShowHud("OK", 1.2f);

            }
            catch
            {
                // Logger.LogError($"TryQuickRestAndSave error: {e}");
            }
        }

        private void OnActiveSceneChanged(Scene from, Scene to)
        {
            try
            {
                D($"OnActiveSceneChanged: {from.name} -> {to.name}, f2Override={_f2OverrideActive}, recordScene={_repositionScene}, justRespawned={_playerJustRespawned}");

                // 回到主菜单/标题场景时，允许下次进入存档再次执行一次性规范化
                try
                {
                    var toName = to.name ?? string.Empty;
                    var low = toName.ToLowerInvariant();
                    if (low.Contains("menu") || low.Contains("title"))
                    {
                        _postEnterNormalized = false;
                    }
                }
                catch { /* ignore */ }


                // 场景切换时强制关闭UI，防止UI状态在场景间残留
                if (_uiOpen)
                {
                    D("OnActiveSceneChanged: 强制关闭UI，防止跨场景UI状态问题");
                    _uiOpen = false;
                    _editingIndex = -1;
                    _editingText = "";
                    _editingFocusSet = false;

                    // 安全清理输入阻塞器
                    try
                    {
                        var hc = HeroController.SilentInstance;
                        if (hc != null) hc.RemoveInputBlocker(this);
                    }
                    catch { /* ignore */ }
                }

                // Refresh cached refs on scene change
                RefreshRefs(true);
                InvalidateSceneCaches();


                // F2 覆盖：仅当开启且记录了场景且玩家刚刚复活时生效
                if (!_f2OverrideActive || string.IsNullOrEmpty(_repositionScene) || !_playerJustRespawned)
                {
                    if (_f2OverrideActive && !string.IsNullOrEmpty(_repositionScene))
                    {
                        D("OnActiveSceneChanged: F2 active but player didn't just respawn, skipping reposition");
                    }
                    return;
                }

                if (to.name == _repositionScene)
                {
                    D("进入记录场景且玩家刚复活，开始重定位");
                    StartCoroutine(RepositionAfterEnter());


                }
            }
            catch
            {
                // Logger.LogError($"OnActiveSceneChanged error: {e}");
            }
        }

        private IEnumerator RepositionAfterEnter()
        {
            // 等待进入完成、可接收输入
            float timeout = Time.unscaledTime + 6f;
            while (Time.unscaledTime < timeout)
            {
                var hc = HeroController.instance;
                if (hc != null && hc.acceptingInput)
                {
                    break;
                }
                yield return null;
            }

            var hc2 = HeroController.instance;
            var pd = PlayerData.instance;
            if (hc2 == null || pd == null)
            {
                // Logger.LogError("HeroController or PlayerData not found in RepositionAfterEnter");
                yield break;
            }

            try
            {
                // 检查并处理椅子状态
                if (pd.atBench)
                {
                    D("进入场景后发现玩家在椅子上，先让其起来");
                    ForceGetOffBench();
                }
            }
            catch
            {
                // Logger.LogError($"ForceGetOffBench error in RepositionAfterEnter: {e}");
            }

            yield return null; // 等待一帧让状态更新

            try
            {
                Vector2 desired;

                if (!string.IsNullOrEmpty(_repositionMarker))
                {
                    // 有 RespawnMarker：基于锚点的相对定位
                    BuildMarkerCache();
                    RespawnMarker anchor = null;
                    if (_markerByName != null) _markerByName.TryGetValue(_repositionMarker, out anchor);
                    if (anchor != null)
                    {
                        desired = (Vector2)anchor.transform.position + _repositionLocalOffset;
                        D($"RepositionAfterEnter: anchor={_repositionMarker}, anchorPos={anchor.transform.position}, offset={_repositionLocalOffset}, desired={desired}");
                    }
                    else
                    {
                        // 锚点不存在，使用原始位置
                        desired = (Vector2)_repositionPos;
                        D($"RepositionAfterEnter: anchor not found -> fallback to raw pos {_repositionPos}");
                    }
                }
                else
                {
                    // 无 RespawnMarker：直接使用绝对位置
                    desired = (Vector2)_repositionPos;
                    D($"RepositionAfterEnter: no marker, using absolute pos {_repositionPos}");
                }

                Vector3 target = hc2.FindGroundPoint(desired, true);
                D($"RepositionAfterEnter: ground target={target}");
                DoDirectTeleport(target);

                // 修正相机：立即对齐到主角，避免画面偏移/遮挡
                try { GameCameras.instance?.cameraController?.PositionToHeroInstant(true); } catch { /* ignore */ }
                // 去黑：强制触发一次镜头淡入
                try { GameCameras.instance?.cameraFadeFSM?.SendEventSafe("FADE SCENE IN INSTANT"); } catch { /* ignore */ }
                GameManager.instance?.SaveGame(didSave => { /* Logger.LogInfo($"保存完成: {didSave}"); */ });

                // 重置复活标记，确保只传送一次
                _playerJustRespawned = false;
                D("RepositionAfterEnter: 重置复活标记，F2传送完成");

                // Logger.LogInfo($"已在进入场景后重定位：{_repositionScene} @ {target}");
                // ShowTip("已回到记录场景并定位到记录位置（已保存）", 2.2f);
            }
            catch
            {
                // Logger.LogError($"RepositionAfterEnter error: {e}");
            }
        }

        /// <summary>
        /// 双击F2：传送到玩家当前设定的复活点（优先使用自定义F2复活点，其次使用椅子复活点）
        /// </summary>
        private void TryTeleportToSetRespawnPoint()
        {
            try
            {
                var pd = PlayerData.instance;
                var gm = GameManager.instance;
                if (pd == null || gm == null) return;

                // 1) 优先：F2 自定义复活点（记录了场景和坐标）
                if (_f2OverrideActive && !string.IsNullOrEmpty(_repositionScene))
                {
                    string current = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    if (current == _repositionScene)
                    {
                        D($"Double F2: same-scene teleport to custom respawn -> {_repositionPos}");
                        DoDirectTeleport(_repositionPos);
                    }
                    else
                    {
                        D($"Double F2: scene-change teleport {current} -> {_repositionScene}");
                        StartCoroutine(TeleportWithSceneChange(_repositionScene, _repositionPos));
                    }
                    ShowTip(GetText("传送到复活点", "Teleported to respawn"), 1.2f);
                    return;
                }

                // 2) 其次：玩家的椅子复活点（scene + marker）
                string respawnScene = pd.respawnScene;
                string respawnMarker = pd.respawnMarkerName;
                if (string.IsNullOrEmpty(respawnScene) || string.IsNullOrEmpty(respawnMarker))
                {
                    ShowTip(GetText("未找到复活点", "No respawn point found"), 1.5f);
                    return;
                }

                string cur = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (cur == respawnScene)
                {
                    Vector3 benchPos = GetBenchPositionInCurrentScene(respawnMarker);
                    if (benchPos != Vector3.zero)
                    {
                        D($"Double F2: same-scene teleport to bench respawn -> {respawnMarker} @ {benchPos}");
                        PerformDirectTeleport(benchPos);
                        ShowTip(GetText("传送到复活点", "Teleported to respawn"), 1.2f);
                        return;
                    }
                }

                D($"Double F2: cross-scene teleport to respawn {respawnScene}|{respawnMarker}");
                StartCoroutine(TeleportWithSceneChangeToMarker(respawnScene, respawnMarker));
                ShowTip(GetText("传送到复活点", "Teleported to respawn"), 1.2f);
            }
            catch { /* ignore */ }
        }


        #endregion

        #region Persistence
        internal void AddBench(string scene, string marker)
        {
            if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(marker)) return;

            // 过滤自定义椅子：排除 CustomRestBench(Clone) 等mod生成的椅子
            if (marker.Contains("CustomRestBench(Clone)"))
            {
                D($"Filtered out custom bench: {scene} | {marker}");
                return;
            }

            // 精确去重：同 scene|marker 直接忽略（避免 LINQ 分配）
            for (int i = 0; i < _benches.Count; i++) { var b = _benches[i]; if (b.Scene == scene && b.Marker == marker) return; }

            // 基于距离的去重：同一场景且位置接近（约一个屏幕宽度）则判定为同地点，只保留一个
            try
            {
                string currentScene = SceneManager.GetActiveScene().name;
                if (currentScene == scene)
                {

                    // 

                    // 新椅子位置
                    Vector3 newPos = GetBenchPositionInCurrentScene(marker);
                    if (newPos != Vector3.zero)
                    {
                        float screenWidthWorld = 0f;
                        var cam = Camera.main;
                        if (cam != null && cam.orthographic)
                            screenWidthWorld = 2f * cam.orthographicSize * cam.aspect;
                        if (screenWidthWorld <= 0f) screenWidthWorld = 50f; // 兜底阈值

                        int keepIndex = -1;
                        List<int> toRemove = new List<int>();
                        for (int i = 0; i < _benches.Count; i++)
                        {
                            var b = _benches[i];
                            if (b.Scene != scene) continue;
                            Vector3 pos = GetBenchPositionInCurrentScene(b.Marker);
                            if (pos == Vector3.zero) continue;
                            if ((pos - newPos).sqrMagnitude <= screenWidthWorld * screenWidthWorld)
                            {
                                if (keepIndex < 0) keepIndex = i; // 保留第一个已存在的
                                else toRemove.Add(i);             // 额外的标记为删除
                            }
                        }

                        // 若已存在近距离的椅子，则不新增，并清理多余项
                        if (keepIndex >= 0)
                        {
                            if (toRemove.Count > 0)
                            {
                                toRemove.Sort((a, b) => b.CompareTo(a));
                                foreach (var idx in toRemove) _benches.RemoveAt(idx);
                                SavePersistedBenches();
                                D($"Pruned {toRemove.Count} nearby duplicate benches in scene {scene}");
                            }
                            Logger.LogInfo($"检测到相同地点的椅子（距离很近），跳过新记录：{scene} | {marker}");
                            ShowTip(GetText("已合并相同地点的椅子", "Merged nearby bench entries"), 1.2f);
                            return;
                        }
                    }
                }
            }
            catch { /* ignore */ }

            // 正常新增
            var newBench = new BenchRef(scene, marker);
            // 默认父类目统一存枚举键（英文）：MapZone.ToString()
            try
            {
                var map = SceneTeleportMap.GetTeleportMap();
                if (map != null && map.TryGetValue(scene, out var info))
                {
                    var zoneKey = info.MapZone.ToString();
                    newBench.ParentCategory = zoneKey;

                    // 立即为中文玩家添加 ZoneRenameMap 映射（仅当 UILanguage 设置为 Chinese 时）
                    // 移除 IsGameplayReady() 限制：坐下时 HeroController.acceptingInput 可能为 false，导致映射未添加
                    if (_isChineseLanguage && !string.IsNullOrEmpty(zoneKey))
                    {
                        try
                        {
                            var localizedName = Language.Get(zoneKey, "Map Zones");
                            if (!string.IsNullOrEmpty(localizedName) &&
                                !string.Equals(localizedName, zoneKey, StringComparison.CurrentCulture))
                            {
                                // 只有当本地化名称与枚举键不同时才添加映射（说明是中文等本地化语言）
                                if (!_zoneRenameMap.ContainsKey(zoneKey))
                                {
                                    _zoneRenameMap[zoneKey] = localizedName;
                                    SaveZoneRenameMap();
                                    D($"Auto-added zone mapping for new bench: {zoneKey} = {localizedName}");
                                }
                            }
                        }
                        catch { /* 语言系统可能未就绪，稍后会通过延迟刷新处理 */ }
                    }
                }
            }
            catch { }
            newBench.AddedOrder = ++_benchAddedOrderCounter;
            _benches.Add(newBench);

            // 立即尝试使用本地化区域名（中文用官方本地化，英文用友好名称）；失败则标记延迟刷新
            // 移除语言限制，让所有用户都能享受本地化翻译
            bool updated = false;
            try
            {
                var cat = GetCategoryKey(newBench);
                if (!string.IsNullOrEmpty(cat))
                {
                    // 采用“最小可用编号”策略，避免偶发重复
                    var used = new HashSet<int>();
                    for (int i = 0; i < _benches.Count; i++)
                    {
                        var ob = _benches[i];
                        if (!string.Equals(GetCategoryKey(ob), cat, StringComparison.CurrentCultureIgnoreCase)) continue;
                        var dn = ob.DisplayName ?? string.Empty;
                        int p = dn.LastIndexOf('#');
                        if (p >= 0 && p + 1 < dn.Length)
                        {
                            var numStr = dn.Substring(p + 1).Trim();
                            bool digitsOnly = numStr.Length > 0;
                            for (int k = 0; k < numStr.Length; k++) { if (!char.IsDigit(numStr[k])) { digitsOnly = false; break; } }
                            if (digitsOnly && int.TryParse(numStr, out var n) && n > 0) used.Add(n);
                        }
                    }
                    int idxNum = 1;
                    while (used.Contains(idxNum)) idxNum++;
                    newBench.DisplayName = $"#{idxNum}";
                    newBench.IsRenamed = false; // 保持为“未重命名”
                    updated = true;
                }
            }
            catch { /* ignore */ }

            if (!updated) RequestLocalizedRefresh();

            _benches.Sort(BenchRef.Comparer);
            SavePersistedBenches();
            // 记录最近一次新增，供下次打开UI时自动选中，并将全局选择指向该项
            _pendingSelectKey = scene + "|" + marker;
            int newIndex = _benches.FindIndex(b => b.Scene == scene && b.Marker == marker);
            if (newIndex >= 0) _selected = newIndex;
            Logger.LogInfo($"记录椅子：{scene} | {marker}. 总计：{_benches.Count}");
        }

        private void LoadPersistedBenches()
        {
            _benches.Clear();
            var raw = _benchesPersist.Value ?? string.Empty;

            if (string.IsNullOrEmpty(raw))
            {
                raw = "Tut_01|Death Respawn Marker Init";
                _benchesPersist.Value = raw;
                Config.Save();
            }

            var sceneMap = SceneTeleportMap.GetTeleportMap();
            bool filterByScene = sceneMap != null && sceneMap.Count > 20;

            bool migrated = false;
            int orderCounter = 0;

            foreach (var tok in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = tok.Split('|');
                if (parts.Length >= 1)
                {
                    var scene = parts[0];
                    if (string.IsNullOrEmpty(scene)) { migrated = true; continue; }
                    if (filterByScene && !sceneMap.ContainsKey(scene)) { migrated = true; continue; }

                    var marker = parts.Length >= 2 ? parts[1] : null;
                    if (string.IsNullOrEmpty(marker)) { migrated = true; continue; }

                    var displayName = parts.Length >= 3 ? parts[2] : null;
                    var bench = new BenchRef(scene, marker, displayName);

                    // 新增父类目字段（向后兼容）
                    string parent = null;
                    int idx = 3;
                    if (parts.Length >= 4)
                    {
                        var p3 = parts[3];
                        bool looksBool = string.Equals(p3, "0") || string.Equals(p3, "1")
                                         || string.Equals(p3, "true", StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(p3, "false", StringComparison.OrdinalIgnoreCase);
                        if (!looksBool)
                        {
                            parent = (p3 ?? string.Empty).Trim();
                            idx = 4;
                        }
                    }
                    if (string.IsNullOrEmpty(parent))
                    {
                        try
                        {
                            var map2 = SceneTeleportMap.GetTeleportMap();
                            if (map2 != null && map2.TryGetValue(scene, out var info2)) parent = info2.MapZone.ToString();
                        }
                        catch { }
                    }
                    bench.ParentCategory = parent;

                    // IsRenamed（兼容不同索引）
                    if (parts.Length > idx)
                    {
                        var flag = parts[idx];
                        if (flag == "1" || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)) bench.IsRenamed = true;
                        else if (flag == "0" || string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase)) bench.IsRenamed = false;
                        else bench.IsRenamed = !BenchRef.LooksLikeDefaultNameForScene(scene, bench.DisplayName);
                    }
                    else { bench.IsRenamed = !BenchRef.LooksLikeDefaultNameForScene(scene, bench.DisplayName); migrated = true; }

                    // AddedOrder（兼容索引）
                    if (parts.Length > idx + 1 && int.TryParse(parts[idx + 1], out var parsedOrder))
                    { bench.AddedOrder = parsedOrder; orderCounter = Math.Max(orderCounter, parsedOrder); }
                    else { bench.AddedOrder = ++orderCounter; migrated = true; }

                    // 收藏标记与顺序（兼容索引）
                    if (parts.Length > idx + 2)
                    {
                        var favFlag = parts[idx + 2];
                        bench.IsFavorite = (favFlag == "1" || string.Equals(favFlag, "true", StringComparison.OrdinalIgnoreCase));
                    }
                    if (parts.Length > idx + 3 && int.TryParse(parts[idx + 3], out var favOrder))
                    { bench.FavoriteOrder = favOrder; _benchFavoriteOrderCounter = Math.Max(_benchFavoriteOrderCounter, favOrder); }

                    _benches.Add(bench);
                }
                else { migrated = true; }
            }

            _benches.Sort(BenchRef.Comparer);
            _benchAddedOrderCounter = orderCounter;
            if (migrated) { try { SavePersistedBenches(); } catch { } }
        }

        /// <summary>
        /// 兼容老版本：将老格式的椅子名称转换为新格式
        /// 老格式：Halfway_01|RestBench|中途旅馆|1|15|0|0
        /// 新格式：Halfway_01|RestBench|远野 中途旅馆|1|15|0|0
        /// </summary>
        private bool ConvertOldFormatBenchNames()
        {
            // 迁移老数据：把“父类目 前缀 + #编号”的子项名称批量剥离成“#编号”
            bool converted = false;

            for (int i = 0; i < _benches.Count; i++)
            {
                var bench = _benches[i];
                var dn = bench.DisplayName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dn)) continue;

                // 构造可能的老式前缀候选：友好名、当前分类名、本地化名
                var candidates = new List<string>(3);
                try { var map = SceneTeleportMap.GetTeleportMap(); if (map != null && map.TryGetValue(bench.Scene, out var info)) { var friendly = info.MapZone.ToString(); if (!string.IsNullOrEmpty(friendly)) candidates.Add(friendly); } } catch { }
                try { var desired = ResolveCategoryNameForScene(bench.Scene); if (!string.IsNullOrEmpty(desired) && !candidates.Contains(desired)) candidates.Add(desired); } catch { }
                try { var loc = TryGetLocalizedZoneNameForScene(bench.Scene); if (!string.IsNullOrEmpty(loc) && !candidates.Contains(loc)) candidates.Add(loc); } catch { }

                bool stripped = false;
                foreach (var prefix in candidates)
                {
                    if (string.IsNullOrEmpty(prefix)) continue;
                    if (!dn.StartsWith(prefix + " ", StringComparison.Ordinal)) continue;
                    int p = dn.LastIndexOf('#');
                    if (p < 0 || p + 1 >= dn.Length) continue;
                    var numStr = dn.Substring(p + 1).Trim();
                    bool digitsOnly = numStr.Length > 0;
                    for (int k = 0; k < numStr.Length; k++) { if (!char.IsDigit(numStr[k])) { digitsOnly = false; break; } }
                    if (!digitsOnly) continue;

                    var newDisplayName = "#" + numStr;
                    if (!string.Equals(newDisplayName, dn, StringComparison.Ordinal))
                    {
                        bench.DisplayName = newDisplayName;
                        converted = true;
                        stripped = true;
                        Logger.LogInfo($"剥离旧式前缀：{dn} -> {newDisplayName} (scene={bench.Scene})");
                    }
                    if (stripped) break;
                }
            }

            return converted;
        }

        /// <summary>
        /// 获取场景的本地化名称，根据当前语言设置返回中文或英文
        /// </summary>
        private string GetLocalizedSceneName(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return null;

            // 尝试获取本地化名称
            string localizedName = TryGetLocalizedZoneNameForScene(scene);
            if (!string.IsNullOrEmpty(localizedName))
            {
                return localizedName;
            }

            // 回退到友好名称
            return FriendlyFromScene(scene);
        }
        //
        // 
        //  
        //  
        //
        private int CleanInvalidSceneBenches()
        {
            try
            {
                if (_benches == null || _benches.Count == 0) return 0;
                int before = _benches.Count;
                var keep = new List<BenchRef>(_benches.Count);

                // 从游戏的 SceneTeleportMap 读取有效场景列表；若未就绪，则跳过清理以避免误删
                var sceneMap = SceneTeleportMap.GetTeleportMap();
                if (sceneMap == null || sceneMap.Count <= 20)
                    return 0;

                foreach (var b in _benches)
                {
                    if (!string.IsNullOrEmpty(b?.Scene) && sceneMap.ContainsKey(b.Scene))
                        keep.Add(b);
                }
                int removed = before - keep.Count;
                if (removed > 0)
                {
                    _benches.Clear();
                    _benches.AddRange(keep);
                    _benches.Sort(BenchRef.Comparer);
                }
                return removed;
            }
            catch { return 0; }
        }

        private static bool IsSceneAvailable(string scene)
        {
            try
            {
                if (string.IsNullOrEmpty(scene)) return false;
                // :  Application.CanStreamedLevelBeLoaded   
                return Application.CanStreamedLevelBeLoaded(scene);
            }
            catch { return false; }
        }


        private void SavePersistedBenches()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _benches.Count; i++)
            {
                if (i > 0) sb.Append(';');
                var b = _benches[i];
                var parent = b.ParentCategory;
                if (string.IsNullOrEmpty(parent))
                {
                    try
                    {
                        var map = SceneTeleportMap.GetTeleportMap();
                        if (map != null && map.TryGetValue(b.Scene, out var info)) parent = info.MapZone.ToString();
                    }
                    catch { }
                }
                sb.Append(b.Scene).Append('|').Append(b.Marker).Append('|').Append(b.DisplayName)
                  .Append('|').Append(parent)
                  .Append('|').Append(b.IsRenamed ? "1" : "0")
                  .Append('|').Append(b.AddedOrder)
                  .Append('|').Append(b.IsFavorite ? "1" : "0")
                  .Append('|').Append(b.FavoriteOrder);
            }
            _benchesPersist.Value = sb.ToString();

            Config.Save();
        }


        private void LoadFavoriteScenes()
        {
            _favoriteScenes.Clear();
            _favoriteScenesSet.Clear();
            _favoriteScenesOrder.Clear();
            try
            {
                var raw = _favoriteScenesPersist?.Value ?? string.Empty;
                foreach (var tok in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = tok.Trim();
                    if (name.Length > 0 && !_favoriteScenes.Exists(s => string.Equals(s, name, StringComparison.CurrentCultureIgnoreCase)))
                        _favoriteScenes.Add(name);
                }
                for (int i = 0; i < _favoriteScenes.Count; i++)
                {
                    var n = _favoriteScenes[i];
                    _favoriteScenesSet.Add(n);
                    _favoriteScenesOrder[n] = i;
                }
            }
            catch { /* ignore */ }
        }

        private void SaveFavoriteScenes()
        {
            try
            {
                _favoriteScenesPersist.Value = string.Join(";", _favoriteScenes);
                Config.Save();
            }
            catch { /* ignore */ }
        }

        private bool IsCategoryFavorited(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return false;
            return _favoriteScenesSet.Contains(cat);
        }

        private void ToggleFavoriteCategory(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return;
            int idx = _favoriteScenes.FindIndex(s => string.Equals(s, cat, StringComparison.CurrentCultureIgnoreCase));
            if (idx >= 0)
            {
                _favoriteScenes.RemoveAt(idx);
                _favoriteScenesSet.Remove(cat);
                _favoriteScenesOrder.Clear();
                for (int i = 0; i < _favoriteScenes.Count; i++) _favoriteScenesOrder[_favoriteScenes[i]] = i;
                SaveFavoriteScenes();
                ShowTip(GetText("已取消收藏分类", "Unfavorited group"), 0.8f);
            }
            else
            {
                _favoriteScenes.Add(cat);
                _favoriteScenesSet.Add(cat);
                _favoriteScenesOrder[cat] = _favoriteScenes.Count - 1;
                SaveFavoriteScenes();
                ShowTip(GetText("已收藏分类", "Favorited group"), 0.8f);
            }

            // 立即重排分类与子项
            try { _groupsDirty = true; RebuildGroups(); } catch { /* ignore */ }
            // 让选中跟随该分类在新排序中的位置
            int __newIdx = _catNames.FindIndex(n => string.Equals(n, cat, StringComparison.CurrentCultureIgnoreCase));
            if (__newIdx >= 0)
            {
                _selectedCategory = Mathf.Clamp(__newIdx, 0, Math.Max(0, _catNames.Count - 1));


                _categoryPage = _selectedCategory / ItemsPerPage;
            }
        }

        private void ToggleFavoriteBench(int benchIndex)
        {
            if (benchIndex < 0 || benchIndex >= _benches.Count) return;
            // 先将全局选中指向当前条目，确保重排后仍能根据它定位新位置
            _selected = benchIndex;
            var b = _benches[benchIndex];
            if (b.IsFavorite)
            {
                b.IsFavorite = false;
            }
            else
            {
                b.IsFavorite = true;
                b.FavoriteOrder = ++_benchFavoriteOrderCounter;
            }
            SavePersistedBenches();
            // 重建分组并让选中跟随到该条目在新排序中的位置与页码
            RefreshGroupsAndSelectionAfterMutation();
        }


        private void RemoveBenchAtIndex(int index)
        {
            if (index < 0 || index >= _benches.Count) return;
            var target = _benches[index];
            // 收藏的椅子不可删除：触发行内短暂震动作为反馈
            if (target.IsFavorite)
            {
                _shakeRowKey = (target.Scene ?? string.Empty) + "|" + (target.Marker ?? string.Empty);
                _shakeUntil = Time.unscaledTime + 0.28f; // 约 0.28s 的轻微抖动
                return;
            }

            // 记录删除前的分类与在分类内的位置（用于删除后在同分类内选择相邻项）
            string catKey = GetCategoryKey(target);
            int prevInIdx = 0;
            if (!string.IsNullOrEmpty(catKey) && _catToIndices.TryGetValue(catKey, out var oldList) && oldList != null)
            {
                int t = oldList.IndexOf(index);
                if (t >= 0) prevInIdx = t;
                else prevInIdx = Mathf.Clamp(_selectedInCategory, 0, Math.Max(0, oldList.Count - 1));
            }

            _benches.RemoveAt(index);
            _benches.Sort(BenchRef.Comparer);
            SavePersistedBenches();

            // 重建分组
            _groupsDirty = true;
            RebuildGroups();

            if (!string.IsNullOrEmpty(catKey))
            {
                int catIdx = _catNames.FindIndex(n => string.Equals(n, catKey, StringComparison.CurrentCultureIgnoreCase));
                if (catIdx >= 0 && _catToIndices.TryGetValue(catKey, out var list) && list != null && list.Count > 0)
                {
                    _selectedCategory = Mathf.Clamp(catIdx, 0, Math.Max(0, _catNames.Count - 1));
                    _categoryPage = _selectedCategory / ItemsPerPage;
                    _inCategoryView = false;
                    int desired = Mathf.Clamp(prevInIdx, 0, list.Count - 1);
                    _selectedInCategory = desired;
                    _itemPageInCategory = _selectedInCategory / ItemsPerPage;
                    _selected = list[_selectedInCategory];
                }
                else if (catIdx >= 0)
                {
                    // 分类被删空，回到该分类行
                    _inCategoryView = true;
                    _selectedCategory = Mathf.Clamp(catIdx, 0, Math.Max(0, _catNames.Count - 1));
                    _categoryPage = _selectedCategory / ItemsPerPage;
                    _selectedInCategory = 0;
                    _itemPageInCategory = 0;
                    if (_catToIndices.TryGetValue(catKey, out var emptyList) && emptyList != null && emptyList.Count > 0)
                    {
                        _selected = emptyList[0];
                    }
                    else
                    {
                        _selected = Mathf.Clamp(_selected, 0, Math.Max(0, _benches.Count - 1));
                    }
                }
            }

            ShowTip(GetText("已删除选中的椅子传送点", "Deleted selected bench"), 1.2f);
            D($"Removed bench: {target.Scene}|{target.Marker}");
        }


        // 打开UI时，对“当前场景”内距离在一屏宽内的椅子进行去重，只保留一个
        private void PruneNearbyDuplicatesInActiveScene()
        {
            try
            {
                if (_benches.Count <= 1) return;
                string scene = SceneManager.GetActiveScene().name;

                // 计算屏幕世界宽度（正交相机）
                float screenWidthWorld = 0f;
                var cam = Camera.main;
                if (cam != null && cam.orthographic)
                    screenWidthWorld = 2f * cam.orthographicSize * cam.aspect;
                if (screenWidthWorld <= 0f) screenWidthWorld = 50f; // 兜底

                var keptPositions = new List<Vector3>();
                var toRemove = new List<int>();

                for (int i = 0; i < _benches.Count; i++)
                {
                    var b = _benches[i];
                    if (b.Scene != scene) continue;
                    var pos = GetBenchPositionInCurrentScene(b.Marker);
                    if (pos == Vector3.zero) continue;

                    bool isDup = false;
                    for (int k = 0; k < keptPositions.Count; k++)
                    {
                        if ((keptPositions[k] - pos).sqrMagnitude <= screenWidthWorld * screenWidthWorld)
                        {
                            isDup = true; break;
                        }
                    }

                    if (isDup) toRemove.Add(i);
                    else keptPositions.Add(pos);
                }

                if (toRemove.Count > 0)
                {
                    toRemove.Sort((a, b) => b.CompareTo(a));
                    foreach (var idx in toRemove) _benches.RemoveAt(idx);
                    _selected = Mathf.Clamp(_selected, 0, _benches.Count - 1);
                    _benches.Sort(BenchRef.Comparer);
                    SavePersistedBenches();
                    D($"UI打开自动清理：场景 {scene} 删除 {toRemove.Count} 个近距离重复椅子");
                }
            }
            catch { /* ignore */ }
        }


        // 初次安装兼容：若当前配置中尚无任何椅子数据，则将"当前官方复活椅子"加入列表
        private void SeedCurrentBenchIfEmpty()
        {
            try
            {
                if (_benches.Count == 0)
                {
                    var pd = PlayerData.instance;
                    if (pd != null && !string.IsNullOrEmpty(pd.respawnMarkerName) && !string.IsNullOrEmpty(pd.respawnScene))
                    {
                        AddBench(pd.respawnScene, pd.respawnMarkerName);
                        // Logger.LogInfo($"Seeded current bench: {pd.respawnScene}|{pd.respawnMarkerName}");
                    }
                }
            }
            catch { /* ignore */ }
        }
        #endregion

        #region Harmony Hooks
        // 监听：
        // 1) 所有 PlayerData.SetBenchRespawn 重载（坐椅子/设置复活点时会调用）
        // 2) GameManager.SetDeathRespawnSimple（部分场景通过 PlayMaker 的 SetDeathRespawnV2 设置复活点）
        // 3) BellBench.Open（椅子激活时调用，确保能识别所有椅子类型）
        private static class Hooks
        {
            // Source: 丝之歌官方源码/Assembly-CSharp/PlayerData.cs SetBenchRespawn(RespawnMarker, string, int)
            [HarmonyPostfix]
            [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.SetBenchRespawn), new[] { typeof(RespawnMarker), typeof(string), typeof(int) })]
            private static void PD_SetBenchRespawn_RespawnMarker_Postfix(RespawnMarker spawnMarker, string sceneName, int spawnType)
            {
                try
                {
                    Instance?.D($"Hook: PD.SetBenchRespawn(RespawnMarker) scene={sceneName}, marker={(spawnMarker ? spawnMarker.name : "<null>")}, type={spawnType}");

                    // 改进椅子识别：检查是否真的是椅子相关的调用
                    if (IsValidBenchRespawn(spawnMarker, sceneName, spawnType))
                    {
                        Instance?.AddBench(sceneName, spawnMarker ? spawnMarker.name : null);
                        // 坐椅子时不再清空F2标记，只记录椅子
                        Instance?.ShowTip(Instance?.GetText("椅子已记录", "Bench recorded"), 1.5f);
                    }
                }
                catch { }
            }

            // Source: 丝之歌官方源码/Assembly-CSharp/PlayerData.cs SetBenchRespawn(string, string, bool)
            [HarmonyPostfix]
            [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.SetBenchRespawn), new[] { typeof(string), typeof(string), typeof(bool) })]
            private static void PD_SetBenchRespawn_Str_Bool_Postfix(string spawnMarker, string sceneName, bool facingRight)
            {
                try
                {
                    Instance?.D($"Hook: PD.SetBenchRespawn(string,bool) scene={sceneName}, marker={spawnMarker}, facing={facingRight}");

                    // 改进椅子识别：检查是否真的是椅子相关的调用
                    if (IsValidBenchRespawnByName(spawnMarker, sceneName))
                    {
                        Instance?.AddBench(sceneName, spawnMarker);
                        // 坐椅子时不再清空F2标记，只记录椅子
                        Instance?.ShowTip(Instance?.GetText("椅子已记录", "Bench recorded"), 1.5f);
                    }
                }
                catch { }
            }

            // Source: 丝之歌官方源码/Assembly-CSharp/PlayerData.cs SetBenchRespawn(string, string, int, bool)
            [HarmonyPostfix]
            [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.SetBenchRespawn), new[] { typeof(string), typeof(string), typeof(int), typeof(bool) })]
            private static void PD_SetBenchRespawn_Str_Int_Bool_Postfix(string spawnMarker, string sceneName, int spawnType, bool facingRight)
            {
                try
                {
                    Instance?.D($"Hook: PD.SetBenchRespawn(string,int,bool) scene={sceneName}, marker={spawnMarker}, type={spawnType}, facing={facingRight}");

                    // 改进椅子识别：检查是否真的是椅子相关的调用
                    if (IsValidBenchRespawnByName(spawnMarker, sceneName))
                    {
                        Instance?.AddBench(sceneName, spawnMarker);
                        // 坐椅子时不再清空F2标记，只记录椅子
                        Instance?.ShowTip(Instance?.GetText("椅子已记录", "Bench recorded"), 1.5f);
                    }
                }
                catch { }
            }

            // 额外补充：部分关卡用 SetDeathRespawnV2（PlayMaker）设置复活点
            [HarmonyPostfix]
            [HarmonyPatch(typeof(GameManager), "SetDeathRespawnSimple", new[] { typeof(string), typeof(int), typeof(bool) })]
            private static void GM_SetDeathRespawnSimple_Postfix(string respawnMarkerName, int respawnType, bool respawnFacingRight)
            {
                try
                {
                    var gm = GameManager.instance;
                    Instance?.D($"Hook: GM.SetDeathRespawnSimple scene={(gm != null ? gm.sceneName : "<null>")}, marker={respawnMarkerName}, type={respawnType}, facing={respawnFacingRight}");
                    if (gm != null && !string.IsNullOrEmpty(respawnMarkerName))
                    {
                        string scene = gm.sceneName;
                        // 仅当该标记被识别为“椅子”时才记录，避免误收集到技能/剧情复活点
                        if (IsValidBenchRespawnByName(respawnMarkerName, scene))
                        {
                            Instance?.AddBench(scene, respawnMarkerName);
                        }
                    }
                }
                catch { /* ignore */ }
            }

            // 简化的椅子检测：只保留最基本的Hook，避免复杂逻辑导致卡死
            // 注释掉复杂的BellBench和RestBench Hook，只依赖SetBenchRespawn Hook

            // 添加简化的BellBench Hook来确保椅子激活时能被检测到
            [HarmonyPostfix]
            [HarmonyPatch(typeof(BellBench), "Open")]
            private static void BellBench_Open_Postfix(BellBench __instance)
            {
                try
                {
                    if (__instance == null) return;

                    var gm = GameManager.instance;
                    if (gm == null) return;

                    string sceneName = gm.sceneName;
                    Instance?.D($"Hook: BellBench.Open scene={sceneName}");

                    // 查找附近的RespawnMarker（手动循环，避免LINQ）
                    Instance?.BuildMarkerCache();
                    var list = Instance?._markerList;
                    if (list != null)
                    {
                        RespawnMarker nearbyMarker = null;
                        float best = 15f * 15f;
                        var pos = __instance.transform.position;
                        for (int i = 0; i < list.Count; i++)
                        {
                            var m = list[i]; if (!m) continue;
                            float sq = (m.transform.position - pos).sqrMagnitude;
                            if (sq < best) { best = sq; nearbyMarker = m; }
                        }
                        if (nearbyMarker != null)
                        {
                            Instance?.D($"BellBench激活，附近找到标记: {nearbyMarker.name}（已忽略：仅记录 RestBench）");
                        }
                    }
                }
                catch (Exception e)
                {
                    Instance?.Logger.LogError($"BellBench_Open_Postfix error: {e}");
                }
            }

            // RestBench Hook 已移除，避免与其他 mod 冲突
            // 椅子检测主要通过 PlayerData.SetBenchRespawn Hook 完成

            // 添加GameManager.SetPlayerDataBool Hook来监听atBench状态变化
            [HarmonyPostfix]
            [HarmonyPatch(typeof(GameManager), "SetPlayerDataBool")]
            private static void GameManager_SetPlayerDataBool_Postfix(string boolName, bool value)
            {
                try
                {
                    if (boolName == "atBench" && value) // 玩家坐下椅子
                    {
                        Instance?.D("Hook: GameManager.SetPlayerDataBool(atBench, true), player sat on bench");

                        // 延迟检查椅子，给游戏时间更新状态
                        if (Instance != null)
                        {
                            Instance.StartCoroutine(Instance.DelayedBenchCheck());
                        }
                    }
                }
                catch (Exception e)
                {
                    Instance?.Logger.LogError($"GameManager_SetPlayerDataBool_Postfix error: {e}");
                }
            }

            // 检测玩家死亡
            [HarmonyPostfix]
            [HarmonyPatch(typeof(HeroController), "Die")]
            private static void HeroController_Die_Postfix()
            {
                try
                {
                    var inst = Instance;
                    if (inst != null && inst._f2OverrideActive)
                    {
                        inst._playerJustRespawned = true;
                        Instance?.D("Hook: HeroController.Die -> player died, marking for F2 teleport on respawn");
                    }
                }
                catch { /* ignore */ }
            }

            // F2 覆盖：复活完成后传送到记录位置（仅在死亡复活后的第一次）
            [HarmonyPostfix]
            [HarmonyPatch(typeof(GameManager), "FinishedEnteringScene")]
            private static void GM_FinishedEnteringScene_Postfix()
            {
                try
                {
                    var inst = Instance;
                    if (inst != null && inst._f2OverrideActive && inst._playerJustRespawned && !string.IsNullOrEmpty(inst._repositionScene))
                    {
                        // 重置复活标记，确保只传送一次
                        inst._playerJustRespawned = false;

                        // 启动延迟传送协程，等待玩家完全复活后再传送
                        Instance?.StartCoroutine(Instance.DelayedF2TeleportAfterRespawn());
                        Instance?.D($"Hook: FinishedEnteringScene -> player respawned, starting delayed F2 teleport to {inst._repositionScene} @ {inst._repositionPos}");
                    }
                    else if (inst != null && inst._f2OverrideActive)
                    {
                        Instance?.D("Hook: FinishedEnteringScene -> F2 active but player didn't just respawn, skipping teleport");
                    }

                    // 进入存档后：先处理旧格式，再（若为中文）本地化刷新，最后再做UI预热
                    var instPrewarm = Instance;
                    if (instPrewarm != null)
                    {
                        // 1) 旧格式迁移 + 2) 本地化刷新（进入存档后才会写配置）
                        if (!instPrewarm._postEnterNormalized)
                        {
                            instPrewarm._postEnterNormalized = true;
                            instPrewarm.StartCoroutine(instPrewarm.PostEnterNormalizeRoutine());

                            // 在进入存档后再启动“延迟本地化刷新”，确保不会在主菜单阶段写配置
                            instPrewarm._needsLocalizedRefresh = true;
                            if (!instPrewarm._refreshRoutineRunning)
                            {
                                instPrewarm.StartCoroutine(instPrewarm.DelayedLocalizedNameRefreshRoutine());
                            }
                        }

                        // 3) UI预热放在最后，避免中文环境仍显示英文
                        if (!instPrewarm._uiPrewarmed)
                        {
                            instPrewarm.StartCoroutine(instPrewarm.PrewarmUIRoutine());
                        }
                    }

                    if (instPrewarm != null)
                    {

                        // 可选：首次进入存档时导入所有椅子到配置
                        if (instPrewarm._importAllOnEnter != null && instPrewarm._importAllOnEnter.Value && !instPrewarm._importedAllOnce)
                        {
                            instPrewarm._importedAllOnce = true;
                            instPrewarm.ImportAllBenchesFromMap();
                        }
                    }

                }


                catch { /* ignore */ }
            }

            // 监听InventoryMapManager的OnPaneStart，确保地图状态正确
            [HarmonyPostfix]
            [HarmonyPatch(typeof(InventoryMapManager), "OnPaneStart")]
            private static void InventoryMapManager_OnPaneStart_Postfix(InventoryMapManager __instance)
            {
                try
                {
                    if (Instance != null)
                    {
                        // 每次打开地图面板时都检查并重置缩放状态，防止传送后地图仍然处于缩放状态
                        Instance.D("Hook: InventoryMapManager.OnPaneStart - 检查地图缩放状态");

                        // 确保地图不处于缩放状态 - 缩小到世界地图视图
                        var isZoomedField = typeof(InventoryMapManager).GetField("isZoomed", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (isZoomedField != null)
                        {
                            bool isCurrentlyZoomed = (bool)(isZoomedField.GetValue(__instance) ?? false);
                            if (isCurrentlyZoomed)
                            {
                                Instance.D("Hook: 地图处于缩放状态，正在缩小到世界地图");
                                try
                                {
                                    // 使用延迟缩放，避免与地图初始化冲突 - 已注释
                                    // Instance.StartCoroutine(Instance.DelayedZoomOutOnPaneStart(__instance));
                                    // 直接设置为false，不使用延迟缩放
                                    isZoomedField.SetValue(__instance, false);
                                }
                                catch (Exception e)
                                {
                                    Instance.D($"Hook: 启动延迟缩放时出错: {e.Message}");
                                    isZoomedField.SetValue(__instance, false);
                                }
                            }
                            else
                            {
                                Instance.D("Hook: 地图已处于世界地图视图");
                            }
                        }

                        // 获取GameMap并确保状态正确
                        var gameMapField = typeof(InventoryMapManager).GetField("gameMap", BindingFlags.NonPublic | BindingFlags.Instance);
                        var gameMap = gameMapField?.GetValue(__instance) as GameMap;
                        if (gameMap != null)
                        {
                            gameMap.SetIsZoomed(false);

                            Instance.D("Hook: GameMap状态已重置");
                        }
                    }
                }
                catch (Exception e)
                {
                    Instance?.Logger.LogError($"InventoryMapManager_OnPaneStart_Postfix error: {e}");
                }
            }

            // 修复手柄退出键与使用丝键冲突的问题 - 在输入处理的更早阶段拦截
            [HarmonyPrefix]
            [HarmonyPatch(typeof(InputHandler), "Update")]
            private static void InputHandler_Update_Prefix(InputHandler __instance)
            {
                try
                {
                    // 如果椅子传送UI正在显示，清除SuperDash的WasPressed状态
                    if (Instance != null && Instance._uiOpen && __instance.inputActions != null)
                    {
                        var superDashAction = __instance.inputActions.SuperDash;
                        if (superDashAction != null && superDashAction.WasPressed)
                        {
                            // 使用反射清除按键状态，防止误触发使用丝
                            try
                            {
                                var actionType = superDashAction.GetType();
                                var wasPressedField = actionType.GetField("wasPressed", BindingFlags.NonPublic | BindingFlags.Instance);
                                if (wasPressedField != null)
                                {
                                    wasPressedField.SetValue(superDashAction, false);
                                    Instance.D("Hook: InputHandler - 已清除SuperDash按键状态，避免误触发使用丝");
                                }
                            }
                            catch (Exception e)
                            {
                                Instance.D($"Hook: InputHandler - 清除SuperDash按键状态失败: {e.Message}");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Instance?.Logger.LogError($"InputHandler_Update_Prefix error: {e}");
                }
            }



            // 椅子识别辅助方法：判断是否是有效的椅子复活点设置
            private static bool IsValidBenchRespawn(RespawnMarker spawnMarker, string sceneName, int spawnType)
            {
                try
                {
                    // 基本检查
                    if (spawnMarker == null || string.IsNullOrEmpty(sceneName)) return false;

                    // 仅使用白名单判断
                    return Instance != null && spawnMarker != null && Instance.IsKnownBenchMarker(sceneName, spawnMarker.name);
                }
                catch
                {
                    return true; // 出错时保守处理，认为是有效的
                }
            }

            // 旧有的椅子组件/关键词判断逻辑已移除，仅使用白名单判断

            // 按名称验证椅子的辅助方法
            private static bool IsValidBenchRespawnByName(string spawnMarker, string sceneName)
            {
                try
                {
                    // 基本检查
                    if (string.IsNullOrEmpty(spawnMarker) || string.IsNullOrEmpty(sceneName)) return false;

                    // 仅使用白名单判断
                    return Instance != null && Instance.IsKnownBenchMarker(sceneName, spawnMarker);
                }
                catch
                {
                    return false;
                }
            }
        }
        #endregion

        #region Smart Teleport Methods

        /// <summary>
        /// 获取椅子的实际位置
        /// </summary>
        private Vector3 GetBenchPosition(string sceneName, string markerName)
        {
            try
            {
                // 如果在当前场景，直接查找RespawnMarker
                string currentScene = SceneManager.GetActiveScene().name;
                if (currentScene == sceneName)
                {
                    return GetBenchPositionInCurrentScene(markerName);
                }

                // 如果不在当前场景，返回特殊值表示需要跨场景传送
                D($"椅子不在当前场景: {sceneName}|{markerName}");
                return Vector3.one; // 使用Vector3.one作为标记，表示需要跨场景传送
            }
            catch (Exception e)
            {
                Logger.LogError($"GetBenchPosition error: {e}");
                return Vector3.zero;
            }
        }

        /// <summary>
        /// 在当前场景中查找椅子的实际位置
        /// </summary>
        private Vector3 GetBenchPositionInCurrentScene(string markerName)
        {
            try
            {
                BuildMarkerCache();
                var list = _markerList;
                if (list != null && list.Count > 0)
                {
                    RespawnMarker exact = null;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var m = list[i]; if (!m) continue;
                        var n = m.name;
                        if (n == markerName) { exact = m; break; }
                    }
                    if (exact != null)
                    {
                        D($"在当前场景选择椅子标记(精确匹配): {exact.name} at {exact.transform.position}");
                        return exact.transform.position;
                    }
                }

                D($"在当前场景找不到椅子标记: {markerName}");
                return Vector3.zero;
            }
            catch (Exception e)
            {
                Logger.LogError($"GetBenchPositionInCurrentScene error: {e}");
                return Vector3.zero;
            }
        }

        /// <summary>
        /// 跨场景传送到特定椅子标记 - 使用两步传送法
        /// </summary>
        private IEnumerator TeleportWithSceneChangeToMarker(string targetScene, string markerName)
        {
            var gm = GameManager.instance;
            if (gm == null) yield break;

            D($"开始跨场景传送到椅子: {targetScene}|{markerName}");

            // 第一步：传送到目标场景
            D("第一步：传送到目标场景");
            yield return StartCoroutine(TeleportToScene(targetScene));

            // 等待场景稳定
            yield return new WaitForSeconds(0.8f);

            // 第二步：在目标场景中传送到椅子位置
            D("第二步：传送到椅子位置");
            var hc = HeroController.instance;
            if (hc != null)
            {
                // 强制确保玩家可以接受传送，即使正在移动或有UI打开
                bool wasAcceptingInput = hc.acceptingInput;
                if (!wasAcceptingInput)
                {
                    D("玩家当前不接受输入（可能在移动或有UI），强制启用输入以确保传送成功");
                    try
                    {
                        // 临时启用输入接受，确保传送能够执行
                        hc.acceptingInput = true;
                    }
                    catch (Exception e)
                    {
                        D($"强制启用输入失败: {e}");
                    }
                }

                // 在目标场景中查找椅子的实际位置
                Vector3 benchPosition = GetBenchPositionInCurrentScene(markerName);
                if (benchPosition == Vector3.zero)
                {
                    D($"无法在目标场景找到椅子位置: {markerName}，尝试多种查找方式");

                    // 尝试更多查找方式
                    benchPosition = FindBenchPositionByMultipleMethods(markerName);

                    if (benchPosition == Vector3.zero)
                    {
                        D($"所有方法都无法找到椅子位置: {markerName}");
                        // 恢复原始输入状态
                        if (!wasAcceptingInput)
                        {
                            try { hc.acceptingInput = wasAcceptingInput; } catch { }
                        }
                        yield break;
                    }
                }

                D($"在目标场景找到椅子位置: {markerName} at {benchPosition}");

                // 传送到椅子位置
                PerformDirectTeleport(benchPosition);

                // 等待传送完成
                yield return new WaitForSeconds(0.2f);

                // 恢复原始输入状态
                if (!wasAcceptingInput)
                {
                    try
                    {
                        hc.acceptingInput = wasAcceptingInput;
                        D("已恢复原始输入状态");
                    }
                    catch (Exception e)
                    {
                        D($"恢复输入状态失败: {e}");
                    }
                }

                D($"跨场景椅子传送完成: {targetScene}|{markerName}");
            }
        }

        /// <summary>
        /// 传送到指定场景（第一步传送）
        /// </summary>
        private IEnumerator TeleportToScene(string targetScene)
        {
            var gm = GameManager.instance;
            if (gm == null) yield break;

            // 使用场景入口点传送到目标场景
            string entryGate = GetSafeEntryPointForScene(targetScene);
            D($"使用入口点传送到场景: {targetScene}, 入口: {entryGate}");

            // 使用GameManager的场景切换功能
            gm.BeginSceneTransition(new GameManager.SceneLoadInfo
            {
                SceneName = targetScene,
                EntryGateName = entryGate,
                PreventCameraFadeOut = false,
                WaitForSceneTransitionCameraFade = true,
                Visualization = GameManager.SceneLoadVisualizations.Default,
                AlwaysUnloadUnusedAssets = false
            });

            // 等待场景切换完成
            float timeout = Time.unscaledTime + 15f;
            while (Time.unscaledTime < timeout)
            {
                if (SceneManager.GetActiveScene().name == targetScene)
                {
                    D($"场景切换完成: {targetScene}");
                    break;
                }
                yield return null;
            }

            // 等待玩家控制器就绪
            yield return new WaitForSeconds(0.5f);
        }

        /// <summary>
        /// 使用多种方法查找椅子位置（仅基于 RestBench）
        /// </summary>
        private Vector3 FindBenchPositionByMultipleMethods(string markerName)
        {
            try
            {
                D($"使用多种方法查找椅子位置: {markerName}");

                // 先尝试通过 RespawnMarker 名称直接定位（使用缓存字典）
                BuildMarkerCache();
                if (_markerByName != null && _markerByName.TryGetValue(markerName, out var exact) && exact)
                {
                    D($"通过名称直接找到椅子位置: {exact.name} at {exact.transform.position}");
                    return exact.transform.position;
                }

                // 仅通过 RestBench 附近的 RespawnMarker 进行匹配（避免LINQ）
                var restBenches = FindObjectsByType<RestBench>(FindObjectsSortMode.None);
                var list = _markerList;
                if (list != null)
                {
                    float maxSq = 15f * 15f;
                    foreach (var bench in restBenches)
                    {
                        if (bench == null) continue;
                        var bpos = bench.transform.position;
                        for (int i = 0; i < list.Count; i++)
                        {
                            var m = list[i]; if (!m) continue;
                            if ((m.transform.position - bpos).sqrMagnitude > maxSq) continue;
                            var n = m.name;
                            if (n == markerName || n.Contains(markerName) || markerName.Contains(n))
                            {
                                D($"通过RestBench找到椅子位置: {m.name} at {m.transform.position}");
                                return m.transform.position;
                            }
                        }
                    }
                }

                D("未找到匹配的椅子位置");
                return Vector3.zero;
            }
            catch (Exception e)
            {
                Logger.LogError($"FindBenchPositionByMultipleMethods error: {e}");
                return Vector3.zero;
            }
        }

        /// <summary>
        /// 智能选择场景的安全入口点
        /// </summary>
        private string GetSafeEntryPointForScene(string sceneName)
        {
            try
            {
                BuildTransitionCache();
                var tps = _tpList;
                if (tps == null || tps.Count == 0)
                {
                    D("当前场景没有TransitionPoint，使用默认入口");
                    return "";
                }

                string door = null, direction = null, any = null;
                for (int i = 0; i < tps.Count; i++)
                {
                    var tp = tps[i]; if (tp == null || tp.isInactive) continue;
                    var name = tp.name ?? string.Empty;
                    if (any == null) any = name;
                    if (door == null && name.Contains("door")) door = name;
                    if (direction == null && (name.Contains("left") || name.Contains("right") || name.Contains("top") || name.Contains("bot"))) direction = name;
                }

                if (door != null) { D($"找到门入口点: {door}"); return door; }
                if (direction != null) { D($"找到方向入口点: {direction}"); return direction; }
                if (any != null) { D($"使用第一个可用入口点: {any}"); return any; }

                D("未找到任何可用入口点，使用空字符串");
                return "";
            }
            catch
            {
                // Logger.LogError($"GetSafeEntryPointForScene error: {e}");
                return "";
            }
        }

        /// <summary>
        /// 执行直接传送
        /// </summary>
        private void PerformDirectTeleport(Vector3 targetPosition)
        {
            try
            {
                var hc = HeroController.instance;
                var pd = PlayerData.instance;
                if (hc == null || pd == null)
                {
                    // Logger.LogWarning("HeroController 或 PlayerData 未找到，无法执行传送");
                    return;
                }

                // 检查并处理椅子状态
                if (pd.atBench)
                {
                    D("玩家在椅子上，先让其起来");
                    ForceGetOffBench();
                    // 等待一帧让状态更新
                    StartCoroutine(DelayedTeleport(targetPosition));
                    return;
                }

                // 执行实际传送
                DoDirectTeleport(targetPosition);
            }
            catch
            {
                // Logger.LogError($"PerformDirectTeleport error: {e}");
            }
        }

        /// <summary>
        /// 强制玩家从椅子上起来
        /// </summary>
        private void ForceGetOffBench()
        {
            try
            {
                var hc = HeroController.instance;
                var pd = PlayerData.instance;

                if (hc != null && pd != null && pd.atBench)
                {
                    // 设置不在椅子上
                    pd.atBench = false;

                    // 恢复重力影响
                    hc.AffectedByGravity(true);

                    // 恢复控制
                    hc.RegainControl();
                    hc.StartAnimationControlToIdle();

                    // 查找并结束椅子交互
                    var restBenchHelpers = FindObjectsByType<RestBenchHelper>(FindObjectsSortMode.None);
                    foreach (var helper in restBenchHelpers)
                    {
                        if (helper != null)
                        {
                            helper.SetOnBench(false);
                        }
                    }

                    D("已强制玩家从椅子上起来");
                }
            }
            catch
            {
                // Logger.LogError($"ForceGetOffBench error: {e}");
            }
        }

        /// <summary>
        /// 延迟传送协程
        /// </summary>
        private IEnumerator DelayedTeleport(Vector3 targetPosition)
        {
            yield return null; // 等待一帧
            DoDirectTeleport(targetPosition);
        }

        /// <summary>
        /// F2专用：复活后延迟传送到记录位置
        /// </summary>
        private IEnumerator DelayedF2TeleportAfterRespawn()
        {
            D("DelayedF2TeleportAfterRespawn: 等待玩家完全复活");

            // 等待1秒，确保复活流程完成
            yield return new WaitForSeconds(1.0f);

            var hc = HeroController.instance;
            var pd = PlayerData.instance;

            if (hc == null || pd == null)
            {
                D("DelayedF2TeleportAfterRespawn: PlayerData or HeroController not found");
                yield break;
            }

            // 如果玩家在椅子上，强制起身
            if (pd.atBench)
            {
                D("DelayedF2TeleportAfterRespawn: 玩家在椅子上，强制起身");
                ForceGetOffBench();
                yield return new WaitForSeconds(0.5f); // 等待起身完成
            }

            // 执行F2传送
            var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            D($"DelayedF2TeleportAfterRespawn: 当前场景={currentScene}，目标场景={_repositionScene}");

            if (currentScene == _repositionScene)
            {
                // 同场景传送：直接传送到记录位置
                D($"DelayedF2TeleportAfterRespawn: 同场景传送到 {_repositionPos}");
                DoDirectTeleport(_repositionPos);
            }
            else
            {
                // 跨场景传送：切换到目标场景并传送到记录位置
                D($"DelayedF2TeleportAfterRespawn: 跨场景传送 {currentScene} -> {_repositionScene}");
                yield return StartCoroutine(TeleportWithSceneChange(_repositionScene, _repositionPos));
            }

            D("DelayedF2TeleportAfterRespawn: F2传送完成");
        }

        /// <summary>
        /// F2专用延迟传送协程，等待2秒确保玩家复活完成后再传送
        /// </summary>
        private IEnumerator DelayedF2Teleport(Vector3 targetPosition, bool sameScene, string targetScene = null)
        {
            D("DelayedF2Teleport: 等待2秒确保玩家复活完成");

            // 等待2秒，确保复活流程完成
            yield return new WaitForSeconds(2.0f);

            var hc = HeroController.instance;
            var pd = PlayerData.instance;

            if (hc == null || pd == null)
            {
                // Logger.LogError("DelayedF2Teleport: PlayerData or HeroController not found after 2s wait");
                yield break;
            }

            // 如果玩家在椅子上，强制起身
            if (pd.atBench)
            {
                D("DelayedF2Teleport: 2秒后发现玩家在椅子上，强制起身");
                ForceGetOffBench();
                yield return new WaitForSeconds(0.5f); // 等待起身完成
            }
            else
            {
                D("DelayedF2Teleport: 2秒后玩家不在椅子上，直接传送");
            }

            // 第三阶段：执行传送
            var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            D($"DelayedF2Teleport: 开始执行F2传送，当前场景={currentScene}，目标场景={targetScene}");

            if (!string.IsNullOrEmpty(targetScene) && currentScene != targetScene)
            {
                // 跨场景传送：需要切换到目标场景
                D($"DelayedF2Teleport: 执行跨场景传送 {currentScene} -> {targetScene}");
                yield return StartCoroutine(TeleportWithSceneChange(targetScene, targetPosition));
                D($"DelayedF2Teleport: 跨场景传送完成 -> {targetScene} @ {targetPosition}");
            }
            else if (!string.IsNullOrEmpty(targetScene) && currentScene == targetScene)
            {
                // 同场景直接传送
                D($"DelayedF2Teleport: 执行同场景直接传送");
                DoDirectTeleport(targetPosition);
                D($"DelayedF2Teleport: 同场景传送完成 -> {targetPosition}");
            }
            else
            {
                // Logger.LogError($"DelayedF2Teleport: 无效的传送参数，targetScene={targetScene}, currentScene={currentScene}");
            }
        }

        /// <summary>
        /// 执行实际的直接传送（带安全落点校正）
        /// </summary>
        private void DoDirectTeleport(Vector3 targetPosition)
        {
            try
            {
                var hc = HeroController.instance;
                if (hc == null) return;

                // 1) 优先使用游戏内置的地面吸附，获得更可靠的落点
                Vector3 desired = targetPosition;
                try
                {
                    // 与 RepositionAfterEnter 中一致的做法
                    var ground = hc.FindGroundPoint((Vector2)targetPosition, true);
                    if (ground != Vector3.zero)
                        desired = ground;
                }
                catch { /* 忽略，继续用原始坐标 */ }

                // 2) 基于碰撞盒做一次安全性检查，必要时微调至附近安全点
                if (!IsPositionSafe(desired))
                {
                    var alt = FindSafePositionNearby(desired);
                    if (alt != Vector3.zero)
                        desired = alt;
                    else
                        desired += new Vector3(0f, 2f, 0f); // 兜底：向上抬一点，避免卡入
                }

                // 设置位置
                hc.transform.position = desired;

                // 清除物理状态
                var rb = hc.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    rb.linearVelocity = Vector2.zero;
                }

                // 清除角色状态
                if (hc.cState != null)
                {
                    hc.cState.recoiling = false;
                    hc.cState.transitioning = false;
                }

                // 修正相机位置
                try
                {
                    GameCameras.instance?.cameraController?.PositionToHeroInstant(true);
                }
                catch { /* ignore */ }

                // 3) 传送后稍作延迟再复检一次，进一步避免个别机型/帧同步导致的卡入
                StartCoroutine(PostTeleportSafetyAdjust(desired));

                // 4) 确保输入系统状态正常，防止UI卡死
                StartCoroutine(EnsureInputSystemHealthy());

                D($"直接传送完成: {desired}");
            }
            catch
            {
                // Logger.LogError($"DoDirectTeleport error: {e}");
            }
        }


        // 传送后安全复检：若发现碰撞重叠，尝试微调到附近安全点
        private IEnumerator PostTeleportSafetyAdjust(Vector3 originalTarget)
        {
            yield return new WaitForSeconds(0.1f);
            try
            {
                var hc = HeroController.instance;
                if (hc == null) yield break;

                if (!IsPositionSafe(hc.transform.position))
                {
                    var alt = FindSafePositionNearby(hc.transform.position);
                    if (alt != Vector3.zero)
                    {
                        hc.transform.position = alt;
                        var rb = hc.GetComponent<Rigidbody2D>();
                        if (rb != null) rb.linearVelocity = Vector2.zero;
                        try { GameCameras.instance?.cameraController?.PositionToHeroInstant(true); } catch { }
                        D($"PostTeleportSafetyAdjust: 位置不安全，修正到 {alt}");
                    }
                }
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// 确保输入系统健康，防止传送后UI卡死
        /// </summary>
        private IEnumerator EnsureInputSystemHealthy()
        {
            yield return new WaitForSeconds(0.2f);

            try
            {
                var ih = InputHandler.Instance;
                var gm = GameManager.instance;
                var hc = HeroController.instance;

                if (ih != null && gm != null && hc != null)
                {
                    // 检查输入系统是否正常
                    if (!ih.acceptingInput && gm.GameState == GameState.PLAYING)
                    {
                        D("EnsureInputSystemHealthy: 输入系统异常，尝试恢复");

                        // 尝试恢复输入
                        ih.StartAcceptingInput();

                        // 确保UI输入也正常
                        if (gm.ui != null)
                        {
                            ih.StartUIInput();
                        }

                        D("EnsureInputSystemHealthy: 输入系统已恢复");
                    }

                    // 确保玩家控制器状态正常
                    if (hc.acceptingInput != ih.acceptingInput)
                    {
                        D("EnsureInputSystemHealthy: 同步玩家控制器输入状态");
                        if (ih.acceptingInput)
                        {
                            hc.AcceptInput();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"EnsureInputSystemHealthy error: {e}");
            }
        }

        // 使用主角碰撞盒检测当前位置是否与地形重叠
        private bool IsPositionSafe(Vector3 position)
        {
            try
            {
                var hc = HeroController.instance;
                var col = hc ? hc.GetComponent<Collider2D>() : null;
                if (col == null) return true; // 无碰撞体则视为安全
                int mask = LayerMask.GetMask("Terrain");
                var hit = Physics2D.OverlapBox(position, col.bounds.size, 0f, mask);
                return hit == null;
            }
            catch { return true; }
        }

        // 在原目标附近寻找一个可行的安全位置（优先向上，其次左右）
        private Vector3 FindSafePositionNearby(Vector3 origin)
        {
            try
            {
                var hc = HeroController.instance;
                var col = hc ? hc.GetComponent<Collider2D>() : null;
                if (col == null) return Vector3.zero;
                int mask = LayerMask.GetMask("Terrain");

                // 偏移序列：向上、再更高、再小幅左右、最后左右平移
                Vector3[] offsets = new Vector3[]
                {
                    new Vector3(0f, 2f, 0f),
                    new Vector3(0f, 4f, 0f),
                    new Vector3(0f, 1f, 0f),
                    new Vector3(-1f, 2f, 0f),
                    new Vector3(1f, 2f, 0f),
                    new Vector3(-2f, 0f, 0f),
                    new Vector3(2f, 0f, 0f),
                };

                foreach (var off in offsets)
                {
                    var p = origin + off;
                    var hit = Physics2D.OverlapBox(p, col.bounds.size, 0f, mask);
                    if (hit == null) return p;
                }
            }
            catch { }
            return Vector3.zero;
        }

        /// <summary>
        /// 使用场景切换进行传送
        /// </summary>
        private IEnumerator TeleportWithSceneChange(string targetScene, Vector3 targetPosition, string entryPointName = null)
        {
            var gm = GameManager.instance;
            if (gm == null)
            {
                // Logger.LogError("GameManager not found for scene change teleport");
                yield break;
            }

            D($"开始场景切换传送: {targetScene}, 入口: {entryPointName ?? "auto"}");

            // 如果没有指定入口点，智能选择
            if (string.IsNullOrEmpty(entryPointName))
            {
                entryPointName = GetSafeEntryPointForScene(targetScene);
            }

            // 执行场景切换
            try
            {
                gm.BeginSceneTransition(new GameManager.SceneLoadInfo
                {
                    SceneName = targetScene,
                    EntryGateName = entryPointName,
                    PreventCameraFadeOut = false,
                    WaitForSceneTransitionCameraFade = true,
                    Visualization = GameManager.SceneLoadVisualizations.Default,
                    AlwaysUnloadUnusedAssets = false
                });
            }
            catch
            {
                // Logger.LogError($"BeginSceneTransition error: {e}");
                yield break;
            }

            // 等待场景切换完成
            float timeout = Time.unscaledTime + 10f;
            while (Time.unscaledTime < timeout)
            {
                if (gm.sceneName == targetScene && HeroController.instance != null && HeroController.instance.acceptingInput)
                {
                    break;
                }
                yield return null;
            }

            // F2传送总是需要精确定位到记录的位置
            if (targetPosition != Vector3.zero)
            {
                yield return new WaitForSeconds(0.2f); // 等待场景完全稳定
                D($"TeleportWithSceneChange: 执行精确定位到 {targetPosition}");
                DoDirectTeleport(targetPosition);
            }
            else
            {
                // Logger.LogWarning("TeleportWithSceneChange: targetPosition is zero, skipping precise positioning");
            }

            D($"场景切换传送完成: {targetScene}");
        }

        /// <summary>
        /// 延迟检查椅子，当玩家坐下时自动记录椅子
        /// </summary>
        private IEnumerator DelayedBenchCheck()
        {
            yield return new WaitForSeconds(0.5f); // 等待游戏状态稳定

            try
            {
                var pd = PlayerData.instance;
                var gm = GameManager.instance;

                if (pd == null || gm == null) yield break;

                // 确认玩家确实在椅子上
                if (!pd.atBench)
                {
                    D("DelayedBenchCheck: Player not on bench, skipping");
                    yield break;
                }

                string currentScene = gm.sceneName;
                string respawnScene = pd.respawnScene;
                string respawnMarker = pd.respawnMarkerName;

                D($"DelayedBenchCheck: Player on bench, scene={currentScene}, respawnScene={respawnScene}, respawnMarker={respawnMarker}");

                // 检查是否有有效的复活点信息
                if (!string.IsNullOrEmpty(respawnScene) && !string.IsNullOrEmpty(respawnMarker))
                {
                    // 检查椅子是否已经在列表中（避免 LINQ 分配）
                    bool exists = false; for (int i = 0; i < _benches.Count; i++) { var b = _benches[i]; if (b.Scene == respawnScene && b.Marker == respawnMarker) { exists = true; break; } }
                    if (!exists)
                    {
                        D($"DelayedBenchCheck: Adding new bench {respawnScene}|{respawnMarker}");
                        AddBench(respawnScene, respawnMarker);
                        ShowTip(GetText("椅子已记录", "Bench recorded"), 1.5f);
                    }
                    else
                    {
                        D($"DelayedBenchCheck: Bench {respawnScene}|{respawnMarker} already exists");
                    }
                }
                else
                {
                    D("DelayedBenchCheck: No valid respawn info found");
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"DelayedBenchCheck error: {e}");
            }
        }

        /// <summary>
        /// 延迟添加椅子到列表，等待玩家真正坐下
        /// </summary>
        private IEnumerator DelayedBenchAdd(string scene, string marker, float delay)
        {
            yield return new WaitForSeconds(delay);

            try
            {
                var pd = PlayerData.instance;
                var hc = HeroController.instance;

                // 检查玩家是否真的坐下了
                if (pd != null && hc != null && (pd.atBench || hc.cState.nearBench))
                {
                    D($"DelayedBenchAdd: Player confirmed on bench, adding {scene}|{marker}");
                    AddBench(scene, marker);
                    ShowTip(GetText("椅子已记录", "Bench recorded"), 1.5f);
                }
                else
                {
                    D($"DelayedBenchAdd: Player not on bench, skipping {scene}|{marker}");
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"DelayedBenchAdd error: {e}");
            }
        }

        #endregion

        /// <summary>
        /// 模拟按下取消键（A键）
        /// </summary>
        private IEnumerator SimulateCancelKey()
        {
            D("SimulateCancelKey: 开始模拟按下取消键");

            var ih = InputHandler.Instance;
            if (ih == null || ih.inputActions == null)
            {
                D("SimulateCancelKey: InputHandler 或 inputActions 为空");
                yield break;
            }

            // 查找当前的地图管理器
            var invMgr = UnityEngine.Object.FindFirstObjectByType<InventoryMapManager>();
            if (invMgr != null)
            {
                D("SimulateCancelKey: 找到InventoryMapManager，发送UI CANCEL事件");

                // 方法1: 直接调用地图管理器的取消方法
                bool paneMovePreventedSuccess = false;
                try
                {
                    invMgr.PaneMovePrevented(); // 这会发送 "UI CANCEL" 事件
                    paneMovePreventedSuccess = true;
                    D("SimulateCancelKey: 成功调用PaneMovePrevented");
                }
                catch (Exception e)
                {
                    D($"SimulateCancelKey: 调用PaneMovePrevented失败: {e.Message}");
                }

                // 方法2: 如果地图已放大，尝试缩小
                bool zoomOutSuccess = false;
                try
                {
                    // 使用反射检查是否已放大
                    var isZoomedField = typeof(InventoryMapManager).GetField("isZoomed", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (isZoomedField != null)
                    {
                        bool isZoomed = (bool)isZoomedField.GetValue(invMgr);
                        if (isZoomed)
                        {
                            D("SimulateCancelKey: 地图已放大，执行缩小");
                            invMgr.ZoomOut();
                            zoomOutSuccess = true;
                        }
                        else
                        {
                            D("SimulateCancelKey: 地图未放大");
                        }
                    }
                }
                catch (Exception e)
                {
                    D($"SimulateCancelKey: 检查/执行地图缩放失败: {e.Message}");
                }

                if (!paneMovePreventedSuccess && !zoomOutSuccess)
                {
                    Logger.LogWarning("SimulateCancelKey: 所有模拟按键方法都失败了");
                }
            }
            else
            {
                D("SimulateCancelKey: 未找到InventoryMapManager");
            }

            // 等待一小段时间让操作生效
            yield return new WaitForSecondsRealtime(0.1f);
            D("SimulateCancelKey: 模拟按键完成");
        }


        // ========================= Fullscreen Map Display helpers =========================
        private void StartInventoryMapPreview(BenchRef bench)
        {
            try
            {
                if (_mapPreviewRoutine != null) return;
                _isF3MapPreviewActive = true; // 标记F3地图预览开始
                _mapPreviewRoutine = StartCoroutine(CoStartInventoryMapPreview(bench));
            }
            catch { /* ignore */ }
        }

        private IEnumerator CoStartInventoryMapPreview(BenchRef bench)
        {
            var gm = GameManager.instance;
            var pd = PlayerData.instance;
            if (gm == null || pd == null)
            {
                _isF3MapPreviewActive = false; // 确保清理标志
                yield break;
            }
            if (!pd.HasAnyMap)
            {
                _isF3MapPreviewActive = false; // 确保清理标志
                yield break;
            }

            _inventoryMapOpenForPreview = false;


            // 直接使用官方背包地图路径（全屏）
            InventoryMapManager invMgr = null;
            gm.SetIsInventoryOpen(true);
            var paneList = UnityEngine.Object.FindFirstObjectByType<InventoryPaneList>();
            if (paneList != null)
            {
                try { paneList.SetCurrentPane((int)InventoryPaneList.PaneTypes.Map, null); } catch { }
            }
            float waited = 0f; const float maxWait = 1.0f; // 最长等待 1 秒
            while (waited < maxWait && (invMgr = UnityEngine.Object.FindFirstObjectByType<InventoryMapManager>()) == null)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (invMgr == null)
            {
                gm.SetIsInventoryOpen(false);
                _isF3MapPreviewActive = false; // 确保清理标志
                yield break;
            }
            // 确保地图对象已生成
            try { invMgr.EnsureMapsSpawned(); } catch { }
            var gmField = typeof(InventoryMapManager).GetField("gameMap", BindingFlags.NonPublic | BindingFlags.Instance);
            var gameMap = gmField?.GetValue(invMgr) as GameMap;
            if (gameMap == null)
            {
                gm.SetIsInventoryOpen(false);
                _isF3MapPreviewActive = false; // 确保清理标志
                yield break;
            }

            _inventoryMapOpenForPreview = true;
            gameMap.OverrideMapZoneFromScene(bench.Scene);
            // 立即放大到场景图
            invMgr.ZoomIn(MapZone.NONE, false);
            yield return null;
            try { gameMap.SetupMapMarkers(); } catch { }


            // 定位并将目标椅子居中 - 增强版本，支持附近椅子查找
            try
            {
                var activePins = Traverse.Create<MapPin>().Field("_activePins").GetValue<List<MapPin>>();

                MapPin foundPin = null;
                bool foundExactMatch = false;

                // 第一步：尝试找到精确匹配的椅子图钉
                if (activePins != null)
                {
                    foreach (var mapPin in activePins)
                    {
                        if (mapPin == null || !mapPin.IsActive || !mapPin.gameObject.activeSelf) continue;
                        var parentScene = Traverse.Create(mapPin).Field("parentScene").GetValue<GameMapScene>();
                        if (parentScene == null) continue;
                        if (!mapPin.gameObject.name.StartsWith("Bench Pin")) continue;
                        if (!string.Equals(parentScene.Name, bench.Scene, StringComparison.OrdinalIgnoreCase)) continue;

                        foundPin = mapPin;
                        foundExactMatch = true;
                        break;
                    }
                }

                // 第二步：如果没有找到精确匹配，尝试查找附近的椅子图钉
                if (!foundExactMatch && activePins != null)
                {
                    // 获取目标椅子的世界坐标
                    Vector3 benchWorldPos = FindBenchPositionByMultipleMethods(bench.Marker ?? bench.Scene);
                    if (benchWorldPos != Vector3.zero)
                    {
                        float minDistance = float.MaxValue;
                        MapPin nearestPin = null;

                        foreach (var mapPin in activePins)
                        {
                            if (mapPin == null || !mapPin.IsActive || !mapPin.gameObject.activeSelf) continue;
                            if (!mapPin.gameObject.name.StartsWith("Bench Pin")) continue;

                            var parentScene = Traverse.Create(mapPin).Field("parentScene").GetValue<GameMapScene>();
                            if (parentScene == null) continue;

                            // 计算椅子图钉对应的世界坐标
                            Vector3 pinWorldPos = FindBenchPositionByMultipleMethods(parentScene.Name);
                            if (pinWorldPos == Vector3.zero) continue;

                            float distance = Vector3.Distance(benchWorldPos, pinWorldPos);

                            // 如果距离在合理范围内（比如50个单位以内），考虑作为候选
                            if (distance < 50f && distance < minDistance)
                            {
                                minDistance = distance;
                                nearestPin = mapPin;
                            }
                        }

                        if (nearestPin != null)
                        {
                            foundPin = nearestPin;
                        }
                    }
                }

                // 第三步：处理找到的椅子图钉
                if (foundPin != null)
                {
                    Vector3 p = foundPin.transform.localPosition;
                    Transform par = foundPin.transform.parent;
                    while (par != null && !par.gameObject.name.StartsWith("Game_Map_Hornet"))
                    { p = Vector3.Scale(p, par.localScale) + par.localPosition; par = par.parent; }
                    Vector2 targetPosition = new Vector2(p.x, p.y);

                    // 记录并高亮该 Bench Pin（脉冲放大）
                    _highlightedPin = foundPin;
                    _highlightedPinOrigScale = foundPin.transform.localScale;
                    _highlightPulseStart = Time.unscaledTime;

                    // 将地图居中到目标位置
                    try
                    {
                        // GameMap 局部坐标中，视图中心在 -transform.localPosition/scale
                        // 因此要将 pin 点居中，需将 GameMap 的位置设置为该点的相反数（再做边界钳制）
                        var s = gameMap.transform.localScale; var scale2 = new Vector2(s.x, s.y);
                        var desired = new Vector3(-targetPosition.x, -targetPosition.y, gameMap.transform.localPosition.z);
                        var clamped = gameMap.GetClampedPosition(desired, scale2);
                        gameMap.UpdateMapPosition(new Vector2(clamped.x, clamped.y));
                    }
                    catch
                    {
                        // 回退：直接设置为相反数
                        var desired = new Vector2(-targetPosition.x, -targetPosition.y);
                        gameMap.UpdateMapPosition(desired);
                    }
                }

            }
            catch (Exception e)
            {
                Logger.LogError($"地图预览：定位/居中失败：{e}");
            }
        }



        private void StopInventoryMapPreview()
        {
            try
            {
                _isF3MapPreviewActive = false; // 标记F3地图预览结束
                if (_mapPreviewRoutine != null)
                {
                    try { StopCoroutine(_mapPreviewRoutine); } catch { /* ignore */ }
                    _mapPreviewRoutine = null;
                }
                if (_inventoryMapOpenForPreview)
                {
                    StartCoroutine(CoCloseInventoryMap());
                }
                // 还原被高亮的 Bench Pin 缩放
                if (_highlightedPin != null)
                {
                    try { _highlightedPin.transform.localScale = _highlightedPinOrigScale; } catch { }
                    _highlightedPin = null;
                }

                // 修复bug：恢复地图区域到玩家当前所在区域
                // 通过将 currentRegionMapZone 重置为 NONE，让地图回到玩家当前场景的区域
                try
                {
                    var gm = GameManager.instance;
                    if (gm != null && gm.gameMap != null)
                    {
                        // 使用反射重置 currentRegionMapZone 为 NONE
                        var gameMapType = gm.gameMap.GetType();
                        var currentRegionMapZoneField = gameMapType.GetField("currentRegionMapZone",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (currentRegionMapZoneField != null)
                        {
                            currentRegionMapZoneField.SetValue(gm.gameMap, GlobalEnums.MapZone.NONE);
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError($"地图预览：重置地图区域失败：{e}");
                }
            }
            catch { /* ignore */ }
        }



        private IEnumerator CoCloseInventoryMap()
        {
            var invMgr = UnityEngine.Object.FindFirstObjectByType<InventoryMapManager>();
            if (invMgr != null)
            {
                invMgr.ZoomOut();
                float t = 0f; while (t < 0.35f) { t += Time.unscaledDeltaTime; yield return null; }
            }
            var gm = GameManager.instance; if (gm != null) gm.SetIsInventoryOpen(false);
            _inventoryMapOpenForPreview = false;
        }

        private IEnumerator PostCloseCancelGrace()
        {
            var hc = HeroController.instance;
            if (hc != null) hc.AddInputBlocker(this);
            yield return new WaitForSecondsRealtime(0.2f);
            if (hc != null) hc.RemoveInputBlocker(this);
        }

        /// <summary>
        /// 正确关闭地图，按照游戏原版的方式
        /// </summary>
        private void CloseMapProperly()
        {
            try
            {
                D("CloseMapProperly: 开始关闭地图");

                // 首先确保游戏的背包/地图界面完全关闭
                var gm = GameManager.instance;
                var pd = PlayerData.instance;
                if (gm != null && pd != null && pd.isInventoryOpen)
                {
                    D("CloseMapProperly: 关闭游戏背包界面");
                    gm.SetIsInventoryOpen(false);
                }

                // 查找并关闭地图标记菜单
                var mapMarkerMenu = UnityEngine.Object.FindFirstObjectByType<MapMarkerMenu>();
                if (mapMarkerMenu != null)
                {
                    D("CloseMapProperly: 找到MapMarkerMenu，正在关闭");
                    try
                    {
                        // 使用反射调用Close方法
                        var closeMethod = typeof(MapMarkerMenu).GetMethod("Close", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (closeMethod != null)
                        {
                            closeMethod.Invoke(mapMarkerMenu, null);
                            D("CloseMapProperly: MapMarkerMenu已关闭");
                        }
                        else
                        {
                            // 如果没有Close方法，直接禁用GameObject
                            mapMarkerMenu.gameObject.SetActive(false);
                            D("CloseMapProperly: MapMarkerMenu GameObject已禁用");
                        }
                    }
                    catch (Exception e)
                    {
                        D($"CloseMapProperly: 关闭MapMarkerMenu时出错: {e.Message}");
                        // 作为备选方案，直接禁用GameObject
                        try { mapMarkerMenu.gameObject.SetActive(false); } catch { }
                    }
                }

                // 地图预览现在使用游戏原生界面，通过关闭背包界面来关闭地图
                var playerData = PlayerData.instance;
                var gameManager = GameManager.instance;
                if (playerData != null && gameManager != null && playerData.isInventoryOpen)
                {
                    gameManager.SetIsInventoryOpen(false);
                }

                D("CloseMapProperly: 地图已正确关闭");
            }
            catch (Exception e)
            {
                Logger.LogError($"CloseMapProperly error: {e}");
            }
        }

        internal class BenchRef
        {
            public readonly string Scene;
            public readonly string Marker;
            public string DisplayName { get; set; }
            public string ParentCategory { get; set; }
            public bool IsRenamed { get; set; }

            public int AddedOrder { get; set; }


            // 收藏
            public bool IsFavorite { get; set; }
            public int FavoriteOrder { get; set; }

            public BenchRef(string scene, string marker, string displayName = null)
            {
                Scene = scene;
                Marker = marker;
                if (displayName == null)
                {
                    DisplayName = GenerateDisplayName(scene);
                    IsRenamed = false;
                }
                else
                {
                    DisplayName = displayName;
                    IsRenamed = !LooksLikeDefaultNameForScene(scene, displayName);
                }
            }

            public static bool LooksLikeDefaultNameForScene(string scene, string name)
            {
                if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(name)) return false;

                // 新风格默认名：仅包含“#数字”
                var t = name.Trim();
                if (t.Length > 1 && t[0] == '#')
                {
                    var numStr = t.Substring(1).Trim();
                    bool digitsOnly = numStr.Length > 0;
                    for (int i = 0; i < numStr.Length; i++) { if (!char.IsDigit(numStr[i])) { digitsOnly = false; break; } }
                    if (digitsOnly) return true;
                }

                // 兼容老风格默认名前缀（例如 “骸底镇 #1”/英文等）：以下前缀都视为“默认命名”前缀：
                // 1) MapZone 枚举名（GetFriendlySceneName 返回值）
                // 2) 运行期解析到的分类名（ResolveCategoryNameForScene，包含本地化与重命名映射）
                // 3) 仅本地化名（TryGetLocalizedZoneNameForScene）
                var candidates = new List<string>(3);
                try
                {
                    var friendlyEnum = GetFriendlySceneName(scene);
                    if (!string.IsNullOrEmpty(friendlyEnum)) candidates.Add(friendlyEnum);
                }
                catch { }
                try
                {
                    var inst = Instance;
                    if (inst != null)
                    {
                        var desired = inst.ResolveCategoryNameForScene(scene);
                        if (!string.IsNullOrEmpty(desired) && !candidates.Contains(desired)) candidates.Add(desired);
                        var loc = inst.TryGetLocalizedZoneNameForScene(scene);
                        if (!string.IsNullOrEmpty(loc) && !candidates.Contains(loc)) candidates.Add(loc);
                    }
                }
                catch { }

                foreach (var prefix in candidates)
                {
                    if (name.StartsWith(prefix + " #", StringComparison.Ordinal))
                    {
                        var idx = name.LastIndexOf('#');
                        if (idx < 0 || idx + 1 >= name.Length) continue;
                        bool allDigits = true;
                        for (int i = idx + 1; i < name.Length; i++)
                        {
                            if (!char.IsDigit(name[i])) { allDigits = false; break; }
                        }
                        if (allDigits) return true;


                    }
                }
                return false;
            }

            private static string GenerateDisplayName(string scene)
            {
                // 生成友好的显示名称：场景名 + 编号（仅用于默认名显示，不用于“是否重命名”判断）
                var sceneName = GetFriendlySceneName(scene);
                int existingCount = 0;
                var list = Instance?._benches;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var b = list[i];
                        if (GetFriendlySceneName(b.Scene) == sceneName) existingCount++;
                    }
                }
                return $"{sceneName} #{existingCount + 1}";
            }

            // 名称排序辅助：按“前缀 + #数字”进行自然排序
            private static (string prefix, int? number) SplitNameForSort(string name)
            {
                if (string.IsNullOrEmpty(name)) return ("", null);
                int i = name.LastIndexOf('#');
                if (i >= 0 && i + 1 < name.Length)
                {
                    var numStr = name.Substring(i + 1).Trim();
                    if (int.TryParse(numStr, out var n))
                    {
                        var prefix = name.Substring(0, i).TrimEnd();
                        return (prefix, n);
                    }
                }
                return (name, null);
            }

            private static int CompareDisplayNames(string a, string b)
            {
                var sa = SplitNameForSort(a);
                var sb = SplitNameForSort(b);
                int c = string.Compare(sa.prefix, sb.prefix, StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
                if (sa.number.HasValue && sb.number.HasValue)
                    return sa.number.Value.CompareTo(sb.number.Value);
                if (sa.number.HasValue != sb.number.HasValue)
                    return sa.number.HasValue ? 1 : -1;
                return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            }

            private static string GetFriendlySceneName(string scene)
            {
                if (string.IsNullOrEmpty(scene)) return "未知区域";
                try
                {
                    var map = SceneTeleportMap.GetTeleportMap();
                    if (map != null && map.TryGetValue(scene, out var info))
                    {
                        var key = info.MapZone.ToString();
                        // 静态方法内不访问 Language 系统，避免在早期触发其静态初始化
                        return key;
                    }
                }
                catch { /* ignore */ }
                return "未知区域";
            }

            public override string ToString() => DisplayName;

            public static readonly IComparer<BenchRef> Comparer = new BenchComparer();
            private sealed class BenchComparer : IComparer<BenchRef>
            {
                public int Compare(BenchRef x, BenchRef y)
                {
                    if (ReferenceEquals(x, y)) return 0;
                    if (x == null) return 1;
                    if (y == null) return -1;

                    // 统一按显示名称排序（无论是否重命名）
                    int byName = CompareDisplayNames(x.DisplayName ?? string.Empty, y.DisplayName ?? string.Empty);
                    if (byName != 0) return byName;

                    int byScene = string.Compare(x.Scene, y.Scene, StringComparison.Ordinal);
                    if (byScene != 0) return byScene;
                    return string.Compare(x.Marker, y.Marker, StringComparison.Ordinal);
                }
            }
        }
    }
}
