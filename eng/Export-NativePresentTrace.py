"""Export exact renderer-native call markers for offline display verification."""
import argparse
import csv
import hashlib
import io
import json
from collections import defaultdict
from pathlib import Path, PureWindowsPath

PREFIX = "PACKAGE G MEASURE "
IDENTITY = ("ProcessId", "NativeThreadId", "WindowHandle", "SwapChainAddress")


def verify(condition, message):
    if not condition:
        raise ValueError(message)


def read_verified(path, digest):
    data = path.read_bytes()
    verify(hashlib.sha256(data).hexdigest().lower() == digest.lower(), f"Digest mismatch for {path}")
    return data.decode("utf-8-sig")


def export(log, attempt):
    events = [json.loads(line[len(PREFIX):]) for line in log.splitlines() if line.startswith(PREFIX)]
    inventories = [event for event in events if event["kind"] == "native-present-inventory"]
    verify(len(inventories) == 1, "Expected one completed native-present inventory")
    inventory = inventories[0]
    markers = [event["present"] for event in events if event["kind"] == "native-present"]
    frequency = int(attempt["QpcFrequency"])
    verify(attempt["Started"] == attempt["Passed"] == "True" and attempt["ExitCode"] == "0",
           "The installed attempt did not complete successfully")
    verify(frequency > 0 and inventory["frequency"] == frequency and inventory["dropped"] == 0 and
           inventory["retained"] == len(markers), "Incomplete or mismatched native-present inventory")
    begin = int(attempt["StartedQpc"])
    finish = int(attempt["FinishedQpc"])
    pid = int(attempt["ProcessId"])

    chains = defaultdict(list)
    for marker in markers:
        verify(marker["ProcessId"] == pid and all(marker[field] > 0 for field in IDENTITY)
               and marker["SwapChainLifetime"] > 0, "Invalid native-present identity")
        verify(begin <= marker["BeforeQpc"] <= marker["AfterQpc"] <= finish,
               "Present marker lies outside the exact installed attempt")
        chains[marker["SwapChainLifetime"]].append(marker)
    verify(len(chains) == 3, "Expected three renderer swap-chain lifetimes")
    lifetimes = sorted(chains.values(), key=lambda chain: chain[0]["BeforeQpc"])
    last_end = begin - 1
    for chain in lifetimes:
        last_fence = 0
        identity = tuple(chain[0][field] for field in IDENTITY)
        for ordinal, marker in enumerate(chain, 1):
            verify(marker["CallOrdinal"] == ordinal and last_end < marker["BeforeQpc"] and
                   last_fence < marker["QueueFence"], "Reordered, overlapping or missing native Present call")
            verify(tuple(marker[field] for field in IDENTITY) == identity, "Swap-chain identity changed within its life")
            last_end = marker["AfterQpc"]
            last_fence = marker["QueueFence"]

    handoffs = [event for event in events if event["kind"] == "handoff"]
    verify([event["direction"] for event in handoffs] == ["to-orbit", "to-ground"], "Expected both handoff directions")
    for event, chain in zip(handoffs, lifetimes[1:]):
        verify(event["frequency"] == frequency and event["sequence"] == event["sourceFrameSequence"] > 0,
               "The app did not report an exact content sequence")
        ends = [marker for marker in chain if
                (marker["QueueFence"], marker["AfterQpc"], marker["HResult"]) ==
                (event["fence"], event["end"], 0)]
        verify(len(ends) == 1, f"Missing exact native endpoint for {event['direction']}")

    return {"SchemaVersion": 1, "Frequency": frequency, "Dropped": 0,
            "Markers": [marker for chain in lifetimes for marker in chain]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("attempts", type=Path)
    parser.add_argument("stdout", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--attempts-sha256", required=True)
    parser.add_argument("--stdout-sha256", required=True)
    parser.add_argument("--pair", required=True)
    parser.add_argument("--replay", required=True)
    parser.add_argument("--variant", choices=("on", "off"), required=True)
    args = parser.parse_args()
    attempts = csv.DictReader(io.StringIO(read_verified(args.attempts, args.attempts_sha256)))
    selected = [row for row in attempts if (row["Pair"], row["Replay"], row["Mode"]) ==
                (args.pair, args.replay, args.variant)]
    verify(len(selected) == 1, "Ambiguous installed attempt selection")
    attempt = selected[0]
    verify(attempt["ExecutionHost"] == "InstalledAppHost" and attempt["ObservedCoreClr"].strip(),
           "Missing installed AppHost and observed CLR provenance")
    verify(PureWindowsPath(attempt["Stdout"]).name == args.stdout.name,
           "Stdout file does not belong to the exact installed attempt")
    bundle = export(read_verified(args.stdout, args.stdout_sha256), attempt)
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(bundle, output, indent=2)


if __name__ == "__main__":
    main()
