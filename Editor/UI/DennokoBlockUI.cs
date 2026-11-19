using System;
using System.Linq;
using UnityEngine.UIElements;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// ブロック (blocks/cards.md) の中で繰り返し使う小さな接続処理。
    /// 見た目はすべて DennokoBlocks.uss のクラスで決まり、ここではクラスの付け外しと要素の追加だけをする。
    /// </summary>
    internal static class DennokoBlockUI
    {
        public const string ActiveClass = "dennoko-button-active";

        public static void Show(VisualElement e, bool visible)
        {
            if (e != null) e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// グループ直下のボタンを排他選択にする (.dennoko-segmented / .dennoko-choice-group / タブ)。
        /// 選択中に .dennoko-button-active を付ける。戻り値で外から選択を変えられる (onSelect は呼ばない)。
        /// </summary>
        public static Action<int> BindChoice(VisualElement group, int initial, Action<int> onSelect)
        {
            if (group == null) return _ => { };
            var buttons = group.Children().OfType<Button>().ToArray();

            void Select(int index)
            {
                for (int i = 0; i < buttons.Length; i++)
                    buttons[i].EnableInClassList(ActiveClass, i == index);
            }

            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                buttons[i].clicked += () =>
                {
                    Select(index);
                    onSelect?.Invoke(index);
                };
            }
            Select(initial);
            return Select;
        }

        /// <summary>グループ直下のボタンを個別の ON / OFF にする (ミラー X / Y / Z など)。</summary>
        public static void BindToggles(VisualElement group, bool[] initial, Action<int, bool> onToggle)
        {
            if (group == null) return;
            var buttons = group.Children().OfType<Button>().ToArray();
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                bool on = initial != null && i < initial.Length && initial[i];
                buttons[i].EnableInClassList(ActiveClass, on);
                buttons[i].clicked += () =>
                {
                    bool next = !buttons[index].ClassListContains(ActiveClass);
                    buttons[index].EnableInClassList(ActiveClass, next);
                    onToggle?.Invoke(index, next);
                };
            }
        }

        /// <summary>
        /// B2 の「▶ 詳細」の開閉 (.dennoko-details)。閉じている間は見出し行の .dennoko-details__chips に
        /// 要約チップを出す (開いている間は USS で隠れる)。
        /// </summary>
        public static void BindDetails(VisualElement details, bool open = false, Action<bool> onChanged = null)
        {
            if (details == null) return;
            var header = details.Q(className: "dennoko-details__header");
            var arrow = details.Q<Label>(className: "dennoko-details__arrow");

            void Apply(bool isOpen)
            {
                details.EnableInClassList("dennoko-details--collapsed", !isOpen);
                details.EnableInClassList("dennoko-details--open", isOpen);
                if (arrow != null) arrow.text = isOpen ? "▼" : "▶";
                onChanged?.Invoke(isOpen);
            }

            header?.RegisterCallback<ClickEvent>(_ => Apply(details.ClassListContains("dennoko-details--collapsed")));
            Apply(open);
        }

        /// <summary>要約チップ (.dennoko-summary-chip: アイコン + 値)。初期値のままの項目はチップを出さない。</summary>
        public static VisualElement Chip(string iconNames, string text, string tooltip = null, Action onClick = null)
        {
            var chip = new VisualElement { tooltip = tooltip };
            chip.AddToClassList("dennoko-summary-chip");
            if (iconNames != null) chip.Add(DennokoIcons.MakeIcon(iconNames));
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("dennoko-summary-chip__text");
            chip.Add(label);
            if (onClick != null)
            {
                chip.AddToClassList("dennoko-summary-chip--clickable");
                chip.RegisterCallback<ClickEvent>(e =>
                {
                    onClick();
                    e.StopPropagation();   // 詳細の見出し行の開閉と重ねない
                });
            }
            return chip;
        }

        /// <summary>キーキャップ + 説明の 1 組を container (.dennoko-keyhints) に足す。</summary>
        public static void AddKeyHint(VisualElement container, string key, string text)
        {
            var cap = new Label(key);
            cap.AddToClassList("dennoko-keycap");
            container.Add(cap);
            var label = new Label(text);
            label.AddToClassList("dennoko-keyhint-label");
            container.Add(label);
        }

        /// <summary>
        /// TextField に薄い案内文を重ねる (Unity 2022.3 の TextField にはプレースホルダが無い)。
        /// UXML で TextField の子に Label を書くと読み込めないので、C# で足す。
        /// </summary>
        public static void AddPlaceholder(TextField field, string text)
        {
            if (field == null) return;
            var placeholder = new Label(text) { pickingMode = PickingMode.Ignore };
            placeholder.AddToClassList("dennoko-searchbar__placeholder");
            field.Add(placeholder);
            void Refresh() => Show(placeholder, string.IsNullOrEmpty(field.value));
            field.RegisterValueChangedCallback(_ => Refresh());
            Refresh();
        }
    }
}
