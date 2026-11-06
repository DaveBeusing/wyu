using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Groundwork.Rendering;

namespace WorldsYetUnseen.Client;

internal sealed class TerrainEditSmoke
{
    private enum Step { Initial, Removed, RestoreHeld, RestoreSuperseded, LatestRemoval, Restored, Complete }

    private const uint KeyDown = 0x0100;
    private const uint KeyUp = 0x0101;
    private const uint ToggleKey = 0x54;
    private const int KeyDownParameters = 0x00140001;
    private const int KeyUpParameters = unchecked((int)0xc0140001);
    private readonly TaskCompletionSource _releaseRestore = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _restoreBuilt;
    private Step _step;

    internal Task BeforeCompletion(ulong generation)
    {
        if (generation != 3)
            return Task.CompletedTask;

        Volatile.Write(ref _restoreBuilt, 1);
        return _releaseRestore.Task;
    }

    internal bool Advance(IRenderMeshReplacement meshes, TerrainEdits edits)
    {
        var status = meshes.ReadMeshReplacement();
        if (status.InstalledGeneration == 3 || status.LastPresentedGeneration == 3)
            throw new InvalidOperationException("Terrain smoke installed or presented the held generation 3.");

        switch (_step)
        {
            case Step.Initial when IsPresented(status, 1) && TryPostToggle():
                Log("initial", status);
                _step = Step.Removed;
                break;
            case Step.Removed when IsPresented(status, 2) && TryPostToggle():
                Log("boundary cell removed", status);
                _step = Step.RestoreHeld;
                break;
            case Step.RestoreHeld when Volatile.Read(ref _restoreBuilt) != 0:
                VerifyRemovalRemainedVisible(status, desired: 3);
                if (TryPostToggle())
                {
                    Log("restore build held", status);
                    _step = Step.RestoreSuperseded;
                }
                break;
            case Step.RestoreSuperseded when status.DesiredGeneration == 4:
                VerifyRemovalRemainedVisible(status, desired: 4);
                Log("held restore superseded", status);
                Release();
                _step = Step.LatestRemoval;
                break;
            case Step.LatestRemoval when IsPresented(status, 4):
                if (edits.LastDiscardedGeneration != 3)
                    throw new InvalidOperationException("Terrain smoke did not discard the superseded generation 3.");
                if (TryPostToggle())
                {
                    Log("generation 3 discarded, latest removal", status);
                    _step = Step.Restored;
                }
                break;
            case Step.Restored when IsPresented(status, 5):
                Log("boundary cell restored", status);
                _step = Step.Complete;
                break;
        }

        return _step == Step.Complete;
    }

    internal void Release() => _releaseRestore.TrySetResult();

    private static bool IsPresented(MeshReplacementSnapshot status, ulong generation) =>
        status == new MeshReplacementSnapshot(generation, generation, generation);

    private static void VerifyRemovalRemainedVisible(MeshReplacementSnapshot status, ulong desired)
    {
        if (status != new MeshReplacementSnapshot(desired, 2, 2))
            throw new InvalidOperationException($"Terrain smoke changed the visible removal while a restore was held ({status}).");
    }

    private static void Log(string step, MeshReplacementSnapshot status) =>
        Console.WriteLine($"Terrain edit smoke {step}: desired={status.DesiredGeneration}, " +
            $"installed={status.InstalledGeneration}, presented={status.LastPresentedGeneration}.");

    private static bool TryPostToggle()
    {
        using var process = Process.GetCurrentProcess();
        var window = process.MainWindowHandle;
        if (window == nint.Zero ||
            !string.Equals(process.MainWindowTitle, DesktopSmoke.WindowTitle, StringComparison.Ordinal))
            throw new InvalidOperationException("Could not find the WYU terrain smoke window for native input.");

        if (GetForegroundWindow() != window)
        {
            // Wait until Windows has activated this window before posting input to it.
            _ = SetForegroundWindow(window);
            return false;
        }

        if (!PostMessage(window, KeyDown, ToggleKey, KeyDownParameters) ||
            !PostMessage(window, KeyUp, ToggleKey, KeyUpParameters))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not post the terrain smoke toggle.");

        return true;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
