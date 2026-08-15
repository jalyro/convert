# Sandboxing: what is possible, and what is not

The design document (§5.1, §9) called for Workers running under a
"low-integrity token". **That is not viable for this product**, and this file
records why, so it is not proposed again.

## Why low integrity does not work

A low-integrity process cannot write to ordinary user folders. It can write to
`%TEMP%\Low`, its own AppContainer store, and very little else.

Writing the converted file **next to the source** is the product's entire
interaction model. Right-click a photo, get a JPG beside it. A low-IL Worker
could decode the image and then fail to write the result anywhere the user can
see — which trades a real, everyday capability for a theoretical containment
benefit.

AppContainer has the same problem for the same reason, plus a brokering
requirement that would mean passing file handles from the Host for every
conversion.

## What is implemented instead

Defence in depth, chosen for things that hold without breaking the product.

### Process isolation

Every conversion runs in its own Worker process. A malformed HEIC that
segfaults libheif kills a disposable child, not the queue. This is the single
most valuable property and it was designed in from Phase 2.

### Job Object with a memory ceiling

`WorkerJobObject` holds every Worker and every ffmpeg process:

| Limit | Value | Why |
|---|---|---|
| Per process | 4 GB | Generous enough for a 100-megapixel TIFF; tight enough that runaway allocation dies rather than swapping |
| Job total | 12 GB | Bounds a whole batch |
| `KILL_ON_JOB_CLOSE` | on | The Host cannot leave encoders running, even if terminated abruptly |

`KILL_ON_JOB_CLOSE` fixes an observed problem, not a hypothetical one: testing
found two abandoned ffmpeg processes still encoding for jobs whose Host had
died, one with 94 CPU-minutes accumulated.

### Timeouts

Five minutes for images, four hours for audio and video. The distinction
matters — a flat cap killed a legitimate video encode and told the user their
file had timed out.

### No network

`-protocol_whitelist file` is pinned on every ffmpeg invocation, and inputs
carry a `file:` prefix, so a crafted filename cannot make ffmpeg fetch a URL.
A trimmed ffmpeg build should also use `--disable-network`
(see `trimmed-ffmpeg.md`) so the capability is not merely unused but absent.

### Argument safety

`ProcessStartInfo.ArgumentList` throughout, never a concatenated command line.
A file named `-i` is a filename. This is the most likely vulnerability in the
product and the discipline costs nothing.

### Path refusals

`PathGuard` rejects alternate data streams, reserved device names, reparse
points, and anything resolving outside its own directory. Validation runs
against the original path, before any renaming — see `docs/decisions.md` for what
happens when it does not.

## What is still open

- **Per-executable firewall rule.** The installer could add one blocking
  outbound traffic for the Worker and ffmpeg. Belt and braces over the protocol
  whitelist, and cheap.
- **Dropped privileges.** `CreateRestrictedToken` with `DISABLE_MAX_PRIVILEGE`
  keeps the user SID — so file writes still work — while removing privileges
  the Worker never needs. Worth doing; needs `CreateProcessAsUser`, which
  `ProcessStartInfo` cannot reach.
- **Untrusted-input marking.** Outputs derived from a file carrying
  Mark-of-the-Web already inherit it. Outputs from *any* untrusted source
  arguably should too.

## Honest summary

This is not a sandbox. It is process isolation with bounded resources, no
network, and careful argument handling. A libheif remote-code-execution bug
would run as the user.

The mitigation that actually matters for that case is keeping ffmpeg and
libvips current, which is why both are replaceable binaries rather than linked
libraries.
