"""Writes the S1-S8 virtual-participant scenario files next to this script.

Each scenario is a chronological, scripted event stream. Support-level changes are never
scripted: in the simulation the level moves only through the real decision pipeline.
Steps mirror the authored StepData assets (ids, complexity, expected durations); the piston
steps have four actions (head on mat, rod to head, pin, install), the crankshaft and camshaft
steps one. Re-run this script after editing; the JSON files are the definitions of record.
"""
import json, os

HERE = os.path.dirname(os.path.abspath(__file__))

CRANK = ("step_01_crankshaft", 3, 120)
P1 = ("step_02_piston001", 2, 90)
P2 = ("step_03_piston002", 1, 90)
CAM = ("step_06_camshaft", 2, 120)
PISTON_ACTIONS = ["head_on_mat", "rod_to_head", "pin_through_rod", "install_piston"]


def step(s):
    sid, cx, exp = s
    return {"t": "step", "id": sid, "complexity": cx, "expected_s": exp}


def action(aid):
    return {"t": "action", "id": aid}


def adv(s):
    return {"t": "advance", "s": s}


FAIL = {"t": "failed_attempt"}
ERR = {"t": "error"}
OK = {"t": "success"}


def wrong(part="part.PistonKit002.PistonHead"):
    return {"t": "wrong_component", "part": part}


def clean_piston(s, secs=(14, 16, 12, 18)):
    """A piston step done correctly: four actions, one success each."""
    ev = [step(s)]
    for a, t in zip(PISTON_ACTIONS, secs):
        ev += [action(a), adv(t), OK]
    return ev


def write(scn):
    path = os.path.join(HERE, scn["id"] + ".json")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(scn, f, indent=1)
        f.write("\n")
    print("wrote", path, len(scn["events"]), "events")


# ---------------------------------------------------------------- S1
S1 = {
    "id": "S1_consistently_successful",
    "title": "Consistently successful",
    "description": "Every action is placed correctly at the first attempt with ordinary pacing; no errors, no wrong parts.",
    "expected": "mostly L1; no unnecessary escalation.",
    "events": [step(CRANK), action("place_crankshaft"), adv(28), OK]
    + clean_piston(P1) + clean_piston(P2)
    + [step(CAM), action("place_camshaft"), adv(30), OK],
}

# ---------------------------------------------------------------- S2
S2 = {
    "id": "S2_initial_difficulty_then_recovery",
    "title": "Initial difficulty then recovery",
    "description": "The crankshaft goes in cleanly. On the first piston the head-on-mat action produces failed attempts, two counted errors and a wrong part at L1 within a short burst (one counted error and one failed attempt may still fall after a first support increase); from the head success on, the remaining actions of the step and the next piston are clean.",
    "expected": "reasonable upward adaptation during the difficulty; later KEEP or DECREASE decisions interpretable from the clean current support episode. No exact decrease point is asserted.",
    "events": [step(CRANK), action("place_crankshaft"), adv(30), OK,
               step(P1), action("head_on_mat"), adv(6), FAIL, adv(4), ERR, adv(5), wrong(), adv(6), ERR, adv(8), FAIL, adv(12), OK,
               action("rod_to_head"), adv(14), OK,
               action("pin_through_rod"), adv(12), OK,
               action("install_piston"), adv(16), OK]
    + clean_piston(P2),
}

# ---------------------------------------------------------------- S3
S3 = {
    "id": "S3_persistent_difficulty",
    "title": "Persistent difficulty",
    "description": "Repeated counted errors and failed attempts on every action of the first piston, continuing after any support increase; each action is eventually completed.",
    "expected": "L1 to L2 and potentially L2 to L3; no transition beyond L3 (boundary clamp recorded if attempted).",
    "events": [step(CRANK), action("place_crankshaft"), adv(20), ERR, adv(8), ERR, adv(15), OK,
               step(P1), action("head_on_mat"), adv(5), ERR, adv(4), FAIL, adv(5), ERR, adv(6), ERR, adv(12), OK,
               action("rod_to_head"), adv(6), ERR, adv(5), wrong("part.PistonKit003.ConnectingRod"), adv(6), ERR, adv(7), FAIL, adv(6), ERR, adv(10), OK,
               action("pin_through_rod"), adv(5), ERR, adv(6), ERR, adv(5), FAIL, adv(8), OK,
               action("install_piston"), adv(7), ERR, adv(6), ERR, adv(6), ERR, adv(12), OK],
}

# ---------------------------------------------------------------- S4
S4 = {
    "id": "S4_progressive_deterioration",
    "title": "Progressive deterioration",
    "description": "Starts clean (crankshaft, head), then failed attempts and errors become more frequent action by action within the same step.",
    "expected": "support responds to the emerging difficulty rather than only to cumulative history.",
    "events": [step(CRANK), action("place_crankshaft"), adv(26), OK,
               step(P1), action("head_on_mat"), adv(14), OK,
               action("rod_to_head"), adv(8), FAIL, adv(9), OK,
               action("pin_through_rod"), adv(6), ERR, adv(5), FAIL, adv(6), ERR, adv(9), OK,
               action("install_piston"), adv(5), ERR, adv(4), wrong(), adv(5), ERR, adv(5), ERR, adv(6), FAIL, adv(5), ERR, adv(10), OK],
}

# ---------------------------------------------------------------- S5
S5 = {
    "id": "S5_noisy_erratic",
    "title": "Noisy / erratic",
    "description": "Alternating successes and failures across two piston steps, with occasional wrong-component grabs; no sustained run in either direction.",
    "expected": "no rapid oscillation; hysteresis operates; every decision structurally valid.",
    "events": [step(CRANK), action("place_crankshaft"), adv(25), OK,
               step(P1), action("head_on_mat"), adv(6), ERR, adv(9), OK,
               action("rod_to_head"), adv(5), wrong("part.PistonKit003.ConnectingRod"), adv(8), OK,
               action("pin_through_rod"), adv(11), OK,
               action("install_piston"), adv(6), ERR, adv(5), ERR, adv(9), OK,
               step(P2), action("head_on_mat"), adv(7), FAIL, adv(8), OK,
               action("rod_to_head"), adv(6), ERR, adv(9), OK,
               action("pin_through_rod"), adv(5), wrong("part.PistonKit004.ConnectingPin"), adv(5), ERR, adv(8), OK,
               action("install_piston"), adv(13), OK],
}

# ---------------------------------------------------------------- S6
S6 = {
    "id": "S6_slow_but_accurate",
    "title": "Slow but accurate",
    "description": "No errors at all, but long intervals between successful actions: every step runs well past its expected duration.",
    "expected": "elapsed time alone must not automatically cause escalation.",
    "events": [step(CRANK), action("place_crankshaft"), adv(150), OK]
    + clean_piston(P1, secs=(55, 60, 45, 70))
    + clean_piston(P2, secs=(50, 58, 48, 66)),
}

# ---------------------------------------------------------------- S7
S7 = {
    "id": "S7_fast_but_error_prone",
    "title": "Fast but error-prone",
    "description": "Many attempts in short periods with repeated counted errors; actions still complete quickly.",
    "expected": "difficulty reaches the model promptly through the existing event scheduler.",
    "events": [step(CRANK), action("place_crankshaft"), adv(4), ERR, adv(2), ERR, adv(2), FAIL, adv(3), ERR, adv(4), OK,
               step(P1), action("head_on_mat"), adv(2), ERR, adv(2), ERR, adv(1), FAIL, adv(2), ERR, adv(3), OK,
               action("rod_to_head"), adv(2), ERR, adv(2), wrong("part.PistonKit003.ConnectingRod"), adv(2), ERR, adv(3), OK,
               action("pin_through_rod"), adv(2), ERR, adv(1), ERR, adv(2), FAIL, adv(2), OK,
               action("install_piston"), adv(2), ERR, adv(2), ERR, adv(2), ERR, adv(3), OK],
}

# ---------------------------------------------------------------- S8
S8 = {
    "id": "S8_recovery_at_L3",
    "title": "Recovery at L3",
    "description": "Genuine difficulty on the first piston's head action: a first burst (two counted errors, a failed attempt) and, inside the 15 s hysteresis that follows a first increase, a second burst (wrong part, two counted errors, a failed attempt) that is evaluated when the hysteresis ends, so two upward decisions are possible before any success. From the head success on, every remaining action of the step and the whole next step are clean: no error-like event falls after the second decision opportunity.",
    "expected": "difficulty sufficient to reach L3, then observe the de-escalation behaviour under a clean current support episode. No required number of successes before a decrease is asserted.",
    "events": [step(CRANK), action("place_crankshaft"), adv(28), OK,
               step(P1), action("head_on_mat"), adv(5), ERR, adv(4), FAIL, adv(4), ERR,
               adv(5), wrong(), adv(4), ERR, adv(2), FAIL, adv(2), ERR,
               adv(16), OK,
               action("rod_to_head"), adv(13), OK,
               action("pin_through_rod"), adv(11), OK,
               action("install_piston"), adv(15), OK]
    + clean_piston(P2),
}

for s in (S1, S2, S3, S4, S5, S6, S7, S8):
    s["operator_experience"] = "unspecified"
    s["step_count"] = 6
    write(s)
