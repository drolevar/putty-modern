# PuTTY Modern

A WinUI 3 front end for [PuTTY](https://www.chiark.greenend.org.uk/~sgtatham/putty/), by Andrij Abyzov: tabs and docked
panels, a settings window, saved sessions in folders, and the connections and terminal of PuTTY underneath.

Build, test, publish and the installer are described in [CLAUDE.md](CLAUDE.md); work is tracked with Beads (`bd ready`).

## Credit and licences

PuTTY Modern is a WinUI 3 front end for PuTTY, by Andrij Abyzov. The connections, the terminal emulator and the settings are
PuTTY's own code, by Simon Tatham and the PuTTY contributors, included unmodified in `external/putty`; a few files in
`native/engine` adapt code from PuTTY for Windows, as their headers say. PuTTY is © 1997–2026 Simon Tatham and is
MIT-licensed. PuTTY Modern is independent and not affiliated with or endorsed by the PuTTY team. It shares saved sessions and
host keys with PuTTY (`HKCU\Software\SimonTatham\PuTTY`).

PuTTY Modern itself is under the MIT licence ([LICENSE](LICENSE)). PuTTY's licence, word for word, and the licences of the
other software it contains are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) (the .NET runtime's own third-party notices
are in [notices/](notices/)). The same text is in the installers and under About, then Licences, in the program.
