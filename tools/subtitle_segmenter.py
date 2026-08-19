# SUBTITLE TIMING DRAFTS: speech-segments the cutscene voice tracks (RMS energy
# over 50ms windows) and writes/updates terminal_subtitle_timings.json. the rip's
# wavs carry broken data-chunk sizes (0), so RIFF is parsed by hand. re-run after
# tweaking thresholds; text assignments in the json are preserved by segment
# index when counts match.
import struct, array, math, json, os, sys

SDK = r'C:/Users/peard/Desktop/WTT-SDK-2022 Public/Assets/Terminal_Import/CutsceneAnims'
OUT = r'C:/Users/peard/Desktop/ManimalTerminal/terminal-client/plugin-data/terminal_subtitle_timings.json'

TRACKS = {
    'intro_nocase': 'TRM_CS_3617_1_1_nocase_voice.wav',
    'intro_case': 'TRM_CS_3607_1_1_voices.wav',
    'ending_00': 'TRM_CSA_4103_usec_03_ending_00.wav',
}

def read_riff_pcm16(path):
    b = open(path, 'rb').read()
    i, fmt, data = 12, None, None
    while i < len(b) - 8:
        cid = b[i:i+4]
        sz = struct.unpack('<I', b[i+4:i+8])[0]
        if cid == b'fmt ':
            fmt = struct.unpack('<HHIIHH', b[i+8:i+8+16])
        elif cid == b'data':
            # rip wavs write size 0 — take everything to EOF
            data = b[i+8:] if sz == 0 else b[i+8:i+8+sz]
            break
        i += 8 + sz + (sz & 1)
    assert fmt and fmt[0] == 1 and fmt[5] == 16, f'unsupported wav: {fmt}'
    return fmt[2], fmt[1], array.array('h', data[:len(data) - (len(data) % 2)])

def segments(path, win=0.05, thresh_ratio=0.06, min_gap=0.55, min_len=0.35, tail=0.3):
    rate, ch, data = read_riff_pcm16(path)
    step = int(rate * win) * ch
    rms = []
    for i in range(0, len(data) - step, step):
        s = 0
        for j in range(i, i + step, ch):
            s += data[j] * data[j]
        rms.append(math.sqrt(s / (step / ch)))
    th = max(rms) * thresh_ratio
    segs, cur = [], None
    for i, v in enumerate(rms):
        t = i * win
        if v > th:
            cur = [t, t] if cur is None else [cur[0], t]
        elif cur is not None and t - cur[1] > min_gap:
            if cur[1] - cur[0] >= min_len:
                segs.append((round(cur[0], 2), round(cur[1] + tail, 2)))
            cur = None
    if cur and cur[1] - cur[0] >= min_len:
        segs.append((round(cur[0], 2), round(cur[1] + tail, 2)))
    return segs

def main():
    old = {}
    if os.path.exists(OUT):
        old = json.load(open(OUT, encoding='utf8')).get('cutscenes', {})
    out = {}
    for key, fname in TRACKS.items():
        path = os.path.join(SDK, fname)
        if not os.path.exists(path):
            print(f'{key}: {fname} missing, skipped')
            continue
        segs = segments(path)
        prev = old.get(key, [])
        lines = []
        for i, (s, e) in enumerate(segs):
            text = prev[i]['text'] if i < len(prev) and prev[i].get('text') else ''
            lines.append({'start': s, 'end': e, 'text': text})
        out[key] = lines
        print(f'{key}: {len(segs)} segments' + (f' (kept {sum(1 for l in lines if l["text"])} texts)' if prev else ''))
    json.dump({'note': 'speech-segmented voice tracks; fill text from terminal_subtitles.json locale lines',
               'cutscenes': out}, open(OUT, 'w', encoding='utf8'), ensure_ascii=False, indent=1)
    print('->', OUT)

if __name__ == '__main__':
    main()
