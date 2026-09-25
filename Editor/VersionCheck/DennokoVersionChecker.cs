using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// GitHub Public リポジトリ上の version.json を取得し、ローカル版と比較する
    /// エディタ専用の自己完結アップデートチェッカー。
    /// </summary>
    public static class DennokoVersionChecker
    {
        public enum State { Checking, UpToDate, UpdateAvailable, Error }

        /// <summary>応答が無い場合に待ち続けないための上限（秒）。</summary>
        private const int RequestTimeoutSeconds = 10;

        public struct Result
        {
            public State State;
            public string LocalVersion;
            public string LatestVersion;
            public string Url;
            public string Message;
        }

        [Serializable]
        private class VersionInfo
        {
            public string version;
            public string url;
            public string message;
        }

        /// <summary>
        /// version.json を非同期取得して結果を onResult に渡す。例外は投げず、失敗時は
        /// State.Error を返す。onResult は Unity のメインスレッド上で呼ばれる。
        /// </summary>
        public static void CheckAsync(
            string owner, string repo, string branch, string filePath,
            string localVersion, Action<Result> onResult)
        {
            if (onResult == null) return;

            var branches = new List<string>();
            if (!string.IsNullOrEmpty(branch)) branches.Add(branch);
            if (!branches.Contains("main", StringComparer.OrdinalIgnoreCase)) branches.Add("main");

            TryBranch(owner, repo, branches, 0, filePath, localVersion, onResult);
        }

        private static void TryBranch(
            string owner, string repo, List<string> branches, int index,
            string filePath, string localVersion, Action<Result> onResult)
        {
            if (index >= branches.Count)
            {
                onResult(Error(localVersion));
                return;
            }

            UnityWebRequest req;
            try
            {
                var url = $"https://raw.githubusercontent.com/{owner}/{repo}/{branches[index]}/{filePath}";
                req = UnityWebRequest.Get(url);
                req.SetRequestHeader("User-Agent", $"{repo}-VersionChecker");
                req.timeout = RequestTimeoutSeconds;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DennokoVersionChecker] request build failed: {e.Message}");
                onResult(Error(localVersion));
                return;
            }

            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                Result result;
                long httpCode = 0;
                try
                {
                    result = BuildResult(req, localVersion);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DennokoVersionChecker] callback failed: {e.Message}");
                    result = Error(localVersion);
                }
                finally
                {
                    httpCode = req.responseCode;
                    req.Dispose();
                }

                bool rateLimited = httpCode == 403 || httpCode == 429;

                if (result.State == State.Error && !rateLimited && index + 1 < branches.Count)
                {
                    TryBranch(owner, repo, branches, index + 1, filePath, localVersion, onResult);
                }
                else
                {
                    onResult(result);
                }
            };
        }

        private static Result BuildResult(UnityWebRequest req, string localVersion)
        {
            string url = req != null ? req.url : "(null)";
#if UNITY_2020_2_OR_NEWER
            bool hasError = req.result != UnityWebRequest.Result.Success;
#else
            bool hasError = req.isNetworkError || req.isHttpError;
#endif
            if (hasError)
            {
                var body = req.downloadHandler != null ? req.downloadHandler.text : null;
                if (!string.IsNullOrEmpty(body) && body.Length > 300) body = body.Substring(0, 300) + "...";
                Debug.LogWarning($"[DennokoVersionChecker] 取得失敗: url={url} httpCode={req.responseCode} error={req.error} body={body}");
                return Error(localVersion);
            }

            var json = req.downloadHandler != null ? req.downloadHandler.text : null;
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning($"[DennokoVersionChecker] 取得失敗: レスポンスが空。url={url} httpCode={req.responseCode}");
                return Error(localVersion);
            }

            VersionInfo info;
            try { info = JsonUtility.FromJson<VersionInfo>(json); }
            catch (Exception e) { Debug.LogWarning($"[DennokoVersionChecker] 取得失敗: JSON パース失敗: {e.Message} url={url}"); return Error(localVersion); }

            if (info == null || string.IsNullOrEmpty(info.version))
            {
                Debug.LogWarning($"[DennokoVersionChecker] 取得失敗: version フィールドが空。url={url}");
                return Error(localVersion);
            }

            var state = IsNewer(info.version, localVersion) ? State.UpdateAvailable : State.UpToDate;
            return new Result
            {
                State = state,
                LocalVersion = localVersion,
                LatestVersion = info.version,
                Url = info.url,
                Message = info.message,
            };
        }

        private static Result Error(string localVersion) => new Result
        {
            State = State.Error,
            LocalVersion = localVersion,
            LatestVersion = null,
            Url = null,
            Message = null,
        };

        public static bool IsUpdateAvailable(string latestVersion, string localVersion)
            => IsNewer(latestVersion, localVersion);

        private static bool IsNewer(string latest, string local)
        {
            var l = Normalize(latest);
            var c = Normalize(local);
            if (Version.TryParse(l, out var vLatest) && Version.TryParse(c, out var vLocal))
                return vLatest > vLocal;
            return !string.Equals(l, c, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string v)
        {
            if (string.IsNullOrEmpty(v)) return "0.0.0";
            v = v.Trim().Trim('﻿').Trim();
            if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V')) v = v.Substring(1);
            int cut = v.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) v = v.Substring(0, cut);
            if (string.IsNullOrEmpty(v)) return "0.0.0";

            var parts = v.Split('.');
            if (parts.Length >= 3) return v;
            var padded = new string[3];
            for (int i = 0; i < 3; i++)
                padded[i] = (i < parts.Length && !string.IsNullOrEmpty(parts[i])) ? parts[i] : "0";
            return string.Join(".", padded);
        }
    }
}
