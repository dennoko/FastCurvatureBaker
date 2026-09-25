using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    [InitializeOnLoad]
    internal static class FastCurvatureBakerVersion
    {
        // version.json の GUID
        private const string VersionJsonGuid = "8f9b4c2e6d1a4570b3e5a1c9f802d7e4";
        private const string FallbackVersion = "1.0.0";
        private static string _currentCache = null;

        internal static string Current
        {
            get
            {
                if (string.IsNullOrEmpty(_currentCache))
                {
                    _currentCache = LoadLocalVersion();
                }
                return string.IsNullOrEmpty(_currentCache) ? FallbackVersion : _currentCache;
            }
        }

        internal const string RepoOwner       = "dennoko";
        internal const string RepoName        = "FastCurvatureBaker";
        internal const string RepoBranch      = "main";
        internal const string VersionFilePath = "version.json";

        internal const string VerCheckDoneKey    = "FastCurvatureBaker_VerCheck_Done";
        internal const string VerCheckErrorKey   = "FastCurvatureBaker_VerCheck_Error";
        internal const string VerCheckLatestKey  = "FastCurvatureBaker_VerCheck_Latest";
        internal const string VerCheckUrlKey     = "FastCurvatureBaker_VerCheck_Url";
        internal const string VerCheckMessageKey = "FastCurvatureBaker_VerCheck_Message";

        internal const string VerCheckLastAttemptKey   = "FastCurvatureBaker_VerCheck_LastAttemptUtc";
        internal const string VerCheckCachedLatestKey  = "FastCurvatureBaker_VerCheck_CachedLatest";
        internal const string VerCheckCachedUrlKey     = "FastCurvatureBaker_VerCheck_CachedUrl";
        internal const string VerCheckCachedMessageKey = "FastCurvatureBaker_VerCheck_CachedMessage";

        private const double CheckIntervalHours = 6.0;

        static FastCurvatureBakerVersion()
        {
            EditorApplication.delayCall += StartCheckBackgroundTask;
        }

        private static bool _checking;

        internal static void StartCheckBackgroundTask()
        {
            bool done  = SessionState.GetBool(VerCheckDoneKey, false);
            bool error = SessionState.GetBool(VerCheckErrorKey, false);
            if (done && !error) return;
            if (_checking) return;

            if (IsInCheckInterval())
            {
                ApplyCachedResult();
                return;
            }

            _checking = true;
            EditorPrefs.SetString(VerCheckLastAttemptKey, DateTime.UtcNow.ToString("o"));

            DennokoVersionChecker.CheckAsync(
                RepoOwner, RepoName, RepoBranch, VersionFilePath, Current, OnVersionChecked);
        }

        private static bool IsInCheckInterval()
        {
            var last = EditorPrefs.GetString(VerCheckLastAttemptKey, string.Empty);
            if (string.IsNullOrEmpty(last)) return false;
            if (!DateTime.TryParse(last, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var lastUtc)) return false;

            var elapsed = DateTime.UtcNow - lastUtc.ToUniversalTime();
            if (elapsed < TimeSpan.Zero) return false;

            return elapsed.TotalHours < CheckIntervalHours;
        }

        private static void ApplyCachedResult()
        {
            var latest = EditorPrefs.GetString(VerCheckCachedLatestKey, string.Empty);
            SessionState.SetBool(VerCheckDoneKey, true);
            SessionState.SetBool(VerCheckErrorKey, string.IsNullOrEmpty(latest));
            SessionState.SetString(VerCheckLatestKey, latest);
            SessionState.SetString(VerCheckUrlKey, EditorPrefs.GetString(VerCheckCachedUrlKey, string.Empty));
            SessionState.SetString(VerCheckMessageKey, EditorPrefs.GetString(VerCheckCachedMessageKey, string.Empty));

            RefreshOpenWindows();
        }

        internal static void ForceRecheck()
        {
            if (_checking) return;
            _currentCache = null;
            SessionState.SetBool(VerCheckDoneKey, false);
            SessionState.SetBool(VerCheckErrorKey, false);
            EditorPrefs.DeleteKey(VerCheckLastAttemptKey);
            StartCheckBackgroundTask();
        }

        private static void OnVersionChecked(DennokoVersionChecker.Result result)
        {
            _checking = false;
            bool failed = result.State == DennokoVersionChecker.State.Error;

            SessionState.SetBool(VerCheckDoneKey, true);
            SessionState.SetBool(VerCheckErrorKey, failed);
            SessionState.SetString(VerCheckLatestKey, result.LatestVersion ?? string.Empty);
            SessionState.SetString(VerCheckUrlKey, result.Url ?? string.Empty);
            SessionState.SetString(VerCheckMessageKey, result.Message ?? string.Empty);

            if (!failed)
            {
                EditorPrefs.SetString(VerCheckCachedLatestKey, result.LatestVersion ?? string.Empty);
                EditorPrefs.SetString(VerCheckCachedUrlKey, result.Url ?? string.Empty);
                EditorPrefs.SetString(VerCheckCachedMessageKey, result.Message ?? string.Empty);
            }

            RefreshOpenWindows();
        }

        private static void RefreshOpenWindows()
        {
            var windows = Resources.FindObjectsOfTypeAll<FastCurvatureBakerWindow>();
            if (windows == null) return;
            foreach (var w in windows)
            {
                if (w != null) w.LoadVersionResultFromSessionState();
            }
        }

        [Serializable]
        private class VersionInfo
        {
            public string version;
        }

        private static string LoadLocalVersion()
        {
            var v = TryReadVersion(AssetDatabase.GUIDToAssetPath(VersionJsonGuid));
            if (v != null) return v;

            return TryReadVersion(ResolveVersionJsonByScriptPath());
        }

        private static string TryReadVersion(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var info = JsonUtility.FromJson<VersionInfo>(File.ReadAllText(path));
                if (info != null && !string.IsNullOrEmpty(info.version)) return info.version;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FastCurvatureBakerVersion] Failed to read version.json ({path}): {e.Message}");
            }
            return null;
        }

        private static string ResolveVersionJsonByScriptPath([CallerFilePath] string scriptPath = null)
        {
            if (string.IsNullOrEmpty(scriptPath)) return null;
            var dir = Path.GetDirectoryName(scriptPath);
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "version.json");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}
