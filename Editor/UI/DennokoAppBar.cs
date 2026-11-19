using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// 標準ヘッダー (トップバー .dennoko-appbar) の右側と ↻ を接続する共通クラス。
    ///
    ///   [ツール名  v1.2.0 ↻]                     [EN] [?] [⋮]
    ///    version-label / version-reload-button    appbar-lang / appbar-manual / appbar-settings
    ///
    /// - 要素は名前で探し、無いものは無視する (言語を切り替えないツールは appbar-lang を UXML から消すだけ)。
    /// - バージョン表示の文言と状態クラスは references/version-check-guide.md の ApplyVersionLabel が書く。
    ///   ここでは ↻ と ⋮「更新を確認」から CheckUpdateRequested を出し、更新ありのときにラベルのクリックで公開配布ページを開く。
    /// - ⋮ のメニューは「ツール固有の項目 (上) → 区切り → 共通項目 (下)」の順。
    /// </summary>
    internal sealed class DennokoAppBar
    {
        /// <summary>⋮ メニューの上段 (ツール固有の項目)。</summary>
        public Action<GenericMenu> AddToolMenuItems;

        /// <summary>⋮「設定を初期値に戻す…」で、確認ダイアログの後に呼ぶ。null ならこの項目を出さない。</summary>
        public Action ResetAll;

        /// <summary>↻ と ⋮「更新を確認」。null ならメニュー項目を出さない (↻ は UXML から消す)。</summary>
        public event Action CheckUpdateRequested;

        /// <summary>言語ボタンを押したとき。true = English。</summary>
        public event Action<bool> LanguageChanged;

        /// <summary>公開配布ページ (Booth 等)。更新ありのラベルのクリック用（GitHub への直接導線は作らず、三点リーダーメニューにも含めない）。</summary>
        public string ReleaseUrl;

        /// <summary>マニュアル (? ボタン)。言語ごとに変えるときは LanguageChanged で書き換える。</summary>
        public string ManualUrl;

        private readonly string _toolName;
        private readonly Label _version;
        private readonly Button _lang;
        private bool _english;

        public DennokoAppBar(VisualElement root, string toolName, bool english = false)
        {
            _toolName = toolName;
            _english = english;

            var title = root.Q<Label>("appbar-title");
            if (title != null) title.text = toolName;

            _version = root.Q<Label>("version-label");
            _version?.RegisterCallback<ClickEvent>(_ =>
            {
                // 更新ありのときだけクリックで配布ページへ (version-check-guide の ApplyVersionLabel が --update を付ける)
                if (_version.ClassListContains("dennoko-version-label--update") && !string.IsNullOrEmpty(ReleaseUrl))
                    Application.OpenURL(ReleaseUrl);
            });

            var reload = root.Q<Button>("version-reload-button");
            DennokoIcons.Apply(reload, DennokoIcons.Refresh, "↻", "更新を確認");
            if (reload != null) reload.clicked += () => CheckUpdateRequested?.Invoke();

            _lang = root.Q<Button>("appbar-lang");
            if (_lang != null)
            {
                RefreshLanguageButton();
                _lang.clicked += () =>
                {
                    _english = !_english;
                    RefreshLanguageButton();
                    LanguageChanged?.Invoke(_english);
                };
            }

            var manual = root.Q<Button>("appbar-manual");
            DennokoIcons.Apply(manual, DennokoIcons.Help, "?", "マニュアルを開く");
            if (manual != null) manual.clicked += () =>
            {
                if (!string.IsNullOrEmpty(ManualUrl)) Application.OpenURL(ManualUrl);
            };

            var settings = root.Q<Button>("appbar-settings");
            DennokoIcons.Apply(settings, DennokoIcons.Menu, "⋮", "設定");
            if (settings != null) settings.clicked += () => ShowMenu(settings);
        }

        public bool English => _english;

        /// <summary>バージョン表示を更新ありにしたとき、クリックできることを示す (version-check-guide の ApplyVersionLabel から呼ぶ)。</summary>
        public void SetVersionClickable(bool clickable, string tooltip = null)
        {
            if (_version == null) return;
            _version.EnableInClassList("dennoko-version-label--clickable", clickable);
            _version.tooltip = tooltip;
        }

        // 言語ボタンは「切り替え先」の言語を出す (日本語表示中は EN)
        private void RefreshLanguageButton()
        {
            _lang.text = _english ? "JA" : "EN";
            _lang.tooltip = _english ? "日本語に切り替え" : "Switch to English";
        }

        private void ShowMenu(VisualElement anchor)
        {
            var menu = new GenericMenu();
            AddToolMenuItems?.Invoke(menu);
            if (menu.GetItemCount() > 0) menu.AddSeparator(string.Empty);

            if (ResetAll != null)
            {
                menu.AddItem(new GUIContent("設定を初期値に戻す…"), false, () =>
                {
                    if (EditorUtility.DisplayDialog(_toolName, "すべての設定を初期値に戻しますか？", "戻す", "キャンセル"))
                        ResetAll();
                });
            }
            if (CheckUpdateRequested != null)
                menu.AddItem(new GUIContent("更新を確認"), false, () => CheckUpdateRequested?.Invoke());

            menu.DropDown(anchor.worldBound);
        }
    }
}
