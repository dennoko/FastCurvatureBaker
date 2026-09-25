using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// dennokoworks UI の標準フォント（OS のメイリオ）を SDF FontAsset として生成・保持し、
    /// フォントキャッシュ消失によるテキスト崩壊を自己修復する共通クラス。
    /// </summary>
    [InitializeOnLoad]
    internal static class DennokoUIFont
    {
        private const string FamilyName = "Meiryo";
        private const string StyleName = "Regular";

        private const string AssetName = "Dennoko_UIFont_Meiryo";
        private const double TickIntervalSec = 2.0;

        private const string WarmupAscii =
            " !\"#$%&'()*+,-./0123456789:;<=>?@" +
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`" +
            "abcdefghijklmnopqrstuvwxyz{|}~";

        private const string WarmupJapanese =
            "適用保存解除追加削除設定選択中対象有効無効表示非切替更新確認取消閉開始了" +
            "はいいえ完了失敗警告情報エラー成功準備実行処理読込書出" +
            "曲率ベイク基本詳細項目解説増加入力出力保存先解像度品質スーパーサンプリング" +
            "ハードエッジ曲面なだらか法線シェーディングジオメトリテクスチャピクセルアイランド" +
            "境界拡張ブラーガウシアン反復回数上書き既存同名ファイル連番新規作成" +
            "符号平坦グレー凸部白凹部黒摩耗汚れ埃陰影直角角度直角折り目近接背面除外内積" +
            "しきい値サンプリング点数計算時間メモリ消費量ジャギー滑らかエイリアシング接続" +
            "連結成分衣服皮膚干渉隙間一体急峻溝底部継ぎ目最新版取得再確認カーソル合わせる" +
            "意味影響細かな薄い板裏表マイルド淡い階調微細起伏広範囲全体言語英語日本語戻す中枚";

        private static FontAsset _font;
        private static bool _unavailable;
        private static readonly List<VisualElement> _roots = new List<VisualElement>();
        private static bool _tickHooked;
        private static double _nextTick;

        static DennokoUIFont()
        {
            AssemblyReloadEvents.afterAssemblyReload += Revalidate;
            EditorApplication.playModeStateChanged += _ => Revalidate();
            EditorApplication.projectChanged += Revalidate;
        }

        public static void Apply(VisualElement root)
        {
            if (root == null) return;

            if (!_roots.Contains(root))
            {
                _roots.Add(root);
                root.RegisterCallback<AttachToPanelEvent>(_ => { Track(root); ApplyTo(root); });
                root.RegisterCallback<DetachFromPanelEvent>(_ => _roots.Remove(root));
            }

            HookTick(true);
            ApplyTo(root);
        }

        private static void Track(VisualElement root)
        {
            if (!_roots.Contains(root)) _roots.Add(root);
            HookTick(true);
        }

        private static void ApplyTo(VisualElement root)
        {
            var font = Get();

            root.style.unityFontDefinition = font != null
                ? new StyleFontDefinition(FontDefinition.FromSDFFont(font))
                : new StyleFontDefinition(StyleKeyword.Null);
        }

        private static FontAsset Get()
        {
            if (IsAlive(_font))
            {
                Protect(_font);
                return _font;
            }

            if (_unavailable) return null;

            _font = FindExisting() ?? Create();
            if (_font == null)
            {
                _unavailable = true;
                return null;
            }
            return _font;
        }

        private static FontAsset FindExisting()
        {
            foreach (var fa in Resources.FindObjectsOfTypeAll<FontAsset>())
            {
                if (fa == null || fa.name != AssetName || !IsAlive(fa)) continue;
                Protect(fa);
                return fa;
            }
            return null;
        }

        private static FontAsset Create()
        {
            try
            {
                var fa = FontAsset.CreateFontAsset(FamilyName, StyleName);
                if (fa == null) return null;

                fa.name = AssetName;
                Protect(fa);
                PreWarm(fa);
                Protect(fa);
                return fa;
            }
            catch
            {
                return null;
            }
        }

        private static void PreWarm(FontAsset fa)
        {
            try { fa.TryAddCharacters(WarmupAscii + WarmupJapanese, out _); }
            catch { }
        }

        private static void Protect(FontAsset fa)
        {
            if (fa == null) return;

            fa.hideFlags = HideFlags.HideAndDontSave;

            if (fa.material != null)
                fa.material.hideFlags = HideFlags.HideAndDontSave;

            var atlasTextures = fa.atlasTextures;
            if (atlasTextures == null) return;
            foreach (var tex in atlasTextures)
            {
                if (tex != null)
                    tex.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        private static bool IsAlive(FontAsset fa)
        {
            if (fa == null) return false;
            if (fa.material == null) return false;
            var atlasTextures = fa.atlasTextures;
            return atlasTextures != null && atlasTextures.Length > 0 && atlasTextures[0] != null;
        }

        private static void Revalidate()
        {
            if (IsAlive(_font))
            {
                Protect(_font);
                return;
            }

            _font = null;
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                var root = _roots[i];
                if (root == null || root.panel == null) { _roots.RemoveAt(i); continue; }
                ApplyTo(root);
            }
        }

        private static void HookTick(bool on)
        {
            if (on == _tickHooked) return;
            if (on) EditorApplication.update += Tick;
            else EditorApplication.update -= Tick;
            _tickHooked = on;
        }

        private static void Tick()
        {
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                var root = _roots[i];
                if (root == null || root.panel == null) _roots.RemoveAt(i);
            }
            if (_roots.Count == 0) { HookTick(false); return; }

            if (EditorApplication.timeSinceStartup < _nextTick) return;
            _nextTick = EditorApplication.timeSinceStartup + TickIntervalSec;

            Revalidate();
        }
    }
}
