import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


module_path = Path(__file__).with_name("stop-obs-with-buddy.py")
spec = importlib.util.spec_from_file_location("obs_watcher", module_path)
watcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(watcher)


class TraceTests(unittest.TestCase):
    def test_plain_websocket_is_closed_when_obs_is_idle(self):
        class Socket:
            closed = False

            def close(self):
                self.closed = True

        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "obs.json"
            config.write_text("{}")
            socket = Socket()  # websocket-client sockets have no context manager.
            with patch.object(watcher, "connect_obs", return_value=socket), patch.object(
                watcher, "obs_request", return_value={"outputActive": False}
            ):
                self.assertEqual(watcher.main(["--obs-config", str(config), "--traces", directory]), 0)
            self.assertTrue(socket.closed)

    def test_existing_active_run_and_manual_stop(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = Path(directory) / "a.jsonl"
            trace.write_text('{"event":"play_started"}\n')
            monitor = watcher.BuddyTrace(Path(directory))
            self.assertFalse(monitor.stopped())
            with trace.open("a") as stream:
                stream.write('{"action_ids":["a1"],"executed":true}\n')
                stream.write('{"event":"stop_requested"}\n')
            self.assertTrue(monitor.stopped())

    def test_non_event_and_incomplete_lines_do_not_hide_finish(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = Path(directory) / "mixed.jsonl"
            trace.write_text(
                '{"event":"play_started"}\n'
                '{"executed":true}\n'
                '{"event":"play_finished","status":"idle"}\n'
                '{"event":'
            )
            self.assertEqual(watcher.trace_events(trace), ["play_started", "play_finished"])

    def test_new_run_finishes_with_error(self):
        with tempfile.TemporaryDirectory() as directory:
            monitor = watcher.BuddyTrace(Path(directory))
            trace = Path(directory) / "b.jsonl"
            trace.write_text('{"event":"play_started"}\n')
            self.assertFalse(monitor.stopped())
            with trace.open("a") as stream:
                stream.write('{"event":"play_finished","status":"error"}\n')
            self.assertTrue(monitor.stopped())

    def test_previous_finished_run_is_ignored(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = Path(directory) / "old.jsonl"
            trace.write_text('{"event":"play_started"}\n{"event":"play_finished"}\n')
            monitor = watcher.BuddyTrace(Path(directory))
            self.assertFalse(monitor.stopped())

    def test_large_active_trace(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = Path(directory) / "large.jsonl"
            trace.write_text('{"event":"play_started"}\n' + '{"event":"model_response"}\n' * 4000)
            monitor = watcher.BuddyTrace(Path(directory))
            self.assertFalse(monitor.stopped())
            with trace.open("a") as stream:
                stream.write('{"event":"play_finished"}\n')
            self.assertTrue(monitor.stopped())


if __name__ == "__main__":
    unittest.main()
