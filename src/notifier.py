import time
import logging
from pywinauto import Desktop


class ToastWatcher:
    def __init__(self, class_name='FlexibleToastView', automation_id='PriorityToastView',
                 min_seconds=10, max_seconds=30, poll_interval=1.0, debug=False):
        self.class_name = class_name
        self.automation_id = automation_id
        self.min_seconds = min_seconds
        self.max_seconds = max_seconds
        self.poll = poll_interval
        self.desktop = Desktop(backend='uia')
        self.debug = debug
        # tracked: key -> first_seen_timestamp
        self.tracked = {}

    def _make_key(self, win):
        # Create a reasonably stable key for a notification window using process id and rectangle
        try:
            pid = win.process_id()
        except Exception:
            pid = 0
        try:
            r = win.rectangle()
            rect_key = f"{r.left}-{r.top}-{r.right}-{r.bottom}"
        except Exception:
            rect_key = "no-rect"
        return f"{pid}:{rect_key}"

    def _close_via_button(self, win):
        try:
            # Child button titled '閉じる'
            btn = win.child_window(title='閉じる', control_type='Button')
            if btn.exists(timeout=0.2):
                try:
                    btn.invoke()
                    logging.info('Invoked close button')
                    return True
                except Exception:
                    try:
                        btn.click_input()
                        logging.info('Clicked close button (input)')
                        return True
                    except Exception as e:
                        logging.warning('Failed to click/invoke close button: %s', e)
            else:
                logging.debug('Close button not found via child_window')
        except Exception as e:
            logging.debug('Exception while closing via button: %s', e)
        return False

    def scan_once(self):
        # Find all toast windows matching class name (and optionally automation id)
        try:
            candidates = self.desktop.windows(class_name=self.class_name)
        except Exception as e:
            logging.error('Desktop.windows threw: %s', e)
            candidates = []

        # If no candidates found, perform a broader scan to discover possible variations
        if not candidates:
            logging.debug('No candidates found by class_name; performing broad scan')
            try:
                all_windows = self.desktop.windows()
            except Exception as e:
                logging.debug('Failed to list all windows: %s', e)
                all_windows = []

            fallback = []
            for w in all_windows:
                try:
                    name = ''
                    try:
                        name = w.window_text() or ''
                    except Exception:
                        name = ''
                    ei = getattr(w, 'element_info', None)
                    aid = ''
                    cname = ''
                    ctrl = ''
                    try:
                        if ei is not None:
                            aid = getattr(ei, 'automation_id', '') or ''
                            cname = getattr(ei, 'class_name', '') or ''
                            ctrl = getattr(ei, 'control_type', '') or ''
                    except Exception:
                        pass

                    text_for_match = ' '.join([str(name), str(aid), str(cname), str(ctrl)]).lower()
                    # heuristics: contain chrome/youtube/通知/prioritytoast/flexible/toast
                    if ('google chrome' in text_for_match or 'youtube' in text_for_match or '通知' in text_for_match
                            or 'prioritytoast' in text_for_match or 'prioritytoastview' in text_for_match
                            or 'flexibletoast' in text_for_match or 'flexibletoastview' in text_for_match
                            or 'toast' in text_for_match):
                        fallback.append(w)
                except Exception:
                    continue

            if fallback:
                logging.info('Fallback scan found %d possible windows', len(fallback))
                candidates = fallback

        now = time.time()
        current_keys = set()

        for w in candidates:
            # Filter by automation id if available
            try:
                aid = ''
                try:
                    aid = w.automation_id()
                except Exception:
                    aid = ''
                if self.automation_id and aid and aid != self.automation_id:
                    continue
            except Exception:
                pass

            key = self._make_key(w)
            current_keys.add(key)
            if key not in self.tracked:
                self.tracked[key] = now
                logging.info('Found new toast: key=%s name=%s', key, w.window_text())
                if self.debug:
                    try:
                        info = {}
                        try:
                            info['process_id'] = w.process_id()
                        except Exception:
                            info['process_id'] = None
                        try:
                            info['handle'] = getattr(w, 'handle', None)
                        except Exception:
                            info['handle'] = None
                        try:
                            info['rect'] = w.rectangle()
                        except Exception:
                            info['rect'] = None
                        try:
                            # pywinauto element_info may provide automation_id/class_name
                            ei = getattr(w, 'element_info', None)
                            if ei is not None:
                                info['automation_id'] = getattr(ei, 'automation_id', None)
                                info['class_name'] = getattr(ei, 'class_name', None)
                                info['name'] = getattr(ei, 'name', None)
                                info['control_type'] = getattr(ei, 'control_type', None)
                        except Exception:
                            pass
                        logging.info('DEBUG toast properties: %s', info)
                    except Exception:
                        logging.exception('Failed to log debug info for toast')
                continue

            elapsed = now - self.tracked[key]
            logging.debug('Toast %s elapsed=%.1f', key, elapsed)

            if elapsed >= self.min_seconds:
                logging.info('Toast %s exceeded min_seconds (%.1f), attempting close', key, elapsed)
                closed = self._close_via_button(w)
                if not closed and elapsed >= self.max_seconds:
                    # As fallback, try to close via hwnd if available
                    try:
                        hwnd = w.handle
                        if hwnd:
                            import ctypes
                            WM_CLOSE = 0x0010
                            ctypes.windll.user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
                            logging.info('Posted WM_CLOSE to hwnd %s', hwnd)
                            closed = True
                    except Exception as e:
                        logging.debug('Failed to post WM_CLOSE: %s', e)

                if closed:
                    # remove from tracked; it will disappear on next scan
                    try:
                        del self.tracked[key]
                    except KeyError:
                        pass

        # Clean up tracked entries that are no longer present
        removed = []
        for k in list(self.tracked.keys()):
            if k not in current_keys:
                # If it's been gone for a while, remove tracking
                if now - self.tracked[k] > 5.0:
                    removed.append(k)
                    del self.tracked[k]

        if removed:
            logging.debug('Removed stale tracked keys: %s', removed)

    def run(self):
        logging.info('ToastWatcher started (min=%s max=%s poll=%s)', self.min_seconds, self.max_seconds, self.poll)
        try:
            while True:
                self.scan_once()
                time.sleep(self.poll)
        except KeyboardInterrupt:
            logging.info('Interrupted, exiting')
