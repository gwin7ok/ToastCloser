import argparse
import logging
from notifier import ToastWatcher


def main():
    parser = argparse.ArgumentParser(description='Chrome toast auto-closer')
    parser.add_argument('--min-seconds', type=float, default=10.0, help='minimum seconds before attempting close')
    parser.add_argument('--max-seconds', type=float, default=30.0, help='maximum seconds before forcing close')
    parser.add_argument('--poll', type=float, default=1.0, help='poll interval seconds')
    parser.add_argument('--log-level', default='INFO', help='logging level')
    parser.add_argument('--debug', action='store_true', help='enable debug output (more properties)')
    args = parser.parse_args()

    logging.basicConfig(level=getattr(logging, args.log_level.upper(), logging.INFO),
                        format='%(asctime)s %(levelname)s %(message)s')

    watcher = ToastWatcher(min_seconds=args.min_seconds, max_seconds=args.max_seconds, poll_interval=args.poll, debug=args.debug)
    watcher.run()


if __name__ == '__main__':
    main()
