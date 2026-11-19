using UnityEngine;
using UnityEngine.UIElements;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    internal enum DennokoStatusType { Info, Success, Warning, Error, Busy }

    /// <summary>
    /// 標準フッターのステータスバー (.dennoko-status の Label) を操作する共通クラス。
    ///   Info    : 通常 (Ready など)。自動では戻らない
    ///   Success / Warning / Error : 3 秒で Ready に戻る
    ///   Busy    : 処理中。progress (0〜1) を渡すと下端に進捗線を描く。自動では戻らない
    ///
    /// ⚠ 進捗線は Label の子要素にしない。Label に子要素を足すと文字の高さが測られなくなり、
    ///   ステータスが上下の余白 (8px) だけの高さに潰れる。ここでは generateVisualContent で Label 自身に描く。
    ///   線の色は USS の --dennoko-status-progress (DennokoTheme.uss で selection 色) から読む。
    /// </summary>
    internal sealed class DennokoStatus
    {
        private static readonly CustomStyleProperty<Color> ProgressColorProperty =
            new CustomStyleProperty<Color>("--dennoko-status-progress");

        private readonly Label _label;
        private readonly string _readyText;
        private Color _progressColor = Color.clear;
        private float _progress = -1f;
        private IVisualElementScheduledItem _reset;

        public DennokoStatus(Label label, string readyText = "Ready", long autoResetMs = 3000)
        {
            _label = label;
            _readyText = readyText;
            AutoResetMs = autoResetMs;
            if (_label == null) return;   // UXML の読み込み失敗・名前の変更でも NRE にしない

            _label.generateVisualContent += DrawProgress;
            _label.RegisterCallback<CustomStyleResolvedEvent>(e =>
            {
                if (e.customStyle.TryGetValue(ProgressColorProperty, out var color)) _progressColor = color;
                _label.MarkDirtyRepaint();
            });
        }

        public long AutoResetMs { get; set; }

        public void Set(string message, DennokoStatusType type = DennokoStatusType.Info, float progress = -1f)
        {
            if (_label == null) return;
            _label.text = message;
            _label.EnableInClassList("dennoko-status--success", type == DennokoStatusType.Success);
            _label.EnableInClassList("dennoko-status--warning", type == DennokoStatusType.Warning);
            _label.EnableInClassList("dennoko-status--error",   type == DennokoStatusType.Error);
            _label.EnableInClassList("dennoko-status--busy",    type == DennokoStatusType.Busy);

            _progress = type == DennokoStatusType.Busy && progress >= 0f ? Mathf.Clamp01(progress) : -1f;
            _label.MarkDirtyRepaint();

            _reset?.Pause();
            if (type == DennokoStatusType.Success || type == DennokoStatusType.Warning || type == DennokoStatusType.Error)
                _reset = _label.schedule.Execute(Ready).StartingIn(AutoResetMs);
        }

        public void Ready() => Set(_readyText);

        private void DrawProgress(MeshGenerationContext context)
        {
            if (_progress < 0f || _progressColor.a <= 0f) return;
            float width = _label.layout.width * _progress;
            float bottom = _label.layout.height;
            if (width <= 0f || float.IsNaN(bottom)) return;

            var painter = context.painter2D;
            painter.fillColor = _progressColor;
            painter.BeginPath();
            painter.MoveTo(new Vector2(0f, bottom - 2f));
            painter.LineTo(new Vector2(width, bottom - 2f));
            painter.LineTo(new Vector2(width, bottom));
            painter.LineTo(new Vector2(0f, bottom));
            painter.ClosePath();
            painter.Fill();
        }
    }
}
