# WP11-W05 installed planet playtest

WYU Alpha installs per user on Windows 11 x64. The offline installer
includes the .NET runtime and the ten verified Groundwork packages.
The game needs a supported D3D12 adapter. The NVIDIA qualification uses
Graphics Tools for the D3D12 debug layer. Build with the pinned SDK,
compiler, Groundwork manifest and locked restore documented in the
[installer build instructions](build-bootstrap.md).

On a desktop without an existing WYU Alpha installation, run the installer
test without elevation. Supply the manifest digest independently.

    ./eng/Test-OfflineInstaller.ps1 -InstallerDirectory $installerDirectory -ExpectedManifestSha256 $approvedInstallerManifestHash -PlanetPlaytest

The test installs locally, checks the Start menu shortcut and bundled
runtime, repairs a missing program file by reinstalling, then exercises the
planet UI in a separate temporary save namespace. It uninstalls the test
build and verifies that user data are kept. The temporary evidence directory
contains bounded client logs, screenshots and walking cadence samples. The
samples come from the gameplay overlay and do not establish production
CPU, GPU or frame-fence budgets.

The keyboard and mouse route enters seed **42**, holds **D** as loading
finishes, crosses the origin change, then walks partway back with **A**.
It adds ochre terrain, saves and relaunches through
**Continue**. It starts the automatic flight with **F6**, observes orbit
and touchdown, saves again and confirms the landed state after restart.
The [flight replay](wp11-flight.md) separately compares deterministic
checkpoints on WARP and NVIDIA.

The installed test checks invalid seed input, an unavailable edit target,
an occupied writer, an unsupported world format and damaged checkpoint
data. Controlled graphics startup and streaming quota failures use the
same visible error reporting as the regular client. Invalid or damaged
slots keep their bytes. Create a separate world to continue playing.
The earlier alpha rescue mission keeps its own save namespace.

See [exploration controls](wp11-exploration.md),
[terrain editing and saves](wp11-volume-save.md) and
[flight controls](wp11-flight.md). The Groundwork reference-system
qualification and production acceptance are tracked separately.
