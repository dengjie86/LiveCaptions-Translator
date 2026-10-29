import time
import threading
import types
import unittest
from unittest.mock import Mock, patch
import numpy as np
import soxr  # Keep the native module loaded across mocked PyAudio module scopes.
from capture import JobQueue, RecognitionJob, UtteranceSegmenter, capture_system_audio, _capture_endpoint


class SignalVad:
    def is_speech(self, pcm, rate):
        return bool(np.any(np.frombuffer(pcm, dtype='<i2')))


class CaptureChecks(unittest.TestCase):
    def test_silence_does_not_submit_recognition(self):
        submitted = []
        segmenter = UtteranceSegmenter(submitted.append)
        segmenter.feed(np.zeros(16000 * 2, dtype=np.float32))
        segmenter.finish()
        self.assertEqual(submitted, [])

    def test_partial_then_one_final_after_pause(self):
        submitted = []
        segmenter = UtteranceSegmenter(submitted.append)
        segmenter.vad = SignalVad()
        segmenter.feed(np.full(480 * 60, 0.1, dtype=np.float32))
        segmenter.feed(np.zeros(480 * 30, dtype=np.float32))
        segmenter.finish()
        self.assertTrue(any(not job.final for job in submitted))
        finals = [job for job in submitted if job.final]
        self.assertEqual(len(finals), 1)
        self.assertEqual({job.sequence for job in submitted}, {1})
        self.assertEqual(np.count_nonzero(finals[0].audio), 480 * 60)

    def test_continuous_speech_is_bounded_and_keeps_audio(self):
        submitted = []
        segmenter = UtteranceSegmenter(submitted.append, max_seconds=2.0)
        segmenter.vad = SignalVad()
        segmenter.feed(np.full(480 * 150, 0.1, dtype=np.float32))
        segmenter.finish()
        finals = [job for job in submitted if job.final]
        self.assertEqual(len(finals), 3)
        self.assertEqual(sum(np.count_nonzero(job.audio) for job in finals), 480 * 150)
        self.assertTrue(all(len(job.audio) <= 16000 * 2 + 480 for job in finals))

    def test_phrase_commits_at_short_pause_before_speaker_continues(self):
        submitted = []
        segmenter = UtteranceSegmenter(submitted.append)
        segmenter.vad = SignalVad()
        segmenter.feed(np.full(480 * 80, 0.1, dtype=np.float32))
        segmenter.feed(np.zeros(480 * 8, dtype=np.float32))
        finals = [job for job in submitted if job.final]
        self.assertEqual(len(finals), 1)
        self.assertEqual(np.count_nonzero(finals[0].audio), 480 * 80)
        segmenter.feed(np.full(480 * 70, 0.2, dtype=np.float32))
        segmenter.feed(np.zeros(480 * 8, dtype=np.float32))
        finals = [job for job in submitted if job.final]
        self.assertEqual([job.sequence for job in finals], [1, 2])
        self.assertEqual(np.count_nonzero(finals[1].audio), 480 * 70)

    def test_brief_hesitation_does_not_fragment_a_short_phrase(self):
        submitted = []
        segmenter = UtteranceSegmenter(submitted.append)
        segmenter.vad = SignalVad()
        segmenter.feed(np.full(480 * 30, 0.1, dtype=np.float32))
        segmenter.feed(np.zeros(480 * 8, dtype=np.float32))
        self.assertFalse(any(job.final for job in submitted))
        segmenter.feed(np.full(480 * 50, 0.1, dtype=np.float32))
        segmenter.feed(np.zeros(480 * 3, dtype=np.float32))
        self.assertFalse(any(job.final for job in submitted))
        segmenter.feed(np.full(480 * 20, 0.1, dtype=np.float32))
        segmenter.feed(np.zeros(480 * 8, dtype=np.float32))
        finals = [job for job in submitted if job.final]
        self.assertEqual(len(finals), 1)
        self.assertEqual(np.count_nonzero(finals[0].audio), 480 * 100)

    def test_short_reply_finishes_without_waiting_for_phrase_length(self):
        submitted = []
        segmenter = UtteranceSegmenter(submitted.append)
        segmenter.vad = SignalVad()
        segmenter.feed(np.full(480 * 15, 0.1, dtype=np.float32))
        segmenter.feed(np.zeros(480 * 15, dtype=np.float32))
        self.assertEqual(len([job for job in submitted if job.final]), 1)

    def test_default_never_waits_twelve_seconds_for_continuous_speech(self):
        submitted = []
        segmenter = UtteranceSegmenter(submitted.append)
        segmenter.vad = SignalVad()
        signal = np.linspace(0.05, 0.2, 480 * 400, dtype=np.float32)
        segmenter.feed(signal[:480 * 167])
        self.assertEqual(len([job for job in submitted if job.final]), 1)
        segmenter.feed(signal[480 * 167:])
        segmenter.finish()
        finals = [job for job in submitted if job.final]
        self.assertEqual(len(finals), 3)
        self.assertTrue(all(len(job.audio) <= 16000 * 5 + 480 for job in finals))
        np.testing.assert_array_equal(np.concatenate([job.audio for job in finals]), signal)

    def test_queue_coalesces_previews_and_keeps_finals(self):
        jobs = JobQueue()
        audio = np.zeros(480, dtype=np.float32)
        for final in [False, False, True, False]:
            jobs.put(RecognitionJob(1, audio, final, time.perf_counter()))
        jobs.put(RecognitionJob(2, audio, True, time.perf_counter()))
        jobs.close()
        self.assertTrue(jobs.get().final)
        self.assertEqual(jobs.get().sequence, 2)
        self.assertIsNone(jobs.get())

    def test_time_limit_prefers_quiet_boundary_and_retains_entire_tail(self):
        submitted = []
        segmenter = UtteranceSegmenter(submitted.append)
        segmenter.vad = SignalVad()
        signal = np.full(480 * 300, 0.1, dtype=np.float32)
        signal[480 * 145:480 * 149] = 0.005
        segmenter.feed(signal[:480 * 167])
        first = [job for job in submitted if job.final]
        self.assertEqual(len(first), 1)
        # The subtitle ends in the quiet interval rather than halfway through
        # the following loud word. Previously displayed audio is not rewound.
        self.assertGreaterEqual(len(first[0].audio), 480 * 145)
        self.assertLessEqual(len(first[0].audio), 480 * 149)
        self.assertLessEqual(max(len(job.audio) for job in submitted if not job.final), len(first[0].audio))
        segmenter.feed(signal[480 * 167:])
        segmenter.finish()
        finals = [job for job in submitted if job.final]
        self.assertEqual([job.sequence for job in finals], [1, 2])
        np.testing.assert_array_equal(np.concatenate([job.audio for job in finals]), signal)

    def test_queue_reports_overload_instead_of_silent_loss(self):
        jobs = JobQueue()
        for sequence in range(8):
            jobs.put(RecognitionJob(sequence + 1, np.zeros(1), True, time.perf_counter()))
        with self.assertRaises(RuntimeError):
            jobs.put(RecognitionJob(9, np.zeros(1), True, time.perf_counter()))

    def test_time_limit_does_not_drop_a_short_voiced_tail(self):
        for tail_frames in range(1, 6):
            with self.subTest(tail_frames=tail_frames):
                submitted = []
                segmenter = UtteranceSegmenter(submitted.append)
                segmenter.vad = SignalVad()
                signal = np.full(480 * (167 + tail_frames), 0.1, dtype=np.float32)
                segmenter.feed(signal)
                segmenter.feed(np.zeros(480 * 20, dtype=np.float32))
                segmenter.finish()
                finals = [job for job in submitted if job.final]
                self.assertEqual([job.sequence for job in finals], [1, 2])
                self.assertEqual(sum(np.count_nonzero(job.audio) for job in finals), len(signal))

    def test_continuation_ends_at_a_real_pause_before_later_noise(self):
        submitted = []
        segmenter = UtteranceSegmenter(submitted.append)
        segmenter.vad = SignalVad()
        segmenter.feed(np.full(480 * 167, 0.1, dtype=np.float32))
        segmenter.feed(np.zeros(480 * 20, dtype=np.float32))
        segmenter.feed(np.full(480 * 4, 0.1, dtype=np.float32))
        segmenter.feed(np.zeros(480 * 20, dtype=np.float32))
        self.assertEqual(len([job for job in submitted if job.final]), 1)

    def test_device_change_finalizes_pending_audio_and_keeps_distinct_ids(self):
        submitted = []
        segmenter = UtteranceSegmenter(submitted.append)
        segmenter.vad = SignalVad()
        segmenter.feed(np.full(480 * 12 + 97, 0.1, dtype=np.float32))
        segmenter.end_stream()
        segmenter.feed(np.full(480 * 10, 0.2, dtype=np.float32))
        segmenter.end_stream()
        segmenter.end_stream()
        finals = [job for job in submitted if job.final]
        self.assertEqual([job.sequence for job in finals], [1, 2])
        self.assertEqual(np.count_nonzero(finals[0].audio), 480 * 12 + 97)
        self.assertEqual(np.count_nonzero(finals[1].audio), 480 * 10)
        self.assertEqual(len(segmenter.pending), 0)
        self.assertFalse(segmenter.preroll or segmenter.recent_voice or segmenter.continuation)

    def test_device_change_clears_uncommitted_preroll(self):
        submitted = []
        segmenter = UtteranceSegmenter(submitted.append)
        segmenter.vad = SignalVad()
        segmenter.feed(np.full(480, 0.2, dtype=np.float32))
        segmenter.end_stream()
        segmenter.feed(np.full(480 * 12, 0.1, dtype=np.float32))
        segmenter.end_stream()
        self.assertEqual(len(submitted), 1)
        self.assertEqual(np.count_nonzero(submitted[0].audio), 480 * 12)
        self.assertLess(float(np.max(submitted[0].audio)), 0.15)


class DeviceRecoveryChecks(unittest.TestCase):
    def run_capture(self, endpoints, capture):
        stop = threading.Event()
        segmenter, status = Mock(), Mock()
        with patch('capture.get_default_output_id', side_effect=endpoints), \
                patch('capture._capture_endpoint', side_effect=lambda *args: capture(stop, *args)), \
                patch.object(stop, 'wait', return_value=False):
            capture_system_audio(segmenter, stop, status)
        return segmenter, status

    def test_switch_and_temporary_device_absence_recover(self):
        opened = []

        def capture(stop, segmenter, event, status, deadline, endpoint):
            opened.append(endpoint)
            if endpoint == 'headphones':
                stop.set()

        segmenter, status = self.run_capture(['speakers', None, 'headphones'], capture)
        self.assertEqual(opened, ['speakers', 'headphones'])
        self.assertEqual(segmenter.end_stream.call_count, 3)
        self.assertTrue(any('等待连接' in call.args[0] for call in status.call_args_list))

    def test_open_failure_retries_without_losing_segmenter(self):
        attempts = []

        def capture(stop, segmenter, event, status, deadline, endpoint):
            attempts.append(segmenter)
            if len(attempts) == 1:
                raise OSError('device invalidated during open')
            stop.set()

        segmenter, status = self.run_capture(['headphones', 'headphones'], capture)
        self.assertEqual(attempts, [segmenter, segmenter])
        self.assertTrue(any('自动重连' in call.args[0] for call in status.call_args_list))

    def test_query_failure_recovers_when_audio_service_returns(self):
        def capture(stop, *args):
            stop.set()

        segmenter, status = self.run_capture([OSError('service unavailable'), 'headphones'], capture)
        self.assertEqual(segmenter.end_stream.call_count, 2)
        self.assertTrue(any('自动重连' in call.args[0] for call in status.call_args_list))

    def test_recognizer_backlog_is_not_hidden_as_device_error(self):
        def capture(stop, *args):
            raise RuntimeError('recognition backlog')

        with self.assertRaisesRegex(RuntimeError, 'recognition backlog'):
            self.run_capture(['headphones'], capture)

    def test_stop_while_waiting_for_device(self):
        stop = threading.Event()
        with patch('capture.get_default_output_id', return_value=None), \
                patch('capture._capture_endpoint') as capture, \
                patch.object(stop, 'wait', side_effect=lambda seconds: stop.set()):
            capture_system_audio(Mock(), stop, Mock())
        capture.assert_not_called()

    def fake_audio(self, rate=16000, channels=2, active=True, supply_audio=True):
        stream = Mock()
        stream.__enter__ = Mock(return_value=stream)
        stream.__exit__ = Mock(return_value=False)
        stream.is_active.return_value = active
        audio = Mock()
        audio.__enter__ = Mock(return_value=audio)
        audio.__exit__ = Mock(return_value=False)
        audio.get_default_wasapi_loopback.return_value = {
            'defaultSampleRate': rate, 'maxInputChannels': channels,
            'index': 10, 'name': 'Test headphones [Loopback]'}

        def open_stream(**kwargs):
            if supply_audio:
                pcm = np.full((round(rate * 0.03), channels), 3000, dtype='<i2')
                kwargs['stream_callback'](pcm.tobytes(), len(pcm), {}, 0)
            return stream

        audio.open.side_effect = open_stream
        module = types.SimpleNamespace(PyAudio=Mock(return_value=audio),
                                       paInt16=8, paComplete=1, paAbort=2, paContinue=0)
        return module, audio, stream

    def test_real_capture_loop_detects_switch_even_while_old_stream_active(self):
        module, audio, stream = self.fake_audio()
        samples = []
        segmenter = Mock()
        segmenter.feed.side_effect = samples.append
        with patch.dict('sys.modules', {'pyaudiowpatch': module}), \
                patch('capture.get_default_output_id', side_effect=['old', 'old', 'new']), \
                patch('capture.time.monotonic', side_effect=[0, 0, 0.6]):
            _capture_endpoint(segmenter, threading.Event(), Mock(), float('inf'), 'old')
        self.assertEqual(sum(len(chunk) for chunk in samples), 480)
        stream.__exit__.assert_called_once()
        audio.__exit__.assert_called_once()

    def test_channel_count_and_resampler_follow_new_stream_format(self):
        for rate, channels in [(16000, 2), (48000, 1)]:
            with self.subTest(rate=rate, channels=channels):
                module, audio, stream = self.fake_audio(rate, channels)
                samples = []
                segmenter = Mock()
                segmenter.feed.side_effect = samples.append
                with patch.dict('sys.modules', {'pyaudiowpatch': module}), \
                        patch('capture.get_default_output_id', side_effect=['old', 'old', 'new']), \
                        patch('capture.time.monotonic', side_effect=[0, 0, 0.6]):
                    _capture_endpoint(segmenter, threading.Event(), Mock(), float('inf'), 'old')
                self.assertEqual(sum(len(chunk) for chunk in samples), 480)
                self.assertEqual(audio.open.call_args.kwargs['rate'], rate)
                self.assertEqual(audio.open.call_args.kwargs['channels'], channels)

    def test_inactive_stream_enters_reconnect_path(self):
        module, audio, stream = self.fake_audio(active=False)
        with patch.dict('sys.modules', {'pyaudiowpatch': module}), \
                patch('capture.get_default_output_id', return_value='headphones'), \
                self.assertRaises(OSError):
            _capture_endpoint(Mock(), threading.Event(), Mock(), float('inf'), 'headphones')
        stream.__exit__.assert_called_once()
        audio.__exit__.assert_called_once()

    def test_silent_active_stream_does_not_trigger_reconnect(self):
        module, audio, stream = self.fake_audio(supply_audio=False)
        status = Mock()
        segmenter = Mock()
        with patch.dict('sys.modules', {'pyaudiowpatch': module}), \
                patch('capture.get_default_output_id', return_value='headphones'), \
                patch('capture.time.monotonic', side_effect=[0, 0, 0.2, 1.1]):
            _capture_endpoint(segmenter, threading.Event(), status, 1, 'headphones')
        self.assertEqual(status.call_count, 1)
        self.assertIn('正在识别系统声音', status.call_args.args[0])
        self.assertTrue(all(not np.any(call.args[0]) for call in segmenter.feed.call_args_list))

    def test_change_during_enumeration_does_not_open_stale_device(self):
        module, audio, stream = self.fake_audio()
        with patch.dict('sys.modules', {'pyaudiowpatch': module}), \
                patch('capture.get_default_output_id', return_value='new'):
            _capture_endpoint(Mock(), threading.Event(), Mock(), float('inf'), 'old')
        audio.open.assert_not_called()


if __name__ == '__main__':
    unittest.main()
