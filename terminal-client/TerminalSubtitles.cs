using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Playables;

namespace Manimal.Terminal
{
    // CUTSCENE SUBTITLES (2026-08-19): retail 1.0 streams timed SubtitleParams
    // sequences from the backend (SubtitlesStorage keyed by SubtitlesId) — that
    // data never ships in client assets, so ours live in
    // plugin-data/terminal_subtitle_timings.json: speech-segmented from the voice
    // wavs (tools/subtitle_segmenter.py), text from the live locale dump. the
    // overlay follows director.time so pauses/hitches stay in sync.
    //
    // presentation: tarkov style — transparent black box with a thin white
    // border, EFT's own Bender font (grabbed from resources.assets on first
    // use, cached), speaker name bold, body regular. rich text so we can bold
    // the speaker tag inside the localized string.
    internal static class TerminalSubtitles
    {
        private static JObject _timings;
        private static SubtitleHost _host;
        private static Font _benderBold;
        private static Font _benderRegular;

        internal static void Show(string cutsceneKey, PlayableDirector director)
        {
            try
            {
                if (!Plugin.CutsceneSubtitles.Value || director == null) return;
                if (_timings == null)
                {
                    var path = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                        "plugin-data", "terminal_subtitle_timings.json");
                    if (!System.IO.File.Exists(path)) return;
                    _timings = JObject.Parse(System.IO.File.ReadAllText(path));
                }
                var lines = _timings["cutscenes"]?[cutsceneKey] as JArray;
                if (lines == null || lines.Count == 0)
                {
                    Plugin.Log.LogDebug($"[Subtitles] no timing data for '{cutsceneKey}'");
                    return;
                }
                Hide();
                var go = new GameObject("Terminal_Subtitles");
                _host = go.AddComponent<SubtitleHost>();
                _host.Director = director;
                // hold cap: even a long-tail authored window fades on its own after
                // ~6s so silences read as silences. 0.35 chars/second heuristic gives
                // longer strings a bit more room without exceeding the cap
                foreach (var l in lines)
                {
                    var text = l.Value<string>("text");
                    if (string.IsNullOrEmpty(text)) continue;
                    float start = l.Value<float>("start");
                    float end = l.Value<float>("end");
                    float readTime = Mathf.Clamp(text.Length * 0.05f + 1.5f, 2.0f, 6.0f);
                    float capped = start + readTime;
                    if (end > capped) end = capped;
                    _host.Lines.Add(new SubtitleHost.Line
                    {
                        Start = start,
                        End = end,
                        Text = text,
                    });
                }
                Plugin.Log.LogInfo($"[Subtitles] '{cutsceneKey}': {_host.Lines.Count} line(s) armed");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Subtitles] show failed: {e.Message}"); }
        }

        internal static void Hide()
        {
            if (_host) UnityEngine.Object.Destroy(_host.gameObject);
            _host = null;
        }

        // one-shot standalone subtitle for non-cutscene VO (loudspeaker, radio
        // barks). driven off unscaled time, tears down its own host after.
        internal static void ShowStandalone(string text, float duration)
        {
            try
            {
                if (!Plugin.CutsceneSubtitles.Value || string.IsNullOrEmpty(text) || duration <= 0f) return;
                var go = new GameObject("Terminal_Subtitle_OneShot");
                var oneshot = go.AddComponent<StandaloneHost>();
                oneshot.Text = text;
                oneshot.EndAt = Time.unscaledTime + duration;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Subtitles] standalone show failed: {e.Message}"); }
        }

        // grabs both weights on first call; falls back to the current skin's
        // font if EFT's copies didn't load
        internal static void EnsureFonts()
        {
            if (_benderBold != null && _benderRegular != null) return;
            try
            {
                foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (f == null || string.IsNullOrEmpty(f.name)) continue;
                    if (_benderBold == null && f.name.EndsWith("Bender Bold")) _benderBold = f;
                    else if (_benderRegular == null && f.name.EndsWith("Bender")) _benderRegular = f;
                }
            }
            catch { }
        }

        // shared draw so cutscene and standalone match visually — sized/placed
        // per retail screenshot (user 2026-08-19): ~1.7% font, ~45% max width,
        // panel hugs the text with tight padding, no visible border, sits low
        private static GUIStyle _sharedText;
        private static Texture2D _sharedBg;

        private static void DrawPanel(string txt)
        {
            if (string.IsNullOrEmpty(txt)) return;
            if (_sharedText == null)
            {
                EnsureFonts();
                _sharedText = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontSize = Mathf.RoundToInt(Screen.height * 0.017f),
                    richText = true,
                    wordWrap = true,
                    padding = new RectOffset(10, 10, 4, 4),
                    font = _benderRegular ?? GUI.skin.font,
                };
                _sharedText.normal.textColor = new Color(0.94f, 0.94f, 0.94f, 1f);
                // white 1x1 texture — we tint with GUI.color at draw time. IMGUI
                // doesnt reliably honor the pre-baked alpha on a black texture, so
                // this is how you get a translucent black panel through GUI.
                _sharedBg = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _sharedBg.SetPixel(0, 0, Color.white);
                _sharedBg.Apply();
                _sharedBg.hideFlags = HideFlags.HideAndDontSave;
            }
            float maxW = Screen.width * 0.45f;
            var raw = txt.Replace("<b>", "").Replace("</b>", "");
            var content = new GUIContent(raw);
            float h = _sharedText.CalcHeight(content, maxW - _sharedText.padding.horizontal);
            float w = Mathf.Min(_sharedText.CalcSize(content).x + _sharedText.padding.horizontal + 12f, maxW);
            var box = new Rect((Screen.width - w) / 2f, Screen.height * 0.90f, w, h);
            var prevColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(box, _sharedBg);
            GUI.color = prevColor;
            GUI.Label(box, txt, _sharedText);
        }

        internal class StandaloneHost : MonoBehaviour
        {
            public string Text;
            public float EndAt;
            private void Update()
            {
                if (Time.unscaledTime >= EndAt) Destroy(gameObject);
            }
            private void OnGUI() { DrawPanel(Text); }
        }

        internal class SubtitleHost : MonoBehaviour
        {
            internal class Line
            {
                public float Start, End;
                public string Text;
            }

            public PlayableDirector Director;
            public readonly List<Line> Lines = new List<Line>();

            private string _current;

            private void Update()
            {
                if (Director == null) { _current = null; return; }
                float t = (float)Director.time;
                _current = null;
                for (int i = 0; i < Lines.Count; i++)
                    if (t >= Lines[i].Start && t <= Lines[i].End) { _current = Lines[i].Text; break; }
            }

            private void OnGUI() { DrawPanel(_current); }
        }
    }
}
