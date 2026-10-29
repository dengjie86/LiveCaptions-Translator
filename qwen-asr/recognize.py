"""Local Qwen ASR worker. stdout is JSONL; diagnostic output goes to stderr."""
import argparse
import json
import os
from pathlib import Path
import sys
import threading
import time
import traceback

os.environ.setdefault('HF_HUB_OFFLINE', '1')
os.environ.setdefault('HF_HUB_DISABLE_TELEMETRY', '1')
os.environ.setdefault('TOKENIZERS_PARALLELISM', 'false')

_output_lock = threading.Lock()


def emit(kind, **fields):
    with _output_lock:
        print(json.dumps({'type': kind, **fields}, ensure_ascii=True), flush=True)


def load_model(path):
    import torch
    from qwen_asr import Qwen3ASRModel

    if not torch.cuda.is_available():
        raise RuntimeError('GPU 不可用，请检查专用环境的 PyTorch 安装')
    torch.set_num_threads(4)
    torch.backends.cuda.matmul.allow_tf32 = True
    started = time.perf_counter()
    model = Qwen3ASRModel.from_pretrained(
        path, dtype=torch.bfloat16, device_map='cuda:0', attn_implementation='sdpa',
        max_inference_batch_size=1, max_new_tokens=192, local_files_only=True,
    )
    emit('loaded', seconds=round(time.perf_counter() - started, 3),
         gpu=torch.cuda.get_device_name(0), model=Path(path).name)
    return model


def transcribe(model, audio):
    import torch
    with torch.inference_mode():
        result = model.transcribe(audio=(audio, 16000), language=None)[0]
    return result.text.strip(), result.language


def read_audio(path):
    import numpy as np
    import soundfile as sf
    import soxr
    samples, rate = sf.read(path, dtype='float32', always_2d=True)
    samples = samples.mean(axis=1)
    return soxr.resample(samples, rate, 16000).astype(np.float32) if rate != 16000 else samples


def benchmark(model, paths):
    import torch
    for path in paths:
        samples = read_audio(path)
        # Report a first pass and a warmed pass without repeatedly loading weights.
        for repeat in range(2):
            torch.cuda.reset_peak_memory_stats()
            torch.cuda.synchronize()
            started = time.perf_counter()
            text, language = transcribe(model, samples)
            torch.cuda.synchronize()
            elapsed = time.perf_counter() - started
            emit('benchmark', file=Path(path).name, repeat=repeat + 1,
                 audio_seconds=round(len(samples) / 16000, 3), seconds=round(elapsed, 3),
                 peak_allocated_mb=round(torch.cuda.max_memory_allocated() / 1024**2),
                 peak_reserved_mb=round(torch.cuda.max_memory_reserved() / 1024**2),
                 text=text, language=language)


def run_live(model, args):
    import numpy as np
    from capture import JobQueue, UtteranceSegmenter, capture_system_audio

    stop = threading.Event()
    jobs = JobQueue()
    segmenter = UtteranceSegmenter(jobs.put)
    failure = []

    def input_commands():
        for command in sys.stdin:
            if command.strip() == 'stop':
                stop.set()
                break

    def recognize_jobs():
        last_text = {}
        try:
            while (job := jobs.get()) is not None:
                started = time.perf_counter()
                text, language = transcribe(model, job.audio)
                fallback = False
                if not text and job.final:
                    text = last_text.get(job.sequence, '')
                    fallback = bool(text)
                if job.final:
                    # Count finalized segments, including empty recognition, without
                    # retaining audio or source text in the diagnostic log.
                    print(json.dumps({'stage': 'asr-final', 'sequence': job.sequence,
                                      'audio_ms': round(len(job.audio) / 16),
                                      'text_chars': len(text), 'used_preview': fallback,
                                      'inference_ms': round((time.perf_counter() - started) * 1000)}),
                          file=sys.stderr, flush=True)
                if text:
                    last_text[job.sequence] = text
                    emit('caption', sequence=job.sequence, text=text, final=job.final, language=language,
                         inference_ms=round((time.perf_counter() - started) * 1000),
                         queue_ms=round((started - job.queued_at) * 1000))
                if job.final:
                    last_text.pop(job.sequence, None)
        except Exception as error:
            failure.append(error)
            stop.set()

    threading.Thread(target=input_commands, daemon=True).start()
    worker = threading.Thread(target=recognize_jobs, daemon=True)
    worker.start()
    try:
        if args.file:
            samples = read_audio(args.file)
            emit('status', message='正在回放测试音频')
            deadline = time.monotonic()
            for start in range(0, len(samples), 480):
                if stop.is_set():
                    break
                segmenter.feed(samples[start:start + 480])
                deadline += 0.03
                stop.wait(max(0, deadline - time.monotonic()))
            segmenter.feed(np.zeros(16000, dtype=np.float32))
            segmenter.finish()
        else:
            capture_system_audio(segmenter, stop,
                                 lambda message: emit('status', message=message), args.seconds)
    finally:
        jobs.close()
        worker.join(timeout=60)
    if worker.is_alive():
        raise RuntimeError('识别超时，请重启程序')
    if failure:
        raise failure[0]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--model', required=True)
    parser.add_argument('--file', help='Replay a short fixture at real-time speed through the normal segmentation queue')
    parser.add_argument('--benchmark', nargs='+', help='Short offline accuracy/latency checks')
    parser.add_argument('--seconds', type=float, help='Optional duration for a loopback smoke test')
    args = parser.parse_args()
    try:
        emit('status', message='正在加载模型…')
        model = load_model(args.model)
        if args.benchmark:
            benchmark(model, args.benchmark)
        else:
            run_live(model, args)
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        message = str(error)
        if 'out of memory' in message.lower():
            message = '显存不足，请切换到 0.6B 或释放显存后重启'
        emit('error', message=message[:240])
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
