# Prospero Explorer

<img width="1280" height="720" alt="Prospero Explorer_20260912003907" src="https://github.com/user-attachments/assets/ee314a26-eb29-45ba-b334-2da44f24e36a" />

A file explorer for the console, built on the SharpProspero SDK. It browses and manages the file
system, unpacks and builds archives, plays audio and video, views and edits text, views and converts
pictures, installs packages from the writable area and from removable devices, launches an installed
title, serves the whole file system over the network, opens a web address, and reports what the
machine is doing.

The interface draws its text with the console's own font when the system provides it, falling back to
the built-in one otherwise, and every page carries a breadcrumb of where you are and a header that says
what its buttons do.

It exercises most of the SharpProspero SDK end to end, so it doubles as a way to check that a build of the toolchain
works on real hardware.

## Building

The result is always a module folder, not an installable pkg file (for now).

```bash
pwsh ./build.ps1
```

The module lands in `out/module`. Copy the whole folder, not just `eboot.bin`: it carries the
metadata and the modules the application needs beside it.

The SDK is expected beside this project in the same checkout. Point elsewhere with `-SdkRoot`, or set
`SHARPPROSPERO_ROOT`.

An installer treats a title already on the machine as present and declines to replace it, so a build
meant to sit beside the last one needs a title of its own (or replaced):

```bash
pwsh ./build.ps1 -TitleId PPSA99108
```

## What it does

| Area | What is there |
|---|---|
| Browsing | Listing with sizes and kinds, sorting, hidden names, find within a folder, multiple selection, copy, cut, paste, rename, delete, new folder, new file, properties, checksums |
| Archives | Browse and unpack zip and tar, unpack a lone gzip or zlib stream, build a zip or tar from a selection |
| Media | Audio and video playback with seeking, looping, volume and tags; WAV and VAG play through the decoders when the player will not take them |
| Text | Viewer with line numbers, wrapping, a hex view and a table view for comma- or tab-separated files; editing by line, find, go to line, save and save as |
| Pictures | PNG, JPEG, BMP, TGA and GIF; fit modes, panning and pinch-zoom by touch, rotate, flip, grayscale, invert, brightness, contrast, blur, a screenshot of the view, and conversion between every form that can be written |
| Packages | Finds installable files in the writable area and on removable devices, installs them, reports whether a title is already present, and launches an installed title by its id |
| Network | A file service over the network reaching the whole file system, with optional login, a read-only mode and a root it cannot climb above |
| Web | Opens a web address in the system browser |
| System | Console, users, network, memory, storage and display readings |
| Settings | Every part of the above, kept between runs |

## Where it keeps things

Settings and the log live in `/data/prospero-explorer`. Nothing else is written unless you ask for it.

## Controls

D-pad moves, Cross selects, Circle goes back, and Circle on the first page leaves. Where a page offers
more, its header says so: Triangle usually opens the actions for whatever is selected, and Square
usually toggles a selection.
