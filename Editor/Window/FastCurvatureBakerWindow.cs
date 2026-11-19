using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// Fast Curvature Baker のウィンドウ。dennokoworks デザインシステムの画面パターン P1 (変換) で組む:
    ///   標準ヘッダー → B1 対象カード → B2 設定カード (ベイク / エッジ / 曲面) → 出力ドック → フッター → ステータス。
    /// 日本語/英語のローカライズに対応。
    /// </summary>
    public sealed class FastCurvatureBakerWindow : EditorWindow
    {
        private const string Title = "Fast Curvature Baker";

        private static string Tr(string ja, string en) => FastCurvatureBakerLoc.Tr(ja, en);
        private static bool IsJapanese => FastCurvatureBakerLoc.IsJapanese;

        // UXML / USS の GUID (.meta と一致させる)。USS はテーマ → ブロック → ツール固有の順に読む
        private const string UXML_GUID       = "0123456789abcdef0123456789abcde6";
        private const string USS_GUID        = "e123456789abcdef0123456789abcde4";
        private const string BLOCKS_USS_GUID = "1e41444802ba42389462cde4cb9f129b";
        private const string TOOL_USS_GUID   = "09531036242140308f50c205e1acc405";

        private const string DropOverClass = "fcb-drop--over";

        [SerializeField] private List<GameObject> _targets = new List<GameObject>();
        [SerializeField] private CurvatureBakeSettings _settings = new CurvatureBakeSettings();
        [SerializeField] private bool _advancedOpen;

        private CancellationTokenSource _cancellation;
        private bool IsBaking => _cancellation != null;

        private DennokoAppBar _appBar;
        private DennokoStatus _status;
        private Label _statusLabel;
        private string _savedStatusText;
        private string _lastSavedPath;

        // B1 対象カード
        private VisualElement _targetCard;
        private VisualElement _targetList;
        private VisualElement _targetEmpty;
        private Label _targetEmptyMessage;
        private VisualElement _targetInfo;
        private Label _targetInfoRenderers;
        private Label _targetInfoOutputs;
        private VisualElement _targetWarning;
        private Label _targetWarningText;
        private Button _targetFixButton;
        private Label _targetHint;
        private Button _targetClearButton;
        private Button _targetFromSelectionButton;
        private List<Renderer> _renderers = new List<Renderer>();
        private List<Mesh> _unreadable = new List<Mesh>();

        // B2 ベイク
        private Label _bakeCardTitle;
        private Button _bakeCardReset;
        private VisualElement _modeRow;
        private Label _modeLabel;
        private VisualElement _modeChoice;
        private Action<int> _selectMode;
        private DropdownField _resolutionField;
        private DropdownField _uvChannelField;
        private VisualElement _qualityRow;
        private Label _qualityLabel;
        private Action<int> _selectQuality;
        private Toggle _supersampleToggle;
        private VisualElement _advancedDetails;
        private Label _advancedTitle;
        private VisualElement _advancedChips;
        private Toggle _sameComponentToggle;
        private Slider _normalRejectionSlider;
        private SliderInt _blurPassesSlider;
        private SliderInt _dilationSlider;

        // B2 エッジ
        private Label _edgeCardTitle;
        private Button _edgeCardReset;
        private FloatField _edgeWidthField;
        private Slider _edgeStrengthSlider;
        private VisualElement _sharpConvexContent;
        private Slider _sharpMinAngleSlider;
        private FloatField _maxBevelWidthField;

        // B2 曲面
        private VisualElement _smoothCard;
        private Label _smoothCardTitle;
        private Button _smoothCardReset;
        private FloatField _radiusField;
        private Slider _strengthSlider;
        private VisualElement _sourceRow;
        private Label _sourceLabel;
        private VisualElement _sourceChoice;
        private Action<int> _selectSource;

        // B5 出力ドック + フッター
        private Label _outputPathLabel;
        private TextField _outputPathField;
        private Label _outputNameLabel;
        private Label _outputName;
        private Toggle _overwriteToggle;
        private Button _bakeButton;

        private DennokoVersionChecker.Result _versionResult = new DennokoVersionChecker.Result
        {
            State = DennokoVersionChecker.State.Checking,
            LocalVersion = "0.0.0"
        };
        private Label _versionLabel;

        [MenuItem("dennokoworks/Fast Curvature Baker")]
        public static void Open()
        {
            var window = GetWindow<FastCurvatureBakerWindow>(Title);
            window.titleContent = new GUIContent(Title);
            window.minSize = new Vector2(360, 480);
        }

        private void OnEnable()
        {
            FastCurvatureBakerLoc.OnLanguageChanged += ApplyLanguage;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private void OnDisable()
        {
            FastCurvatureBakerLoc.OnLanguageChanged -= ApplyLanguage;
            Undo.undoRedoPerformed -= OnUndoRedo;
            _cancellation?.Cancel();
        }

        private void OnUndoRedo()
        {
            if (_bakeButton == null) return;
            SyncSettingsToUI();
            RefreshTargets();
        }

        // 対象の自動入力は「対象が空のときだけ」(blocks/cards.md B1)。指定済みの値は勝手に変えない
        private void OnSelectionChange()
        {
            if (_bakeButton == null || IsBaking) return;
            _targets.RemoveAll(t => t == null);
            if (_targets.Count == 0 && AddGameObjects(Selection.gameObjects, "Add Targets"))
                RefreshTargets();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();

            // テーマ非依存のためのルートクラス。USS ロード失敗時も背景が明るくならないよう Surface0 を保証
            root.AddToClassList("dennoko-root");
            root.style.backgroundColor = (Color)new Color32(0x12, 0x12, 0x12, 0xFF);
            root.style.flexGrow = 1;

            // 標準フォント: OS のメイリオを全体に適用
            DennokoUIFont.Apply(root);

            AddStyleSheet(root, USS_GUID, "DennokoTheme");
            AddStyleSheet(root, BLOCKS_USS_GUID, "DennokoBlocks");
            AddStyleSheet(root, TOOL_USS_GUID, "FastCurvatureBaker");

            var uxml = LoadAsset<VisualTreeAsset>(UXML_GUID, "FastCurvatureBakerWindow t:VisualTreeAsset");
            if (uxml == null)
            {
                root.Add(new Label("UXML Asset が見つかりません。GUID を確認してください。"));
                return;
            }
            uxml.CloneTree(root);

            InitializeUI(root);
            StartVersionCheck();
        }

        private static void AddStyleSheet(VisualElement root, string guid, string fallbackName)
        {
            var uss = LoadAsset<StyleSheet>(guid, fallbackName + " t:StyleSheet");
            if (uss != null) root.styleSheets.Add(uss);
            else Debug.LogWarning($"[{nameof(FastCurvatureBakerWindow)}] USS が見つかりません。GUID を確認してください: {guid}");
        }

        /// <summary>GUID で読み、見つからなければ名前で探す (.meta を作り直されたときの保険)。</summary>
        private static T LoadAsset<T>(string guid, string searchFilter) where T : UnityEngine.Object
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path))
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null) return asset;
            }
            foreach (var g in AssetDatabase.FindAssets(searchFilter))
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(g));
                if (asset != null) return asset;
            }
            return null;
        }

        // ─── バインディング ─────────────────────────────────────────────

        private void InitializeUI(VisualElement root)
        {
            // 標準フッターのステータスバー。保存の表示をクリックすると保存したテクスチャを Ping する
            _statusLabel = root.Q<Label>("status-label");
            _status = new DennokoStatus(_statusLabel);
            _statusLabel?.RegisterCallback<ClickEvent>(_ =>
            {
                if (_statusLabel.text != _savedStatusText || string.IsNullOrEmpty(_lastSavedPath)) return;
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(_lastSavedPath);
                if (tex != null) EditorGUIUtility.PingObject(tex);
            });

            // 標準ヘッダー
            _versionLabel = root.Q<Label>("version-label");
            _appBar = new DennokoAppBar(root, Title, english: !IsJapanese)
            {
                ManualUrl = ResolveManualUrl(),
                ResetAll = ResetAllSettings,
            };
            _appBar.LanguageChanged += english =>
                FastCurvatureBakerLoc.CurrentLanguage = english
                    ? FastCurvatureBakerLanguage.English
                    : FastCurvatureBakerLanguage.Japanese;
            _appBar.CheckUpdateRequested += () =>
            {
                FastCurvatureBakerVersion.ForceRecheck();
                LoadVersionResultFromSessionState();
            };

            InitializeTargetCard(root);
            InitializeSettingsCards(root);

            // 出力ドック + フッター
            _outputPathLabel = root.Q<Label>("output-path-label");
            _outputPathField = root.Q<TextField>("output-path");
            _outputNameLabel = root.Q<Label>("output-name-label");
            _outputName = root.Q<Label>("output-name");
            _overwriteToggle = root.Q<Toggle>("overwrite-toggle");
            _bakeButton = root.Q<Button>("bake-button");
            if (_bakeButton != null) _bakeButton.clicked += Bake;

            // 開いたときに対象が空なら、Hierarchy の選択で埋める
            _targets.RemoveAll(t => t == null);
            if (_targets.Count == 0) AddGameObjects(Selection.gameObjects, null);

            SetupDropdownChoices();
            SyncSettingsToUI();
            BindSettingsEvents();
            ApplyLanguage();
        }

        private void InitializeTargetCard(VisualElement root)
        {
            _targetCard = root.Q<VisualElement>("target-card");
            _targetList = root.Q<VisualElement>("target-list");
            _targetEmpty = root.Q<VisualElement>("target-empty");
            _targetEmptyMessage = root.Q<Label>("target-empty-message");
            _targetInfo = root.Q<VisualElement>("target-info");
            _targetInfoRenderers = root.Q<Label>("target-info-renderers");
            _targetInfoOutputs = root.Q<Label>("target-info-outputs");
            _targetWarning = root.Q<VisualElement>("target-warning");
            _targetWarningText = root.Q<Label>("target-warning-text");
            _targetFixButton = root.Q<Button>("target-fix");
            _targetHint = root.Q<Label>("target-hint");
            _targetClearButton = root.Q<Button>("target-clear");
            _targetFromSelectionButton = root.Q<Button>("target-from-selection");

            if (_targetFromSelectionButton != null)
            {
                _targetFromSelectionButton.clicked += () =>
                {
                    if (AddGameObjects(Selection.gameObjects, "Add Targets"))
                        RefreshTargets();
                    else
                        _status.Set(Tr("Hierarchy で GameObject を選んでください。", "Select GameObjects in the Hierarchy."), DennokoStatusType.Warning);
                };
            }

            if (_targetClearButton != null)
            {
                _targetClearButton.clicked += () =>
                {
                    Undo.RecordObject(this, "Clear Targets");
                    _targets.Clear();
                    RefreshTargets();
                };
            }

            if (_targetFixButton != null)
            {
                _targetFixButton.clicked += () =>
                {
                    List<Mesh> failed = MeshReadabilityUtility.EnableReadWrite(_unreadable);
                    RefreshTargets();
                    if (failed.Count == 0)
                        _status.Set(Tr("Read/Write を有効にしました。", "Read/Write enabled."), DennokoStatusType.Success);
                    else
                        _status.Set(Tr($"{failed.Count} 個のメッシュは Read/Write を変更できません (モデル以外のメッシュ)。",
                                       $"Could not enable Read/Write for {failed.Count} mesh(es) (not imported from a model)."),
                                    DennokoStatusType.Error);
                };
            }

            SetupDropTarget(_targetCard);
        }

        private void InitializeSettingsCards(VisualElement root)
        {
            // ベイク
            _bakeCardTitle = root.Q<Label>("bake-card-title");
            _bakeCardReset = root.Q<Button>("bake-card-reset");
            _modeRow = root.Q<VisualElement>("mode-row");
            _modeLabel = root.Q<Label>("mode-label");
            _modeChoice = root.Q<VisualElement>("mode-choice");
            _resolutionField = root.Q<DropdownField>("resolution-field");
            _uvChannelField = root.Q<DropdownField>("uv-channel-field");
            _qualityRow = root.Q<VisualElement>("quality-row");
            _qualityLabel = root.Q<Label>("quality-label");
            _supersampleToggle = root.Q<Toggle>("supersample-toggle");
            _advancedDetails = root.Q<VisualElement>("advanced-details");
            _advancedTitle = root.Q<Label>("advanced-title");
            _advancedChips = root.Q<VisualElement>("advanced-chips");
            _sameComponentToggle = root.Q<Toggle>("same-component-toggle");
            _normalRejectionSlider = root.Q<Slider>("normal-rejection-slider");
            _blurPassesSlider = root.Q<SliderInt>("blur-passes-slider");
            _dilationSlider = root.Q<SliderInt>("dilation-slider");

            _selectMode = DennokoBlockUI.BindChoice(_modeChoice, (int)_settings.Mode, index =>
            {
                Undo.RecordObject(this, "Change Mode");
                _settings.Mode = (CurvatureBakeMode)index;
                UpdateModeVisibility();
                RefreshOutputDock();
            });
            _selectQuality = DennokoBlockUI.BindChoice(root.Q<VisualElement>("quality-choice"), (int)_settings.Quality, index =>
            {
                Undo.RecordObject(this, "Change Quality");
                _settings.Quality = (BakeQuality)index;
            });
            DennokoBlockUI.BindDetails(_advancedDetails, _advancedOpen, open => _advancedOpen = open);

            // エッジ
            _edgeCardTitle = root.Q<Label>("edge-card-title");
            _edgeCardReset = root.Q<Button>("edge-card-reset");
            _edgeWidthField = root.Q<FloatField>("edge-width-field");
            _edgeStrengthSlider = root.Q<Slider>("edge-strength-slider");
            _sharpConvexContent = root.Q<VisualElement>("sharp-convex-content");
            _sharpMinAngleSlider = root.Q<Slider>("sharp-min-angle-slider");
            _maxBevelWidthField = root.Q<FloatField>("max-bevel-width-field");

            // 曲面
            _smoothCard = root.Q<VisualElement>("smooth-card");
            _smoothCardTitle = root.Q<Label>("smooth-card-title");
            _smoothCardReset = root.Q<Button>("smooth-card-reset");
            _radiusField = root.Q<FloatField>("radius-field");
            _strengthSlider = root.Q<Slider>("strength-slider");
            _sourceRow = root.Q<VisualElement>("source-row");
            _sourceLabel = root.Q<Label>("source-label");
            _sourceChoice = root.Q<VisualElement>("source-choice");
            _selectSource = DennokoBlockUI.BindChoice(_sourceChoice, (int)_settings.Source, index =>
            {
                Undo.RecordObject(this, "Change Source");
                _settings.Source = (CurvatureSource)index;
            });

            // カード見出しの ↺: そのカードだけを初期値に戻す (Undo で戻せるので確認なし)
            BindCardReset(_bakeCardReset, "Reset Bake Settings", d =>
            {
                _settings.Mode = d.Mode;
                _settings.Resolution = d.Resolution;
                _settings.UVChannel = d.UVChannel;
                _settings.Quality = d.Quality;
                _settings.Supersample = d.Supersample;
                _settings.SameComponentOnly = d.SameComponentOnly;
                _settings.NormalRejection = d.NormalRejection;
                _settings.BlurPasses = d.BlurPasses;
                _settings.DilationPixels = d.DilationPixels;
            });
            BindCardReset(_edgeCardReset, "Reset Edge Settings", d =>
            {
                _settings.EdgeWidth = d.EdgeWidth;
                _settings.EdgeStrength = d.EdgeStrength;
                _settings.SharpMinAngle = d.SharpMinAngle;
                _settings.MaxBevelWidth = d.MaxBevelWidth;
            });
            BindCardReset(_smoothCardReset, "Reset Smooth Surface Settings", d =>
            {
                _settings.Radius = d.Radius;
                _settings.Strength = d.Strength;
                _settings.Source = d.Source;
            });
        }

        private void BindCardReset(Button button, string undoName, Action<CurvatureBakeSettings> reset)
        {
            if (button == null) return;
            DennokoIcons.Apply(button, DennokoIcons.Refresh, "↺");
            button.clicked += () =>
            {
                Undo.RecordObject(this, undoName);
                reset(new CurvatureBakeSettings());
                SyncSettingsToUI();
                _status.Set(Tr("このカードを初期値に戻しました。", "Card reset to defaults."));
            };
        }

        /// <summary>⋮「設定を初期値に戻す…」(確認ダイアログは DennokoAppBar が出す)。対象は残す。</summary>
        private void ResetAllSettings()
        {
            Undo.RecordObject(this, "Reset Curvature Bake Settings");
            _settings = new CurvatureBakeSettings();
            SyncSettingsToUI();
            _status.Set(Tr("設定を初期値に戻しました。", "Settings reset to default."));
        }

        /// <summary>マニュアル (?) はパッケージ同梱の Docs/user_manual_*.pdf を開く。GitHub には直接つながない。</summary>
        private static string ResolveManualUrl()
        {
            string uxmlPath = AssetDatabase.GUIDToAssetPath(UXML_GUID);
            if (string.IsNullOrEmpty(uxmlPath)) return null;

            // <ツール>/Editor/UI/FastCurvatureBakerWindow.uxml → <ツール>/Docs/
            string toolRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(uxmlPath)));
            if (string.IsNullOrEmpty(toolRoot)) return null;
            string file = Path.Combine(toolRoot, "Docs", IsJapanese ? "user_manual_ja.pdf" : "user_manual_en.pdf");
            if (!File.Exists(file)) return null;
            return "file:///" + Path.GetFullPath(file).Replace('\\', '/');
        }

        private void SetupDropdownChoices()
        {
            if (_resolutionField != null)
                _resolutionField.choices = CurvatureBakeSettings.SupportedResolutions.Select(r => r.ToString()).ToList();
            if (_uvChannelField != null)
                _uvChannelField.choices = Enumerable.Range(0, 8).Select(i => $"UV{i}").ToList();
        }

        /// <summary>設定値を UI に書き戻す (コールバックは呼ばない)。</summary>
        private void SyncSettingsToUI()
        {
            _settings.Validate();

            _selectMode?.Invoke((int)_settings.Mode);
            _resolutionField?.SetValueWithoutNotify(_settings.Resolution.ToString());
            _uvChannelField?.SetValueWithoutNotify($"UV{_settings.UVChannel}");
            _selectQuality?.Invoke((int)_settings.Quality);
            _supersampleToggle?.SetValueWithoutNotify(_settings.Supersample);
            _sameComponentToggle?.SetValueWithoutNotify(_settings.SameComponentOnly);
            _normalRejectionSlider?.SetValueWithoutNotify(_settings.NormalRejection);
            _blurPassesSlider?.SetValueWithoutNotify(_settings.BlurPasses);
            _dilationSlider?.SetValueWithoutNotify(_settings.DilationPixels);

            _edgeWidthField?.SetValueWithoutNotify(_settings.EdgeWidth);
            _edgeStrengthSlider?.SetValueWithoutNotify(_settings.EdgeStrength);
            _sharpMinAngleSlider?.SetValueWithoutNotify(_settings.SharpMinAngle);
            _maxBevelWidthField?.SetValueWithoutNotify(_settings.MaxBevelWidth);

            _radiusField?.SetValueWithoutNotify(_settings.Radius);
            _strengthSlider?.SetValueWithoutNotify(_settings.Strength);
            _selectSource?.Invoke((int)_settings.Source);

            _overwriteToggle?.SetValueWithoutNotify(_settings.OverwriteExisting);

            UpdateModeVisibility();
            RefreshAdvancedChips();
            RefreshOutputDock();
        }

        private void UpdateModeVisibility()
        {
            bool isSharp = _settings.Mode == CurvatureBakeMode.SharpConvex;
            DennokoBlockUI.Show(_sharpConvexContent, isSharp);
            DennokoBlockUI.Show(_smoothCard, !isSharp);

            if (_edgeCardTitle != null)
                _edgeCardTitle.text = isSharp
                    ? Tr("シャープ凸エッジ / 狭ベベル", "SHARP CONVEX / NARROW BEVEL")
                    : Tr("ハードエッジ", "HARD EDGES");
            if (_edgeWidthField != null)
                _edgeWidthField.label = isSharp ? Tr("マスク幅 (m)", "Mask Width (m)") : Tr("エッジ幅 (m)", "Edge Width (m)");
        }

        /// <summary>「詳細」を閉じている間、初期値と違う項目だけをチップで見出し行に並べる。</summary>
        private void RefreshAdvancedChips()
        {
            if (_advancedChips == null) return;
            _advancedChips.Clear();
            var d = new CurvatureBakeSettings();

            void Add(string text) => _advancedChips.Add(DennokoBlockUI.Chip(null, text));

            if (_settings.SameComponentOnly != d.SameComponentOnly)
                Add(_settings.SameComponentOnly ? Tr("同一パーツのみ", "Same part") : Tr("全パーツ参照", "All parts"));
            if (!Mathf.Approximately(_settings.NormalRejection, d.NormalRejection))
                Add(Tr("法線 ", "Normal ") + _settings.NormalRejection.ToString("0.##"));
            if (_settings.BlurPasses != d.BlurPasses)
                Add(Tr("ブラー ", "Blur ") + _settings.BlurPasses);
            if (_settings.DilationPixels != d.DilationPixels)
                Add(Tr("拡張 ", "Dilation ") + _settings.DilationPixels + "px");
        }

        private void BindSettingsEvents()
        {
            _resolutionField?.RegisterValueChangedCallback(evt =>
            {
                if (!int.TryParse(evt.newValue, out int r)) return;
                Undo.RecordObject(this, "Change Resolution");
                _settings.Resolution = r;
                _settings.Validate();
            });

            _uvChannelField?.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == null || !evt.newValue.StartsWith("UV") ||
                    !int.TryParse(evt.newValue.Substring(2), out int uv)) return;
                Undo.RecordObject(this, "Change UV Channel");
                _settings.UVChannel = uv;
            });

            Bind(_supersampleToggle, "Change Supersample", v => _settings.Supersample = v);
            Bind(_sameComponentToggle, "Change Same Component", v => _settings.SameComponentOnly = v, RefreshAdvancedChips);
            Bind(_normalRejectionSlider, "Change Normal Rejection",
                 v => _settings.NormalRejection = Mathf.Clamp(v, -1f, CurvatureBakeSettings.MaxNormalRejection), RefreshAdvancedChips);
            Bind(_blurPassesSlider, "Change Blur Passes", v => _settings.BlurPasses = Mathf.Clamp(v, 0, 16), RefreshAdvancedChips);
            Bind(_dilationSlider, "Change Dilation", v => _settings.DilationPixels = Mathf.Clamp(v, 0, 64), RefreshAdvancedChips);

            Bind(_edgeWidthField, "Change Edge Width", v => _settings.EdgeWidth = Mathf.Max(v, 1e-5f));
            Bind(_edgeStrengthSlider, "Change Edge Strength", v => _settings.EdgeStrength = Mathf.Max(v, 0f));
            Bind(_sharpMinAngleSlider, "Change Sharp Min Angle", v => _settings.SharpMinAngle = Mathf.Clamp(v, 5f, 150f));
            Bind(_maxBevelWidthField, "Change Max Bevel Width", v => _settings.MaxBevelWidth = Mathf.Clamp(v, 0f, 0.05f));

            Bind(_radiusField, "Change Radius", v => _settings.Radius = Mathf.Max(v, 1e-5f));
            Bind(_strengthSlider, "Change Strength", v => _settings.Strength = Mathf.Max(v, 0f));

            Bind(_overwriteToggle, "Change Overwrite", v => _settings.OverwriteExisting = v);
        }

        private void Bind<T>(INotifyValueChanged<T> field, string undoName, Action<T> apply, Action after = null)
        {
            field?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, undoName);
                apply(evt.newValue);
                after?.Invoke();
            });
        }

        // ─── 言語適用 (ApplyLanguage) ───────────────────────────────────────

        private void ApplyLanguage()
        {
            if (_bakeButton == null) return;

            if (_appBar != null) _appBar.ManualUrl = ResolveManualUrl();

            // B1 対象カード
            SetText(_targetEmptyMessage, Tr("ベイクする GameObject をここへドラッグ＆ドロップ", "Drag & drop GameObjects to bake here"));
            SetText(_targetWarningText, Tr("Read/Write が無効なメッシュがあります", "Some meshes have Read/Write disabled"));
            SetText(_targetFixButton, Tr("有効にする", "Enable"));
            SetText(_targetHint, Tr("子の Renderer もまとめてベイクします", "Child renderers are baked too"));
            SetText(_targetClearButton, Tr("すべて外す", "Clear"));
            SetText(_targetFromSelectionButton, Tr("選択から追加", "Add Selected"));
            if (_targetFromSelectionButton != null)
                _targetFromSelectionButton.tooltip = Tr("Hierarchy で選択中の GameObject を対象に追加します", "Add the GameObjects selected in the Hierarchy");
            if (_targetClearButton != null)
                _targetClearButton.tooltip = Tr("対象をすべて一覧から外します", "Remove all targets from the list");

            // B2 ベイク
            SetText(_bakeCardTitle, Tr("ベイク", "BAKE"));
            SetText(_modeLabel, Tr("モード", "Mode"));
            SetChoiceTexts(_modeChoice, IsJapanese
                ? new[] { "符号付き", "凸", "凹", "シャープ凸" }
                : new[] { "Signed", "Convex", "Concave", "Sharp" });
            if (_resolutionField != null) _resolutionField.label = Tr("解像度", "Resolution");
            if (_uvChannelField != null) _uvChannelField.label = Tr("UV チャンネル", "UV Channel");
            SetText(_qualityLabel, Tr("品質", "Quality"));
            if (_supersampleToggle != null) _supersampleToggle.text = Tr("スーパーサンプリング 2x", "Supersample 2x");
            SetText(_advancedTitle, Tr("詳細", "Advanced"));
            if (_sameComponentToggle != null) _sameComponentToggle.text = Tr("同一接続パーツのみ", "Same Part Only");
            if (_normalRejectionSlider != null) _normalRejectionSlider.label = Tr("法線拒絶しきい値", "Normal Rejection");
            if (_blurPassesSlider != null) _blurPassesSlider.label = Tr("ブラー反復回数", "Blur Passes");
            if (_dilationSlider != null) _dilationSlider.label = Tr("ピクセル拡張幅 (px)", "Dilation (px)");

            // B2 エッジ (見出しとエッジ幅のラベルはモードで変わる)
            if (_edgeStrengthSlider != null) _edgeStrengthSlider.label = Tr("エッジ強度", "Edge Strength");
            if (_sharpMinAngleSlider != null) _sharpMinAngleSlider.label = Tr("最小角角度 (°)", "Min Corner Angle (°)");
            if (_maxBevelWidthField != null) _maxBevelWidthField.label = Tr("最大ベベル幅 (m)", "Max Bevel Width (m)");
            UpdateModeVisibility();

            // B2 曲面
            SetText(_smoothCardTitle, Tr("曲面", "SMOOTH SURFACES"));
            if (_radiusField != null) _radiusField.label = Tr("曲面計測半径 (m)", "Radius (m)");
            if (_strengthSlider != null) _strengthSlider.label = Tr("曲面強度", "Strength");
            SetText(_sourceLabel, Tr("計算ソース", "Source"));
            SetChoiceTexts(_sourceChoice, IsJapanese
                ? new[] { "シェーディング法線", "ジオメトリ" }
                : new[] { "Shading Normals", "Geometry" });

            foreach (var reset in new[] { _bakeCardReset, _edgeCardReset, _smoothCardReset })
                if (reset != null) reset.tooltip = Tr("このカードを初期値に戻す", "Reset this card to defaults");

            // 出力ドック + フッター
            SetText(_outputPathLabel, Tr("出力先", "Output"));
            SetText(_outputNameLabel, Tr("名前", "Name"));
            if (_overwriteToggle != null) _overwriteToggle.text = Tr("上書き", "Overwrite");

            SetupPropertyExplanations();
            ApplyVersionLabel();
            RefreshAdvancedChips();
            RefreshTargets();
        }

        private static void SetText(TextElement element, string text)
        {
            if (element != null) element.text = text;
        }

        private static void SetChoiceTexts(VisualElement group, string[] texts)
        {
            if (group == null) return;
            var buttons = group.Children().OfType<Button>().ToArray();
            for (int i = 0; i < buttons.Length && i < texts.Length; i++)
                buttons[i].text = texts[i];
        }

        // ─── 項目解説 (Hover / Guide) ────────────────────────────────────────

        private struct PropertyExplanation
        {
            public string Name;
            public string Description;
            public string IncreaseImpact;
            public string DecreaseImpact;
        }

        private void SetupPropertyExplanations()
        {
            AttachExplanation(_modeRow, new PropertyExplanation
            {
                Name = Tr("Bake Mode (ベイクモード)", "Bake Mode"),
                Description = Tr(
                    "出力する曲率テクスチャの種類を指定します。\n・Default: 符号付き曲率 (平坦=0.5、凸=白、凹=黒)\n・Convex: 凸部のみ抽出 (エッジの摩耗・ハイライト用)\n・Concave: 凹部のみ抽出 (溝の汚れ・影マスク用)\n・SharpConvex: 凸ハードエッジと狭いベベルの急な角のみをワールド空間幅で抽出 (緩やかな曲面は除外)",
                    "Selects what the baked curvature texture represents:\n• Default: Signed curvature (0.5 = flat, brighter = convex, darker = concave)\n• Convex: White on convex edges & smooth convex surfaces (wear mask)\n• Concave: White in creases (dirt / grime mask)\n• SharpConvex: White only on convex hard edges and sharp corners with narrow bevels (ignores smooth curves)"),
                IncreaseImpact = Tr(
                    "【切替効果】用途に応じた白黒マスク（凸/凹/シャープ凸角のみ）または全曲率マップを切り替えます。",
                    "[Mode change] Selects full signed curvature map, wear/dirt masks, or sharp convex edge mask."),
                DecreaseImpact = ""
            });

            AttachExplanation(_resolutionField, new PropertyExplanation
            {
                Name = Tr("Resolution (テクスチャ解像度)", "Resolution"),
                Description = Tr(
                    "出力テクスチャのピクセル解像度（256〜4096）を指定します。",
                    "Output texture resolution in pixels (256 to 4096)."),
                IncreaseImpact = Tr(
                    "【値を増やす (▲)】曲率エッジの解像度やディテールが鮮明になりますが、ベイク時間とメモリ消費量が増加します。",
                    "[Increase ▲] Sharper details and edge definition, but increases bake time and memory usage."),
                DecreaseImpact = Tr(
                    "【値を減らす (▼)】ベイクが高速化しファイル容量が軽くなりますが、エッジが粗くなりジャギーが出やすくなります。",
                    "[Decrease ▼] Faster bake time and smaller file size, but lowers edge sharpness and may introduce pixelation.")
            });

            AttachExplanation(_uvChannelField, new PropertyExplanation
            {
                Name = Tr("UV Channel (使用UVチャンネル)", "UV Channel"),
                Description = Tr(
                    "テクスチャのベイクに使用するメッシュのUVチャンネル (UV0〜UV7) を選択します。",
                    "UV channel used for baking (UV0 to UV7)."),
                IncreaseImpact = Tr(
                    "【インデックス変更】通常はベーステクスチャ展開用のUV0を使用します。セカンドUV等にベイクしたい場合に変更します。",
                    "[Channel change] UV0 is typically used for base textures. Switch only when baking for a secondary UV set."),
                DecreaseImpact = ""
            });

            AttachExplanation(_qualityRow, new PropertyExplanation
            {
                Name = Tr("Quality (サンプリング品質)", "Quality"),
                Description = Tr(
                    "半径あたりに配置するサンプリング点数（Draft: 4点, Standard: 6点, High: 8点, Ultra: 12点）を指定します。",
                    "Number of sampling points per radius (Draft: 4, Standard: 6, High: 8, Ultra: 12)."),
                IncreaseImpact = Tr(
                    "【品質を上げる (▲)】サンプリング密度が上がりノイズが低減され滑らかになりますが、ベイク計算時間が増加します。",
                    "[Higher ▲] Reduces noise and improves curvature estimation accuracy, but increases bake time."),
                DecreaseImpact = Tr(
                    "【品質を下げる (▼)】計算時間を短縮できます。ベイク結果の当たりをつけるプレビュー用途に適しています。",
                    "[Lower ▼] Faster processing, ideal for quick preview and iterative tuning.")
            });

            AttachExplanation(_supersampleToggle, new PropertyExplanation
            {
                Name = Tr("Supersample 2x (スーパーサンプリング)", "Supersample 2x"),
                Description = Tr(
                    "内部で2倍の解像度で計算を行い、平均化（ダウンサンプリング）して出力します（解像度4096時は自動無効）。",
                    "Evaluates at 2x resolution and downsamples (skipped when resolution is 4096)."),
                IncreaseImpact = Tr(
                    "【有効 (ON)】UV境界や細かなエッジのエイリアシング（ギザギザ）を劇的に低減し高品質にします（計算時間は約4倍）。",
                    "[Enabled ON] Greatly reduces aliasing and jagged edges on thin features (takes ~4x bake time)."),
                DecreaseImpact = Tr(
                    "【無効 (OFF)】計算時間を短縮して高速にベイクします。",
                    "[Disabled OFF] Significantly shortens baking time.")
            });

            AttachExplanation(_edgeWidthField, new PropertyExplanation
            {
                Name = Tr("Edge Width [m] (ハードエッジ幅)", "Edge Width (m)"),
                Description = Tr(
                    "法線が分割されているハードエッジ（ポリゴンの明確な折り目）から、曲率として検出するワールド空間の幅（メートル単位）です。",
                    "World-space distance (meters) from a hard edge (split normals) treated as convex/concave."),
                IncreaseImpact = Tr(
                    "【値を増やす (▲)】ハードエッジ周囲の白/黒の帯が太く広がり、角の摩耗表現などが広範囲に目立つようになります。",
                    "[Increase ▲] Produces broader wear/crease bands along hard edges."),
                DecreaseImpact = Tr(
                    "【値を減らす (▼)】極めて細くシャープなエッジラインになり、金属の硬質なコーナー角などに適します。",
                    "[Decrease ▼] Produces very thin, sharp lines suitable for crisp metal corners.")
            });

            AttachExplanation(_edgeStrengthSlider, new PropertyExplanation
            {
                Name = Tr("Edge Strength (ハードエッジ強度)", "Edge Strength"),
                Description = Tr(
                    "ハードエッジにおける曲率の乗数（倍率）です。直角（90度）の折り目は値1.0で最大輝度（白または黒）に達します。",
                    "Intensity multiplier for hard edges. A 90-degree fold reaches full intensity at 1.0."),
                IncreaseImpact = Tr(
                    "【値を増やす (▲)】緩やかな角度のエッジでも強く白黒が際立つようになり、コントラストが強調されます。",
                    "[Increase ▲] Enhances contrast so even shallow creases become prominent."),
                DecreaseImpact = Tr(
                    "【値を減らす (▼)】ハードエッジの影響が弱まり、0.0にするとハードエッジ検出を無効化できます。",
                    "[Decrease ▼] Reduces hard edge intensity; setting to 0 disables hard edge detection.")
            });

            AttachExplanation(_sharpMinAngleSlider, new PropertyExplanation
            {
                Name = Tr("Min Corner Angle [°] (最小角角度)", "Min Corner Angle (°)"),
                Description = Tr(
                    "SharpConvex モードでマスク対象にする凸角の最小合計角度（度）です。この角度を超える急な凸角・狭ベベルだけを抽出します。",
                    "Minimum total convex bending angle (degrees) included in SharpConvex mode. Only corners sharper than this threshold are masked."),
                IncreaseImpact = Tr(
                    "【値を増やす (▲)】より鋭い角（直角や鋭角など）だけを厳選し、緩やかな角や低ポリ曲面の折れを除外します。",
                    "[Increase ▲] Restricts the mask to sharper corners (e.g. 60°–90°+) and excludes gentle bends."),
                DecreaseImpact = Tr(
                    "【値を減らす (▼)】やや緩やかな凸角もマスクに含めるようになります。",
                    "[Decrease ▼] Includes shallower convex corners in the mask.")
            });

            AttachExplanation(_maxBevelWidthField, new PropertyExplanation
            {
                Name = Tr("Max Bevel Width [m] (最大ベベル幅)", "Max Bevel Width (m)"),
                Description = Tr(
                    "SharpConvex モードで「急な角」として認識するベベルの最大幅（メートル単位）です。例: 0.004 = 4mm 以下の狭いベベルを検出し、大きな曲面や広い丸角は除外します。",
                    "Maximum world-space bevel width (meters) recognized as a sharp corner in SharpConvex mode. E.g. 0.004 (4 mm) detects narrow chamfers/bevels while ignoring broad curves."),
                IncreaseImpact = Tr(
                    "【値を増やす (▲)】より幅の広いベベルや丸角もマスク対象に含まれます（大きくしすぎると細い円柱等も拾います）。",
                    "[Increase ▲] Captures wider bevels and fillets (too large may include thin curved cylinders)."),
                DecreaseImpact = Tr(
                    "【値を減らす (▼)】極細ベベルとハードエッジのみに絞ります。0 にすると単一エッジの急な角（ハード/シャープエッジ）のみを検出します。",
                    "[Decrease ▼] Narrows detection to tight bevels and hard edges; 0 detects single-edge sharp creases only.")
            });

            AttachExplanation(_radiusField, new PropertyExplanation
            {
                Name = Tr("Radius [m] (曲面計測半径)", "Radius (m)"),
                Description = Tr(
                    "スムース面（なだらかな曲面）の曲率を計算するために周囲のサーフェスを探索する球の半径（メートル単位）です。",
                    "World-space sampling sphere radius used to estimate the curvature of smooth surfaces."),
                IncreaseImpact = Tr(
                    "【値を増やす (▲)】より広範囲な大きなうねりや緩やかな曲面を捉え、滑らかな大域グラデーションになります。",
                    "[Increase ▲] Captures broad, macroscopic surface curvature with smooth gradients."),
                DecreaseImpact = Tr(
                    "【値を減らす (▼)】微細な凹凸・小さなモールドに鋭く反応するようになり、大まかな曲面には反応しなくなります。",
                    "[Decrease ▼] Focuses on fine, localized relief details and ignores broad curvature.")
            });

            AttachExplanation(_strengthSlider, new PropertyExplanation
            {
                Name = Tr("Strength (曲面強度)", "Strength"),
                Description = Tr(
                    "スムース面の曲率の出力乗数（倍率）です。Radiusと等しい半径を持つ球面の曲率は値1.0で最大輝度に達します。",
                    "Output intensity multiplier for smooth surfaces. A sphere with radius equal to Radius reaches full intensity at 1.0."),
                IncreaseImpact = Tr(
                    "【値を増やす (▲)】緩やかな曲面でも白黒のメリハリ・コントラストが強調されます。",
                    "[Increase ▲] Boosts contrast and punchiness on gentle curved surfaces."),
                DecreaseImpact = Tr(
                    "【値を減らす (▼)】曲率の変化がマイルドで淡く柔らかいグラデーションになります。",
                    "[Decrease ▼] Produces milder, subtle gradients.")
            });

            AttachExplanation(_sourceRow, new PropertyExplanation
            {
                Name = Tr("Source (曲率計算ソース)", "Source"),
                Description = Tr(
                    "スムース面の曲率計算に使用する法線シグナルを選択します。\n・ShadingNormals: 頂点法線（滑らかな陰影）に追従\n・Geometry: 実際のポリゴン面法線（面の折り目を直接計測）",
                    "Surface signal used to compute curvature:\n• ShadingNormals: Smooth, follows shading of low-poly meshes\n• Geometry: Uses face normals, detecting every polygon crease"),
                IncreaseImpact = Tr(
                    "【ShadingNormals】ローポリでもシェーディング通りの滑らかな曲率が得られます。",
                    "[ShadingNormals] Produces smooth, artifact-free gradients on low-poly meshes."),
                DecreaseImpact = Tr(
                    "【Geometry】ハイポリやハードサーフェスで各ポリゴンの微小な角度変化をすべて拾いたい場合に使用します。",
                    "[Geometry] Picks up every polygon facet; ideal for dense cad/hard-surface models.")
            });

            AttachExplanation(_sameComponentToggle, new PropertyExplanation
            {
                Name = Tr("Same Part Only (同一接続パーツのみ)", "Same Part Only"),
                Description = Tr(
                    "テクセルと同一の接続されたメッシュパーツ（連結成分）のみから曲率サンプルを取得します。",
                    "Only use surface samples from the same connected mesh island as the texel."),
                IncreaseImpact = Tr(
                    "【有効 (ON)】衣服と素肌のように、近接しているが別パーツである部分同士の不要な曲率干渉・影の映り込みを防ぎます。",
                    "[Enabled ON] Prevents disconnected overlapping parts (e.g. clothing over skin) from projecting false curvature."),
                DecreaseImpact = Tr(
                    "【無効 (OFF)】別パーツ同士の隙間や重なり合いも一体の幾何形状として凹凸（陰影）を検出します。",
                    "[Disabled OFF] Treats nearby disconnected parts as continuous geometry to detect contact crevices.")
            });

            AttachExplanation(_normalRejectionSlider, new PropertyExplanation
            {
                Name = Tr("Normal Rejection (法線拒絶しきい値)", "Normal Rejection"),
                Description = Tr(
                    "テクセル法線と反対方向（裏面など）を向いている近接面を除外する内積のしきい値です（そこから+0.25の間で重みが徐々に復帰）。",
                    "Dot product threshold below which opposite-facing surfaces are rejected (fades back in over the next 0.25)."),
                IncreaseImpact = Tr(
                    "【値を増やす (▲)】薄い板の裏面や急角度の背面が厳格に除外され、裏抜けや不要な干渉を防ぎます（上げすぎると溝の検出が弱まります）。",
                    "[Increase ▲] Strictly excludes back-facing surfaces, avoiding leak-through on thin sheets."),
                DecreaseImpact = Tr(
                    "【値を減らす (▼)】より広い角度の面がサンプリング対象に含まれ、急峻な溝の谷底でも確実に曲率を検出できます。",
                    "[Decrease ▼] Includes wider angles in sampling, ensuring deep V-shaped crevices are detected.")
            });

            AttachExplanation(_blurPassesSlider, new PropertyExplanation
            {
                Name = Tr("Blur Passes (ブラー反復回数)", "Blur Passes"),
                Description = Tr(
                    "ベイク完了後に適用する3x3ガウシアンブラーの反復回数（0〜16回）です。",
                    "Number of 3x3 gaussian blur passes applied after evaluation (0 to 16)."),
                IncreaseImpact = Tr(
                    "【値を増やす (▲)】メッシュの微細なノイズが平滑化されて滑らかになりますが、エッジの鋭さが甘くなります。",
                    "[Increase ▲] Smooths out noise and micro-facets at the cost of softening sharp edges."),
                DecreaseImpact = Tr(
                    "【値を減らす (▼)】元のシャープなベイク結果がそのまま保持されます（0でブラー無効）。",
                    "[Decrease ▼] Preserves raw, crisp bake sharpness (0 disables blur).")
            });

            AttachExplanation(_dilationSlider, new PropertyExplanation
            {
                Name = Tr("Dilation [px] (ピクセル拡張幅)", "Dilation (px)"),
                Description = Tr(
                    "UVアイランドの境界線からテクスチャ結果を外側へ何ピクセル引き伸ばすか（0〜64px）を指定します。",
                    "Pixels to extend valid texture colors outward from UV island boundaries (0 to 64px)."),
                IncreaseImpact = Tr(
                    "【値を増やす (▲)】ミップマップ生成時や遠景レンダリング時にUV境界のフチに生じる黒い継ぎ目（シーム）を防止できます。",
                    "[Increase ▲] Prevents dark seams/borders caused by texture mipmapping and texture filtering."),
                DecreaseImpact = Tr(
                    "【値を減らす (▼)】アイランド外への余白が小さくなります。UVアイランド間の隙間が狭く隣の島と接触しやすい場合に小さくします。",
                    "[Decrease ▼] Reduces padding between tightly packed UV islands to avoid bleeding.")
            });

            AttachExplanation(_overwriteToggle, new PropertyExplanation
            {
                Name = Tr("Overwrite Existing (同名ファイルを上書き)", "Overwrite Existing"),
                Description = Tr(
                    "出力先フォルダに同名のテクスチャファイルが既に存在する場合の保存動作です。",
                    "Determines saving behavior when an identical file name exists in the output folder."),
                IncreaseImpact = Tr(
                    "【有効 (ON)】既存のファイルを上書き更新します。再ベイクによる差し替えに便利です。",
                    "[Enabled ON] Overwrites existing texture files directly."),
                DecreaseImpact = Tr(
                    "【無効 (OFF)】既存ファイルを保護し、ファイル名末尾にスペース＋連番（例: Name 1.png）を付与して別名保存します。",
                    "[Disabled OFF] Protects existing files and saves with space + number suffix (e.g. Name 1.png).")
            });
        }

        private static void AttachExplanation(VisualElement element, PropertyExplanation exp)
        {
            if (element == null) return;

            string fullTooltip = $"{exp.Name}\n{exp.Description}";
            if (!string.IsNullOrEmpty(exp.IncreaseImpact))
                fullTooltip += $"\n{exp.IncreaseImpact}";
            if (!string.IsNullOrEmpty(exp.DecreaseImpact))
                fullTooltip += $"\n{exp.DecreaseImpact}";

            element.tooltip = fullTooltip;
        }

        // ─── B1 対象カード ─────────────────────────────────────────────

        private void SetupDropTarget(VisualElement card)
        {
            if (card == null) return;

            card.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                bool any = !IsBaking && DragAndDrop.objectReferences.OfType<GameObject>().Any(go => !EditorUtility.IsPersistent(go));
                DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                card.EnableInClassList(DropOverClass, any);
                evt.StopPropagation();
            });
            card.RegisterCallback<DragLeaveEvent>(_ => card.RemoveFromClassList(DropOverClass));
            card.RegisterCallback<DragExitedEvent>(_ => card.RemoveFromClassList(DropOverClass));
            card.RegisterCallback<DragPerformEvent>(evt =>
            {
                DragAndDrop.AcceptDrag();
                card.RemoveFromClassList(DropOverClass);
                if (!IsBaking && AddGameObjects(DragAndDrop.objectReferences.OfType<GameObject>(), "Add Dropped Targets"))
                    RefreshTargets();
                evt.StopPropagation();
            });
        }

        /// <summary>シーン上の GameObject を対象に足す。1 つでも足したら true。undoName が null なら Undo に積まない。</summary>
        private bool AddGameObjects(IEnumerable<GameObject> objects, string undoName)
        {
            var toAdd = objects
                .Where(go => go != null && !EditorUtility.IsPersistent(go) && !_targets.Contains(go))
                .Distinct()
                .ToList();
            if (toAdd.Count == 0) return false;
            if (undoName != null) Undo.RecordObject(this, undoName);
            _targets.AddRange(toAdd);
            return true;
        }

        private void RefreshTargets()
        {
            if (_targetList == null) return;
            _targets.RemoveAll(t => t == null);

            // 対象の行 (動的リストなので C# で組む。見た目は USS のクラスだけで決める)
            _targetList.Clear();
            for (int i = 0; i < _targets.Count; i++)
            {
                int index = i;
                var row = new VisualElement();
                row.AddToClassList("dennoko-target-row");

                var field = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = true, value = _targets[index] };
                field.RegisterValueChangedCallback(evt =>
                {
                    Undo.RecordObject(this, "Change Target Object");
                    _targets[index] = evt.newValue as GameObject;
                    RefreshTargets();
                });
                field.SetEnabled(!IsBaking);

                var remove = new Button(() =>
                {
                    Undo.RecordObject(this, "Remove Target");
                    _targets.RemoveAt(index);
                    RefreshTargets();
                });
                remove.AddToClassList("dennoko-icon-button");
                DennokoIcons.Apply(remove, DennokoIcons.Close, "×", Tr("一覧から外す", "Remove from list"));
                remove.SetEnabled(!IsBaking);

                row.Add(field);
                row.Add(remove);
                _targetList.Add(row);
            }

            bool hasTargets = _targets.Count > 0;
            _renderers = hasTargets ? CurvatureBakePipeline.CollectRenderers(_targets) : new List<Renderer>();
            _unreadable = MeshReadabilityUtility.FindUnreadableMeshes(_renderers);

            DennokoBlockUI.Show(_targetEmpty, !hasTargets);
            DennokoBlockUI.Show(_targetInfo, hasTargets);
            DennokoBlockUI.Show(_targetWarning, _unreadable.Count > 0);
            DennokoBlockUI.Show(_targetClearButton, hasTargets);

            int outputs = _renderers.Sum(r => SurfaceMesh.GetSharedMesh(r)?.subMeshCount ?? 0);
            SetText(_targetInfoRenderers, $"Renderer {_renderers.Count}");
            SetText(_targetInfoOutputs, Tr($"出力 {outputs} 枚", $"{outputs} texture(s)"));
            if (_targetWarningText != null)
            {
                _targetWarningText.text = Tr($"Read/Write が無効なメッシュがあります ({_unreadable.Count})",
                                             $"Read/Write is disabled on {_unreadable.Count} mesh(es)");
                _targetWarningText.tooltip = string.Join("\n", _unreadable.Select(m => "- " + m.name));
            }

            RefreshOutputDock();
            RefreshBakeButton();
        }

        // ─── B5 出力ドック + 主ボタン ──────────────────────────────────

        private void RefreshOutputDock()
        {
            if (_outputPathField == null) return;

            string rule = Tr(
                $"出力先: <ベーステクスチャのフォルダ>/{CurvatureTextureExporter.OutputFolderName}/ (テクスチャがない場合は {CurvatureTextureExporter.FallbackFolder}/)",
                $"Output: <base texture folder>/{CurvatureTextureExporter.OutputFolderName}/ (or {CurvatureTextureExporter.FallbackFolder}/ if no texture)");

            var folders = _renderers
                .SelectMany(r => r.sharedMaterials.Where(m => m != null).DefaultIfEmpty(null))
                .Select(CurvatureTextureExporter.ResolveOutputFolder)
                .Distinct()
                .ToList();

            string path;
            if (folders.Count == 0)
                path = Tr("<ベーステクスチャのフォルダ>/", "<base texture folder>/") + CurvatureTextureExporter.OutputFolderName;
            else if (folders.Count == 1)
                path = folders[0];
            else
                path = folders[0] + Tr($"  ほか {folders.Count - 1} か所", $"  +{folders.Count - 1} more");

            _outputPathField.SetValueWithoutNotify(path);
            _outputPathField.tooltip = folders.Count > 1 ? rule + "\n\n" + string.Join("\n", folders) : rule;

            if (_outputName != null)
            {
                string baseName = _renderers.Count == 1 ? _renderers[0].name : Tr("<Renderer 名>", "<renderer name>");
                _outputName.text = $"{baseName}{_settings.FileSuffix}.png";
                _outputName.tooltip = Tr("サブメッシュが複数あるときは <Renderer 名>_<マテリアル名> になります",
                                         "With several sub-meshes the name becomes <renderer>_<material>");
            }
        }

        private void RefreshBakeButton()
        {
            if (_bakeButton == null) return;
            _bakeButton.text = IsBaking ? Tr("ベイク中…", "Baking…") : Tr("ベイク", "Bake");

            bool ready = _renderers.Count > 0;
            _bakeButton.SetEnabled(ready && !IsBaking);
            _bakeButton.tooltip = IsBaking ? null
                : _targets.Count == 0 ? Tr("ベイクする GameObject を追加してください", "Add GameObjects to bake")
                : !ready ? Tr("対象に MeshRenderer / SkinnedMeshRenderer がありません", "No MeshRenderer or SkinnedMeshRenderer in the targets")
                : null;
        }

        // ─── バージョンチェック ─────────────────────────────────────────────

        private void StartVersionCheck()
        {
            LoadVersionResultFromSessionState();
            FastCurvatureBakerVersion.StartCheckBackgroundTask();
        }

        internal void LoadVersionResultFromSessionState()
        {
            string local  = FastCurvatureBakerVersion.Current;
            string latest = SessionState.GetString(FastCurvatureBakerVersion.VerCheckLatestKey, string.Empty);
            bool   done   = SessionState.GetBool(FastCurvatureBakerVersion.VerCheckDoneKey, false);
            bool   error  = SessionState.GetBool(FastCurvatureBakerVersion.VerCheckErrorKey, false);

            DennokoVersionChecker.State state;
            if (!done)
                state = DennokoVersionChecker.State.Checking;
            else if (error || string.IsNullOrEmpty(latest))
                state = DennokoVersionChecker.State.Error;
            else if (DennokoVersionChecker.IsUpdateAvailable(latest, local))
                state = DennokoVersionChecker.State.UpdateAvailable;
            else
                state = DennokoVersionChecker.State.UpToDate;

            _versionResult = new DennokoVersionChecker.Result
            {
                State = state,
                LocalVersion = local,
                LatestVersion = latest,
                Url = SessionState.GetString(FastCurvatureBakerVersion.VerCheckUrlKey, string.Empty),
                Message = SessionState.GetString(FastCurvatureBakerVersion.VerCheckMessageKey, string.Empty)
            };
            ApplyVersionLabel();
        }

        private void ApplyVersionLabel()
        {
            if (_versionLabel == null) return;

            var r = _versionResult;
            string baseText = "v" + r.LocalVersion;
            string text;
            bool update = false, error = false;
            switch (r.State)
            {
                case DennokoVersionChecker.State.UpdateAvailable:
                    text = $"{baseText}  " + Tr($"更新あり {r.LatestVersion}", $"Update available {r.LatestVersion}");
                    update = true;
                    break;
                case DennokoVersionChecker.State.Error:
                    text = $"{baseText}  " + Tr("最新版を取得できません", "Cannot fetch latest");
                    error = true;
                    break;
                case DennokoVersionChecker.State.Checking:
                    text = $"{baseText}  " + Tr("確認中…", "Checking…");
                    break;
                default:
                    text = baseText;
                    break;
            }
            _versionLabel.text = text;
            _versionLabel.EnableInClassList("dennoko-version-label--update", update);
            _versionLabel.EnableInClassList("dennoko-version-label--error", error);

            // 更新ありのラベルは公開配布ページを開く。GitHub への直接の導線は作らない
            bool hasReleasePage = !string.IsNullOrEmpty(r.Url) &&
                                  r.Url.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) < 0;
            if (_appBar != null)
            {
                _appBar.ReleaseUrl = hasReleasePage ? r.Url : null;
                _appBar.SetVersionClickable(update && hasReleasePage,
                    update && hasReleasePage ? Tr("クリックで配布ページを開く", "Click to open the download page") : null);
            }
        }

        // ─── ベイクパイプライン実行 ─────────────────────────────────────────

        private async void Bake()
        {
            var targets = _targets.Where(t => t != null).Distinct().ToList();
            var renderers = CurvatureBakePipeline.CollectRenderers(targets);
            if (renderers.Count == 0)
            {
                RefreshTargets();
                _status.Set(Tr("対象に MeshRenderer / SkinnedMeshRenderer がありません。", "No MeshRenderer or SkinnedMeshRenderer in the targets."), DennokoStatusType.Error);
                return;
            }
            if (!EnsureReadable(renderers))
                return;

            _cancellation = new CancellationTokenSource();
            SetBakeInProgress(true);
            _status.Set(Tr("ベイク中…", "Baking…"), DennokoStatusType.Busy, 0f);

            // キャンセルは Unity のプログレスバーの「Cancel」で受ける。ステータスにも同じ進捗を出す
            var progress = new ImmediateProgress(p =>
            {
                _status.Set(Tr("ベイク中… ", "Baking… ") + p.Message, DennokoStatusType.Busy, p.Progress);
                if (EditorUtility.DisplayCancelableProgressBar(Title, p.Message, p.Progress))
                    _cancellation?.Cancel();
            });

            BakeReport report = null;
            try
            {
                report = await new CurvatureBakePipeline().RunAsync(targets, _settings, progress, _cancellation.Token);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _status.Set(Tr($"ベイクに失敗しました: {e.Message}", $"Bake failed: {e.Message}"), DennokoStatusType.Error);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _cancellation.Dispose();
                _cancellation = null;
                SetBakeInProgress(false);
            }

            if (report != null)
                ShowReport(report);
        }

        private void SetBakeInProgress(bool baking)
        {
            _targetFromSelectionButton?.SetEnabled(!baking);
            _targetClearButton?.SetEnabled(!baking);
            _targetFixButton?.SetEnabled(!baking);
            RefreshTargets();
        }

        private bool EnsureReadable(List<Renderer> renderers)
        {
            List<Mesh> unreadable = MeshReadabilityUtility.FindUnreadableMeshes(renderers);
            if (unreadable.Count == 0)
                return true;

            string names = string.Join("\n", unreadable.Select(m => "- " + m.name));
            string message = Tr(
                $"以下のメッシュをベイクするには Read/Write の有効化が必要です:\n{names}\n\n今すぐ有効化しますか？",
                $"The following meshes need Read/Write enabled to be baked:\n{names}\n\nEnable it now?");

            if (!EditorUtility.DisplayDialog(Title, message, Tr("有効化", "Enable"), Tr("キャンセル", "Cancel")))
                return false;

            List<Mesh> failed = MeshReadabilityUtility.EnableReadWrite(unreadable);
            RefreshTargets();
            if (failed.Count == 0)
                return true;

            string failedNames = string.Join("\n", failed.Select(m => "- " + m.name));
            EditorUtility.DisplayDialog(Title,
                Tr($"以下のメッシュの Read/Write を有効化できませんでした:\n{failedNames}", $"Could not enable Read/Write for:\n{failedNames}"), "OK");
            return false;
        }

        private void ShowReport(BakeReport report)
        {
            _lastSavedPath = report.SavedAssetPaths.Count > 0 ? report.SavedAssetPaths[0] : null;
            if (_lastSavedPath != null)
            {
                var first = AssetDatabase.LoadAssetAtPath<Texture2D>(_lastSavedPath);
                if (first != null) EditorGUIUtility.PingObject(first);
            }

            if (report.Cancelled)
            {
                _status.Set(Tr("ベイクを中断しました。", "Bake cancelled."), DennokoStatusType.Warning);
                return;
            }

            if (report.Errors.Count > 0)
            {
                // エラーは件数が多くなり得るので一覧をダイアログで出す
                string message = Tr(
                    $"{report.SavedAssetPaths.Count} 枚のテクスチャを保存しました ({report.Elapsed.TotalSeconds:F1}s)\n\nエラー:\n{string.Join("\n", report.Errors)}",
                    $"Saved {report.SavedAssetPaths.Count} texture(s) in {report.Elapsed.TotalSeconds:F1}s.\n\nErrors:\n{string.Join("\n", report.Errors)}");
                _status.Set(Tr($"{report.Errors.Count} 件のエラーがありました。", $"{report.Errors.Count} error(s) occurred."), DennokoStatusType.Error);
                EditorUtility.DisplayDialog(Title, message, "OK");
                return;
            }

            // P1: 保存したらステータスに「保存: <ファイル名>」。クリックで Project ビューの該当ファイルを Ping
            int count = report.SavedAssetPaths.Count;
            string file = _lastSavedPath != null ? Path.GetFileName(_lastSavedPath) : string.Empty;
            _savedStatusText = count <= 1
                ? Tr($"保存: {file} ({report.Elapsed.TotalSeconds:F1}s)", $"Saved: {file} ({report.Elapsed.TotalSeconds:F1}s)")
                : Tr($"保存: {file} ほか {count - 1} 枚 ({report.Elapsed.TotalSeconds:F1}s)", $"Saved: {file} +{count - 1} more ({report.Elapsed.TotalSeconds:F1}s)");
            _status.Set(_savedStatusText, DennokoStatusType.Success);
        }

        private sealed class ImmediateProgress : IProgress<BakeProgress>
        {
            private readonly Action<BakeProgress> _callback;
            public ImmediateProgress(Action<BakeProgress> callback) => _callback = callback;
            public void Report(BakeProgress value) => _callback(value);
        }
    }
}
