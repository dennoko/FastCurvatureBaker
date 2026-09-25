using System;
using UnityEditor;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    public enum FastCurvatureBakerLanguage
    {
        Japanese = 0,
        English = 1
    }

    /// <summary>
    /// Fast Curvature Baker のローカライズ管理クラス。
    /// デフォルトは日本語で、英語との切り替えに対応します。
    /// </summary>
    public static class FastCurvatureBakerLoc
    {
        private const string PrefsKey = "FastCurvatureBaker_Language";

        public static event Action OnLanguageChanged;

        public static FastCurvatureBakerLanguage CurrentLanguage
        {
            get
            {
                int val = EditorPrefs.GetInt(PrefsKey, (int)FastCurvatureBakerLanguage.Japanese);
                return (FastCurvatureBakerLanguage)val;
            }
            set
            {
                if (CurrentLanguage != value)
                {
                    EditorPrefs.SetInt(PrefsKey, (int)value);
                    OnLanguageChanged?.Invoke();
                }
            }
        }

        public static bool IsJapanese => CurrentLanguage == FastCurvatureBakerLanguage.Japanese;

        public static string Tr(string ja, string en)
        {
            return IsJapanese ? ja : en;
        }

        public static string Tr(string ja, string en, params object[] args)
        {
            string format = IsJapanese ? ja : en;
            return string.Format(format, args);
        }

        public static void ToggleLanguage()
        {
            CurrentLanguage = IsJapanese ? FastCurvatureBakerLanguage.English : FastCurvatureBakerLanguage.Japanese;
        }
    }
}
