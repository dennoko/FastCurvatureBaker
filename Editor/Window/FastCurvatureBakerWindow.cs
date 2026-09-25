using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// dennokoworks フローティングデザインシステムに準拠した Fast Curvature Baker の UI Toolkit ウィンドウ。
    /// 日本語/英語のローカライズに対応。
    /// </summary>
    public sealed class FastCurvatureBakerWindow : EditorWindow
    {
        private const string Title = "Fast Curvature Baker";

        private static string Tr(string ja, string en) => FastCurvatureBakerLoc.Tr(ja, en);
        private static string Tr(string ja, string en, params object[] args) => FastCurvatureBakerLoc.Tr(ja, en, args);
        private static bool IsJapanese => FastCurvatureBakerLoc.IsJapanese;

        private const string UXML_GUID = "0123456789abcdef0123456789abcde6";
        private const string USS_GUID  = "e123456789abcdef0123456789abcde4";

        public enum StatusType { Info, Success, Error }

        [SerializeField] private List<GameObject> _targets = new List<GameObject>();
        [SerializeField] private CurvatureBakeSettings _settings = new CurvatureBakeSettings();

        private CancellationTokenSource _cancellation;
        private bool IsBaking => _cancellation != null;

        // UI 要素
        private VisualElement _root;
        private Label _versionLabel;
        private Button _versionReloadButton;
        private Button _langButton;

        private Label _targetsCardTitle;
        private Button _addSelectedButton;
        private Button _clearTargetsButton;
        private VisualElement _targetsContainer;
        private VisualElement _dropArea;
        private Label _dropHintLabel;

        private Label _bakeSettingsCardTitle;
        private EnumField _modeField;
        private DropdownField _resolutionField;
        private DropdownField _uvChannelField;
        private EnumField _qualityField;
        private Toggle _supersampleToggle;

        private Label _hardEdgesCardTitle;
        private FloatField _edgeWidthField;
        private Slider _edgeStrengthSlider;

        private Label _smoothSurfacesCardTitle;
        private FloatField _radiusField;
        private Slider _strengthSlider;
        private EnumField _sourceField;

        private Foldout _advancedFoldout;
        private Toggle _sameComponentToggle;
        private Slider _normalRejectionSlider;
        private SliderInt _blurPassesSlider;
        private SliderInt _dilationSlider;

        private Label _outputCardTitle;
        private Toggle _overwriteToggle;
        private Label _outputInfoLabel;

        private Button _bakeButton;
        private Button _resetSettingsButton;
        private Label _statusLabel;
        private IVisualElementScheduledItem _statusResetSchedule;

        private DennokoVersionChecker.Result _versionResult = new DennokoVersionChecker.Result
        {
            State = DennokoVersionChecker.State.Checking,
            LocalVersion = "0.0.0"
        };

        [MenuItem("dennokoworks/Fast Curvature Baker")]
        public static void Open()
        {
            var window = GetWindow<FastCurvatureBakerWindow>(Title);
            window.titleContent = new GUIContent(Title);
            window.minSize = new Vector2(380, 540);
        }

        private void OnEnable()
        {
            FastCurvatureBakerLoc.OnLanguageChanged += ApplyLanguage;
        }

        private void OnDisable()
        {
            FastCurvatureBakerLoc.OnLanguageChanged -= ApplyLanguage;
            _cancellation?.Cancel();
        }

        public void CreateGUI()
        {
            _root = rootVisualElement;
            _root.Clear();

            // テーマ非依存のためのルートクラスを適用
            _root.AddToClassList("dennoko-root");
            _root.style.backgroundColor = (Color)new Color32(0x12, 0x12, 0x12, 0xFF);
            _root.style.flexGrow = 1;

            // 標準フォント: OS のメイリオを全体に適用
            DennokoUIFont.Apply(_root);

            // USS のロードと適用
            var uss = LoadUss();
            if (uss != null)
            {
                _root.styleSheets.Add(uss);
            }
            else
            {
                Debug.LogWarning($"[{nameof(FastCurvatureBakerWindow)}] USS が見つかりません。GUID を確認してください: {USS_GUID}");
            }

            // UXML のロードとインスタンス化
            var uxml = LoadUxml();
            if (uxml == null)
            {
                _root.Add(new Label("UXML Asset が見つかりません。GUID を確認してください。"));
                return;
            }
            uxml.CloneTree(_root);

            InitializeUI(_root);
            StartVersionCheck();
        }

        private static VisualTreeAsset LoadUxml()
        {
            string path = AssetDatabase.GUIDToAssetPath(UXML_GUID);
            if (!string.IsNullOrEmpty(path))
            {
                var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
                if (asset != null) return asset;
            }

            var guids = AssetDatabase.FindAssets("FastCurvatureBakerWindow t:VisualTreeAsset");
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(p);
                if (asset != null) return asset;
            }
            return null;
        }

        private static StyleSheet LoadUss()
        {
            string path = AssetDatabase.GUIDToAssetPath(USS_GUID);
            if (!string.IsNullOrEmpty(path))
            {
                var asset = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (asset != null) return asset;
            }

            var guids = AssetDatabase.FindAssets("DennokoTheme t:StyleSheet");
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                var asset = AssetDatabase.LoadAssetAtPath<StyleSheet>(p);
                if (asset != null) return asset;
            }
            return null;
        }

        private void InitializeUI(VisualElement root)
        {
            _statusLabel = root.Q<Label>("status-label");

            // 言語切替ボタン
            _langButton = root.Q<Button>("lang-button");
            if (_langButton != null)
            {
                _langButton.clicked += FastCurvatureBakerLoc.ToggleLanguage;
            }

            // バージョン表示関連
            _versionLabel = root.Q<Label>("version-label");
            _versionReloadButton = root.Q<Button>("version-reload-button");
            if (_versionReloadButton != null)
            {
                _versionReloadButton.clicked += () =>
                {
                    FastCurvatureBakerVersion.ForceRecheck();
                    LoadVersionResultFromSessionState();
                };
            }

            // ターゲット管理関連
            _targetsCardTitle = root.Q<Label>("targets-card-title");
            _targetsContainer = root.Q<VisualElement>("targets-container");
            _dropArea = root.Q<VisualElement>("drop-area");
            _dropHintLabel = root.Q<Label>("drop-hint-label");
            _addSelectedButton = root.Q<Button>("add-selected-button");
            _clearTargetsButton = root.Q<Button>("clear-targets-button");

            if (_addSelectedButton != null)
                _addSelectedButton.clicked += AddSelection;

            if (_clearTargetsButton != null)
            {
                _clearTargetsButton.clicked += () =>
                {
                    Undo.RecordObject(this, "Clear Targets");
                    _targets.Clear();
                    RefreshTargetsList();
                };
            }

            SetupDropArea(_dropArea);
            RefreshTargetsList();

            // 設定フィールド
            _bakeSettingsCardTitle = root.Q<Label>("bake-settings-card-title");
            _modeField = root.Q<EnumField>("mode-field");
            _resolutionField = root.Q<DropdownField>("resolution-field");
            _uvChannelField = root.Q<DropdownField>("uv-channel-field");
            _qualityField = root.Q<EnumField>("quality-field");
            _supersampleToggle = root.Q<Toggle>("supersample-toggle");

            _hardEdgesCardTitle = root.Q<Label>("hard-edges-card-title");
            _edgeWidthField = root.Q<FloatField>("edge-width-field");
            _edgeStrengthSlider = root.Q<Slider>("edge-strength-slider");

            _smoothSurfacesCardTitle = root.Q<Label>("smooth-surfaces-card-title");
            _radiusField = root.Q<FloatField>("radius-field");
            _strengthSlider = root.Q<Slider>("strength-slider");
            _sourceField = root.Q<EnumField>("source-field");

            _advancedFoldout = root.Q<Foldout>("advanced-foldout");
            _sameComponentToggle = root.Q<Toggle>("same-component-toggle");
            _normalRejectionSlider = root.Q<Slider>("normal-rejection-slider");
            _blurPassesSlider = root.Q<SliderInt>("blur-passes-slider");
            _dilationSlider = root.Q<SliderInt>("dilation-slider");

            _outputCardTitle = root.Q<Label>("output-card-title");
            _overwriteToggle = root.Q<Toggle>("overwrite-toggle");
            _outputInfoLabel = root.Q<Label>("output-info-label");

            // アクションボタン
            _bakeButton = root.Q<Button>("bake-button");
            _resetSettingsButton = root.Q<Button>("reset-settings-button");

            if (_bakeButton != null)
                _bakeButton.clicked += Bake;

            if (_resetSettingsButton != null)
            {
                _resetSettingsButton.clicked += () =>
                {
                    Undo.RecordObject(this, "Reset Curvature Bake Settings");
                    _settings = new CurvatureBakeSettings();
                    SyncSettingsToUI();
                    SetStatus(Tr("設定を初期値にリセットしました。", "Settings reset to default."), StatusType.Info);
                };
            }

            SetupDropdownChoices();
            SyncSettingsToUI();
            BindSettingsEvents();
            ApplyLanguage();
        }

        private void SetupDropdownChoices()
        {
            if (_resolutionField != null)
            {
                _resolutionField.choices = CurvatureBakeSettings.SupportedResolutions
                    .Select(r => r.ToString())
                    .ToList();
            }

            if (_uvChannelField != null)
            {
                _uvChannelField.choices = Enumerable.Range(0, 8)
                    .Select(i => $"UV{i}")
                    .ToList();
            }
        }

        private void SyncSettingsToUI()
        {
            _settings.Validate();

            if (_modeField != null)
                _modeField.Init(_settings.Mode);

            if (_resolutionField != null)
                _resolutionField.value = _settings.Resolution.ToString();

            if (_uvChannelField != null)
                _uvChannelField.value = $"UV{_settings.UVChannel}";

            if (_qualityField != null)
                _qualityField.Init(_settings.Quality);

            if (_supersampleToggle != null)
                _supersampleToggle.value = _settings.Supersample;

            if (_edgeWidthField != null)
                _edgeWidthField.value = _settings.EdgeWidth;

            if (_edgeStrengthSlider != null)
                _edgeStrengthSlider.value = _settings.EdgeStrength;

            if (_radiusField != null)
                _radiusField.value = _settings.Radius;

            if (_strengthSlider != null)
                _strengthSlider.value = _settings.Strength;

            if (_sourceField != null)
                _sourceField.Init(_settings.Source);

            if (_sameComponentToggle != null)
                _sameComponentToggle.value = _settings.SameComponentOnly;

            if (_normalRejectionSlider != null)
                _normalRejectionSlider.value = _settings.NormalRejection;

            if (_blurPassesSlider != null)
                _blurPassesSlider.value = _settings.BlurPasses;

            if (_dilationSlider != null)
                _dilationSlider.value = _settings.DilationPixels;

            if (_overwriteToggle != null)
                _overwriteToggle.value = _settings.OverwriteExisting;
        }

        private void BindSettingsEvents()
        {
            _modeField?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Mode");
                _settings.Mode = (CurvatureBakeMode)evt.newValue;
            });

            _resolutionField?.RegisterValueChangedCallback(evt =>
            {
                if (int.TryParse(evt.newValue, out int r))
                {
                    Undo.RecordObject(this, "Change Resolution");
                    _settings.Resolution = r;
                    _settings.Validate();
                }
            });

            _uvChannelField?.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue != null && evt.newValue.StartsWith("UV") &&
                    int.TryParse(evt.newValue.Substring(2), out int uv))
                {
                    Undo.RecordObject(this, "Change UV Channel");
                    _settings.UVChannel = uv;
                }
            });

            _qualityField?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Quality");
                _settings.Quality = (BakeQuality)evt.newValue;
            });

            _supersampleToggle?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Supersample");
                _settings.Supersample = evt.newValue;
            });

            _edgeWidthField?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Edge Width");
                _settings.EdgeWidth = Mathf.Max(evt.newValue, 1e-5f);
            });

            _edgeStrengthSlider?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Edge Strength");
                _settings.EdgeStrength = Mathf.Max(evt.newValue, 0f);
            });

            _radiusField?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Radius");
                _settings.Radius = Mathf.Max(evt.newValue, 1e-5f);
            });

            _strengthSlider?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Strength");
                _settings.Strength = Mathf.Max(evt.newValue, 0f);
            });

            _sourceField?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Source");
                _settings.Source = (CurvatureSource)evt.newValue;
            });

            _sameComponentToggle?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Same Component");
                _settings.SameComponentOnly = evt.newValue;
            });

            _normalRejectionSlider?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Normal Rejection");
                _settings.NormalRejection = Mathf.Clamp(evt.newValue, -1f, CurvatureBakeSettings.MaxNormalRejection);
            });

            _blurPassesSlider?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Blur Passes");
                _settings.BlurPasses = Mathf.Clamp(evt.newValue, 0, 16);
            });

            _dilationSlider?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Dilation");
                _settings.DilationPixels = Mathf.Clamp(evt.newValue, 0, 64);
            });

            _overwriteToggle?.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "Change Overwrite");
                _settings.OverwriteExisting = evt.newValue;
            });
        }

        // ─── 言語適用 (ApplyLanguage) ───────────────────────────────────────

        private void ApplyLanguage()
        {
            if (_langButton != null)
            {
                _langButton.text = IsJapanese ? "EN" : "JA";
                _langButton.tooltip = Tr("英語表示に切り替えます", "Switch to Japanese");
            }

            if (_versionReloadButton != null)
            {
                _versionReloadButton.tooltip = Tr("アップデートを再確認", "Recheck for updates");
            }

            // セクションタイトル
            if (_targetsCardTitle != null) _targetsCardTitle.text = Tr("対象オブジェクト", "TARGET OBJECTS");
            if (_addSelectedButton != null) _addSelectedButton.text = Tr("選択中を追加", "Add Selected");
            if (_clearTargetsButton != null) _clearTargetsButton.text = Tr("クリア", "Clear");
            if (_dropHintLabel != null) _dropHintLabel.text = Tr("GameObject をドラッグ＆ドロップして追加", "Drag & Drop GameObjects here");

            if (_bakeSettingsCardTitle != null) _bakeSettingsCardTitle.text = Tr("ベイク基本設定", "BAKE SETTINGS");
            if (_modeField != null) _modeField.label = Tr("ベイクモード", "Bake Mode");
            if (_resolutionField != null) _resolutionField.label = Tr("解像度", "Resolution");
            if (_uvChannelField != null) _uvChannelField.label = Tr("UVチャンネル", "UV Channel");
            if (_qualityField != null) _qualityField.label = Tr("品質", "Quality");
            if (_supersampleToggle != null) _supersampleToggle.label = Tr("スーパーサンプリング 2x", "Supersample 2x");

            if (_hardEdgesCardTitle != null) _hardEdgesCardTitle.text = Tr("ハードエッジ (エッジ検出)", "HARD EDGES (Edge Detection)");
            if (_edgeWidthField != null) _edgeWidthField.label = Tr("エッジ幅 (m)", "Edge Width (m)");
            if (_edgeStrengthSlider != null) _edgeStrengthSlider.label = Tr("エッジ強度", "Edge Strength");

            if (_smoothSurfacesCardTitle != null) _smoothSurfacesCardTitle.text = Tr("曲面設定 (曲面検出)", "SMOOTH SURFACES (Curvature)");
            if (_radiusField != null) _radiusField.label = Tr("曲面計測半径 (m)", "Radius (m)");
            if (_strengthSlider != null) _strengthSlider.label = Tr("曲面強度", "Strength");
            if (_sourceField != null) _sourceField.label = Tr("計算ソース", "Source");

            if (_advancedFoldout != null) _advancedFoldout.text = Tr("詳細設定", "ADVANCED");
            if (_sameComponentToggle != null) _sameComponentToggle.label = Tr("同一接続パーツのみ", "Same Part Only");
            if (_normalRejectionSlider != null) _normalRejectionSlider.label = Tr("法線拒絶しきい値", "Normal Rejection");
            if (_blurPassesSlider != null) _blurPassesSlider.label = Tr("ブラー反復回数", "Blur Passes");
            if (_dilationSlider != null) _dilationSlider.label = Tr("ピクセル拡張幅 (px)", "Dilation (px)");

            if (_outputCardTitle != null) _outputCardTitle.text = Tr("出力設定", "OUTPUT SETTINGS");
            if (_overwriteToggle != null) _overwriteToggle.label = Tr("同名ファイルを上書き保存する", "Overwrite Existing Files");
            if (_outputInfoLabel != null)
            {
                _outputInfoLabel.text = Tr(
                    $"出力先: <ベーステクスチャのフォルダ>/{CurvatureTextureExporter.OutputFolderName}/ (テクスチャがない場合は {CurvatureTextureExporter.FallbackFolder}/)",
                    $"Output: <base texture folder>/{CurvatureTextureExporter.OutputFolderName}/ (or {CurvatureTextureExporter.FallbackFolder}/ if no texture)");
            }

            if (_bakeButton != null)
                _bakeButton.text = IsBaking ? Tr("ベイク中...", "Baking...") : Tr("曲率テクスチャをベイク", "Bake Curvature");
            if (_resetSettingsButton != null)
                _resetSettingsButton.text = Tr("設定を初期値にリセット", "Reset Settings");

            SetupPropertyExplanations();
            ApplyVersionLabel();
            RefreshTargetsList();
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
            AttachExplanation(_modeField, new PropertyExplanation
            {
                Name = Tr("Bake Mode (ベイクモード)", "Bake Mode"),
                Description = Tr(
                    "出力する曲率テクスチャの種類を指定します。\n・Default: 符号付き曲率 (平坦=0.5、凸=白、凹=黒)\n・Convex: 凸部のみ抽出 (エッジの摩耗・ハイライト用)\n・Concave: 凹部のみ抽出 (溝の汚れ・影マスク用)",
                    "Selects what the baked curvature texture represents:\n• Default: Signed curvature (0.5 = flat, brighter = convex, darker = concave)\n• Convex: White on convex edges (edge wear / scratch mask)\n• Concave: White in creases (dirt / grime mask)"),
                IncreaseImpact = Tr(
                    "【切替効果】用途に応じた白黒マスク（凸/凹）または全曲率マップを切り替えます。",
                    "[Mode change] Selects full signed curvature map or wear/dirt masks."),
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

            AttachExplanation(_qualityField, new PropertyExplanation
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

            AttachExplanation(_sourceField, new PropertyExplanation
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

        // ─── ターゲット管理 (Targets) ───────────────────────────────────────

        private void SetupDropArea(VisualElement dropArea)
        {
            if (dropArea == null) return;

            dropArea.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (DragAndDrop.objectReferences.OfType<GameObject>().Any())
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    dropArea.AddToClassList("dennoko-drop-area--dragover");
                }
                else
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                }
                evt.StopPropagation();
            });

            dropArea.RegisterCallback<DragLeaveEvent>(_ =>
            {
                dropArea.RemoveFromClassList("dennoko-drop-area--dragover");
            });

            dropArea.RegisterCallback<DragPerformEvent>(evt =>
            {
                DragAndDrop.AcceptDrag();
                dropArea.RemoveFromClassList("dennoko-drop-area--dragover");

                var dropped = DragAndDrop.objectReferences.OfType<GameObject>();
                Undo.RecordObject(this, "Add Dropped Targets");
                bool added = false;
                foreach (var go in dropped)
                {
                    if (go != null && !_targets.Contains(go))
                    {
                        _targets.Add(go);
                        added = true;
                    }
                }
                if (added) RefreshTargetsList();
                evt.StopPropagation();
            });
        }

        private void AddSelection()
        {
            Undo.RecordObject(this, "Add Targets");
            bool added = false;
            foreach (var go in Selection.gameObjects)
            {
                if (!EditorUtility.IsPersistent(go) && !_targets.Contains(go))
                {
                    _targets.Add(go);
                    added = true;
                }
            }
            _targets.RemoveAll(t => t == null);
            if (added)
            {
                RefreshTargetsList();
                SetStatus(Tr($"{_targets.Count} 個のターゲットが設定されています。", $"{_targets.Count} target(s) configured."), StatusType.Info);
            }
        }

        private void RefreshTargetsList()
        {
            if (_targetsContainer == null) return;
            _targetsContainer.Clear();

            _targets.RemoveAll(t => t == null);

            for (int i = 0; i < _targets.Count; i++)
            {
                int index = i;
                var row = new VisualElement();
                row.AddToClassList("dennoko-target-row");

                var objField = new ObjectField
                {
                    objectType = typeof(GameObject),
                    value = _targets[index]
                };
                objField.AddToClassList("dennoko-target-row-field");
                objField.RegisterValueChangedCallback(evt =>
                {
                    Undo.RecordObject(this, "Change Target Object");
                    _targets[index] = evt.newValue as GameObject;
                });

                var removeBtn = new Button(() =>
                {
                    Undo.RecordObject(this, "Remove Target");
                    _targets.RemoveAt(index);
                    RefreshTargetsList();
                })
                {
                    text = "×"
                };
                removeBtn.AddToClassList("dennoko-target-row-remove");
                removeBtn.tooltip = Tr("このターゲットを一覧から除外", "Remove this target from list");

                row.Add(objField);
                row.Add(removeBtn);
                _targetsContainer.Add(row);
            }
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
                    text = $"{baseText}  " + Tr("確認中...", "Checking...");
                    break;
                default:
                    text = baseText;
                    break;
            }
            _versionLabel.text = text;
            _versionLabel.EnableInClassList("dennoko-version-label--update", update);
            _versionLabel.EnableInClassList("dennoko-version-label--error", error);
        }

        // ─── ステータスバー ─────────────────────────────────────────────────

        private void SetStatus(string message, StatusType type, long autoResetMs = 3000)
        {
            if (_statusLabel == null) return;

            _statusLabel.text = message;
            _statusLabel.EnableInClassList("dennoko-status--success", type == StatusType.Success);
            _statusLabel.EnableInClassList("dennoko-status--error",   type == StatusType.Error);

            _statusResetSchedule?.Pause();
            if (type != StatusType.Info)
            {
                _statusResetSchedule = _statusLabel.schedule
                    .Execute(() => SetStatus("Ready", StatusType.Info))
                    .StartingIn(autoResetMs);
            }
        }

        // ─── ベイクパイプライン実行 ─────────────────────────────────────────

        private async void Bake()
        {
            var targets = _targets.Where(t => t != null).Distinct().ToList();
            if (targets.Count == 0)
            {
                SetStatus(Tr("ターゲットが設定されていません。", "No targets selected."), StatusType.Error);
                EditorUtility.DisplayDialog(Title, Tr("ターゲットの GameObject を1つ以上追加してください。", "Please add at least one target GameObject."), "OK");
                return;
            }

            var renderers = CurvatureBakePipeline.CollectRenderers(targets);
            if (renderers.Count == 0)
            {
                SetStatus(Tr("対象に MeshRenderer または SkinnedMeshRenderer が見つかりません。", "No MeshRenderer or SkinnedMeshRenderer found in the targets."), StatusType.Error);
                EditorUtility.DisplayDialog(Title, Tr("対象に MeshRenderer または SkinnedMeshRenderer が見つかりません。", "No MeshRenderer or SkinnedMeshRenderer found in the targets."), "OK");
                return;
            }
            if (!EnsureReadable(renderers))
                return;

            _cancellation = new CancellationTokenSource();
            SetBakeInProgress(true);
            SetStatus(Tr("ベイク処理を実行中...", "Bake in progress..."), StatusType.Info);

            var progress = new ImmediateProgress(p =>
            {
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
                SetStatus(Tr($"ベイクが失敗しました: {e.Message}", $"Bake failed: {e.Message}"), StatusType.Error);
                EditorUtility.DisplayDialog(Title, Tr($"ベイクが失敗しました: {e.Message}", $"Bake failed: {e.Message}"), "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _cancellation.Dispose();
                _cancellation = null;
                SetBakeInProgress(false);
            }

            if (report != null)
            {
                ShowReport(report);
            }
        }

        private void SetBakeInProgress(bool baking)
        {
            if (_bakeButton != null)
            {
                _bakeButton.text = baking ? Tr("ベイク中...", "Baking...") : Tr("曲率テクスチャをベイク", "Bake Curvature");
                _bakeButton.SetEnabled(!baking);
            }
            _addSelectedButton?.SetEnabled(!baking);
            _clearTargetsButton?.SetEnabled(!baking);
            _resetSettingsButton?.SetEnabled(!baking);
        }

        private static bool EnsureReadable(List<Renderer> renderers)
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
            if (failed.Count == 0)
                return true;

            string failedNames = string.Join("\n", failed.Select(m => "- " + m.name));
            EditorUtility.DisplayDialog(Title,
                Tr($"以下のメッシュの Read/Write を有効化できませんでした:\n{failedNames}", $"Could not enable Read/Write for:\n{failedNames}"), "OK");
            return false;
        }

        private void ShowReport(BakeReport report)
        {
            if (report.SavedAssetPaths.Count > 0)
            {
                var first = AssetDatabase.LoadAssetAtPath<Texture2D>(report.SavedAssetPaths[0]);
                if (first != null)
                    EditorGUIUtility.PingObject(first);
            }

            if (report.Cancelled)
            {
                SetStatus(Tr("ベイクが中断されました。", "Bake cancelled."), StatusType.Info);
                EditorUtility.DisplayDialog(Title, Tr("ベイク処理がキャンセルされました。", "Bake process was cancelled."), "OK");
                return;
            }

            if (report.Errors.Count > 0)
            {
                string message = Tr(
                    $"{report.SavedAssetPaths.Count} 枚のテクスチャを保存しました ({report.Elapsed.TotalSeconds:F1}s)\n\nエラー:\n{string.Join("\n", report.Errors)}",
                    $"Saved {report.SavedAssetPaths.Count} texture(s) in {report.Elapsed.TotalSeconds:F1}s.\n\nErrors:\n{string.Join("\n", report.Errors)}");
                SetStatus(Tr("エラーが発生しました。", "An error occurred."), StatusType.Error);
                EditorUtility.DisplayDialog(Title, message, "OK");
                return;
            }

            SetStatus(Tr($"ベイク完了: {report.SavedAssetPaths.Count} 枚のテクスチャを保存しました ({report.Elapsed.TotalSeconds:F1}s)", $"Bake completed: Saved {report.SavedAssetPaths.Count} texture(s) ({report.Elapsed.TotalSeconds:F1}s)"), StatusType.Success);
        }

        private sealed class ImmediateProgress : IProgress<BakeProgress>
        {
            private readonly Action<BakeProgress> _callback;
            public ImmediateProgress(Action<BakeProgress> callback) => _callback = callback;
            public void Report(BakeProgress value) => _callback(value);
        }
    }
}
