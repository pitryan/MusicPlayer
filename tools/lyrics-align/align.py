"""Add word-level timings (Enhanced LRC) to line-timed .lrc files by aligning
the known lyric text to the song's vocals.

Pipeline per song:
  1. find the audio file next to the .lrc (same stem, compared after NFC),
  2. separate the vocals with Demucs (htdemucs), cached outside the library,
  3. force-align the known lyrics with stable-ts (Whisper cross-attention
     alignment; no transcription): first the whole song's lines against the
     whole vocal track, then, for lines that fail the checks, the line alone
     against the vocals from (line - 0.3 s) to (next line + 0.3 s),
  4. check each line's result; doubtful lines stay line-level,
  5. write `[mm:ss.xx]<t>seg<t>seg...<t_end>` for the lines that passed.

Default is a dry run into --out. See README.md.
"""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import io
import os
import re
import shutil
import subprocess
import sys
import time
import unicodedata
import wave
import warnings
from dataclasses import dataclass, field, replace
from pathlib import Path

import numpy as np

warnings.filterwarnings("ignore", category=SyntaxWarning)

AUDIO_EXTS = {".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".alac", ".ape", ".wv"}
SR = 16000  # Whisper sample rate; the vocals cache is stored at this rate (mono, 16-bit)

STAMP_RE = re.compile(r"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")
LEAD_STAMPS_RE = re.compile(r"^((?:\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\])+)(.*)$", re.S)
WORD_TAG_RE = re.compile(r"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")
OFFSET_RE = re.compile(r"^\[offset:\s*([+-]?\d+)\s*\]\s*$", re.I)
PAREN_RE = re.compile(r"\([^()]*\)|（[^（）]*）")
NEWLINE_SPLIT_RE = re.compile(r"(\r\n|\n|\r)")

# Window around each line (seconds).
PAD = 0.3
MAX_WINDOW = 20.0      # never align against more than this many seconds after the line stamp
EPS = 0.02
MIN_SEG_CS = 3     # segments shorter than this (centiseconds) count as zero-length
EARLY_TOL = 1.0   # whole-song pass: a line may start this much before its stamp
LATE_TOL = 0.5    # ... and its last word may start this much after the next stamp (then clamped)


# --------------------------------------------------------------------------- LRC parsing

def parse_stamp(m: re.Match) -> float:
    mm, ss, frac = m.group(1), m.group(2), m.group(3)
    t = int(mm) * 60 + int(ss)
    if frac:
        t += int(frac) / (10 ** len(frac))
    return t


def fmt_cs(cs: int) -> str:
    """Centiseconds -> 'mm:ss.xx'."""
    m, rem = divmod(cs, 6000)
    s, c = divmod(rem, 100)
    return f"{m:02d}:{s:02d}.{c:02d}"


def has_lyric_chars(text: str) -> bool:
    return any(unicodedata.category(c)[0] in "LN" for c in text)


@dataclass
class LrcLine:
    idx: int            # index into the line list
    stamps: list        # parsed stamp seconds
    prefix: str         # the raw leading stamps, e.g. "[00:02.35]"
    text: str           # text after the stamps


@dataclass
class LrcFile:
    path: Path
    raw: bytes
    encoding: str
    bom: bytes
    lines: list         # text of each line (without newline)
    seps: list          # newline after each line ('' for the last one)
    offset_s: float = 0.0
    has_word_tags: bool = False
    timed: list = field(default_factory=list)   # list[LrcLine]


def read_lrc(path: Path) -> LrcFile:
    raw = path.read_bytes()
    bom, enc = b"", "utf-8"
    for b, e in ((b"\xef\xbb\xbf", "utf-8"), (b"\xff\xfe", "utf-16-le"), (b"\xfe\xff", "utf-16-be")):
        if raw.startswith(b):
            bom, enc = b, e
            break
    body = raw[len(bom):]
    text = None
    for e in ([enc] if bom else ["utf-8", "cp932", "cp1252"]):
        try:
            cand = body.decode(e)
        except UnicodeDecodeError:
            continue
        if cand.encode(e) == body:          # must round-trip byte-for-byte
            text, enc = cand, e
            break
    if text is None:
        raise ValueError("cannot decode file losslessly")
    parts = NEWLINE_SPLIT_RE.split(text)
    lines, seps = parts[0::2], parts[1::2] + [""]
    f = LrcFile(path, raw, enc, bom, lines, seps)
    for i, ln in enumerate(lines):
        om = OFFSET_RE.match(ln.strip())
        if om:
            f.offset_s = int(om.group(1)) / 1000.0
            continue
        m = LEAD_STAMPS_RE.match(ln)
        if not m:
            continue
        stamps = [parse_stamp(s) for s in STAMP_RE.finditer(m.group(1))]
        f.timed.append(LrcLine(i, stamps, m.group(1), m.group(2)))
        if WORD_TAG_RE.search(m.group(2)):
            f.has_word_tags = True
    return f


def encode_lrc(f: LrcFile, lines: list) -> bytes:
    return f.bom + "".join(l + s for l, s in zip(lines, f.seps)).encode(f.encoding)


def detect_language(texts: list) -> str:
    kana = hangul = han = 0
    for t in texts:
        for c in t:
            o = ord(c)
            if 0x3040 <= o <= 0x30FF or 0x31F0 <= o <= 0x31FF or 0xFF66 <= o <= 0xFF9F:
                kana += 1
            elif 0xAC00 <= o <= 0xD7AF or 0x1100 <= o <= 0x11FF or 0x3130 <= o <= 0x318F:
                hangul += 1
            elif 0x4E00 <= o <= 0x9FFF or 0x3400 <= o <= 0x4DBF:
                han += 1
    if kana or han:
        if hangul > kana + han:
            return "ko"
        return "ja"
    if hangul:
        return "ko"
    return "en"


# --------------------------------------------------------------------------- audio / vocals

def find_audio(lrc: Path) -> Path | None:
    stem = unicodedata.normalize("NFC", lrc.stem)
    for p in sorted(lrc.parent.iterdir()):
        if p.suffix.lower() in AUDIO_EXTS and unicodedata.normalize("NFC", p.stem) == stem:
            return p
    return None


def ffmpeg_load(path: Path, sr: int, channels: int) -> np.ndarray:
    cmd = ["ffmpeg", "-nostdin", "-v", "error", "-i", str(path), "-f", "f32le",
           "-ac", str(channels), "-ar", str(sr), "-"]
    out = subprocess.run(cmd, capture_output=True, check=True).stdout
    a = np.frombuffer(out, dtype=np.float32)
    return a.reshape(-1, channels).T.copy()   # (channels, samples)


def cache_key(audio: Path) -> str:
    st = audio.stat()
    h = hashlib.sha1(f"{unicodedata.normalize('NFC', str(audio.resolve()))}|{st.st_size}|{int(st.st_mtime)}".encode("utf-8"))
    return h.hexdigest()[:20]


def save_wav16(path: Path, x: np.ndarray):
    pcm = (np.clip(x, -1.0, 1.0) * 32767.0).astype("<i2")
    tmp = path.with_suffix(".tmp")
    with wave.open(str(tmp), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    os.replace(tmp, path)


def load_wav16(path: Path) -> np.ndarray:
    with wave.open(str(path), "rb") as w:
        data = w.readframes(w.getnframes())
    return np.frombuffer(data, dtype="<i2").astype(np.float32) / 32767.0


class Separator:
    """Demucs htdemucs vocals separation (GPU, falls back to CPU on OOM)."""

    def __init__(self, device: str):
        import torch
        from demucs.pretrained import get_model
        self.torch = torch
        self.model = get_model("htdemucs")
        self.model.eval()
        self.device = device

    def vocals(self, audio: Path) -> np.ndarray:
        import julius
        from demucs.apply import apply_model
        torch = self.torch
        sr = self.model.samplerate
        mix = torch.from_numpy(ffmpeg_load(audio, sr, self.model.audio_channels))
        ref = mix.mean(0)
        mean, std = ref.mean(), ref.std() + 1e-8
        x = (mix - mean) / std
        for dev in ([self.device, "cpu"] if self.device != "cpu" else ["cpu"]):
            try:
                with torch.no_grad():
                    out = apply_model(self.model, x[None], device=dev, shifts=1, split=True,
                                      overlap=0.25, progress=False)[0]
                break
            except torch.cuda.OutOfMemoryError:
                print("    demucs: out of GPU memory, retrying on CPU", flush=True)
                torch.cuda.empty_cache()
        out = out * std + mean
        voc = out[self.model.sources.index("vocals")].mean(0)
        voc = julius.resample_frac(voc.float(), sr, SR)
        return voc.cpu().numpy()

    def close(self):
        del self.model
        if self.torch.cuda.is_available():
            self.torch.cuda.empty_cache()


# --------------------------------------------------------------------------- alignment of one line

@dataclass
class Word:
    first: int      # index into the original text of the first char
    last: int       # index of the last char
    start: float    # seconds, song (audio) time
    end: float
    prob: float
    failed: bool = False   # stable-ts could not place it (it parks such words at the end with p=0)


def build_align_text(text: str):
    """Text given to the aligner, plus the original index of every char.

    Parenthesised parts (backing vocals sung over the main line) are removed
    from what gets aligned; they stay in the output, folded into the segment
    before them. A line that is only parentheses is aligned without the
    bracket characters."""
    keep = [True] * len(text)
    for m in PAREN_RE.finditer(text):
        for i in range(m.start(), m.end()):
            keep[i] = False
    if not has_lyric_chars("".join(c for c, k in zip(text, keep) if k)):
        keep = [c not in "()（）" for c in text]
    idx = [i for i, k in enumerate(keep) if k]
    return "".join(text[i] for i in idx), idx


def map_words(words, align_text: str, owners: list, t0: float):
    """Map stable-ts words back onto the original characters, whitespace-insensitive.

    owners[i] = (line_no, index_in_original_text) for every char of align_text
    (None for the newlines between lines). Never trusts the aligner's text:
    every char it returns must match the next char we gave it.
    Returns ({line_no: [Word, ...]}, None) or (None, error)."""
    p = 0
    out: dict = {}
    n = len(align_text)
    for w in words:
        chars = [c for c in w.word if not c.isspace()]
        if not chars:
            continue
        prob = float(w.probability) if w.probability is not None else 1.0
        failed = prob == 0.0 and w.end - w.start <= 0
        groups: dict = {}
        for c in chars:
            while p < n and align_text[p].isspace():
                p += 1
            if p >= n or align_text[p] != c:
                return None, f"text mismatch near {w.word!r}"
            line_no, oi = owners[p]
            g = groups.setdefault(line_no, [oi, oi])
            g[1] = oi
            p += 1
        for line_no, (first, last) in groups.items():   # a word spanning two lines is split
            out.setdefault(line_no, []).append(Word(first, last, w.start + t0, w.end + t0, prob, failed))
    while p < n and align_text[p].isspace():
        p += 1
    if p != n:
        return None, "aligner dropped part of the text"
    return out, None


def merge_punct(words: list, text: str) -> list:
    """Words without letters/digits (e.g. '、' or '!') join the previous word
    (or the next one when they open the line)."""
    res = []
    for w in words:
        if res and not has_lyric_chars(text[w.first:w.last + 1]):
            res[-1] = replace(res[-1], last=w.last, end=max(res[-1].end, w.end), failed=res[-1].failed or w.failed)
        elif res and not has_lyric_chars(text[res[-1].first:res[-1].last + 1]):
            res[-1] = replace(w, first=res[-1].first, failed=res[-1].failed or w.failed)
        else:
            res.append(w)
    return res


def check_and_tag(words: list, text: str, line_t: float, next_t: float | None,
                  win_start: float, win_end: float, offset_s: float, min_prob: float,
                  clamp_next: bool = False):
    """Plausibility checks for one line; returns (tagged_text, None) or (None, reason).
    line_t / next_t / window are audio times; the tags are written in file time
    (audio time + [offset:]), in centiseconds, never before the line stamp.
    Word starts may run up to LATE_TOL past the next line's stamp (stamps are
    often a little early); with clamp_next they are squeezed to the next stamp."""
    if not words:
        return None, "no words aligned"
    if any(w.failed for w in words):
        return None, "aligner could not place some words"
    starts = [w.start for w in words]
    if any(b < a - EPS for a, b in zip(starts, starts[1:])):
        return None, "non-monotonic word times"
    if any(w.end < w.start - EPS for w in words):
        return None, "word ends before it starts"
    if starts[0] < win_start - EPS or starts[-1] > win_end + EPS:
        return None, "times outside window"
    limit = next_t if next_t is not None else win_end
    gap = limit - line_t
    if starts[0] > line_t + max(2.0, 0.5 * gap):
        return None, "starts late"
    if next_t is not None and starts[-1] > next_t + LATE_TOL:
        return None, "runs into the next line"
    span = words[-1].end - starts[0]
    units = sum(1 for c in text if unicodedata.category(c)[0] in "LN")
    if len(words) > 1:
        expected = min(gap, 0.5 * units)
        if span < 0.3 * expected:
            return None, "aligned span too short"
    mean_prob = sum(w.prob for w in words) / len(words)
    if mean_prob < min_prob:
        return None, "low confidence"

    lo = int(np.ceil(round((line_t + offset_s) * 100, 6)))
    hi = None
    if next_t is not None:
        hi = int(np.floor(round((next_t + offset_s + (0 if clamp_next else 2 * LATE_TOL)) * 100, 6)))
    raw = [int(round((s + offset_s) * 100)) for s in starts] + [int(round((words[-1].end + offset_s) * 100))]
    tags, prev = [], lo
    for t in raw:
        t = max(t, prev)
        if hi is not None:
            t = min(t, max(hi, lo))
        tags.append(t)
        prev = t
    bounds = [0] + [w.first for w in words[1:]] + [len(text)]
    n = len(words)
    if tags[-1] - tags[0] < 10:
        return None, "aligned span too short"
    if n > 1:
        lens = [tags[k + 1] - tags[k] for k in range(n)]
        if sum(1 for d in lens if d < MIN_SEG_CS) > n / 2:
            return None, "collapsed timings"
        run = 0
        for d in lens:
            run = run + 1 if d < MIN_SEG_CS else 0
            if run >= 3:
                return None, "collapsed timings"
        # One or two (near) zero-length segments: share the duration of the
        # neighbouring real segment (the next one, or the previous one at the
        # end of the line) between them by letter count, so the fill does not jump.
        def letters(i):
            return max(1, sum(1 for ch in text[bounds[i]:bounds[i + 1]] if unicodedata.category(ch)[0] in "LN"))
        k = 0
        while k < n:
            if lens[k] >= MIN_SEG_CS:
                k += 1
                continue
            j = k
            while j < n and lens[j] < MIN_SEG_CS:
                j += 1
            a, b = (k, j) if j < n else (k - 1, n - 1)     # segments a..b share tags[a]..tags[b+1]
            weights = [letters(i) for i in range(a, b + 1)]
            total, acc = tags[b + 1] - tags[a], 0
            for i in range(a, b):
                acc += weights[i - a]
                tags[i + 1] = tags[a] + int(round(total * acc / sum(weights)))
            k = j + 1
    parts = [f"<{fmt_cs(tags[k])}>{text[bounds[k]:bounds[k + 1]]}" for k in range(n)]
    parts.append(f"<{fmt_cs(tags[-1])}>")
    return "".join(parts), None


def strip_tags_norm(s: str) -> str:
    return " ".join(WORD_TAG_RE.sub("", s).split())


def validate_tagged(prefix: str, original_text: str, tagged_text: str) -> str | None:
    if strip_tags_norm(tagged_text) != " ".join(original_text.split()):
        return "text rule violated"
    if WORD_TAG_RE.sub("", tagged_text) != original_text:
        return "characters changed"
    line_cs = int(round(parse_stamp(STAMP_RE.match(prefix)) * 100))
    ts = [int(round(parse_stamp(STAMP_RE.match("[" + m.group(0)[1:-1] + "]")) * 100))
          for m in WORD_TAG_RE.finditer(tagged_text)]
    if not ts or ts[0] < line_cs or any(b < a for a, b in zip(ts, ts[1:])):
        return "tag times invalid"
    if not tagged_text.startswith("<") or not tagged_text.endswith(">"):
        return "tag layout invalid"
    return None


# --------------------------------------------------------------------------- per-file processing

@dataclass
class Job:
    lrc: Path
    f: LrcFile
    audio: Path
    lang: str
    candidates: list     # list[LrcLine] original lines to align
    skipped: dict        # reason -> count
    vocals_path: Path


def plan_file(lrc: Path, args, cache_dir: Path):
    """Returns (Job, None) or (None, reason)."""
    try:
        f = read_lrc(lrc)
    except Exception as e:  # noqa: BLE001
        return None, f"unreadable: {e}"
    if f.has_word_tags and not args.force:
        return None, "already has word tags (use --force)"
    audio = find_audio(lrc)
    if audio is None:
        return None, "no matching audio file"
    multi_times = {round(s, 3) for ln in f.timed if len(ln.stamps) > 1 for s in ln.stamps}
    seen = set()
    candidates, skipped = [], {}
    for ln in f.timed:
        if len(ln.stamps) != 1:
            skipped["repeated-timestamp line [t1][t2]"] = skipped.get("repeated-timestamp line [t1][t2]", 0) + 1
            continue
        key = round(ln.stamps[0], 3)
        if key in seen:
            continue                     # reading / translation layer: never touched
        seen.add(key)
        if key in multi_times:
            skipped["shares time with [t1][t2] line"] = skipped.get("shares time with [t1][t2] line", 0) + 1
            continue
        text = WORD_TAG_RE.sub("", ln.text) if args.force else ln.text
        if not has_lyric_chars(text):
            if text.strip():
                skipped["no lyric text"] = skipped.get("no lyric text", 0) + 1
            continue
        candidates.append(LrcLine(ln.idx, ln.stamps, ln.prefix, text))
    if not candidates:
        return None, "no lyric lines"
    lang = args.lang or detect_language([c.text for c in candidates])
    return Job(lrc, f, audio, lang, candidates, skipped, cache_dir / f"{cache_key(audio)}.vocals16k.wav"), None


@contextlib.contextmanager
def quiet():
    """Hide stable-ts progress bars and warnings (it prints them on stderr)."""
    with warnings.catch_warnings(), contextlib.redirect_stderr(io.StringIO()):
        warnings.simplefilter("ignore")
        yield


def run_align(model, audio: np.ndarray, texts: list, lang: str, t0: float):
    """Align several lyric lines (in sung order) against one piece of audio.
    Returns ({line_no: [Word]}, None) or (None, reason)."""
    combined, owners = [], []
    for k, text in enumerate(texts):
        at, idx = build_align_text(text)
        combined.append(at)
        owners.extend((k, i) for i in idx)
        combined.append("\n")
        owners.append(None)
    align_text = "".join(combined)
    try:
        with quiet():
            res = model.align(audio, align_text, language=lang, verbose=None, regroup=False,
                              original_split=True)
    except Exception as e:  # noqa: BLE001
        return None, f"aligner error: {type(e).__name__}"
    if res is None:
        return None, "aligner gave up"
    return map_words(res.all_words(), align_text, owners, t0)


def align_job(job: Job, model, args, log):
    """Pass 1: align the whole song's lyrics against the whole vocal track, so
    neighbouring lines compete for the audio (gives clean line boundaries).
    Pass 2: lines that fail the checks are retried alone in a window
    (line - 0.3 s .. next line + 0.3 s). Still failing -> left untouched."""
    f = job.f
    vocals = load_wav16(job.vocals_path)
    dur = len(vocals) / SR
    off = f.offset_s
    all_times = sorted({round(s, 3) for ln in f.timed for s in ln.stamps})

    def next_stamp(t_file):
        later = [t for t in all_times if t > round(t_file, 3) + 1e-6]
        return later[0] if later else None

    # Everything that is sung, in time order: the lines we tag, plus the
    # repeated-timestamp lines ([t1][t2]text) once per stamp, as context only.
    entries = [(c.stamps[0], c.text, c) for c in job.candidates]
    for ln in f.timed:
        text = WORD_TAG_RE.sub("", ln.text)
        if len(ln.stamps) > 1 and has_lyric_chars(text):
            entries.extend((s, text, None) for s in ln.stamps)
    entries.sort(key=lambda e: e[0])

    full, full_err = run_align(model, vocals, [e[1] for e in entries], job.lang, 0.0)
    new_lines = list(f.lines)
    tagged, reasons, via_retry = 0, dict(job.skipped), 0
    for k, (t_file, text, c) in enumerate(entries):
        if c is None:
            continue
        nf = next_stamp(t_file)
        line_t = t_file - off
        next_t = nf - off if nf is not None else None
        result, reason = None, full_err
        if full is not None:
            words = merge_punct(full.get(k, []), text)
            hi = (next_t if next_t is not None else min(dur, line_t + MAX_WINDOW)) + EARLY_TOL
            result, reason = check_and_tag(words, text, line_t, next_t, line_t - EARLY_TOL, hi, off, args.min_prob, args.clamp_next)
        first_reason = reason
        if result is None:
            ws = max(0.0, line_t - PAD)
            we = min(dur, (next_t + PAD) if next_t is not None else dur, line_t + MAX_WINDOW)
            if we - ws < 0.3:
                reason = "window too short / beyond audio"
            else:
                got, reason = run_align(model, vocals[int(ws * SR):int(we * SR)], [text], job.lang, ws)
                if got is not None:
                    words = merge_punct(got.get(0, []), text)
                    result, reason = check_and_tag(words, text, line_t, next_t, ws, we, off, args.retry_min_prob,
                                                   args.clamp_next)
                    if args.verbose and words:
                        log(f"      retry p={sum(w.prob for w in words) / len(words):.2f}")
                    if result is not None:
                        via_retry += 1
        if result is not None:
            reason = validate_tagged(c.prefix, text, result)
        if result is not None and reason is None:
            new_lines[c.idx] = c.prefix + result
            tagged += 1
            if args.verbose:
                how = f"retry ({first_reason})" if first_reason else "song"
                log(f"    ok   {c.prefix}{result}   [{how}]")
            continue
        reasons[reason] = reasons.get(reason, 0) + 1
        if args.verbose:
            log(f"    skip {c.prefix}{text}   <- {first_reason}; retry: {reason}")
    if via_retry:
        log(f"  ({via_retry} line(s) tagged by the per-line retry)")
    return new_lines, tagged, reasons


def out_path_for(lrc: Path, out_dir: Path) -> Path:
    p = lrc.resolve()
    return out_dir / p.relative_to(p.anchor)


def collect(paths: list) -> list:
    res = []
    for p in paths:
        p = Path(p)
        if p.is_dir():
            res.extend(sorted(x for x in p.rglob("*") if x.suffix.lower() == ".lrc" and x.is_file()))
        elif p.is_file() and p.suffix.lower() == ".lrc":
            res.append(p)
        else:
            print(f"warning: not an .lrc file or folder: {p}", file=sys.stderr)
    seen, uniq = set(), []
    for p in res:
        k = str(p.resolve()).lower()
        if k not in seen:
            seen.add(k)
            uniq.append(p)
    return uniq


def load_whisper(name: str, device: str):
    import stable_whisper
    import torch
    names = [name] + (["small"] if name not in ("small", "base", "tiny") else [])
    for n in names:
        try:
            m = stable_whisper.load_model(n, device=device)
            if device == "cuda":
                # probe: a short alignment so an OOM shows up now, not in the middle of a file
                with quiet():
                    m.align(np.zeros(SR * 5, dtype=np.float32), "test", language="en", verbose=None)
            return m, n
        except torch.cuda.OutOfMemoryError:
            print(f"whisper '{n}' does not fit in GPU memory, trying a smaller model", flush=True)
            m = None
            torch.cuda.empty_cache()
    raise SystemExit("no Whisper model fits on the GPU; try --device cpu")


def main(argv=None):
    ap = argparse.ArgumentParser(description="Add Enhanced-LRC word timings to line-timed .lrc files.")
    ap.add_argument("paths", nargs="+", help=".lrc files or folders (searched recursively)")
    ap.add_argument("--out", default=str(Path(__file__).parent / "out"),
                    help="dry-run output folder (mirrors the full path, e.g. OUT/music/Japan/...)")
    ap.add_argument("--write", action="store_true",
                    help="overwrite the .lrc in place (original copied to <name>.lrc.bak first)")
    ap.add_argument("--force", action="store_true", help="also process files that already have word tags")
    ap.add_argument("--model", default="medium", help="Whisper model (default: medium; falls back to small)")
    ap.add_argument("--lang", default=None, help="force language (ja/ko/en/...); default: detect per file")
    ap.add_argument("--device", default=None, help="cuda or cpu (default: cuda if available)")
    ap.add_argument("--cache-dir", default=os.path.join(os.environ.get("LOCALAPPDATA", str(Path.home())),
                                                        "lyrics-align", "cache"),
                    help="where separated vocals are cached")
    ap.add_argument("--min-prob", type=float, default=0.20,
                    help="minimum mean word probability per line, whole-song pass (default 0.20)")
    ap.add_argument("--retry-min-prob", type=float, default=0.35,
                    help="minimum mean word probability for the per-line retry (default 0.35; it is less reliable)")
    ap.add_argument("--clamp-next", action="store_true",
                    help="never let a word start after the next line's stamp (squeezes late words)")
    ap.add_argument("-v", "--verbose", action="store_true", help="print every line's result")
    args = ap.parse_args(argv)

    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:  # noqa: BLE001
        pass
    if shutil.which("ffmpeg") is None:
        raise SystemExit("ffmpeg not found on PATH")
    import torch
    device = args.device or ("cuda" if torch.cuda.is_available() else "cpu")
    cache_dir = Path(args.cache_dir)
    cache_dir.mkdir(parents=True, exist_ok=True)
    out_dir = Path(args.out)

    files = collect(args.paths)
    print(f"{len(files)} .lrc file(s); device={device}; mode={'WRITE' if args.write else 'dry run -> ' + str(out_dir)}")
    jobs, results = [], []
    for lrc in files:
        job, why = plan_file(lrc, args, cache_dir)
        if job is None:
            results.append((lrc, f"skipped: {why}"))
            print(f"skip {lrc}: {why}")
        else:
            jobs.append(job)

    # Phase 1: vocals (Demucs), only for songs not cached yet.
    todo = [j for j in jobs if not j.vocals_path.exists()]
    sep_time = {}
    if todo:
        print(f"separating vocals for {len(todo)} song(s) (htdemucs on {device}) ...", flush=True)
        sep = Separator(device)
        for j in todo:
            t0 = time.time()
            try:
                save_wav16(j.vocals_path, sep.vocals(j.audio))
            except Exception as e:  # noqa: BLE001
                print(f"  vocals failed for {j.audio}: {e}")
            sep_time[j.lrc] = time.time() - t0
            print(f"  {j.audio.name}: {sep_time[j.lrc]:.1f}s", flush=True)
        sep.close()
        del sep
    jobs = [j for j in jobs if j.vocals_path.exists() or results.append((j.lrc, "skipped: no vocals")) or False]

    # Phase 2: alignment.
    if jobs:
        model, model_name = load_whisper(args.model, device)
        print(f"whisper model: {model_name}", flush=True)
    for j in jobs:
        t0 = time.time()
        print(f"\n{j.lrc}\n  audio: {j.audio.name}  lang={j.lang}  lines={len(j.candidates)}", flush=True)
        new_lines, tagged, reasons = align_job(j, model, args, print)
        data = encode_lrc(j.f, new_lines)
        # untouched lines must be byte-identical: re-check by decoding what we are about to write
        assert data.startswith(j.f.bom)
        elapsed = time.time() - t0 + sep_time.get(j.lrc, 0.0)
        if args.write:
            if tagged:
                bak = j.lrc.with_name(j.lrc.name + ".bak")
                if not bak.exists():
                    shutil.copy2(j.lrc, bak)
                j.lrc.write_bytes(data)
            dest = j.lrc
        else:
            dest = out_path_for(j.lrc, out_dir)
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(data)
        total = len(j.candidates)
        print(f"  tagged {tagged}/{total} lines in {elapsed:.1f}s -> {dest if (tagged or not args.write) else '(unchanged)'}")
        for r, n in sorted(reasons.items(), key=lambda kv: -kv[1]):
            print(f"    skipped {n:3d}: {r}")
        results.append((j.lrc, f"{tagged}/{total} tagged, {elapsed:.1f}s"))

    print("\nsummary:")
    for p, s in results:
        print(f"  {s:28s} {p}")


if __name__ == "__main__":
    main()
