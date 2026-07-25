# Test corpus

Drop sample files here (any subfolder layout). Git-ignored except this README —
these files are large and not ours to redistribute. `S0.Hello` walks up to find
this folder and counts files by extension.

Aim for **one real file per format** so the later spikes prove coverage, not just JPEG:

| Group | Extensions | Source |
|---|---|---|
| RAW | `.cr2 .cr3 .nef .arw .raf .orf .rw2 .pef .dng .x3f` | https://raw.pixls.us (per-camera sample sets) |
| Stills | `.jpg .heic .tif .png` | any camera / phone |
| Video | `.mp4` (H.264 **and** HEVC), `.mov` | any camera / phone |

Notes:
- CR3 and HEVC are the coverage traps — make sure at least one of each is present.
- Keep it small (a dozen files is plenty for M0); these are for correctness/speed
  spot-checks, not a full benchmark suite.
