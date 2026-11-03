# Default guidelines

For every task, follow the `karpathy-guidelines` skill: think before coding, keep it simple, make surgical changes, and define verifiable success criteria. For trivial tasks, use judgment.

After making changes to the manga-downloader app, rebuild the runnable `manga-downloader/dist` output before finishing. Publish the CLI, BrowserWindow, and Launcher projects in Release configuration to that directory, as in `Start manga-downloader.cmd`; preserve the existing `dist/launcher.json` settings, and update `dist/.build-stamp` only after all three publishes succeed. Report any build failure.

