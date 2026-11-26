# Audio corpus

Drop captured `.wav` files here, then run:

```bash
dotnet test DireControl.Tests/DireControl.Tests.csproj --filter Corpus_ReplayReport
```

Each file is replayed through the real demodulator (`AfskReceiver`, 1024-sample
blocks, exactly as the live capture loop feeds it) and a diagnostic report is
written to `replay-report.txt` here as well as to the test log.

The report gives, per capture: duration, peak/RMS level, clipping percentage, DC
offset, how long carrier was armed and over how many bursts, valid and failed
frame counts per demod profile, TNC2 lines for anything that decoded, and a
first-pass diagnosis.

Captures come from two places:

- **Automatic** — a transmission that armed carrier but produced no valid frame
  is saved on its own, rate-limited, to the API's `recordings/` directory.
- **Manual** — the record button in the audio menu.

To replay the API's recordings directory directly instead of copying files:

```bash
DIRECONTROL_CORPUS=/home/william/DireControl/DireControl.Api/recordings \
  dotnet test DireControl.Tests/DireControl.Tests.csproj --filter Corpus_ReplayReport
```

`.wav` files and `replay-report.txt` are gitignored — they are a local
diagnostic workspace, not a committed fixture set.
