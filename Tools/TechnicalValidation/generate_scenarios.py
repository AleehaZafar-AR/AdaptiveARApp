"""Writes the Level 2 technical-validation scenarios TV1-TV6 to Assets/Resources/TechnicalValidation.

Each scenario is a chronological script of HIGH-LEVEL participant events over the REAL
authored step sequence of the Quest build (StepRunner steps 1-6 and their action ids):

  continue        the Continue control (READ acknowledgement / step intro / auto-complete)
  success         the armed action's part accepted (production validator success path)
  failed_attempt  an unsuccessful attempt that is not a counted error (left the zone)
  error           a counted error (released in the zone, out of tolerance)
  wrong_component a wrong part picked up and put down
  wait            nothing for the delay
  finish_sequence clean continue/success events until the sequence ends

"after" is the authored delay in real seconds before the event (delay scale 1 = validation
mode). "action" is the production action the event is expected to meet (alignment check
only). Support levels are never scripted: the parser rejects any other event kind.

The scripted difficulty mirrors the Level 1 synthetic scenarios (S1, S2, S3, S5, S6, S8)
so Level 2 exercises the same evidence patterns through the real runtime. Re-run after
editing; the JSON files are the definitions of record. .meta files are generated once with
stable GUIDs (Unity needs them for Resources.Load in a player build).
"""
import json, os, uuid

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Resources", "TechnicalValidation"))

S1 = "step_01_crankshaft"
S2 = "step_02_piston001"
S3 = "step_03_piston002"
S4 = "step_04_remaining"
S6 = "step_06_camshaft"
PISTON_PHYSICAL = ["PistonHead", "ConnectingRod", "ConnectingPin", "install", "PistonEnd",
                   "pistonBolt", "pistonBoltOther", "PistonNut", "PistonNutOther"]


def ev(kind, after, action=None, part=None, note=None):
    d = {"t": kind, "after": after}
    if action: d["action"] = action
    if part: d["part"] = part
    if note: d["note"] = note
    return d


def cont(after, action): return ev("continue", after, action)
def ok(after, action): return ev("success", after, action)
def fail(after, action): return ev("failed_attempt", after, action)
def err(after, action): return ev("error", after, action)
def wrong(after, action, part=None): return ev("wrong_component", after, action, part)
def finish(after=4): return ev("finish_sequence", after)


def crank_clean(place_after=28):
    return [cont(3, S1 + ".locate"), ok(place_after, S1 + ".place")]


def piston_clean(step, secs):
    """READ acknowledgement, then every physical action accepted after its delay."""
    events = [cont(5, step + ".locate")]
    for a, s in zip(PISTON_PHYSICAL, secs):
        events.append(ok(s, step + "." + a))
    return events


def write(scn):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, scn["id"] + ".json")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(scn, f, indent=1)
        f.write("\n")
    meta = path + ".meta"
    if not os.path.exists(meta):
        with open(meta, "w", encoding="utf-8", newline="\n") as f:
            f.write("fileFormatVersion: 2\nguid: " + uuid.uuid4().hex + "\nTextScriptImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")
    print("wrote", path, len(scn["events"]), "events")


def folder_meta():
    meta = OUT + ".meta"
    if not os.path.exists(meta):
        with open(meta, "w", encoding="utf-8", newline="\n") as f:
            f.write("fileFormatVersion: 2\nguid: " + uuid.uuid4().hex + "\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")


# ---------------------------------------------------------------- TV1
TV1 = {
    "id": "TV1", "title": "Clean successful assembly",
    "description": "Every action acknowledged or placed correctly at the first attempt with ordinary pacing across the crankshaft and the first piston; the rest of the sequence is completed cleanly.",
    "expected": "remain L1; no unnecessary escalation; normal step reset behaviour at each step entry.",
    "events": crank_clean(28)
    + piston_clean(S2, (14, 16, 12, 18, 10, 8, 8, 8, 8))
    + [finish()],
}

# ---------------------------------------------------------------- TV2
TV2 = {
    "id": "TV2", "title": "Initial difficulty then recovery",
    "description": "Crankshaft clean. On the first piston's head-on-mat action: a failed attempt, a counted error, a wrong part, a counted error, a failed attempt, then success; every remaining action of the step and the rest of the sequence is clean.",
    "expected": "L1 to L2 during the burst and, if the model chooses it, L2 to L3; recovery behaviour observed naturally; no DECREASE is prescribed.",
    "events": crank_clean(30)
    + [cont(5, S2 + ".locate"),
       fail(6, S2 + ".PistonHead"), err(4, S2 + ".PistonHead"), wrong(5, S2 + ".PistonHead"), err(6, S2 + ".PistonHead"), fail(8, S2 + ".PistonHead"), ok(12, S2 + ".PistonHead"),
       ok(14, S2 + ".ConnectingRod"), ok(12, S2 + ".ConnectingPin"), ok(16, S2 + ".install"),
       ok(10, S2 + ".PistonEnd"), ok(8, S2 + ".pistonBolt"), ok(8, S2 + ".pistonBoltOther"), ok(8, S2 + ".PistonNut"), ok(8, S2 + ".PistonNutOther")]
    + [finish()],
}

# ---------------------------------------------------------------- TV3
TV3 = {
    "id": "TV3", "title": "Persistent difficulty",
    "description": "Counted errors and failed attempts on the crankshaft and on every placement of the first piston's sub-assembly, continuing after any support increase; each action is eventually completed.",
    "expected": "escalation through the real runtime, L1 to L2 and potentially L2 to L3; the L3 presentation applied; no transition beyond L3.",
    "events": [cont(3, S1 + ".locate"), err(20, S1 + ".place"), err(8, S1 + ".place"), ok(15, S1 + ".place"),
               cont(5, S2 + ".locate"),
               err(5, S2 + ".PistonHead"), fail(4, S2 + ".PistonHead"), err(5, S2 + ".PistonHead"), err(6, S2 + ".PistonHead"), ok(12, S2 + ".PistonHead"),
               err(6, S2 + ".ConnectingRod"), wrong(5, S2 + ".ConnectingRod"), err(6, S2 + ".ConnectingRod"), fail(7, S2 + ".ConnectingRod"), err(6, S2 + ".ConnectingRod"), ok(10, S2 + ".ConnectingRod"),
               err(5, S2 + ".ConnectingPin"), err(6, S2 + ".ConnectingPin"), fail(5, S2 + ".ConnectingPin"), ok(8, S2 + ".ConnectingPin"),
               err(7, S2 + ".install"), err(6, S2 + ".install"), err(6, S2 + ".install"), ok(12, S2 + ".install"),
               err(6, S2 + ".PistonEnd"), ok(9, S2 + ".PistonEnd"),
               ok(8, S2 + ".pistonBolt"), ok(8, S2 + ".pistonBoltOther"), ok(8, S2 + ".PistonNut"), ok(8, S2 + ".PistonNutOther")]
    + [finish()],
}

# ---------------------------------------------------------------- TV4
TV4 = {
    "id": "TV4", "title": "Noisy / erratic",
    "description": "Alternating successes and failures across the first two piston steps with occasional wrong-part grabs; no sustained run in either direction.",
    "expected": "scheduler and hysteresis integration; no rapid oscillation or invalid state; every decision structurally valid.",
    "events": crank_clean(25)
    + [cont(5, S2 + ".locate"),
       err(6, S2 + ".PistonHead"), ok(9, S2 + ".PistonHead"),
       wrong(5, S2 + ".ConnectingRod"), ok(8, S2 + ".ConnectingRod"),
       ok(11, S2 + ".ConnectingPin"),
       err(6, S2 + ".install"), err(5, S2 + ".install"), ok(9, S2 + ".install"),
       fail(7, S2 + ".PistonEnd"), ok(8, S2 + ".PistonEnd"),
       err(6, S2 + ".pistonBolt"), ok(9, S2 + ".pistonBolt"),
       wrong(5, S2 + ".pistonBoltOther"), err(5, S2 + ".pistonBoltOther"), ok(8, S2 + ".pistonBoltOther"),
       ok(13, S2 + ".PistonNut"), ok(8, S2 + ".PistonNutOther"),
       cont(5, S3 + ".locate"),
       fail(7, S3 + ".PistonHead"), ok(8, S3 + ".PistonHead"),
       err(6, S3 + ".ConnectingRod"), ok(9, S3 + ".ConnectingRod"),
       wrong(5, S3 + ".ConnectingPin"), err(5, S3 + ".ConnectingPin"), ok(8, S3 + ".ConnectingPin"),
       ok(13, S3 + ".install"), ok(9, S3 + ".PistonEnd"),
       err(6, S3 + ".pistonBolt"), ok(8, S3 + ".pistonBolt"), ok(8, S3 + ".pistonBoltOther"), ok(8, S3 + ".PistonNut"), ok(8, S3 + ".PistonNutOther")]
    + [finish()],
}

# ---------------------------------------------------------------- TV5
TV5 = {
    "id": "TV5", "title": "Slow but accurate",
    "description": "No errors at all, but long intervals between successful actions: the crankshaft and the first piston run well past their expected durations (120 s and 90 s).",
    "expected": "elapsed time alone must not produce inappropriate escalation.",
    "events": crank_clean(150)
    + piston_clean(S2, (55, 60, 45, 70, 40, 30, 30, 30, 30))
    + [finish()],
}

# ---------------------------------------------------------------- TV6
TV6 = {
    "id": "TV6", "title": "L3 recovery",
    "description": "Genuine difficulty on the first piston's head action: a first burst (two counted errors, a failed attempt) and, inside the hysteresis after a first increase, a second burst (wrong part, two counted errors, a failed attempt); from the head success on, every remaining action of the step and the rest of the sequence is clean.",
    "expected": "difficulty sufficient to reach L3 through the real runtime, then the frozen model's recovery behaviour under a clean current support episode; no DECREASE is required.",
    "events": crank_clean(28)
    + [cont(5, S2 + ".locate"),
       err(5, S2 + ".PistonHead"), fail(4, S2 + ".PistonHead"), err(4, S2 + ".PistonHead"),
       wrong(5, S2 + ".PistonHead"), err(4, S2 + ".PistonHead"), fail(2, S2 + ".PistonHead"), err(2, S2 + ".PistonHead"),
       ok(16, S2 + ".PistonHead"),
       ok(13, S2 + ".ConnectingRod"), ok(11, S2 + ".ConnectingPin"), ok(15, S2 + ".install"),
       ok(10, S2 + ".PistonEnd"), ok(8, S2 + ".pistonBolt"), ok(8, S2 + ".pistonBoltOther"), ok(8, S2 + ".PistonNut"), ok(8, S2 + ".PistonNutOther")]
    + [finish()],
}

folder_meta()
for s in (TV1, TV2, TV3, TV4, TV5, TV6):
    s["physiology"] = None          # the config's condition applies (nominal for the primary runs)
    write(s)
