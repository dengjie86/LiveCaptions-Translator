"""Bounded utterance segmentation and Windows system-output capture."""
from collections import deque
from dataclasses import dataclass
import queue
import threading
import time

import numpy as np
from audio_devices import get_default_output_id

RATE = 16000
FRAME_SAMPLES = 480  # WebRTC VAD requires exactly 30 ms.


@dataclass
class RecognitionJob:
    sequence: int
    audio: np.ndarray
    final: bool
    queued_at: float


class JobQueue:
    """Coalesce unfinished previews; preserve every final utterance in order."""
    def __init__(self):
        self.condition = threading.Condition()
        self.jobs = deque()
        self.closed = False

    def put(self, job):
        with self.condition:
            if self.closed:
                return
            if self.jobs and self.jobs[-1].sequence == job.sequence:
                if self.jobs[-1].final:
                    return
                self.jobs[-1] = job
            else:
                if len(self.jobs) >= 8:
                    raise RuntimeError('识别积压过多，请使用 0.6B 或释放 GPU 资源后重启')
                self.jobs.append(job)
            self.condition.notify()

    def get(self):
        with self.condition:
            self.condition.wait_for(lambda: self.jobs or self.closed)
            return self.jobs.popleft() if self.jobs else None

    def close(self):
        with self.condition:
            self.closed = True
            self.condition.notify_all()


class UtteranceSegmenter:
    def __init__(self, submit, partial_seconds=1.0, silence_seconds=0.45, max_seconds=5.0,
                 phrase_seconds=2.0, phrase_silence_seconds=0.24):
        import webrtcvad
        self.vad = webrtcvad.Vad(2)
        self.submit = submit
        self.partial_samples = int(partial_seconds * RATE)
        self.silence_frames = round(silence_seconds / 0.03)
        self.max_samples = int(max_seconds * RATE)
        self.phrase_samples = int(phrase_seconds * RATE)
        self.phrase_silence_frames = round(phrase_silence_seconds / 0.03)
        self.preroll = deque(maxlen=8)
        self.recent_voice = deque(maxlen=3)
        self.frames = []
        self.voice_flags = []
        self.sequence = 0
        self.silent = 0
        self.last_partial = 0
        self.voiced_frames = 0
        self.continuation = False
        self.continuation_silence = 0
        self.pending = np.empty(0, dtype=np.float32)

    def feed(self, samples):
        self.pending = np.concatenate((self.pending, np.asarray(samples, dtype=np.float32)))
        while len(self.pending) >= FRAME_SAMPLES:
            frame, self.pending = self.pending[:FRAME_SAMPLES].copy(), self.pending[FRAME_SAMPLES:]
            pcm = (np.clip(frame, -1, 1) * 32767).astype('<i2').tobytes()
            voiced = float(np.sqrt(np.mean(frame * frame))) >= 0.003 and self.vad.is_speech(pcm, RATE)
            if not self.frames:
                self.preroll.append((frame, voiced))
                self.recent_voice.append(voiced)
                if self.continuation and not voiced:
                    self.continuation_silence += 1
                    if self.continuation_silence >= self.silence_frames:
                        self.continuation = False
                # A time-limited split can leave even one voiced frame of a
                # valid utterance. Do not apply the new-utterance noise gate.
                if not (self.continuation and voiced) and sum(self.recent_voice) < 2:
                    continue
                self.sequence += 1
                self.frames = [item[0] for item in self.preroll]
                self.voice_flags = [item[1] for item in self.preroll]
                self.preroll.clear()
                self.recent_voice.clear()
                self.silent = self.last_partial = 0
                self.voiced_frames = sum(self.voice_flags)
            else:
                self.frames.append(frame)
                self.voice_flags.append(voiced)
                self.voiced_frames += int(voiced)
            self.silent = 0 if voiced else self.silent + 1
            length = len(self.frames) * FRAME_SAMPLES
            # Commit a readable clause at a brief pause, without turning every
            # hesitation into a subtitle. Continuous speech is also bounded.
            spoken_length = length - self.silent * FRAME_SAMPLES
            phrase_pause = (spoken_length >= self.phrase_samples and
                            self.silent >= self.phrase_silence_frames)
            if phrase_pause or self.silent >= self.silence_frames:
                self.finish()
            elif length >= self.max_samples:
                self.finish(self.quiet_boundary())
            elif length - self.last_partial >= self.partial_samples and self.voiced_frames >= 6:
                self.submit(RecognitionJob(self.sequence, np.concatenate(self.frames), False, time.perf_counter()))
                self.last_partial = length

    def quiet_boundary(self):
        # At the time limit, prefer a low-energy valley in the last 0.9 s.
        # Never cut behind audio already sent in the latest preview. The tail
        # belongs to the next subtitle and is retained, without audio overlap.
        lower = max(self.last_partial // FRAME_SAMPLES, len(self.frames) - 30, 6)
        upper = len(self.frames) - 6
        if upper <= lower:
            return len(self.frames)
        energy = np.array([np.mean(frame * frame) for frame in self.frames])
        smoothed = np.convolve(energy, np.ones(3) / 3, mode='same')
        candidates = smoothed[lower:upper]
        boundary = lower + int(np.argmin(candidates))
        # A flat signal offers no evidence of a better acoustic boundary.
        if smoothed[boundary] < np.max(candidates) * 0.7:
            return boundary
        return len(self.frames)

    def finish(self, prefix_frames=None):
        cut = len(self.frames) if prefix_frames is None else prefix_frames
        frames, voice_flags = self.frames[:cut], self.voice_flags[:cut]
        silent = 0
        for voiced in reversed(voice_flags):
            if voiced:
                break
            silent += 1
        minimum_voice = 1 if self.continuation else 6
        if frames and sum(voice_flags) >= minimum_voice:
            # Keep a short tail, but avoid repeatedly decoding long silence.
            trim = max(0, silent - 5)
            frames = frames[:-trim] if trim else frames
            self.submit(RecognitionJob(self.sequence, np.concatenate(frames), True, time.perf_counter()))
        self.frames = self.frames[cut:]
        self.voice_flags = self.voice_flags[cut:]
        self.last_partial = 0
        self.voiced_frames = sum(self.voice_flags)
        self.silent = 0
        for voiced in reversed(self.voice_flags):
            if voiced:
                break
            self.silent += 1
        self.continuation = prefix_frames is not None
        self.continuation_silence = self.silent
        if self.frames:
            self.sequence += 1

    def end_stream(self):
        # Finish the old endpoint's speech, but never carry its pre-roll or a
        # partial PCM frame into another device. Sequence IDs remain monotonic.
        if len(self.pending):
            self.feed(np.zeros(FRAME_SAMPLES - len(self.pending), dtype=np.float32))
        self.finish()
        self.preroll.clear()
        self.recent_voice.clear()
        self.pending = np.empty(0, dtype=np.float32)
        self.continuation = False
        self.continuation_silence = 0


def capture_system_audio(segmenter, stop, status, max_seconds=None):
    deadline = time.monotonic() + max_seconds if max_seconds is not None else float('inf')
    last_status = None

    def report(message):
        nonlocal last_status
        if message != last_status:
            last_status = message
            status(message)

    while not stop.is_set() and time.monotonic() < deadline:
        try:
            endpoint = get_default_output_id()
            if endpoint is None:
                report('未检测到声音输出设备，等待连接…')
            else:
                # PyAudio caches endpoints at initialization. Closing the old
                # instance before creating a new one refreshes hot-plugged devices.
                _capture_endpoint(segmenter, stop, report, deadline, endpoint)
        except (OSError, LookupError):
            report('声音设备暂不可用，正在自动重连…')
        finally:
            segmenter.end_stream()
        if not stop.is_set():
            stop.wait(max(0, min(0.5, deadline - time.monotonic())))


def _capture_endpoint(segmenter, stop, status, deadline, endpoint):
    import pyaudiowpatch as pyaudio
    import soxr

    pending = queue.Queue(maxsize=400)
    overflow = threading.Event()
    with pyaudio.PyAudio() as audio:
        device = audio.get_default_wasapi_loopback()
        rate = int(device['defaultSampleRate'])
        channels = int(device['maxInputChannels'])
        if channels < 1:
            raise OSError('没有可用的系统声音输出设备')
        if get_default_output_id() != endpoint:
            status('声音输出设备已切换，正在重新连接…')
            return
        resampler = soxr.ResampleStream(rate, RATE, 1, dtype='float32') if rate != RATE else None

        def callback(data, frame_count, time_info, flags):
            if stop.is_set():
                return None, pyaudio.paComplete
            try:
                pending.put_nowait(data)
            except queue.Full:
                overflow.set()
                return None, pyaudio.paAbort
            return None, pyaudio.paContinue

        with audio.open(format=pyaudio.paInt16, channels=channels, rate=rate, input=True,
                        input_device_index=device['index'], frames_per_buffer=round(rate * 0.03),
                        stream_callback=callback) as stream:
            device_name = device['name'].removesuffix(' [Loopback]')
            status(f'正在识别系统声音 · {device_name} · 自动识别语言')
            next_device_check = time.monotonic()
            while not stop.is_set():
                now = time.monotonic()
                if now >= deadline:
                    break
                if now >= next_device_check:
                    next_device_check = now + 0.5
                    if get_default_output_id() != endpoint:
                        status('声音输出设备已切换，正在重新连接…')
                        break
                    if not stream.is_active():
                        raise OSError('声音输出设备已断开')
                if overflow.is_set():
                    raise OSError('声音采集缓冲已满，重新连接采集流')
                try:
                    data = pending.get(timeout=0.1)
                except queue.Empty:
                    if not stream.is_active():
                        raise OSError('声音输出设备已断开')
                    # WASAPI can emit no callbacks while every app is silent.
                    segmenter.feed(np.zeros(1600, dtype=np.float32))
                    continue
                samples = np.frombuffer(data, dtype='<i2').astype(np.float32).reshape(-1, channels).mean(axis=1) / 32768
                segmenter.feed(resampler.resample_chunk(samples) if resampler else samples)
        if resampler:
            segmenter.feed(resampler.resample_chunk(np.empty(0, dtype=np.float32), last=True))
