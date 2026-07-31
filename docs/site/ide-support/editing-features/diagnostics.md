# Diagnostics — Errors & Warnings

Steps in a `.feature` file with no matching step definition are underlined
with a warning squiggle and listed in your IDE's Error List / Problems
panel. Structural errors — a missing `Feature:` header, invalid tag syntax,
and similar parse errors — are underlined with a red error squiggle instead,
so you can tell "not written correctly" apart from "no binding exists yet"
at a glance.

Diagnostics refresh live as you type, and whenever step bindings change (on
a C# file save or a build) — no need to reopen the file to see an unmatched
step turn green once you add the matching binding.

TODO(media): 📷 screenshot — a squiggle under an unmatched step, plus the
matching Error List / Problems panel entry.

TODO(media): 🎬 gif (optional) — a squiggle disappearing live as a matching
binding is added. Nice to have; the screenshot above already conveys the
static state on its own.
