using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace MacroShelf
{
    internal enum UpdateOutcome
    {
        UpToDate,
        UpdateAvailable,
        NoReleases,
        Failed
    }

    internal class UpdateStatus
    {
        public UpdateOutcome Outcome;
        public string LatestVersion; // as published, without any leading "v"
        public string Detail;        // why it failed, for the log and the dialog

        public static UpdateStatus Failure(string detail)
        {
            UpdateStatus status = new UpdateStatus();
            status.Outcome = UpdateOutcome.Failed;
            status.Detail = detail;
            return status;
        }
    }

    // Asks GitHub whether a newer MacroShelf has been released.
    //
    // Manual only. The user clicking "Check for updates" is the consent, and
    // that one decision removes the need for a first-run prompt, a settings
    // flag, a daily throttle and any network traffic at startup. It also leaves
    // nothing for a corporate IT department to object to, since it does no more
    // than opening the releases page in a browser would.
    //
    // Nothing is downloaded and nothing is installed: the most this does is
    // hand a URL to the user's browser. There is no installer logic here and no
    // security surface to get wrong.
    internal static class UpdateChecker
    {
        public const string ReleasesPageUrl = "https://github.com/james-debono/macroshelf-sw-addin/releases";
        private const string LatestReleaseApi =
            "https://api.github.com/repos/james-debono/macroshelf-sw-addin/releases/latest";

        // Tests point this at a repository that actually has releases, so the
        // request, the TLS handshake and the parse can be exercised for real
        // (same idea as Settings.SettingsPathOverride).
        internal static string ApiUrlOverride;

        private static string ApiUrl()
        {
            return string.IsNullOrEmpty(ApiUrlOverride) ? LatestReleaseApi : ApiUrlOverride;
        }

        private const int RequestTimeoutMs = 15000;
        private const int MaxResponseBytes = 1024 * 1024;

        // Successful answers are held briefly so that repeated clicks cost
        // nothing and cannot burn through GitHub's unauthenticated rate limit.
        // Failures are deliberately NOT cached: somebody who reconnects their
        // network and clicks again should get a real attempt, not five minutes
        // of the same complaint.
        private static readonly TimeSpan CacheLife = TimeSpan.FromMinutes(5);
        private static readonly object Gate = new object();
        private static UpdateStatus _cached;
        private static DateTime _cachedAtUtc;

        // The last answer, if it is still fresh; otherwise null. Used by the
        // Library flyout, which must not make a network request just because
        // somebody opened a menu.
        public static UpdateStatus Cached()
        {
            lock (Gate)
            {
                if (_cached == null || DateTime.UtcNow - _cachedAtUtc > CacheLife)
                {
                    return null;
                }
                return _cached;
            }
        }

        // Blocking. Call it off the UI thread.
        public static UpdateStatus Check(string installedVersion)
        {
            UpdateStatus cached = Cached();
            if (cached != null)
            {
                return cached;
            }
            UpdateStatus status = Fetch(installedVersion);
            if (status.Outcome != UpdateOutcome.Failed)
            {
                lock (Gate)
                {
                    _cached = status;
                    _cachedAtUtc = DateTime.UtcNow;
                }
            }
            return status;
        }

        internal static void ClearCache()
        {
            lock (Gate)
            {
                _cached = null;
            }
        }

        private static UpdateStatus Fetch(string installedVersion)
        {
            try
            {
                // .NET Framework's default protocol list predates TLS 1.2 on
                // older machines and GitHub accepts nothing less, which fails
                // in a thoroughly confusing way. OR it in rather than assign,
                // so nothing SolidWorks or another add-in turned on is lost.
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                }
                catch { }

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ApiUrl());
                request.Method = "GET";
                request.Timeout = RequestTimeoutMs;
                request.ReadWriteTimeout = RequestTimeoutMs;
                // GitHub rejects requests that do not identify themselves.
                request.UserAgent = "MacroShelf/" + (installedVersion == null ? "0" : installedVersion);
                request.Accept = "application/vnd.github+json";

                string json;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    json = ReadCapped(response);
                }

                string tag = ParseTagName(json);
                if (string.IsNullOrEmpty(tag))
                {
                    return UpdateStatus.Failure("GitHub's reply did not name a release.");
                }

                string latest = StripLeadingV(tag);
                if (!IsComparable(latest))
                {
                    // Not every project tags releases with a version number -
                    // WiX's own latest is "wix3141rtm". Saying "up to date" on
                    // the strength of a tag that cannot be compared would be a
                    // false reassurance, so say plainly that it is unknown.
                    return UpdateStatus.Failure("GitHub's latest release is tagged \""
                        + tag + "\", which is not a version number this can compare.");
                }

                UpdateStatus status = new UpdateStatus();
                status.LatestVersion = latest;
                status.Outcome = Compare(latest, installedVersion) > 0
                    ? UpdateOutcome.UpdateAvailable
                    : UpdateOutcome.UpToDate;
                return status;
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    // A repository with no published releases answers 404, and
                    // so does one that does not exist yet. Either way it is not
                    // a failure worth alarming anybody about.
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        UpdateStatus none = new UpdateStatus();
                        none.Outcome = UpdateOutcome.NoReleases;
                        return none;
                    }
                    if ((int)response.StatusCode == 403)
                    {
                        return UpdateStatus.Failure(
                            "GitHub replied 403, which usually means too many checks from this "
                            + "network for now. Try again later.");
                    }
                    return UpdateStatus.Failure("GitHub replied " + (int)response.StatusCode
                        + " " + response.StatusCode + ".");
                }
                return UpdateStatus.Failure(ex.Message);
            }
            catch (Exception ex)
            {
                return UpdateStatus.Failure(ex.Message);
            }
        }

        // Reads the body but refuses to grow without bound on a reply that is
        // not what was expected.
        private static string ReadCapped(HttpWebResponse response)
        {
            using (Stream stream = response.GetResponseStream())
            {
                if (stream == null)
                {
                    return null;
                }
                byte[] buffer = new byte[8192];
                using (MemoryStream collected = new MemoryStream())
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (collected.Length + read > MaxResponseBytes)
                        {
                            break;
                        }
                        collected.Write(buffer, 0, read);
                    }
                    return Encoding.UTF8.GetString(collected.ToArray());
                }
            }
        }

        // internal for the tests: the shape of GitHub's reply, without GitHub.
        internal static string ParseTagName(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = MaxResponseBytes;
                Dictionary<string, object> root =
                    serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null)
                {
                    return null;
                }
                object tag;
                if (!root.TryGetValue("tag_name", out tag) || tag == null)
                {
                    return null;
                }
                string text = Convert.ToString(tag).Trim();
                return text.Length == 0 ? null : text;
            }
            catch
            {
                return null;
            }
        }

        internal static string StripLeadingV(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return tag;
            }
            string text = tag.Trim();
            if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V'))
            {
                return text.Substring(1);
            }
            return text;
        }

        // Compares the first three fields and no more. A release is numbered
        // x.y.z; MacroShelf's fourth field only ever marks a build handed round
        // for testing, and nobody should be told that a released x.y.z is
        // "newer" than the test build of x.y.z they are running.
        internal static int Compare(string left, string right)
        {
            int[] a = Fields(left);
            int[] b = Fields(right);
            for (int i = 0; i < 3; i++)
            {
                if (a[i] != b[i])
                {
                    return a[i] < b[i] ? -1 : 1;
                }
            }
            return 0;
        }

        // Whether the text begins with something that can be read as a version
        // at all. A tag like "wix3141rtm" or "nightly" cannot, and must not be
        // silently treated as 0.0.0.
        internal static bool IsComparable(string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return false;
            }
            string[] parts = StripLeadingV(version).Split('.');
            int value;
            return parts.Length >= 2 && TryLeadingNumber(parts[0], out value);
        }

        private static int[] Fields(string version)
        {
            int[] fields = new int[3];
            if (string.IsNullOrEmpty(version))
            {
                return fields;
            }
            string[] parts = StripLeadingV(version).Split('.');
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                int value;
                if (!TryLeadingNumber(parts[i], out value))
                {
                    break; // e.g. "1.0.0-beta2": stop rather than guess
                }
                fields[i] = value;
            }
            return fields;
        }

        private static bool TryLeadingNumber(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            int end = 0;
            while (end < text.Length && text[end] >= '0' && text[end] <= '9')
            {
                end++;
            }
            if (end == 0)
            {
                return false;
            }
            return int.TryParse(text.Substring(0, end), out value);
        }
    }
}
