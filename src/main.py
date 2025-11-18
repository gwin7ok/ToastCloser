import argparse
import logging
from notifier import ToastWatcher


def main():
    parser = argparse.ArgumentParser(description='Chrome toast auto-closer')
    parser.add_argument('--display-limit-seconds', type=float, default=10.0, help='seconds before attempting close')
    # --max-seconds removed: hard timeout behavior was removed; only use --display-limit-seconds and --poll-interval-seconds
    parser.add_argument('--poll-interval-seconds', type=float, default=1.0, help='poll interval seconds')
    parser.add_argument('--log-level', default='INFO', help='logging level')
    parser.add_argument('--debug', action='store_true', help='enable debug output (more properties)')
    args = parser.parse_args()

    logging.basicConfig(level=getattr(logging, args.log_level.upper(), logging.INFO),
                        format='%(asctime)s %(levelname)s %(message)s')

    watcher = ToastWatcher(min_seconds=args.display_limit_seconds, poll_interval=args.poll_interval_seconds, debug=args.debug)
    watcher.run()


if __name__ == '__main__':
    main()
